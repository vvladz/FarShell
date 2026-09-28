using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using FarShell.Protocol;
using FarShell.Security;

namespace FarShell.Client;

internal sealed class ClientConnection : IDisposable
{
    private readonly TcpClient _client;
    internal SslStream Transport { get; }
    internal FrameWriter Writer { get; }
    internal ClientCredentials Credentials { get; }

    private ClientConnection(TcpClient client, SslStream transport, ClientCredentials credentials)
    {
        _client = client;
        Transport = transport;
        Writer = new FrameWriter(transport);
        Credentials = credentials;
    }

    internal static async Task<ClientConnection> ConnectAsync(string host, int port, string directory,
        bool authenticate = true)
    {
        var endpoint = ClientCredentials.Endpoint(host, port);
        var credentials = ClientCredentials.Load(directory, endpoint)
            ?? await TrustAsync(host, port, directory, expectedFingerprint: null);
        if (authenticate && credentials.Key is not { Length: 32 })
        {
            throw new AuthenticationException("This client is not paired. Run FarShell.Client.exe --pair with the same server address.");
        }

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var tcp = new TcpClient { NoDelay = true };
        SslStream? tls = null;
        ClientConnection? connection = null;
        string? observed = null;
        try
        {
            await tcp.ConnectAsync(host, port, timeout.Token);
            tls = new SslStream(tcp.GetStream(), false, (_, certificate, _, _) =>
            {
                observed = certificate is null ? null : BrokerIdentity.GetFingerprint(certificate);
                return string.Equals(observed, credentials.Fingerprint, StringComparison.Ordinal);
            });
            await tls.AuthenticateAsClientAsync(TlsOptions(host), timeout.Token);
            connection = new ClientConnection(tcp, tls, credentials);
            await connection.Writer.WriteAsync(MessageType.Hello,
                ProtocolPayloads.EncodeVersion(ProtocolPayloads.CurrentVersion), timeout.Token);
            var hello = await connection.ReadAsync(timeout.Token);
            if (hello.Type != MessageType.HelloAck || ProtocolPayloads.DecodeVersion(hello.Payload) != ProtocolPayloads.CurrentVersion)
            {
                throw new ProtocolException("Incompatible broker protocol; update client and broker together.");
            }

            if (authenticate)
            {
                await connection.Writer.WriteAsync(MessageType.Authenticate, credentials.Key!, timeout.Token);
                var result = await connection.ReadAsync(timeout.Token);
                if (result.Type != MessageType.Authenticated) { throw new ProtocolException("Expected AUTHENTICATED."); }
                ProtocolPayloads.RequireEmpty(result);
            }

            return connection;
        }
        catch (AuthenticationException) when (observed is not null && observed != credentials.Fingerprint)
        {
            connection?.Dispose();
            tls?.Dispose();
            tcp.Dispose();
            throw new AuthenticationException($"Broker certificate changed. Expected SHA-256 {credentials.Fingerprint}; received {observed}. Verify the new fingerprint independently, then use --trust <fingerprint> to replace it.");
        }
        catch
        {
            connection?.Dispose();
            tls?.Dispose();
            tcp.Dispose();
            throw;
        }
    }

    internal static async Task<ClientCredentials> TrustAsync(string host, int port, string directory,
        string? expectedFingerprint)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        string? fingerprint = null;
        using (var tcp = new TcpClient())
        {
            await tcp.ConnectAsync(host, port, timeout.Token);
            using var tls = new SslStream(tcp.GetStream(), false, (_, certificate, _, _) =>
            {
                fingerprint = certificate is null ? null : BrokerIdentity.GetFingerprint(certificate);
                return fingerprint is not null;
            });
            // This probe never sends a protocol frame or credential.
            await tls.AuthenticateAsClientAsync(TlsOptions(host), timeout.Token);
        }

        if (fingerprint is null) { throw new AuthenticationException("Broker did not provide a certificate."); }
        if (expectedFingerprint is not null)
        {
            if (!fingerprint.Equals(expectedFingerprint, StringComparison.OrdinalIgnoreCase))
            {
                throw new AuthenticationException($"Certificate does not match the supplied SHA-256 fingerprint. Received {fingerprint}.");
            }
        }
        else
        {
            Console.Error.WriteLine($"First connection to {host}:{port}. Broker certificate SHA-256:");
            Console.Error.WriteLine(fingerprint);
            if (Console.IsInputRedirected)
            {
                throw new AuthenticationException("Certificate approval requires a terminal. Verify the fingerprint and run --trust <fingerprint> first.");
            }

            Console.Error.Write("Compare with the broker's --fingerprint output. Trust this certificate? Type yes: ");
            if (!string.Equals(Console.ReadLine(), "yes", StringComparison.OrdinalIgnoreCase))
            {
                throw new AuthenticationException("Certificate was not approved.");
            }
        }

        var endpoint = ClientCredentials.Endpoint(host, port);
        var previous = ClientCredentials.Load(directory, endpoint);
        var credentials = new ClientCredentials(fingerprint, previous?.Fingerprint == fingerprint ? previous.Key : null);
        credentials.Save(directory, endpoint);
        return credentials;
    }

    private static SslClientAuthenticationOptions TlsOptions(string host) => new()
    {
        TargetHost = host,
        EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
        AllowRenegotiation = false,
    };

    internal async Task<ProtocolFrame> ReadAsync(CancellationToken cancellationToken)
    {
        var frame = await FrameCodec.ReadAsync(Transport, cancellationToken)
            ?? throw new IOException("Broker closed the connection.");
        if (frame.Type == MessageType.Error) { throw new ProtocolException(ProtocolPayloads.DecodeError(frame.Payload)); }
        return frame;
    }

    public void Dispose()
    {
        _client.Dispose();
        Transport.Dispose();
        Writer.Dispose();
        if (Credentials.Key is not null) { System.Security.Cryptography.CryptographicOperations.ZeroMemory(Credentials.Key); }
    }
}
