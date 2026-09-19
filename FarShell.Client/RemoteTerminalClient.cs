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

    internal async Task<int> RunAsync()
    {
        using var tcpClient = new TcpClient { NoDelay = true };
        await tcpClient.ConnectAsync(_host, _port);

        var transport = tcpClient.GetStream();
        var writer = new FrameWriter(transport);
        var initialSize = GetTerminalSize();
        await writer.WriteAsync(MessageType.Resize, ProtocolPayloads.EncodeResize(initialSize));

        using var sessionCancellation = new CancellationTokenSource();
        var receiveTask = ReceiveAsync(transport, writer, sessionCancellation.Token);
        var inputTask = PumpInputAsync(writer, sessionCancellation.Token);
        var resizeTask = MonitorResizeAsync(writer, initialSize, sessionCancellation.Token);
        var pingTask = SendPingsAsync(writer, sessionCancellation.Token);

        try
        {
            var firstCompleted = await Task.WhenAny(receiveTask, inputTask);
            if (firstCompleted == inputTask)
            {
                await inputTask;
            }

            return await receiveTask;
        }
        finally
        {
            sessionCancellation.Cancel();
            Observe(inputTask);
            Observe(resizeTask);
            Observe(pingTask);
            writer.Dispose();
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
        CancellationToken cancellationToken)
    {
        var standardOutput = Console.OpenStandardOutput();

        while (true)
        {
            var frame = await FrameCodec.ReadAsync(transport, cancellationToken);
            if (frame is null)
            {
                throw new IOException("Broker closed the connection.");
            }

            switch (frame.Type)
            {
                case MessageType.DataOut:
                    await standardOutput.WriteAsync(frame.Payload, cancellationToken);
                    await standardOutput.FlushAsync(cancellationToken);
                    break;

                case MessageType.Ping:
                    RequireEmpty(frame);
                    await writer.WriteAsync(MessageType.Pong, ReadOnlyMemory<byte>.Empty, cancellationToken);
                    break;

                case MessageType.Pong:
                    RequireEmpty(frame);
                    break;

                case MessageType.Exit:
                    return ProtocolPayloads.DecodeExitCode(frame.Payload);

                case MessageType.DataIn:
                case MessageType.Resize:
                    throw new ProtocolException($"Broker cannot send {frame.Type}.");

                default:
                    throw new ProtocolException($"Unsupported broker message: {frame.Type}.");
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
            await writer.WriteAsync(MessageType.Ping, ReadOnlyMemory<byte>.Empty, cancellationToken);
        }
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

    private static void RequireEmpty(ProtocolFrame frame)
    {
        if (frame.Payload.Length != 0)
        {
            throw new ProtocolException($"{frame.Type} payload must be empty.");
        }
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
}
