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
        + "--terminate <session-id> [host] [port]; "
        + "FARSHELL_SERVER format: host[:port]";

    internal bool IsInteractive => Operation is ClientOperation.Create or ClientOperation.Attach;

    internal static ClientOptions Parse(string[] args, string? defaultServer = null)
    {
        if (args.Length == 0 || !args[0].StartsWith("--", StringComparison.Ordinal))
        {
            return new ClientOptions(
                ClientOperation.Create,
                null,
                ParseEndpoint(args, defaultServer));
        }

        return args[0] switch
        {
            "--list" => new ClientOptions(
                ClientOperation.List,
                null,
                ParseEndpoint(args[1..], defaultServer)),
            "--attach" => ParseSessionOperation(ClientOperation.Attach, args, defaultServer),
            "--terminate" => ParseSessionOperation(ClientOperation.Terminate, args, defaultServer),
            _ => throw new ArgumentException(Usage),
        };
    }

    private static ClientOptions ParseSessionOperation(
        ClientOperation operation,
        string[] args,
        string? defaultServer)
    {
        if (args.Length < 2
            || !Guid.TryParse(args[1], out var sessionId)
            || sessionId == Guid.Empty)
        {
            throw new ArgumentException(Usage);
        }

        return new ClientOptions(operation, sessionId, ParseEndpoint(args[2..], defaultServer));
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
