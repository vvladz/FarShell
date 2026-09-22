namespace FarShell.Broker;

internal sealed record BrokerOptions(
    int Port,
    int MaxConnections,
    int MaxSessions,
    TimeSpan HandshakeTimeout)
{
    private const int DefaultPort = 8022;
    private const int DefaultMaxConnections = 32;
    private const int DefaultMaxSessions = 16;
    private const int DefaultHandshakeTimeoutSeconds = 10;
    private const string Usage =
        "Usage: [port] [--max-connections <count>] [--max-sessions <count>] "
        + "[--handshake-timeout-seconds <seconds>]";

    internal static BrokerOptions Parse(string[] args)
    {
        var port = DefaultPort;
        var maxConnections = DefaultMaxConnections;
        var maxSessions = DefaultMaxSessions;
        var handshakeTimeoutSeconds = DefaultHandshakeTimeoutSeconds;
        var portSeen = false;

        for (var index = 0; index < args.Length; index++)
        {
            var argument = args[index];
            if (!argument.StartsWith("--", StringComparison.Ordinal))
            {
                if (portSeen)
                {
                    throw new ArgumentException(Usage);
                }

                port = ParseRange(argument, 1, 65535);
                portSeen = true;
                continue;
            }

            if (++index >= args.Length)
            {
                throw new ArgumentException(Usage);
            }

            switch (argument)
            {
                case "--max-connections":
                    maxConnections = ParseRange(args[index], 1, 1024);
                    break;

                case "--max-sessions":
                    maxSessions = ParseRange(args[index], 1, 1024);
                    break;

                case "--handshake-timeout-seconds":
                    handshakeTimeoutSeconds = ParseRange(args[index], 1, 300);
                    break;

                default:
                    throw new ArgumentException(Usage);
            }
        }

        return new BrokerOptions(
            port,
            maxConnections,
            maxSessions,
            TimeSpan.FromSeconds(handshakeTimeoutSeconds));
    }

    private static int ParseRange(string value, int minimum, int maximum)
    {
        if (!int.TryParse(value, out var parsed) || parsed < minimum || parsed > maximum)
        {
            throw new ArgumentException(Usage);
        }

        return parsed;
    }
}
