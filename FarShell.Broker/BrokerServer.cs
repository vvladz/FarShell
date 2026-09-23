using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Runtime.Versioning;
using FarShell.Protocol;

namespace FarShell.Broker;

[SupportedOSPlatform("windows10.0.17763")]
internal sealed class BrokerServer
{
    private readonly BrokerOptions _options;
    private readonly IConnectionAuthenticator _authenticator;
    private readonly SessionManager _sessions;
    private readonly FileTransferHandler _files = new();
    private readonly ConcurrentDictionary<long, Task> _connections = new();
    private readonly TaskCompletionSource<int> _listeningPort =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private long _nextConnectionId;

    internal BrokerServer(BrokerOptions options)
        : this(options, new AnonymousConnectionAuthenticator(), new OwnerSessionAuthorizer())
    {
    }

    internal BrokerServer(
        BrokerOptions options,
        IConnectionAuthenticator authenticator,
        ISessionAuthorizer authorizer)
    {
        _options = options;
        _authenticator = authenticator;
        _sessions = new SessionManager(options.MaxSessions, authorizer);
    }

    internal Task<int> ListeningPort => _listeningPort.Task;

    internal async Task RunAsync(CancellationToken cancellationToken)
    {
        using var runCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var connectionSlots = new SemaphoreSlim(
            _options.MaxConnections,
            _options.MaxConnections);
        var listener = new TcpListener(IPAddress.Any, _options.Port);
        listener.Start(backlog: _options.MaxConnections);
        var listeningPort = ((IPEndPoint)listener.LocalEndpoint).Port;
        _listeningPort.TrySetResult(listeningPort);
        Console.WriteLine($"Listening on 0.0.0.0:{listeningPort}.");

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(cancellationToken);
                client.NoDelay = true;

                if (!connectionSlots.Wait(0))
                {
                    Console.Error.WriteLine(
                        $"Connection rejected from {client.Client.RemoteEndPoint}: limit reached.");
                    client.Dispose();
                    continue;
                }

                var connectionId = Interlocked.Increment(ref _nextConnectionId);
                var task = TrackConnectionAsync(
                    client,
                    connectionSlots,
                    runCancellation.Token);
                _connections[connectionId] = task;
                RemoveCompletedConnections();
            }
        }
        finally
        {
            listener.Stop();
            runCancellation.Cancel();
            await Task.WhenAll(_connections.Values.ToArray());
            await _sessions.DisposeAsync();
        }
    }

    private async Task TrackConnectionAsync(
        TcpClient client,
        SemaphoreSlim connectionSlots,
        CancellationToken cancellationToken)
    {
        try
        {
            await HandleConnectionSafelyAsync(client, cancellationToken);
        }
        catch (Exception exception)
        {
            client.Dispose();
            Console.Error.WriteLine($"Connection error: {exception.Message}");
        }
        finally
        {
            connectionSlots.Release();
        }
    }

    private void RemoveCompletedConnections()
    {
        foreach (var connection in _connections)
        {
            if (connection.Value.IsCompleted)
            {
                _connections.TryRemove(connection.Key, out _);
            }
        }
    }

    private async Task HandleConnectionSafelyAsync(
        TcpClient client,
        CancellationToken cancellationToken)
    {
        using var connection = new ConnectionContext(client, cancellationToken);
        Console.WriteLine($"Client connected from {connection.RemoteEndpoint}.");

        try
        {
            await NegotiateAsync(connection);
            connection.Identity = await _authenticator.AuthenticateAsync(
                connection,
                connection.CancellationToken);
            await DispatchAsync(connection);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Broker shutdown cancels all active connections.
        }
        catch (Exception exception)
        {
            await TrySendErrorAsync(connection, GetClientError(exception));
            Console.Error.WriteLine($"Session error: {exception.Message}");
        }
        finally
        {
            Console.WriteLine($"Client disconnected from {connection.RemoteEndpoint}.");
        }
    }

    private async Task NegotiateAsync(ConnectionContext connection)
    {
        using var handshakeCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            connection.CancellationToken);
        handshakeCancellation.CancelAfter(_options.HandshakeTimeout);

        ProtocolFrame? hello;
        try
        {
            hello = await FrameCodec.ReadAsync(
                connection.Transport,
                handshakeCancellation.Token);
        }
        catch (OperationCanceledException) when (!connection.CancellationToken.IsCancellationRequested)
        {
            throw new ProtocolException("Protocol handshake timed out.");
        }

        if (hello is null || hello.Type != MessageType.Hello)
        {
            throw new ProtocolException("Client must start with HELLO.");
        }

        var version = ProtocolPayloads.DecodeVersion(hello.Payload);
        if (version != ProtocolPayloads.CurrentVersion)
        {
            throw new ProtocolException(
                $"Unsupported protocol version {version}; expected {ProtocolPayloads.CurrentVersion}.");
        }

        connection.ProtocolVersion = version;
        await connection.Writer.WriteAsync(
            MessageType.HelloAck,
            ProtocolPayloads.EncodeVersion(version),
            handshakeCancellation.Token);
    }

    private async Task DispatchAsync(ConnectionContext connection)
    {
        var request = await FrameCodec.ReadAsync(
            connection.Transport,
            connection.CancellationToken);
        if (request is null)
        {
            return;
        }

        switch (request.Type)
        {
            case MessageType.ListSessions:
                ProtocolPayloads.RequireEmpty(request);
                await connection.Writer.WriteAsync(
                    MessageType.SessionList,
                    ProtocolPayloads.EncodeSessionList(_sessions.List(connection.Identity)),
                    connection.CancellationToken);
                return;

            case MessageType.CreateSession:
                await CreateAndAttachAsync(
                    connection,
                    ProtocolPayloads.DecodeResize(request.Payload));
                return;

            case MessageType.AttachSession:
                var attachRequest = ProtocolPayloads.DecodeAttachSession(request.Payload);
                await AttachAsync(connection, attachRequest.SessionId, attachRequest.Size);
                return;

            case MessageType.TerminateSession:
                var sessionId = ProtocolPayloads.DecodeSessionId(request.Payload);
                await _sessions.TerminateAsync(connection.Identity, sessionId);
                await connection.Writer.WriteAsync(
                    MessageType.SessionTerminated,
                    ProtocolPayloads.EncodeSessionId(sessionId),
                    connection.CancellationToken);
                return;

            case MessageType.UploadFile:
                await _files.UploadAsync(
                    connection,
                    FileTransferPayloads.DecodeUploadFile(request.Payload));
                return;

            case MessageType.DownloadFile:
                await _files.DownloadAsync(
                    connection,
                    FileTransferPayloads.DecodeFilePath(request.Payload));
                return;

            default:
                throw new ProtocolException($"Unsupported operation: {request.Type}.");
        }
    }

    private async Task CreateAndAttachAsync(
        ConnectionContext connection,
        TerminalSize size)
    {
        var session = _sessions.Create(connection.Identity, size);
        await RunAttachedAsync(connection, session, size, MessageType.SessionCreated);
    }

    private async Task AttachAsync(
        ConnectionContext connection,
        Guid sessionId,
        TerminalSize size)
    {
        var session = _sessions.Get(connection.Identity, sessionId);
        await RunAttachedAsync(connection, session, size, MessageType.SessionAttached);
    }

    private static async Task RunAttachedAsync(
        ConnectionContext connection,
        ShellSession session,
        TerminalSize size,
        MessageType responseType)
    {
        var attachment = session.ReserveAttachment(connection);
        try
        {
            await connection.Writer.WriteAsync(
                responseType,
                ProtocolPayloads.EncodeSessionId(session.Id),
                connection.CancellationToken);
            session.Activate(attachment, size);
            try
            {
                await PumpAttachedConnectionAsync(connection, session, attachment);
            }
            catch (IOException exception) when (exception is not ProtocolException)
            {
                // A broken transport detaches the client without ending the session.
            }
            catch (SocketException)
            {
                // A reset TCP connection has the same detach semantics.
            }
        }
        finally
        {
            session.Detach(attachment);
        }
    }

    private static async Task PumpAttachedConnectionAsync(
        ConnectionContext connection,
        ShellSession session,
        SessionAttachment attachment)
    {
        while (true)
        {
            var readTask = FrameCodec.ReadAsync(
                connection.Transport,
                connection.CancellationToken).AsTask();
            var completed = await Task.WhenAny(readTask, session.Completion);
            if (completed == session.Completion)
            {
                await session.Completion;
                connection.Cancel();
                await IgnoreCompletionAsync(readTask);
                return;
            }

            var frame = await readTask;
            if (frame is null)
            {
                return;
            }

            switch (frame.Type)
            {
                case MessageType.DataIn:
                    await session.WriteInputAsync(
                        attachment,
                        frame.Payload,
                        connection.CancellationToken);
                    break;

                case MessageType.Resize:
                    session.Resize(attachment, ProtocolPayloads.DecodeResize(frame.Payload));
                    break;

                case MessageType.Ping:
                    ProtocolPayloads.RequireEmpty(frame);
                    await connection.Writer.WriteAsync(
                        MessageType.Pong,
                        ReadOnlyMemory<byte>.Empty,
                        connection.CancellationToken);
                    break;

                case MessageType.Pong:
                    ProtocolPayloads.RequireEmpty(frame);
                    break;

                case MessageType.DetachSession:
                    ProtocolPayloads.RequireEmpty(frame);
                    return;

                case MessageType.SessionFileStatus:
                    session.HandleFileStatus(
                        attachment,
                        FileTransferPayloads.DecodeSessionFileStatus(frame.Payload));
                    break;

                default:
                    throw new ProtocolException(
                        $"Unsupported message while attached: {frame.Type}.");
            }
        }
    }

    private static async Task TrySendErrorAsync(
        ConnectionContext connection,
        string message)
    {
        try
        {
            await connection.Writer.WriteAsync(
                MessageType.Error,
                ProtocolPayloads.EncodeError(message),
                connection.CancellationToken);
        }
        catch
        {
            // The original failure remains the useful diagnostic.
        }
    }

    private static string GetClientError(Exception exception)
    {
        return exception is ProtocolException
            or SessionOperationException
            or FileTransferException
            ? exception.Message
            : "Operation failed.";
    }

    private static async Task IgnoreCompletionAsync(Task task)
    {
        try
        {
            await task;
        }
        catch
        {
            // Connection cancellation interrupts the pending protocol read.
        }
    }
}
