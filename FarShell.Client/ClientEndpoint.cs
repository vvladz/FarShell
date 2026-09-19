namespace FarShell.Client;

internal sealed record ClientEndpoint(string Host, int Port)
{
    private const string DefaultHost = "127.0.0.1";
    private const int DefaultPort = 8022;

    internal static ClientEndpoint Parse(string[] args)
    {
        return args.Length switch
        {
            0 => new ClientEndpoint(DefaultHost, DefaultPort),
            1 => new ClientEndpoint(args[0], DefaultPort),
            2 when int.TryParse(args[1], out var port) && port is > 0 and <= 65535
                => new ClientEndpoint(args[0], port),
            _ => throw new ArgumentException("Usage: [host] [port]"),
        };
    }
}
