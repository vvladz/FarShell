using System.Net;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Text;
using FarShell.Broker;
using FarShell.Protocol;

namespace FarShell.Tests;

[SupportedOSPlatform("windows10.0.17763")]
public sealed class BrokerIntegrationTests
{
    [Fact(Timeout = 10_000)]
    public async Task RejectsConnectionsBeyondTheConfiguredLimit()
    {
        using var shutdown = new CancellationTokenSource();
        var server = new BrokerServer(
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
        var server = new BrokerServer(
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

            Assert.NotNull(response);
            Assert.Equal(MessageType.Error, response.Type);
            Assert.Contains(
                "timed out",
                ProtocolPayloads.DecodeError(response.Payload),
                StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            shutdown.Cancel();
            await WaitForServerStopAsync(serverTask);
        }
    }

    [Fact(Timeout = 30_000)]
    public async Task SupportsConcurrentSessionsDetachReattachLimitsAndShutdown()
    {
        using var shutdown = new CancellationTokenSource();
        var server = new BrokerServer(
            new BrokerOptions(
                Port: 0,
                MaxConnections: 8,
                MaxSessions: 2,
                HandshakeTimeout: TimeSpan.FromSeconds(2)));
        var serverTask = server.RunAsync(shutdown.Token);
        var port = await server.ListeningPort.WaitAsync(TimeSpan.FromSeconds(5));
        var detachedMarkerPath = Path.Combine(
            Path.GetTempPath(),
            $"farshell-detached-{Guid.NewGuid():N}.txt");
        var serverStopped = false;

        try
        {
            Guid firstSessionId;
            using (var first = await TestClient.ConnectAsync(port))
            {
                firstSessionId = await first.CreateAsync(new TerminalSize(100, 32));
                await first.SendInputAsync("Write-Output 'FIRST-READY'\r");
                await first.ReadOutputContainingAsync("FIRST-READY");

                var escapedPath = detachedMarkerPath.Replace("'", "''", StringComparison.Ordinal);
                await first.SendInputAsync(
                    $"[Console]::Out.Write(('x' * 200000)); "
                    + $"[IO.File]::WriteAllText('{escapedPath}', 'done')\r");
            }

            await WaitForFileAsync(detachedMarkerPath);
            var detachedSessions = await WaitForSessionsAsync(
                port,
                sessions => sessions.Any(
                    session => session.SessionId == firstSessionId && !session.IsAttached));
            Assert.Single(detachedSessions);

            using var reattached = await TestClient.ConnectAsync(port);
            await reattached.AttachAsync(firstSessionId, new TerminalSize(110, 35));
            var attachedSessions = await WaitForSessionsAsync(
                port,
                sessions => sessions.Any(
                    session => session.SessionId == firstSessionId
                        && session.IsAttached
                        && session.Size == new TerminalSize(110, 35)));
            Assert.Single(attachedSessions);
            await reattached.SendInputAsync("Write-Output 'AFTER-REATTACH'\r");
            await reattached.ReadOutputContainingAsync("AFTER-REATTACH");

            using (var competing = await TestClient.ConnectAsync(port))
            {
                await competing.SendAsync(
                    MessageType.AttachSession,
                    ProtocolPayloads.EncodeAttachSession(
                        firstSessionId,
                        new TerminalSize(80, 24)));
                var response = await competing.ReadAsync();
                Assert.Equal(MessageType.Error, response.Type);
                Assert.Contains(
                    "already attached",
                    ProtocolPayloads.DecodeError(response.Payload),
                    StringComparison.OrdinalIgnoreCase);
            }

            using var second = await TestClient.ConnectAsync(port);
            var secondSessionId = await second.CreateAsync(new TerminalSize(90, 28));

            using (var overLimit = await TestClient.ConnectAsync(port))
            {
                await overLimit.SendAsync(
                    MessageType.CreateSession,
                    ProtocolPayloads.EncodeResize(new TerminalSize(80, 24)));
                var response = await overLimit.ReadAsync();
                Assert.Equal(MessageType.Error, response.Type);
                Assert.Contains(
                    "limit",
                    ProtocolPayloads.DecodeError(response.Payload),
                    StringComparison.OrdinalIgnoreCase);
            }

            using (var terminator = await TestClient.ConnectAsync(port))
            {
                await terminator.SendAsync(
                    MessageType.TerminateSession,
                    ProtocolPayloads.EncodeSessionId(secondSessionId));
                var response = await terminator.ReadAsync();
                Assert.Equal(MessageType.SessionTerminated, response.Type);
                Assert.Equal(
                    secondSessionId,
                    ProtocolPayloads.DecodeSessionId(response.Payload));
            }

            var terminatedExit = ProtocolPayloads.DecodeSessionExit(
                (await second.ReadUntilAsync(MessageType.SessionExited)).Payload);
            Assert.Equal(secondSessionId, terminatedExit.SessionId);

            await reattached.SendInputAsync("exit\r");
            var normalExit = ProtocolPayloads.DecodeSessionExit(
                (await reattached.ReadUntilAsync(MessageType.SessionExited)).Payload);
            Assert.Equal(firstSessionId, normalExit.SessionId);
            Assert.Equal(0, normalExit.ExitCode);

            await WaitForSessionsAsync(port, sessions => sessions.Count == 0);

            using var liveAtShutdown = await TestClient.ConnectAsync(port);
            _ = await liveAtShutdown.CreateAsync(new TerminalSize(80, 24));
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

            File.Delete(detachedMarkerPath);
        }
    }

    private static async Task<IReadOnlyList<SessionInfo>> WaitForSessionsAsync(
        int port,
        Func<IReadOnlyList<SessionInfo>, bool> predicate)
    {
        for (var attempt = 0; attempt < 50; attempt++)
        {
            using var client = await TestClient.ConnectAsync(port);
            var sessions = await client.ListAsync();
            if (predicate(sessions))
            {
                return sessions;
            }

            await Task.Delay(100);
        }

        throw new TimeoutException("Session registry did not reach the expected state.");
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

        throw new TimeoutException("Detached session output was not drained.");
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
        private readonly NetworkStream _transport;
        private readonly FrameWriter _writer;

        private TestClient(TcpClient client)
        {
            _client = client;
            _transport = client.GetStream();
            _writer = new FrameWriter(_transport);
        }

        internal static async Task<TestClient> ConnectAsync(int port)
        {
            var tcpClient = new TcpClient { NoDelay = true };
            await tcpClient.ConnectAsync(IPAddress.Loopback, port);
            var client = new TestClient(tcpClient);

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
                return client;
            }
            catch
            {
                client.Dispose();
                throw;
            }
        }

        internal async Task<Guid> CreateAsync(TerminalSize size)
        {
            await SendAsync(
                MessageType.CreateSession,
                ProtocolPayloads.EncodeResize(size));
            var response = await ReadAsync();
            Assert.Equal(MessageType.SessionCreated, response.Type);
            return ProtocolPayloads.DecodeSessionId(response.Payload);
        }

        internal async Task AttachAsync(Guid sessionId, TerminalSize size)
        {
            await SendAsync(
                MessageType.AttachSession,
                ProtocolPayloads.EncodeAttachSession(sessionId, size));
            var response = await ReadAsync();
            Assert.Equal(MessageType.SessionAttached, response.Type);
            Assert.Equal(sessionId, ProtocolPayloads.DecodeSessionId(response.Payload));
        }

        internal async Task<IReadOnlyList<SessionInfo>> ListAsync()
        {
            await SendAsync(MessageType.ListSessions, ReadOnlyMemory<byte>.Empty);
            var response = await ReadAsync();
            Assert.Equal(MessageType.SessionList, response.Type);
            return ProtocolPayloads.DecodeSessionList(response.Payload);
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

        public void Dispose()
        {
            _client.Dispose();
            _writer.Dispose();
        }
    }
}
