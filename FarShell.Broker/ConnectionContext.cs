using System.Net;
using System.Net.Sockets;
using FarShell.Protocol;

namespace FarShell.Broker;

internal sealed class ConnectionContext : IDisposable
{
    private readonly TcpClient _client;
    private readonly CancellationTokenSource _lifetime;

    internal ConnectionContext(TcpClient client, CancellationToken serverCancellationToken)
    {
        _client = client;
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(serverCancellationToken);
        Transport = client.GetStream();
        Writer = new FrameWriter(Transport);
        RemoteEndpoint = client.Client.RemoteEndPoint;
    }

    internal Stream Transport { get; }

    internal FrameWriter Writer { get; }

    internal EndPoint? RemoteEndpoint { get; }

    internal CancellationToken CancellationToken => _lifetime.Token;

    internal int ProtocolVersion { get; set; }

    internal ConnectionIdentity Identity { get; set; }

    internal void Cancel()
    {
        _lifetime.Cancel();
    }

    public void Dispose()
    {
        _lifetime.Cancel();
        _client.Dispose();
        Writer.Dispose();
        _lifetime.Dispose();
    }
}
