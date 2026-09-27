namespace FarShell.Broker;

internal static class BrokerUsage
{
    internal const string Text = """
        FarShell broker — TLS-protected shells in the current Windows user session

        Usage:
          FarShell.Broker.exe [port] [options]
          FarShell.Broker.exe --approve <request-id> [--state-dir <directory>]
          FarShell.Broker.exe --fingerprint [--state-dir <directory>]
          FarShell.Broker.exe --rotate-key --confirm [--state-dir <directory>]
          FarShell.Broker.exe --replace-certificate --confirm [--state-dir <directory>]
          FarShell.Broker.exe --send <relative-file-or-pattern> [more-files...]

        Server options:
          --config <path>                 Profile configuration JSON.
                                          Default: %LOCALAPPDATA%\FarShell\config.json.
          --state-dir <directory>         Private certificate/key storage.
                                          Default: ~/.farshell.
          --max-connections <1-1024>       Connection limit (default: 32).
          --max-sessions <1-1024>          Active shell limit (default: 16).
          --handshake-timeout-seconds <1-300>
                                          TLS/auth/first-operation timeout (default: 10).
          -h, --help                      Show help without starting the broker.
          --version                       Show application and protocol versions.

        The listener binds all IPv4 interfaces; default port: 8022. Start it in
        the interactive Windows/Entra desktop session that should own the shells.
        Every paired client has that account's shell and file access.

        First start creates an identity. Verify its --fingerprint on the client.
        Pairing requests expire after 2 minutes. Approve under the broker's user.
        Stop the broker before rotating its key or replacing its certificate.
        Key rotation requires all clients to pair again. Certificate replacement
        requires explicit client --trust approval and pairing again.

        --send runs inside a FarShell remote shell, accepts relative file paths
        and final-component * / ? wildcards, and sends at most 256 files per call.
        Exit codes: 0 success, 1 operation failure, 2 invalid arguments.
        Documentation: https://github.com/vvladz/FarShell/tree/master/docs
        """;
}
