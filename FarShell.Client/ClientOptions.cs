using System.Globalization;

namespace FarShell.Client;

internal enum ClientOperation
{
    Create,
    List,
    Attach,
    Terminate,
    Upload,
    Download,
}

internal sealed record ClientOptions(
    ClientOperation Operation,
    Guid? SessionId,
    string? LocalPath,
    string? RemotePath,
    ClientEndpoint Endpoint,
    TimeSpan OutputBatchDelay)
{
    private const int DefaultOutputBatchMilliseconds = 4;
    private const int MaximumOutputBatchMilliseconds = 100;
    private const string Usage =
        "Usage: [--output-batch-ms <0-100>] ([host] [port] | --list [host] [port] | "
        + "--attach <session-id> [host] [port] | "
        + "--terminate <session-id> [host] [port] | "
        + "--upload <local-path> <remote-path> [host] [port] | "
        + "--download <remote-path> <local-path> [host] [port]); "
        + "FARSHELL_SERVER format: host[:port]; "
        + "FARSHELL_OUTPUT_BATCH_MS range: 0-100";

    internal bool IsInteractive => Operation is ClientOperation.Create or ClientOperation.Attach;

    internal static ClientOptions Parse(
        string[] args,
        string? defaultServer = null,
        string? defaultOutputBatchMilliseconds = null)
    {
        var (operationArgs, commandLineOutputBatchMilliseconds) =
            ExtractOutputBatchOption(args);
        var outputBatchMilliseconds = commandLineOutputBatchMilliseconds
            ?? ParseOutputBatchMilliseconds(
                defaultOutputBatchMilliseconds,
                DefaultOutputBatchMilliseconds);
        var outputBatchDelay = TimeSpan.FromMilliseconds(outputBatchMilliseconds);

        if (operationArgs.Length == 0
            || !operationArgs[0].StartsWith("--", StringComparison.Ordinal))
        {
            return new ClientOptions(
                ClientOperation.Create,
                null,
                null,
                null,
                ParseEndpoint(operationArgs, defaultServer),
                outputBatchDelay);
        }

        return operationArgs[0] switch
        {
            "--list" => new ClientOptions(
                ClientOperation.List,
                null,
                null,
                null,
                ParseEndpoint(operationArgs[1..], defaultServer),
                outputBatchDelay),
            "--attach" => ParseSessionOperation(
                ClientOperation.Attach,
                operationArgs,
                defaultServer,
                outputBatchDelay),
            "--terminate" => ParseSessionOperation(
                ClientOperation.Terminate,
                operationArgs,
                defaultServer,
                outputBatchDelay),
            "--upload" => ParseFileOperation(
                ClientOperation.Upload,
                operationArgs,
                defaultServer,
                outputBatchDelay),
            "--download" => ParseFileOperation(
                ClientOperation.Download,
                operationArgs,
                defaultServer,
                outputBatchDelay),
            _ => throw new ArgumentException(Usage),
        };
    }

    private static ClientOptions ParseSessionOperation(
        ClientOperation operation,
        string[] args,
        string? defaultServer,
        TimeSpan outputBatchDelay)
    {
        if (args.Length < 2
            || !Guid.TryParse(args[1], out var sessionId)
            || sessionId == Guid.Empty)
        {
            throw new ArgumentException(Usage);
        }

        return new ClientOptions(
            operation,
            sessionId,
            null,
            null,
            ParseEndpoint(args[2..], defaultServer),
            outputBatchDelay);
    }

    private static ClientOptions ParseFileOperation(
        ClientOperation operation,
        string[] args,
        string? defaultServer,
        TimeSpan outputBatchDelay)
    {
        if (args.Length < 3
            || string.IsNullOrEmpty(args[1])
            || string.IsNullOrEmpty(args[2]))
        {
            throw new ArgumentException(Usage);
        }

        var isUpload = operation == ClientOperation.Upload;
        return new ClientOptions(
            operation,
            null,
            isUpload ? args[1] : args[2],
            isUpload ? args[2] : args[1],
            ParseEndpoint(args[3..], defaultServer),
            outputBatchDelay);
    }

    private static (string[] Args, int? OutputBatchMilliseconds) ExtractOutputBatchOption(
        string[] args)
    {
        var remainingArgs = new List<string>(args.Length);
        int? outputBatchMilliseconds = null;

        for (var index = 0; index < args.Length; index++)
        {
            if (args[index] != "--output-batch-ms")
            {
                remainingArgs.Add(args[index]);
                continue;
            }

            if (outputBatchMilliseconds.HasValue || ++index >= args.Length)
            {
                throw new ArgumentException(Usage);
            }

            outputBatchMilliseconds = ParseOutputBatchMilliseconds(args[index]);
        }

        return (remainingArgs.ToArray(), outputBatchMilliseconds);
    }

    private static int ParseOutputBatchMilliseconds(string? value, int? defaultValue = null)
    {
        if (value is null && defaultValue.HasValue)
        {
            return defaultValue.Value;
        }

        if (!int.TryParse(
                value,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var milliseconds)
            || milliseconds is < 0 or > MaximumOutputBatchMilliseconds)
        {
            throw new ArgumentException(Usage);
        }

        return milliseconds;
    }

    private static ClientEndpoint ParseEndpoint(string[] args, string? defaultServer)
    {
        try
        {
            return ClientEndpoint.Parse(args, defaultServer);
        }
        catch (ArgumentException)
        {
            throw new ArgumentException(Usage);
        }
    }
}
