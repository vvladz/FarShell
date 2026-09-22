namespace FarShell.Client;

internal enum ClientOperation
{
    Create,
    List,
    Attach,
    Terminate,
}

internal sealed record ClientOptions(
    ClientOperation Operation,
    Guid? SessionId,
    ClientEndpoint Endpoint)
{
    private const string Usage =
        "Usage: [host] [port] | --list [host] [port] | "
        + "--attach <session-id> [host] [port] | "
        + "--terminate <session-id> [host] [port]";

    internal bool IsInteractive => Operation is ClientOperation.Create or ClientOperation.Attach;

    internal static ClientOptions Parse(string[] args)
    {
        if (args.Length == 0 || !args[0].StartsWith("--", StringComparison.Ordinal))
        {
            return new ClientOptions(
                ClientOperation.Create,
                null,
                ParseEndpoint(args));
        }

        return args[0] switch
        {
            "--list" => new ClientOptions(
                ClientOperation.List,
                null,
                ParseEndpoint(args[1..])),
            "--attach" => ParseSessionOperation(ClientOperation.Attach, args),
            "--terminate" => ParseSessionOperation(ClientOperation.Terminate, args),
            _ => throw new ArgumentException(Usage),
        };
    }

    private static ClientOptions ParseSessionOperation(
        ClientOperation operation,
        string[] args)
    {
        if (args.Length < 2
            || !Guid.TryParse(args[1], out var sessionId)
            || sessionId == Guid.Empty)
        {
            throw new ArgumentException(Usage);
        }

        return new ClientOptions(operation, sessionId, ParseEndpoint(args[2..]));
    }

    private static ClientEndpoint ParseEndpoint(string[] args)
    {
        try
        {
            return ClientEndpoint.Parse(args);
        }
        catch (ArgumentException)
        {
            throw new ArgumentException(Usage);
        }
    }
}
