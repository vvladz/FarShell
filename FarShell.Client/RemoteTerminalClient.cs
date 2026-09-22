using System.Net.Sockets;
using FarShell.Protocol;

namespace FarShell.Client;

internal sealed class RemoteTerminalClient
{
    private static readonly TimeSpan ResizePollInterval = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan PingInterval = TimeSpan.FromSeconds(30);
    private readonly string _host;
    private readonly int _port;

    internal RemoteTerminalClient(string host, int port)
    {
        _host = host;
        _port = port;
    }

    internal Task<int> CreateAsync()
    {
        return RunInteractiveAsync(null);
    }

    internal Task<int> AttachAsync(Guid sessionId)
    {
        return RunInteractiveAsync(sessionId);
    }

    internal async Task<int> ListAsync()
    {
        using var connection = await ConnectAsync();
        await connection.Writer.WriteAsync(
            MessageType.ListSessions,
            ReadOnlyMemory<byte>.Empty);

        var response = await ReadRequiredAsync(connection.Transport);
        if (response.Type != MessageType.SessionList)
        {
            throw new ProtocolException($"Expected SESSION_LIST, received {response.Type}.");
        }

        var sessions = ProtocolPayloads.DecodeSessionList(response.Payload);
        if (sessions.Count == 0)
        {
            Console.WriteLine("No sessions.");
            return 0;
        }

        Console.WriteLine("SESSION ID                       STATE     CREATED UTC          SIZE");
        foreach (var session in sessions)
        {
            Console.WriteLine(
                $"{session.SessionId:N}  "
                + $"{(session.IsAttached ? "attached" : "detached"),-8}  "
                + $"{session.CreatedAt.UtcDateTime:yyyy-MM-dd HH:mm:ss}  "
                + $"{session.Size.Columns}x{session.Size.Rows}");
        }

        return 0;
    }

    internal async Task<int> TerminateAsync(Guid sessionId)
    {
        using var connection = await ConnectAsync();
        await connection.Writer.WriteAsync(
            MessageType.TerminateSession,
            ProtocolPayloads.EncodeSessionId(sessionId));

        var response = await ReadRequiredAsync(connection.Transport);
        if (response.Type != MessageType.SessionTerminated)
        {
            throw new ProtocolException($"Expected SESSION_TERMINATED, received {response.Type}.");
        }

        var terminatedId = ProtocolPayloads.DecodeSessionId(response.Payload);
        if (terminatedId != sessionId)
        {
            throw new ProtocolException("Broker confirmed termination for a different session.");
        }

        Console.WriteLine($"Session {sessionId:N} terminated.");
        return 0;
    }

    private async Task<int> RunInteractiveAsync(Guid? requestedSessionId)
    {
        using var connection = await ConnectAsync();
        var initialSize = GetTerminalSize();
        if (requestedSessionId is { } sessionId)
        {
            await connection.Writer.WriteAsync(
                MessageType.AttachSession,
                ProtocolPayloads.EncodeAttachSession(sessionId, initialSize));
        }
        else
        {
            await connection.Writer.WriteAsync(
                MessageType.CreateSession,
                ProtocolPayloads.EncodeResize(initialSize));
        }

        var response = await ReadRequiredAsync(connection.Transport);
        var expectedResponse = requestedSessionId.HasValue
            ? MessageType.SessionAttached
            : MessageType.SessionCreated;
        if (response.Type != expectedResponse)
        {
            throw new ProtocolException(
                $"Expected {expectedResponse}, received {response.Type}.");
        }

        var attachedSessionId = ProtocolPayloads.DecodeSessionId(response.Payload);
        if (requestedSessionId.HasValue && attachedSessionId != requestedSessionId.Value)
        {
            throw new ProtocolException("Broker attached a different session.");
        }

        Console.Error.WriteLine($"Session {attachedSessionId:N} attached.");

        using var sessionCancellation = new CancellationTokenSource();
        var receiveTask = ReceiveAsync(
            connection.Transport,
            connection.Writer,
            attachedSessionId,
            sessionCancellation.Token);
        var inputTask = PumpInputAsync(connection.Writer, sessionCancellation.Token);
        var resizeTask = MonitorResizeAsync(
            connection.Writer,
            initialSize,
            sessionCancellation.Token);
        var pingTask = SendPingsAsync(connection.Writer, sessionCancellation.Token);

        try
        {
            var firstCompleted = await Task.WhenAny(receiveTask, inputTask);
            if (firstCompleted == inputTask)
            {
                await inputTask;
                return 0;
            }

            return await receiveTask;
        }
        finally
        {
            sessionCancellation.Cancel();
            Observe(receiveTask);
            Observe(inputTask);
            Observe(resizeTask);
            Observe(pingTask);
        }
    }

    private async Task<ClientConnection> ConnectAsync()
    {
        var connection = new ClientConnection();
        try
        {
            await connection.ConnectAsync(_host, _port);
            await connection.Writer.WriteAsync(
                MessageType.Hello,
                ProtocolPayloads.EncodeVersion(ProtocolPayloads.CurrentVersion));

            var response = await ReadRequiredAsync(connection.Transport);
            if (response.Type != MessageType.HelloAck)
            {
                throw new ProtocolException($"Expected HELLO_ACK, received {response.Type}.");
            }

            var version = ProtocolPayloads.DecodeVersion(response.Payload);
            if (version != ProtocolPayloads.CurrentVersion)
            {
                throw new ProtocolException(
                    $"Broker selected protocol version {version}; expected {ProtocolPayloads.CurrentVersion}.");
            }

            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    private static async Task PumpInputAsync(
        FrameWriter writer,
        CancellationToken cancellationToken)
    {
        var standardInput = Console.OpenStandardInput();
        var buffer = new byte[4096];

        while (true)
        {
            var bytesRead = await standardInput.ReadAsync(buffer, cancellationToken);
            if (bytesRead == 0)
            {
                return;
            }

            await writer.WriteAsync(
                MessageType.DataIn,
                buffer.AsMemory(0, bytesRead),
                cancellationToken);
        }
    }

    private static async Task<int> ReceiveAsync(
        Stream transport,
        FrameWriter writer,
        Guid sessionId,
        CancellationToken cancellationToken)
    {
        var standardOutput = Console.OpenStandardOutput();

        while (true)
        {
            var frame = await ReadRequiredAsync(transport, cancellationToken);
            switch (frame.Type)
            {
                case MessageType.DataOut:
                    await standardOutput.WriteAsync(frame.Payload, cancellationToken);
                    await standardOutput.FlushAsync(cancellationToken);
                    break;

                case MessageType.Ping:
                    ProtocolPayloads.RequireEmpty(frame);
                    await writer.WriteAsync(
                        MessageType.Pong,
                        ReadOnlyMemory<byte>.Empty,
                        cancellationToken);
                    break;

                case MessageType.Pong:
                    ProtocolPayloads.RequireEmpty(frame);
                    break;

                case MessageType.SessionExited:
                    var sessionExit = ProtocolPayloads.DecodeSessionExit(frame.Payload);
                    if (sessionExit.SessionId != sessionId)
                    {
                        throw new ProtocolException("Broker reported exit for a different session.");
                    }

                    return sessionExit.ExitCode;

                default:
                    throw new ProtocolException(
                        $"Unsupported broker message while attached: {frame.Type}.");
            }
        }
    }

    private static async Task MonitorResizeAsync(
        FrameWriter writer,
        TerminalSize initialSize,
        CancellationToken cancellationToken)
    {
        var lastSize = initialSize;
        while (true)
        {
            await Task.Delay(ResizePollInterval, cancellationToken);
            var currentSize = GetTerminalSize();
            if (currentSize == lastSize)
            {
                continue;
            }

            await writer.WriteAsync(
                MessageType.Resize,
                ProtocolPayloads.EncodeResize(currentSize),
                cancellationToken);
            lastSize = currentSize;
        }
    }

    private static async Task SendPingsAsync(
        FrameWriter writer,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            await Task.Delay(PingInterval, cancellationToken);
            await writer.WriteAsync(
                MessageType.Ping,
                ReadOnlyMemory<byte>.Empty,
                cancellationToken);
        }
    }

    private static async Task<ProtocolFrame> ReadRequiredAsync(
        Stream transport,
        CancellationToken cancellationToken = default)
    {
        var frame = await FrameCodec.ReadAsync(transport, cancellationToken);
        if (frame is null)
        {
            throw new IOException("Broker closed the connection.");
        }

        if (frame.Type == MessageType.Error)
        {
            throw new ProtocolException(ProtocolPayloads.DecodeError(frame.Payload));
        }

        return frame;
    }

    private static TerminalSize GetTerminalSize()
    {
        try
        {
            var columns = Console.WindowWidth;
            var rows = Console.WindowHeight;
            if (columns > 0 && rows > 0)
            {
                return new TerminalSize(columns, rows);
            }
        }
        catch (IOException)
        {
            // Redirected stdio has no terminal dimensions.
        }

        return new TerminalSize(120, 30);
    }

    private static void Observe(Task task)
    {
        if (!task.IsCompleted)
        {
            _ = task.ContinueWith(
                completed => _ = completed.Exception,
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
    }

    private sealed class ClientConnection : IDisposable
    {
        private readonly TcpClient _client = new() { NoDelay = true };
        private NetworkStream? _transport;
        private FrameWriter? _writer;

        internal Stream Transport =>
            _transport ?? throw new InvalidOperationException("Client is not connected.");

        internal FrameWriter Writer =>
            _writer ?? throw new InvalidOperationException("Client is not connected.");

        internal async Task ConnectAsync(string host, int port)
        {
            await _client.ConnectAsync(host, port);
            _transport = _client.GetStream();
            _writer = new FrameWriter(_transport);
        }

        public void Dispose()
        {
            _client.Dispose();
            _writer?.Dispose();
        }
    }
}
