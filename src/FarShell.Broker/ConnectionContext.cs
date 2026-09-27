using System.Net;
using System.Net.Sockets;
using System.Net.Security;
using System.Security.Authentication;
using FarShell.Protocol;
using FarShell.Security;

namespace FarShell.Broker;

internal sealed class ConnectionContext : IDisposable
{
    private readonly TcpClient _client;
    private readonly CancellationTokenSource _lifetime;

    internal ConnectionContext(TcpClient client, CancellationToken serverCancellationToken)
    {
        _client = client;
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(serverCancellationToken);
        Transport = new SslStream(client.GetStream(), leaveInnerStreamOpen: false);
        Writer = new FrameWriter(Transport);
        RemoteEndpoint = client.Client.RemoteEndPoint;
    }

    internal Stream Transport { get; }
    internal bool IsSecure => ((SslStream)Transport).IsAuthenticated;

    internal Task SecureAsync(BrokerIdentity identity, CancellationToken cancellationToken)
        => ((SslStream)Transport).AuthenticateAsServerAsync(new SslServerAuthenticationOptions
        {
            ServerCertificate = identity.Certificate,
            EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
            AllowRenegotiation = false,
        }, cancellationToken);

    internal FrameWriter Writer { get; }

    internal EndPoint? RemoteEndpoint { get; }

    internal CancellationToken CancellationToken => _lifetime.Token;

    internal int ProtocolVersion { get; set; }

    internal void Cancel()
    {
        _lifetime.Cancel();
    }

    public void Dispose()
    {
        _lifetime.Cancel();
        _client.Dispose();
        Transport.Dispose();
        Writer.Dispose();
        _lifetime.Dispose();
    }
}
