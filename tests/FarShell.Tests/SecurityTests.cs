using System.Diagnostics;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Authentication;
using System.Security.Principal;
using FarShell.Broker;
using FarShell.Client;
using FarShell.Protocol;
using FarShell.Security;

namespace FarShell.Tests;

[SupportedOSPlatform("windows10.0.17763")]
public sealed class SecurityTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"farshell-security-{Guid.NewGuid():N}");
    public Task InitializeAsync() { Directory.CreateDirectory(_root); return Task.CompletedTask; }
    public Task DisposeAsync() { Directory.Delete(_root, true); return Task.CompletedTask; }

    [Fact]
    public void IdentityAndClientKeysRoundTripThroughUserProtectedStorage()
    {
        using var identity = BrokerIdentity.LoadOrCreate(_root);
        using var loaded = BrokerIdentity.Load(_root);
        Assert.Equal(identity.Fingerprint, loaded.Fingerprint);
        Assert.Equal(identity.Key, loaded.Key);
        var raw = File.ReadAllBytes(Path.Combine(_root, "broker.bin"));
        Assert.False(raw.AsSpan().IndexOf(identity.Key) >= 0);
        var user = WindowsIdentity.GetCurrent().User!;
        var security = new DirectoryInfo(_root).GetAccessControl();
        Assert.True(security.AreAccessRulesProtected);
        Assert.All(security.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>(),
            rule => Assert.Equal(user, rule.IdentityReference));
        var credentials = new ClientCredentials(identity.Fingerprint, identity.Key);
        credentials.Save(_root, "localhost:8022");
        Assert.Single(Directory.EnumerateFiles(Path.Combine(_root, "servers"), "*.bin"));
        Assert.Equal(identity.Key, ClientCredentials.Load(_root, "localhost:8022")!.Key);
        using var held = BrokerIdentity.AcquireLock(_root);
        Assert.Throws<IOException>(() => BrokerIdentity.AcquireLock(_root));
    }

    [Fact(Timeout = 15_000)]
    public async Task PairingRequiresLocalApprovalAndDeliversTheKeyOnlyOnce()
    {
        using var identity = BrokerIdentity.Create();
        using var shutdown = new CancellationTokenSource();
        await using var pairing = new PairingService(_root);
        pairing.Start();
        var server = new BrokerServer(new(0, 8, 1, TimeSpan.FromSeconds(2)), identity, TestProfiles.Default, pairing);
        var running = server.RunAsync(shutdown.Token);
        var port = await server.ListeningPort;
        var endpoint = ClientCredentials.Endpoint("127.0.0.1", port);
        new ClientCredentials(identity.Fingerprint, null).Save(_root, endpoint);
        try
        {
            using (var connection = await ClientConnection.ConnectAsync("127.0.0.1", port, _root, false))
            {
                await connection.Writer.WriteAsync(MessageType.PairRequest, ReadOnlyMemory<byte>.Empty);
                var response = await connection.ReadAsync(CancellationToken.None);
                Assert.Equal(MessageType.PairPending, response.Type);
                var id = ProtocolPayloads.DecodeSessionId(response.Payload);
                await PairingService.ApproveAsync(_root, id);
                await Assert.ThrowsAsync<ArgumentException>(() => PairingService.ApproveAsync(_root, id));
                var accepted = await connection.ReadAsync(CancellationToken.None);
                Assert.Equal(MessageType.PairAccepted, accepted.Type);
                Assert.Equal(identity.Key, accepted.Payload);
                new ClientCredentials(identity.Fingerprint, accepted.Payload).Save(_root, endpoint);
            }

            using var authenticated = await ClientConnection.ConnectAsync("127.0.0.1", port, _root);
            var target = Path.Combine(_root, "upload.bin");
            await authenticated.Writer.WriteAsync(MessageType.UploadFile, FileTransferPayloads.EncodeUploadFile(target, 1));
            Assert.Equal(MessageType.UploadReady, (await authenticated.ReadAsync(CancellationToken.None)).Type);
            await authenticated.Writer.WriteAsync(MessageType.FileData, new byte[] { 42 });
            Assert.Equal(MessageType.FileCompleted, (await authenticated.ReadAsync(CancellationToken.None)).Type);
            Assert.Equal(new byte[] { 42 }, await File.ReadAllBytesAsync(target));
        }
        finally { shutdown.Cancel(); await Stop(running); }
    }

    [Fact(Timeout = 15_000)]
    public async Task ExpiredAndDisconnectedPairingRequestsCannotBeApproved()
    {
        using var identity = BrokerIdentity.Create();
        using var shutdown = new CancellationTokenSource();
        await using var pairing = new PairingService(_root, TimeSpan.FromMilliseconds(250));
        var server = new BrokerServer(new(0, 4, 1, TimeSpan.FromSeconds(2)), identity, TestProfiles.Default, pairing);
        var running = server.RunAsync(shutdown.Token);
        var port = await server.ListeningPort;
        new ClientCredentials(identity.Fingerprint, null).Save(_root, ClientCredentials.Endpoint("127.0.0.1", port));
        try
        {
            using var connection = await ClientConnection.ConnectAsync("127.0.0.1", port, _root, false);
            await connection.Writer.WriteAsync(MessageType.PairRequest, ReadOnlyMemory<byte>.Empty);
            var id = ProtocolPayloads.DecodeSessionId((await connection.ReadAsync(CancellationToken.None)).Payload);
            var expired = await Assert.ThrowsAsync<ProtocolException>(() => connection.ReadAsync(CancellationToken.None));
            Assert.Contains("expired", expired.Message);
            Assert.False(pairing.Approve(id));

            Guid disconnectedId;
            using (var disconnected = await ClientConnection.ConnectAsync("127.0.0.1", port, _root, false))
            {
                await disconnected.Writer.WriteAsync(MessageType.PairRequest, ReadOnlyMemory<byte>.Empty);
                disconnectedId = ProtocolPayloads.DecodeSessionId((await disconnected.ReadAsync(CancellationToken.None)).Payload);
            }
            await Task.Delay(100);
            Assert.False(pairing.Approve(disconnectedId));
        }
        finally { shutdown.Cancel(); await Stop(running); }
    }

    [Fact(Timeout = 15_000)]
    public async Task WrongKeyAndUnauthenticatedOperationsCannotCreateFilesOrShells()
    {
        using var identity = BrokerIdentity.Create();
        using var shutdown = new CancellationTokenSource();
        var server = new BrokerServer(new(0, 8, 1, TimeSpan.FromSeconds(2)), identity, TestProfiles.Default);
        var running = server.RunAsync(shutdown.Token);
        var port = await server.ListeningPort;
        var endpoint = ClientCredentials.Endpoint("127.0.0.1", port);
        new ClientCredentials(identity.Fingerprint, new byte[32]).Save(_root, endpoint);
        try
        {
            await Assert.ThrowsAsync<ProtocolException>(() => ClientConnection.ConnectAsync("127.0.0.1", port, _root));
            foreach (var operation in new[] { MessageType.CreateSession, MessageType.UploadFile, MessageType.DownloadFile })
            {
                using var connection = await ClientConnection.ConnectAsync("127.0.0.1", port, _root, false);
                await connection.Writer.WriteAsync(operation, ReadOnlyMemory<byte>.Empty);
                var error = await Assert.ThrowsAsync<ProtocolException>(() => connection.ReadAsync(CancellationToken.None));
                Assert.Contains("Authentication failed", error.Message);
            }

            // The only session slot is still available after the rejected operations.
            new ClientCredentials(identity.Fingerprint, identity.Key).Save(_root, endpoint);
            using var accepted = await ClientConnection.ConnectAsync("127.0.0.1", port, _root);
            await accepted.Writer.WriteAsync(MessageType.CreateSession, ProtocolPayloads.EncodeCreateSession(new(new(80, 24))));
            Assert.Equal(MessageType.SessionCreated, (await accepted.ReadAsync(CancellationToken.None)).Type);
        }
        finally { shutdown.Cancel(); await Stop(running); }
    }

    [Fact(Timeout = 15_000)]
    public async Task CertificateChangesAreRejectedUntilExplicitTrustAndPairing()
    {
        using var identity = BrokerIdentity.Create();
        using var shutdown = new CancellationTokenSource();
        var server = new BrokerServer(new(0, 8, 1, TimeSpan.FromSeconds(2)), identity, TestProfiles.Default);
        var running = server.RunAsync(shutdown.Token);
        var port = await server.ListeningPort;
        var endpoint = ClientCredentials.Endpoint("127.0.0.1", port);
        new ClientCredentials(new string('0', 64), identity.Key).Save(_root, endpoint);
        try
        {
            var error = await Assert.ThrowsAsync<AuthenticationException>(() => ClientConnection.ConnectAsync("127.0.0.1", port, _root));
            Assert.Contains("certificate changed", error.Message);
            await Assert.ThrowsAsync<AuthenticationException>(() => ClientConnection.TrustAsync("127.0.0.1", port, _root, new string('1', 64)));
            Assert.Equal(new string('0', 64), ClientCredentials.Load(_root, endpoint)!.Fingerprint);
            await ClientConnection.TrustAsync("127.0.0.1", port, _root, identity.Fingerprint);
            Assert.Null(ClientCredentials.Load(_root, endpoint)!.Key);
        }
        finally { shutdown.Cancel(); await Stop(running); }
    }

    [Theory(Timeout = 15_000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SetupTimeoutIncludesProtocolAndAuthentication(bool sendHello)
    {
        using var identity = BrokerIdentity.Create();
        using var shutdown = new CancellationTokenSource();
        var server = new BrokerServer(new(0, 1, 1, TimeSpan.FromMilliseconds(600)), identity, TestProfiles.Default);
        var running = server.RunAsync(shutdown.Token);
        var port = await server.ListeningPort;
        try
        {
            using var tcp = new TcpClient();
            await tcp.ConnectAsync(IPAddress.Loopback, port);
            using var tls = new SslStream(tcp.GetStream(), false, (_, cert, _, _) => cert is not null && BrokerIdentity.GetFingerprint(cert) == identity.Fingerprint);
            await tls.AuthenticateAsClientAsync("localhost");
            if (sendHello)
            {
                await FrameCodec.WriteAsync(tls, MessageType.Hello, ProtocolPayloads.EncodeVersion(ProtocolPayloads.CurrentVersion));
                Assert.Equal(MessageType.HelloAck, (await FrameCodec.ReadAsync(tls))!.Type);
            }
            Assert.Equal(MessageType.Error, (await FrameCodec.ReadAsync(tls))!.Type);
            Assert.Null(await FrameCodec.ReadAsync(tls));
            new ClientCredentials(identity.Fingerprint, identity.Key).Save(_root, ClientCredentials.Endpoint("127.0.0.1", port));
            using var next = await ClientConnection.ConnectAsync("127.0.0.1", port, _root);
        }
        finally { shutdown.Cancel(); await Stop(running); }
    }

    [Fact(Timeout = 25_000)]
    public async Task RotationCommandsRequireConfirmationAndRevokeOldCredentials()
    {
        using var original = BrokerIdentity.LoadOrCreate(_root);
        Assert.Equal(2, (await RunBroker("--rotate-key")).ExitCode);
        using (BrokerIdentity.AcquireLock(_root))
        {
            Assert.Equal(1, (await RunBroker("--rotate-key", "--confirm")).ExitCode);
        }
        var rotation = await RunBroker("--rotate-key", "--confirm");
        Assert.Equal(0, rotation.ExitCode);
        using var rotated = BrokerIdentity.Load(_root);
        Assert.Equal(original.Fingerprint, rotated.Fingerprint);
        Assert.NotEqual(original.Key, rotated.Key);
        Assert.DoesNotContain(Convert.ToBase64String(rotated.Key), rotation.Output);

        using var shutdown = new CancellationTokenSource();
        var server = new BrokerServer(new(0, 4, 1, TimeSpan.FromSeconds(2)), rotated, TestProfiles.Default);
        var running = server.RunAsync(shutdown.Token);
        var port = await server.ListeningPort;
        var endpoint = ClientCredentials.Endpoint("127.0.0.1", port);
        try
        {
            new ClientCredentials(original.Fingerprint, original.Key).Save(_root, endpoint);
            await Assert.ThrowsAsync<ProtocolException>(() => ClientConnection.ConnectAsync("127.0.0.1", port, _root));
            new ClientCredentials(rotated.Fingerprint, rotated.Key).Save(_root, endpoint);
            using var accepted = await ClientConnection.ConnectAsync("127.0.0.1", port, _root);
        }
        finally { shutdown.Cancel(); await Stop(running); }

        Assert.Equal(0, (await RunBroker("--replace-certificate", "--confirm")).ExitCode);
        using var replaced = BrokerIdentity.Load(_root);
        Assert.NotEqual(rotated.Fingerprint, replaced.Fingerprint);
        Assert.NotEqual(rotated.Key, replaced.Key);
        var fingerprint = await RunBroker("--fingerprint");
        Assert.Equal(0, fingerprint.ExitCode);
        Assert.Contains(replaced.Fingerprint, fingerprint.Output);
    }

    [Fact(Timeout = 15_000)]
    public async Task ApprovalTimeoutReturnsFailure()
    {
        Assert.Equal(1, (await RunBroker("--approve", Guid.NewGuid().ToString("N"))).ExitCode);
    }

    private async Task<(int ExitCode, string Output)> RunBroker(params string[] arguments)
    {
        var start = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "FarShell.Broker.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in arguments) { start.ArgumentList.Add(argument); }
        start.ArgumentList.Add("--state-dir");
        start.ArgumentList.Add(_root);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            return (process.ExitCode, await output + await error);
        }
        finally { if (!process.HasExited) { process.Kill(entireProcessTree: true); } }
    }

    private static async Task Stop(Task task)
    {
        try { await task.WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (OperationCanceledException) { }
    }
}
