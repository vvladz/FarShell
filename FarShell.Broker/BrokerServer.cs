using System.Net;
using System.Net.Sockets;
using System.Runtime.Versioning;
using FarShell.ConPTY;
using FarShell.Protocol;

namespace FarShell.Broker;

[SupportedOSPlatform("windows10.0.17763")]
internal sealed class BrokerServer
{
    private static readonly TerminalSize DefaultTerminalSize = new(120, 30);
    private readonly int _port;

    internal BrokerServer(int port)
    {
        _port = port;
    }

    internal async Task RunAsync(CancellationToken cancellationToken)
    {
        var listener = new TcpListener(IPAddress.Any, _port);
        listener.Start(backlog: 1);
        Console.WriteLine($"Listening on 0.0.0.0:{_port}.");

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                using var client = await listener.AcceptTcpClientAsync(cancellationToken);
                client.NoDelay = true;
                var remoteEndpoint = client.Client.RemoteEndPoint;
                Console.WriteLine($"Client connected from {remoteEndpoint}.");

                try
                {
                    await HandleClientAsync(client, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    Console.Error.WriteLine($"Session error: {exception.Message}");
                }

                Console.WriteLine($"Client disconnected from {remoteEndpoint}.");
            }
        }
        finally
        {
            listener.Stop();
        }
    }

    private static async Task HandleClientAsync(
        TcpClient client,
        CancellationToken cancellationToken)
    {
        var transport = client.GetStream();
        using var writer = new FrameWriter(transport);

        var firstFrame = await FrameCodec.ReadAsync(transport, cancellationToken);
        if (firstFrame is null)
        {
            return;
        }

        var initialSize = firstFrame.Type == MessageType.Resize
            ? ProtocolPayloads.DecodeResize(firstFrame.Payload)
            : DefaultTerminalSize;
        var pendingFrame = firstFrame.Type == MessageType.Resize ? null : firstFrame;

        using var session = ConPtySession.Start(initialSize.Columns, initialSize.Rows);
        var inputTask = PumpInputAsync(
            transport,
            writer,
            session,
            pendingFrame,
            cancellationToken);
        var outputTask = PumpOutputAsync(writer, session, cancellationToken);

        var completed = await Task.WhenAny(inputTask, outputTask);
        Exception? failure = null;
        try
        {
            await completed;
        }
        catch (Exception exception)
        {
            failure = exception;
        }
        finally
        {
            session.Terminate();
            client.Dispose();
            await IgnoreCompletionAsync(inputTask);
            await IgnoreCompletionAsync(outputTask);
            await IgnoreCompletionAsync(session.WaitForExitAsync());
        }

        if (failure is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }

    private static async Task PumpInputAsync(
        Stream transport,
        FrameWriter writer,
        ConPtySession session,
        ProtocolFrame? pendingFrame,
        CancellationToken cancellationToken)
    {
        var frame = pendingFrame;
        while (true)
        {
            frame ??= await FrameCodec.ReadAsync(transport, cancellationToken);
            if (frame is null)
            {
                return;
            }

            switch (frame.Type)
            {
                case MessageType.DataIn:
                    await session.Input.WriteAsync(frame.Payload, cancellationToken);
                    await session.Input.FlushAsync(cancellationToken);
                    break;

                case MessageType.Resize:
                    var size = ProtocolPayloads.DecodeResize(frame.Payload);
                    session.Resize(size.Columns, size.Rows);
                    break;

                case MessageType.Ping:
                    RequireEmpty(frame);
                    await writer.WriteAsync(MessageType.Pong, ReadOnlyMemory<byte>.Empty, cancellationToken);
                    break;

                case MessageType.Pong:
                    RequireEmpty(frame);
                    break;

                case MessageType.Exit:
                    return;

                case MessageType.DataOut:
                    throw new ProtocolException("Client cannot send DATA_OUT.");

                default:
                    throw new ProtocolException($"Unsupported client message: {frame.Type}.");
            }

            frame = null;
        }
    }

    private static async Task PumpOutputAsync(
        FrameWriter writer,
        ConPtySession session,
        CancellationToken cancellationToken)
    {
        var exitTask = session.WaitForExitAsync();
        var buffer = new byte[32 * 1024];
        while (true)
        {
            var bytesRead = await session.Output.ReadAsync(buffer, cancellationToken);
            if (bytesRead == 0)
            {
                break;
            }

            await writer.WriteAsync(
                MessageType.DataOut,
                buffer.AsMemory(0, bytesRead),
                cancellationToken);
        }

        var exitCode = await exitTask;
        await writer.WriteAsync(
            MessageType.Exit,
            ProtocolPayloads.EncodeExitCode(exitCode),
            cancellationToken);
    }

    private static void RequireEmpty(ProtocolFrame frame)
    {
        if (frame.Payload.Length != 0)
        {
            throw new ProtocolException($"{frame.Type} payload must be empty.");
        }
    }

    private static async Task IgnoreCompletionAsync(Task task)
    {
        try
        {
            await task;
        }
        catch
        {
            // The first completed task determines the session result. Remaining
            // operations are interrupted during coordinated cleanup.
        }
    }

}
