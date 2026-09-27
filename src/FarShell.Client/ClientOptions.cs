using System.Globalization;
using FarShell.Protocol;

namespace FarShell.Client;

internal enum ClientOperation { Create, Upload, Download, Pair, Trust, Help, Version }

internal sealed record ClientOptions(ClientOperation Operation, string? LocalPath, string? RemotePath,
    ClientEndpoint Endpoint, TimeSpan OutputBatchDelay, string ProfileName = "",
    string? StateDirectory = null, string? Fingerprint = null)
{
    internal bool IsInteractive => Operation == ClientOperation.Create;

    internal static ClientOptions Parse(string[] args, string? defaultServer = null,
        string? defaultOutputBatchMilliseconds = null)
    {
        if (args.Any(a => a is "--help" or "-h")) { return Informational(ClientOperation.Help); }
        if (args is ["--version"]) { return Informational(ClientOperation.Version); }
        var operation = ClientOperation.Create;
        string? local = null, remote = null, state = null, fingerprint = null;
        var profile = "";
        int? delay = null;
        var endpoint = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < args.Length; i++)
        {
            var option = args[i];
            if (option == "--") { endpoint.AddRange(args[(i + 1)..]); break; }
            if (!option.StartsWith("-", StringComparison.Ordinal)) { endpoint.Add(option); continue; }
            if (!seen.Add(option)) { throw new ArgumentException($"Duplicate option: {option}."); }
            string Value()
            {
                if (++i >= args.Length || string.IsNullOrEmpty(args[i])) { throw new ArgumentException($"{option} requires a value."); }
                return args[i];
            }

            switch (option)
            {
                case "--output-batch-ms": delay = ParseDelay(Value()); break;
                case "--profile":
                    profile = Value();
                    try { ProtocolPayloads.ValidateProfileName(profile, false); }
                    catch (ProtocolException exception) { throw new ArgumentException(exception.Message); }
                    break;
                case "--state-dir": state = Path.GetFullPath(Value()); break;
                case "--upload":
                case "--download":
                    RequireDefault(operation);
                    operation = option == "--upload" ? ClientOperation.Upload : ClientOperation.Download;
                    var first = Value();
                    var second = Value();
                    (local, remote) = operation == ClientOperation.Upload ? (first, second) : (second, first);
                    break;
                case "--pair": RequireDefault(operation); operation = ClientOperation.Pair; break;
                case "--trust":
                    RequireDefault(operation);
                    operation = ClientOperation.Trust;
                    fingerprint = Value().Replace(":", "", StringComparison.Ordinal).ToUpperInvariant();
                    if (fingerprint.Length != 64 || !fingerprint.All(char.IsAsciiHexDigit))
                    {
                        throw new ArgumentException("--trust requires the 64 hexadecimal digits of the SHA-256 fingerprint.");
                    }
                    break;
                default: throw new ArgumentException($"Unknown option: {option}.");
            }
        }

        if (profile.Length != 0 && operation != ClientOperation.Create)
        {
            throw new ArgumentException("--profile is only valid when creating a shell.");
        }

        return new(operation, local, remote, ClientEndpoint.Parse(endpoint.ToArray(), defaultServer),
            TimeSpan.FromMilliseconds(delay ?? ParseDelay(defaultOutputBatchMilliseconds ?? "4")), profile, state, fingerprint);
    }

    private static ClientOptions Informational(ClientOperation operation)
        => new(operation, null, null, ClientEndpoint.Parse([], null), TimeSpan.Zero);

    private static void RequireDefault(ClientOperation operation)
    {
        if (operation != ClientOperation.Create) { throw new ArgumentException("Choose one client operation."); }
    }

    private static int ParseDelay(string value)
    {
        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var delay) || delay is < 0 or > 100)
        {
            throw new ArgumentException("Output batch delay must be an integer from 0 to 100 milliseconds.");
        }
        return delay;
    }
}
