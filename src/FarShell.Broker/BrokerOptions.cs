namespace FarShell.Broker;

internal enum BrokerOperation { Serve, Approve, Fingerprint, RotateKey, ReplaceCertificate, Help, Version }

internal sealed record BrokerOptions(int Port, int MaxConnections, int MaxSessions,
    TimeSpan HandshakeTimeout, string? ConfigPath = null, string? StateDirectory = null,
    BrokerOperation Operation = BrokerOperation.Serve, Guid? RequestId = null, bool Confirm = false)
{
    internal static BrokerOptions Parse(string[] args)
    {
        var result = new BrokerOptions(8022, 32, 16, TimeSpan.FromSeconds(10));
        if (args.Any(a => a is "--help" or "-h")) { return result with { Operation = BrokerOperation.Help }; }
        if (args is ["--version"]) { return result with { Operation = BrokerOperation.Version }; }
        var portSeen = false;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < args.Length; i++)
        {
            var option = args[i];
            if (!option.StartsWith("--", StringComparison.Ordinal))
            {
                if (portSeen) { throw new ArgumentException("Only one listening port may be specified."); }
                result = result with { Port = Range(option, 1, 65535) };
                portSeen = true;
                continue;
            }

            if (!seen.Add(option)) { throw new ArgumentException($"Duplicate option: {option}."); }
            string Value()
            {
                if (++i >= args.Length || string.IsNullOrEmpty(args[i])) { throw new ArgumentException($"{option} requires a value."); }
                return args[i];
            }

            switch (option)
            {
                case "--max-connections": result = result with { MaxConnections = Range(Value(), 1, 1024) }; break;
                case "--max-sessions": result = result with { MaxSessions = Range(Value(), 1, 1024) }; break;
                case "--handshake-timeout-seconds": result = result with { HandshakeTimeout = TimeSpan.FromSeconds(Range(Value(), 1, 300)) }; break;
                case "--config": result = result with { ConfigPath = Path.GetFullPath(Value()) }; break;
                case "--state-dir": result = result with { StateDirectory = Path.GetFullPath(Value()) }; break;
                case "--confirm": result = result with { Confirm = true }; break;
                case "--approve":
                    var text = Value();
                    if (!Guid.TryParseExact(text, "N", out var id)) { throw new ArgumentException("Invalid pairing request ID."); }
                    result = SetOperation(result, BrokerOperation.Approve) with { RequestId = id };
                    break;
                case "--fingerprint": result = SetOperation(result, BrokerOperation.Fingerprint); break;
                case "--rotate-key": result = SetOperation(result, BrokerOperation.RotateKey); break;
                case "--replace-certificate": result = SetOperation(result, BrokerOperation.ReplaceCertificate); break;
                default: throw new ArgumentException($"Unknown option: {option}.");
            }
        }

        if (result.Operation != BrokerOperation.Serve && (portSeen || seen.Overlaps(
            new[] { "--max-connections", "--max-sessions", "--handshake-timeout-seconds", "--config" })))
        {
            throw new ArgumentException("Listening and profile options apply only when starting the broker.");
        }
        var rotates = result.Operation is BrokerOperation.RotateKey or BrokerOperation.ReplaceCertificate;
        if (rotates != result.Confirm) { throw new ArgumentException("Identity replacement requires --confirm; --confirm applies only to --rotate-key or --replace-certificate."); }
        return result;
    }

    private static BrokerOptions SetOperation(BrokerOptions options, BrokerOperation operation)
        => options.Operation == BrokerOperation.Serve ? options with { Operation = operation }
            : throw new ArgumentException("Choose one broker operation.");

    private static int Range(string value, int minimum, int maximum)
        => int.TryParse(value, out var number) && number >= minimum && number <= maximum
            ? number : throw new ArgumentException($"Expected an integer from {minimum} to {maximum}.");
}
