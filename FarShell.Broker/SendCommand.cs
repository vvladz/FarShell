using System.IO.Pipes;
using FarShell.Protocol;

namespace FarShell.Broker;

internal static class SendCommand
{
    internal const string PipeEnvironmentVariable = "FARSHELL_SEND_PIPE";
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(5);

    internal static async Task<int> RunAsync(string[] args)
    {
        if (args.Length == 0 || args.Any(string.IsNullOrEmpty))
        {
            throw new ArgumentException(
                "Usage: FarShell.Broker.exe --send <relative-path> [relative-path ...]");
        }

        var pipeName = Environment.GetEnvironmentVariable(PipeEnvironmentVariable);
        if (string.IsNullOrEmpty(pipeName))
        {
            throw new InvalidOperationException(
                "--send must run inside a FarShell session.");
        }

        using var pipe = new NamedPipeClientStream(
            ".",
            pipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        using var connectCancellation = new CancellationTokenSource(ConnectTimeout);
        await pipe.ConnectAsync(connectCancellation.Token);
        using var writer = new FrameWriter(pipe);
        await writer.WriteAsync(
            MessageType.SendFilesRequest,
            FileTransferPayloads.EncodeSendFilesRequest(
                Environment.CurrentDirectory,
                args));

        var response = await FrameCodec.ReadAsync(pipe)
            ?? throw new IOException("FarShell closed the send control pipe.");
        if (response.Type == MessageType.Error)
        {
            throw new ProtocolException(ProtocolPayloads.DecodeError(response.Payload));
        }

        if (response.Type != MessageType.SendFilesCompleted)
        {
            throw new ProtocolException(
                $"Expected SEND_FILES_COMPLETED, received {response.Type}.");
        }

        var count = FileTransferPayloads.DecodeFileCount(response.Payload);
        Console.WriteLine(
            count == 1
                ? "Transferred 1 file to the attached FarShell client."
                : $"Transferred {count} files to the attached FarShell client.");
        return 0;
    }
}
