namespace FarShell.Client;

internal sealed record ClientEndpoint(string Host, int Port)
{
    private const string DefaultHost = "127.0.0.1";
    private const int DefaultPort = 8022;

    internal static ClientEndpoint Parse(string[] args, string? defaultServer)
    {
        return args.Length switch
        {
            0 when defaultServer is null => new ClientEndpoint(DefaultHost, DefaultPort),
            0 => ParseDefaultServer(defaultServer),
            1 => new ClientEndpoint(args[0], DefaultPort),
            2 when int.TryParse(args[1], out var port) && port is > 0 and <= 65535
                => new ClientEndpoint(args[0], port),
            _ => throw new ArgumentException("Usage: [host] [port]"),
        };
    }

    private static ClientEndpoint ParseDefaultServer(string server)
    {
        if (!Uri.TryCreate($"tcp://{server}", UriKind.Absolute, out var endpoint)
            || string.IsNullOrEmpty(endpoint.Host)
            || endpoint.UserInfo.Length != 0
            || endpoint.AbsolutePath != "/"
            || endpoint.Query.Length != 0
            || endpoint.Fragment.Length != 0
            || endpoint.Port == 0)
        {
            throw new ArgumentException("FARSHELL_SERVER must use the format host[:port].");
        }

        return new ClientEndpoint(
            endpoint.DnsSafeHost,
            endpoint.Port == -1 ? DefaultPort : endpoint.Port);
    }
}
