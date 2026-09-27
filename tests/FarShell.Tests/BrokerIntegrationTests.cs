using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Net.Security;
using FarShell.Security;
using System.Runtime.Versioning;
using System.Text;
using FarShell.Broker;
using FarShell.Client;
using FarShell.Protocol;

namespace FarShell.Tests;

[SupportedOSPlatform("windows10.0.17763")]
public sealed class BrokerIntegrationTests
{
    private static readonly BrokerIdentity Identity = BrokerIdentity.Create();
    private static BrokerServer CreateBroker(BrokerOptions options, SessionProfiles? profiles = null) => new(options, Identity, profiles ?? TestProfiles.Default);

    private static async Task<SslStream> OpenTlsAsync(TcpClient client)
    {
        var stream = new SslStream(client.GetStream(), false, (_, certificate, _, _) =>
            certificate is not null && BrokerIdentity.GetFingerprint(certificate) == Identity.Fingerprint);
        await stream.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
        {
            TargetHost = "localhost",
            EnabledSslProtocols = System.Security.Authentication.SslProtocols.Tls12 | System.Security.Authentication.SslProtocols.Tls13,
        });
        return stream;
    }

    [Theory(Timeout = 20_000)]
    [InlineData("")]
    [InlineData("work")]
    public async Task ProfilesControlArgumentsEnvironmentAndWorkingDirectory(string requestedProfile)
    {
        var root = Path.Combine(Path.GetTempPath(), $"farshell profile {Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var script = Path.Combine(root, "shell script.ps1");
        var resultPath = Path.Combine(root, "result.json");
        await File.WriteAllTextAsync(script, """
            [IO.File]::WriteAllText($env:RESULT_FILE, ([ordered]@{
                Arguments = @($args)
                Directory = [Environment]::CurrentDirectory
                Value = $env:VALUE
                Copy = $env:COPY
                Removed = $env:REMOVE_ME
                Pipe = $env:FARSHELL_SEND_PIPE
                Path = $env:PATH
            } | ConvertTo-Json -Compress))
            exit 7
            """);
        var arguments = new[] { "two words", "quote\"value", @"C:\trailing dir\", "" };
        var environment = new Dictionary<string, string>(TestProfiles.Default.Resolve("").Launch.Environment, StringComparer.OrdinalIgnoreCase)
        {
            ["VALUE"] = "startup",
            ["REMOVE_ME"] = "remove",
        };
        var json = System.Text.Json.JsonSerializer.Serialize(new
        {
            defaultProfile = "Work",
            profiles = new Dictionary<string, object>
            {
                ["Work"] = new
                {
                    workingDirectory = root,
                    shell = new { file = "pwsh.exe", args = new[] { "-NoProfile", "-File", script }.Concat(arguments).ToArray() },
                    env = new Dictionary<string, string?>
                    {
                        ["VALUE"] = "override",
                        ["COPY"] = "%VALUE%",
                        ["REMOVE_ME"] = null,
                        ["RESULT_FILE"] = resultPath,
                        ["FARSHELL_SEND_PIPE"] = null,
                        ["PATH"] = null,
                    },
                },
            },
        });
        var profiles = SessionProfiles.Parse(System.Text.Json.JsonDocument.Parse(json).RootElement, environment);
        using var shutdown = new CancellationTokenSource();
        var server = CreateBroker(new(0, 4, 1, TimeSpan.FromSeconds(2)), profiles);
        var running = server.RunAsync(shutdown.Token);
        var port = await server.ListeningPort;
        try
        {
            using (var unknown = await TestClient.ConnectAsync(port))
            {
                await unknown.SendAsync(MessageType.CreateSession, ProtocolPayloads.EncodeCreateSession(new(new(80, 24), "missing")));
                Assert.Contains("Unknown profile", ProtocolPayloads.DecodeError((await unknown.ReadAsync()).Payload));
                Assert.False(File.Exists(resultPath));
            }
            using var client = await TestClient.ConnectAsync(port);
            await client.CreateAsync(new(80, 24), requestedProfile);
            var exit = ProtocolPayloads.DecodeSessionExit((await client.ReadUntilAsync(MessageType.SessionExited)).Payload);
            Assert.Equal(7, exit.ExitCode);
            using var result = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(resultPath));
            var value = result.RootElement;
            Assert.Equal(arguments, value.GetProperty("Arguments").EnumerateArray().Select(a => a.GetString()).ToArray());
            Assert.Equal(root, value.GetProperty("Directory").GetString());
            Assert.Equal("override", value.GetProperty("Value").GetString());
            Assert.Equal("startup", value.GetProperty("Copy").GetString());
            Assert.Equal(System.Text.Json.JsonValueKind.Null, value.GetProperty("Removed").ValueKind);
            Assert.StartsWith("FarShell.Send.", value.GetProperty("Pipe").GetString());
            Assert.Contains(Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory), value.GetProperty("Path").GetString()!.Split(';'));
        }
        finally
        {
            shutdown.Cancel();
            await WaitForServerStopAsync(running);
            Directory.Delete(root, true);
        }
    }
    [Fact(Timeout = 15_000)]
    public async Task IdleFirstOperationReleasesTheOnlyConnectionSlot()
    {
        using var shutdown = new CancellationTokenSource();
        var server = CreateBroker(new BrokerOptions(0, 1, 1, TimeSpan.FromMilliseconds(400)));
        var running = server.RunAsync(shutdown.Token);
        var port = await server.ListeningPort;
        try
        {
            using (var idle = await TestClient.ConnectAsync(port))
            {
                var error = await idle.ReadAsync();
                Assert.Equal(MessageType.Error, error.Type);
                Assert.Contains("timed out", ProtocolPayloads.DecodeError(error.Payload));
            }

            using var active = await TestClient.ConnectAsync(port);
            var path = Path.Combine(Path.GetTempPath(), $"farshell-timeout-{Guid.NewGuid():N}.txt");
            try
            {
                await active.SendAsync(MessageType.UploadFile, FileTransferPayloads.EncodeUploadFile(path, 1));
                Assert.Equal(MessageType.UploadReady, (await active.ReadAsync()).Type);
                await Task.Delay(600);
                await active.SendAsync(MessageType.FileData, new byte[] { 42 });
                Assert.Equal(MessageType.FileCompleted, (await active.ReadAsync()).Type);
                Assert.Equal(new byte[] { 42 }, await File.ReadAllBytesAsync(path));
            }
            finally { File.Delete(path); }
        }
        finally
        {
            shutdown.Cancel();
            await WaitForServerStopAsync(running);
        }
    }

    [Theory(Timeout = 30_000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BlockedOutputEndsOnDisconnectOrShutdown(bool stopBroker)
    {
        using var shutdown = new CancellationTokenSource();
        var server = CreateBroker(new BrokerOptions(0, 4, 1, TimeSpan.FromSeconds(2)));
        var running = server.RunAsync(shutdown.Token);
        var port = await server.ListeningPort;
        var root = Path.Combine(Path.GetTempPath(), $"farshell-blocked-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            using var client = await TestClient.ConnectAsync(port);
            await client.CreateAsync(new TerminalSize(80, 24));
            await client.ReadOutputContainingAsync("\u001b[2J");
            var prefix = root.Replace("'", "''");
            await client.SendInputAsync(
                "$child = Start-Process pwsh.exe -ArgumentList '-NoProfile', '-Command', 'Start-Sleep 120' -WindowStyle Hidden -PassThru; "
                + $"[IO.File]::WriteAllText('{prefix}\\pids', \"$PID,$($child.Id)\"); "
                + "$s = 'x' * 32768; for ($i=0; $i -lt 65536; $i++) { [Console]::Write($s) }; "
                + $"[IO.File]::WriteAllText('{prefix}\\done', 'done')\r");
            await WaitForFileAsync(Path.Combine(root, "pids"));
            var ids = (await File.ReadAllTextAsync(Path.Combine(root, "pids"))).Split(',');
            using var shell = Process.GetProcessById(int.Parse(ids[0]));
            using var child = Process.GetProcessById(int.Parse(ids[1]));
            await Task.Delay(500);
            Assert.False(File.Exists(Path.Combine(root, "done")));
            if (stopBroker) { shutdown.Cancel(); }
            else { client.Dispose(); }
            await shell.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            if (!stopBroker)
            {
                var next = await WaitForAvailableSessionAsync(port, new TerminalSize(80, 24));
                next.Client.Dispose();
            }
        }
        finally
        {
            shutdown.Cancel();
            await WaitForServerStopAsync(running);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact(Timeout = 10_000)]
    public async Task RejectsConnectionsBeyondTheConfiguredLimit()
    {
        using var shutdown = new CancellationTokenSource();
        var server = CreateBroker(
            new BrokerOptions(
                Port: 0,
                MaxConnections: 1,
                MaxSessions: 1,
                HandshakeTimeout: TimeSpan.FromSeconds(5)));
        var serverTask = server.RunAsync(shutdown.Token);
        var port = await server.ListeningPort.WaitAsync(TimeSpan.FromSeconds(5));

        try
        {
            using var heldConnection = new TcpClient();
            await heldConnection.ConnectAsync(IPAddress.Loopback, port);
            await Task.Delay(100);

            using var rejectedConnection = new TcpClient();
            await rejectedConnection.ConnectAsync(IPAddress.Loopback, port);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var response = await FrameCodec.ReadAsync(
                rejectedConnection.GetStream(),
                timeout.Token);

            Assert.Null(response);
        }
        finally
        {
            shutdown.Cancel();
            await WaitForServerStopAsync(serverTask);
        }
    }

    [Fact(Timeout = 10_000)]
    public async Task RejectsAnIdleHandshakeAfterTheConfiguredTimeout()
    {
        using var shutdown = new CancellationTokenSource();
        var server = CreateBroker(
            new BrokerOptions(
                Port: 0,
                MaxConnections: 2,
                MaxSessions: 1,
                HandshakeTimeout: TimeSpan.FromMilliseconds(200)));
        var serverTask = server.RunAsync(shutdown.Token);
        var port = await server.ListeningPort.WaitAsync(TimeSpan.FromSeconds(5));

        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, port);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var response = await FrameCodec.ReadAsync(client.GetStream(), timeout.Token);

            Assert.Null(response);
        }
        finally
        {
            shutdown.Cancel();
            await WaitForServerStopAsync(serverTask);
        }
    }

    [Theory(Timeout = 10_000)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task RejectsOldVersionsAndRemovedSessionOperations(int version)
    {
        using var shutdown = new CancellationTokenSource();
        var server = CreateBroker(
            new BrokerOptions(
                Port: 0,
                MaxConnections: 2,
                MaxSessions: 1,
                HandshakeTimeout: TimeSpan.FromSeconds(2)));
        var serverTask = server.RunAsync(shutdown.Token);
        var port = await server.ListeningPort.WaitAsync(TimeSpan.FromSeconds(5));

        try
        {
            using (var oldClient = new TcpClient())
            {
                await oldClient.ConnectAsync(IPAddress.Loopback, port);
                using var transport = await OpenTlsAsync(oldClient);
                using var writer = new FrameWriter(transport);
                await writer.WriteAsync(MessageType.Hello, ProtocolPayloads.EncodeVersion(version));
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                var response = await FrameCodec.ReadAsync(transport, timeout.Token);
                Assert.NotNull(response);
                Assert.Equal(MessageType.Error, response.Type);
                Assert.Contains("Unsupported protocol version", ProtocolPayloads.DecodeError(response.Payload));
            }

            using var currentClient = await TestClient.ConnectAsync(port);
            await currentClient.SendAsync((MessageType)22, ReadOnlyMemory<byte>.Empty);
            var removedOperation = await currentClient.ReadAsync();
            Assert.Equal(MessageType.Error, removedOperation.Type);
            Assert.Contains("Unknown message type", ProtocolPayloads.DecodeError(removedOperation.Payload));
        }
        finally
        {
            shutdown.Cancel();
            await WaitForServerStopAsync(serverTask);
        }
    }

    [Fact(Timeout = 30_000)]
    public async Task ConcurrentShellsEndOnDisconnectAndBrokerShutdown()
    {
        using var shutdown = new CancellationTokenSource();
        var server = CreateBroker(
            new BrokerOptions(
                Port: 0,
                MaxConnections: 8,
                MaxSessions: 2,
                HandshakeTimeout: TimeSpan.FromSeconds(2)));
        var serverTask = server.RunAsync(shutdown.Token);
        var port = await server.ListeningPort.WaitAsync(TimeSpan.FromSeconds(5));
        var processIdPath = Path.Combine(
            Path.GetTempPath(),
            $"farshell-process-{Guid.NewGuid():N}.txt");
        var serverStopped = false;

        try
        {
            using var first = await TestClient.ConnectAsync(port);
            _ = await first.CreateAsync(new TerminalSize(100, 32));
            await first.ReadOutputContainingAsync("\u001b[2J");
            var escapedPath = processIdPath.Replace("'", "''", StringComparison.Ordinal);
            await first.SendInputAsync(
                $"[IO.File]::WriteAllText('{escapedPath}', [string]$PID); "
                + "while ($true) { Start-Sleep -Seconds 1 }\r");
            await WaitForFileAsync(processIdPath);
            using var firstProcess = Process.GetProcessById(
                int.Parse(await File.ReadAllTextAsync(processIdPath)));

            using var second = await TestClient.ConnectAsync(port);
            _ = await second.CreateAsync(new TerminalSize(90, 28));
            await second.SendInputAsync("Write-Output 'SECOND-READY'\r");
            await second.ReadOutputContainingAsync("SECOND-READY");

            using (var overLimit = await TestClient.ConnectAsync(port))
            {
                await overLimit.SendAsync(
                    MessageType.CreateSession,
                    ProtocolPayloads.EncodeCreateSession(new(new TerminalSize(80, 24))));
                var response = await overLimit.ReadAsync();
                Assert.Equal(MessageType.Error, response.Type);
                Assert.Contains(
                    "limit",
                    ProtocolPayloads.DecodeError(response.Payload),
                    StringComparison.OrdinalIgnoreCase);
            }

            first.Dispose();
            await firstProcess.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));

            var available = await WaitForAvailableSessionAsync(
                port,
                new TerminalSize(80, 24));
            using var third = available.Client;
            var thirdSessionId = available.SessionId;
            await second.SendInputAsync("Write-Output 'STILL-RUNNING'\r");
            await second.ReadOutputContainingAsync("STILL-RUNNING");

            await third.SendInputAsync("exit\r");
            var normalExit = ProtocolPayloads.DecodeSessionExit(
                (await third.ReadUntilAsync(MessageType.SessionExited)).Payload);
            Assert.Equal(thirdSessionId, normalExit.SessionId);
            Assert.Equal(0, normalExit.ExitCode);

            shutdown.Cancel();
            await WaitForServerStopAsync(serverTask);
            serverStopped = true;
        }
        finally
        {
            if (!serverStopped)
            {
                shutdown.Cancel();
                await WaitForServerStopAsync(serverTask);
            }

            File.Delete(processIdPath);
        }
    }

    [Fact(Timeout = 20_000)]
    public async Task TransfersFilesInBothDirectionsAndKeepsInterruptedUploadsAtomic()
    {
        using var shutdown = new CancellationTokenSource();
        var server = CreateBroker(
            new BrokerOptions(
                Port: 0,
                MaxConnections: 4,
                MaxSessions: 1,
                HandshakeTimeout: TimeSpan.FromSeconds(2)));
        var serverTask = server.RunAsync(shutdown.Token);
        var port = await server.ListeningPort.WaitAsync(TimeSpan.FromSeconds(5));
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"farshell-transfer-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);

        try
        {
            var content = new byte[200_000];
            Random.Shared.NextBytes(content);
            var uploadSource = Path.Combine(directory, "upload-source.bin");
            var remoteTarget = Path.Combine(directory, "remote-target.bin");
            var downloadTarget = Path.Combine(directory, "download-target.bin");
            await File.WriteAllBytesAsync(uploadSource, content);

            new ClientCredentials(Identity.Fingerprint, Identity.Key).Save(directory, ClientCredentials.Endpoint("127.0.0.1", port));
            var client = new RemoteTerminalClient(
                IPAddress.Loopback.ToString(),
                port,
                TimeSpan.Zero, stateDirectory: directory);
            Assert.Equal(0, await client.UploadAsync(uploadSource, remoteTarget));
            Assert.Equal(content, await File.ReadAllBytesAsync(remoteTarget));

            Assert.Equal(0, await client.DownloadAsync(remoteTarget, downloadTarget));
            Assert.Equal(content, await File.ReadAllBytesAsync(downloadTarget));

            var protectedTarget = Path.Combine(directory, "protected-target.txt");
            await File.WriteAllTextAsync(protectedTarget, "original");
            using (var interrupted = await TestClient.ConnectAsync(port))
            {
                await interrupted.SendAsync(
                    MessageType.UploadFile,
                    FileTransferPayloads.EncodeUploadFile(protectedTarget, 100));
                var ready = await interrupted.ReadAsync();
                Assert.Equal(MessageType.UploadReady, ready.Type);
                await interrupted.SendAsync(MessageType.FileData, new byte[] { 1, 2, 3 });
            }

            await WaitForConditionAsync(
                () => !Directory.EnumerateFiles(directory, "*.farshell-upload").Any());
            Assert.Equal("original", await File.ReadAllTextAsync(protectedTarget));
        }
        finally
        {
            shutdown.Cancel();
            await WaitForServerStopAsync(serverTask);
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory(Timeout = 20_000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SendUsesTheRemoteCurrentDirectoryAndAttachedClient(bool selectedProfile)
    {
        using var shutdown = new CancellationTokenSource();
        var profiles = selectedProfile ? SessionProfiles.Parse(System.Text.Json.JsonDocument.Parse("""
            {"defaultProfile":"work","profiles":{"work":{"shell":{"file":"pwsh.exe","args":["-NoLogo"]},"env":{"PATH":null,"FARSHELL_SEND_PIPE":"overridden"}}}}
            """).RootElement, TestProfiles.Default.Resolve("").Launch.Environment) : null;
        var server = CreateBroker(
            new BrokerOptions(
                Port: 0,
                MaxConnections: 4,
                MaxSessions: 1,
                HandshakeTimeout: TimeSpan.FromSeconds(2)), profiles);
        var serverTask = server.RunAsync(shutdown.Token);
        var port = await server.ListeningPort.WaitAsync(TimeSpan.FromSeconds(5));
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"farshell send integration {Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(directory, "results"));

        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(directory, "root.txt"),
                "root-content");
            await File.WriteAllTextAsync(
                Path.Combine(directory, "results", "data.bin"),
                "binary-content");
            using var client = await TestClient.ConnectAsync(port);
            _ = await client.CreateAsync(new TerminalSize(100, 30), selectedProfile ? "work" : "");
            var escapedDirectory = directory.Replace("'", "''", StringComparison.Ordinal);
            await client.SendInputAsync(
                $"Set-Location -LiteralPath '{escapedDirectory}'; "
                + "FarShell.Broker.exe --send '*.txt' 'results\\*.bin'\r");

            var files = await client.ReceiveSessionFilesAsync(2);

            Assert.Equal(
                "root-content",
                Encoding.UTF8.GetString(files["root.txt"]));
            Assert.Equal(
                "binary-content",
                Encoding.UTF8.GetString(files[@"results\data.bin"]));
            await client.ReadOutputContainingAsync(
                "Transferred 2 files to the attached FarShell client.");
        }
        finally
        {
            shutdown.Cancel();
            await WaitForServerStopAsync(serverTask);
            Directory.Delete(directory, recursive: true);
        }
    }

    private static async Task<(TestClient Client, Guid SessionId)> WaitForAvailableSessionAsync(
        int port,
        TerminalSize size)
    {
        for (var attempt = 0; attempt < 50; attempt++)
        {
            var client = await TestClient.ConnectAsync(port);
            var transferred = false;
            try
            {
                await client.SendAsync(MessageType.CreateSession, ProtocolPayloads.EncodeCreateSession(new(size)));
                var response = await client.ReadAsync();
                if (response.Type == MessageType.SessionCreated)
                {
                    var sessionId = ProtocolPayloads.DecodeSessionId(response.Payload);
                    transferred = true;
                    return (client, sessionId);
                }

                Assert.Equal(MessageType.Error, response.Type);
                Assert.Contains(
                    "limit",
                    ProtocolPayloads.DecodeError(response.Payload),
                    StringComparison.OrdinalIgnoreCase);
            }
            finally
            {
                if (!transferred)
                {
                    client.Dispose();
                }
            }

            await Task.Delay(100);
        }

        throw new TimeoutException("Disconnected shell did not release its session slot.");
    }

    private static async Task WaitForFileAsync(string path)
    {
        for (var attempt = 0; attempt < 50; attempt++)
        {
            if (File.Exists(path))
            {
                return;
            }

            await Task.Delay(100);
        }

        throw new TimeoutException("The shell did not write its process ID.");
    }

    private static async Task WaitForConditionAsync(Func<bool> predicate)
    {
        for (var attempt = 0; attempt < 50; attempt++)
        {
            if (predicate())
            {
                return;
            }

            await Task.Delay(100);
        }

        throw new TimeoutException("Expected file-transfer cleanup did not complete.");
    }

    private static async Task WaitForServerStopAsync(Task serverTask)
    {
        try
        {
            await serverTask.WaitAsync(TimeSpan.FromSeconds(10));
        }
        catch (OperationCanceledException)
        {
            // Cancellation is the normal BrokerServer shutdown result.
        }
    }

    private sealed class TestClient : IDisposable
    {
        private static readonly TimeSpan OperationTimeout = TimeSpan.FromSeconds(10);
        private readonly TcpClient _client;
        private readonly SslStream _transport;
        private readonly FrameWriter _writer;

        private TestClient(TcpClient client, SslStream transport)
        {
            _client = client;
            _transport = transport;
            _writer = new FrameWriter(_transport);
        }

        internal static async Task<TestClient> ConnectAsync(int port)
        {
            var tcpClient = new TcpClient { NoDelay = true };
            await tcpClient.ConnectAsync(IPAddress.Loopback, port);
            var client = new TestClient(tcpClient, await OpenTlsAsync(tcpClient));

            try
            {
                await client.SendAsync(
                    MessageType.Hello,
                    ProtocolPayloads.EncodeVersion(ProtocolPayloads.CurrentVersion));
                var response = await client.ReadAsync();
                Assert.Equal(MessageType.HelloAck, response.Type);
                Assert.Equal(
                    ProtocolPayloads.CurrentVersion,
                    ProtocolPayloads.DecodeVersion(response.Payload));
                await client.SendAsync(MessageType.Authenticate, Identity.Key);
                Assert.Equal(MessageType.Authenticated, (await client.ReadAsync()).Type);
                return client;
            }
            catch
            {
                client.Dispose();
                throw;
            }
        }

        internal async Task<Guid> CreateAsync(TerminalSize size, string profile = "")
        {
            await SendAsync(
                MessageType.CreateSession,
                ProtocolPayloads.EncodeCreateSession(new(size, profile)));
            var response = await ReadAsync();
            Assert.Equal(MessageType.SessionCreated, response.Type);
            return ProtocolPayloads.DecodeSessionId(response.Payload);
        }

        internal Task SendInputAsync(string input)
        {
            return SendAsync(MessageType.DataIn, Encoding.UTF8.GetBytes(input));
        }

        internal async Task SendAsync(MessageType type, ReadOnlyMemory<byte> payload)
        {
            using var timeout = new CancellationTokenSource(OperationTimeout);
            await _writer.WriteAsync(type, payload, timeout.Token);
        }

        internal async Task<ProtocolFrame> ReadAsync()
        {
            using var timeout = new CancellationTokenSource(OperationTimeout);
            return await FrameCodec.ReadAsync(_transport, timeout.Token)
                ?? throw new IOException("Broker closed the test connection.");
        }

        internal async Task<ProtocolFrame> ReadUntilAsync(MessageType type)
        {
            while (true)
            {
                var frame = await ReadAsync();
                if (frame.Type == MessageType.Error)
                {
                    throw new ProtocolException(ProtocolPayloads.DecodeError(frame.Payload));
                }

                if (frame.Type == type)
                {
                    return frame;
                }
            }
        }

        internal async Task ReadOutputContainingAsync(string expected)
        {
            using var output = new MemoryStream();
            while (true)
            {
                var frame = await ReadAsync();
                if (frame.Type == MessageType.Error)
                {
                    throw new ProtocolException(ProtocolPayloads.DecodeError(frame.Payload));
                }

                if (frame.Type != MessageType.DataOut)
                {
                    continue;
                }

                output.Write(frame.Payload);
                if (Encoding.UTF8.GetString(output.GetBuffer(), 0, checked((int)output.Length))
                    .Contains(expected, StringComparison.Ordinal))
                {
                    return;
                }
            }
        }

        internal async Task<IReadOnlyDictionary<string, byte[]>> ReceiveSessionFilesAsync(
            int expectedCount)
        {
            var files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
            SessionFileStart? current = null;
            using var content = new MemoryStream();
            while (files.Count < expectedCount)
            {
                var frame = await ReadAsync();
                switch (frame.Type)
                {
                    case MessageType.DataOut:
                        break;

                    case MessageType.SessionFileStart:
                        Assert.Null(current);
                        current = FileTransferPayloads.DecodeSessionFileStart(frame.Payload);
                        content.SetLength(0);
                        await SendAsync(
                            MessageType.SessionFileStatus,
                            FileTransferPayloads.EncodeSessionFileStatus(
                                current.Value.TransferId,
                                SessionFileStatusCode.Ready));
                        break;

                    case MessageType.FileData:
                        Assert.NotNull(current);
                        content.Write(frame.Payload);
                        break;

                    case MessageType.SessionFileEnd:
                        Assert.NotNull(current);
                        var transferId = ProtocolPayloads.DecodeSessionId(frame.Payload);
                        Assert.Equal(current.Value.TransferId, transferId);
                        Assert.Equal(current.Value.Length, content.Length);
                        files.Add(current.Value.RelativePath, content.ToArray());
                        await SendAsync(
                            MessageType.SessionFileStatus,
                            FileTransferPayloads.EncodeSessionFileStatus(
                                transferId,
                                SessionFileStatusCode.Completed));
                        current = null;
                        break;

                    case MessageType.SessionFileAbort:
                        var status = FileTransferPayloads.DecodeSessionFileStatus(frame.Payload);
                        throw new ProtocolException(status.Message);

                    case MessageType.Error:
                        throw new ProtocolException(
                            ProtocolPayloads.DecodeError(frame.Payload));
                }
            }

            return files;
        }

        public void Dispose()
        {
            _client.Dispose();
            _writer.Dispose();
        }
    }
}
