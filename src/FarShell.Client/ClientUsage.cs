namespace FarShell.Client;

internal static class ClientUsage
{
    internal const string Text = """
        FarShell client — secure remote Windows terminal and file transfer

        Usage:
          FarShell.Client.exe [options] [host] [port]
          FarShell.Client.exe --upload <local-file> <remote-file> [options] [host] [port]
          FarShell.Client.exe --download <remote-file> <local-file> [options] [host] [port]
          FarShell.Client.exe --pair [options] [host] [port]
          FarShell.Client.exe --trust <SHA256-fingerprint> [options] [host] [port]

        Options:
          --profile <name>         Broker-defined shell profile (default: broker selection).
          --output-batch-ms <0-100> Idle output delay (default: 4 ms; 0 is immediate).
          --state-dir <directory>  Private trust/key storage (default: ~/.farshell).
          -h, --help               Show this help without connecting.
          --version                Show application and protocol versions.

        Defaults:
          Server: explicit host/port > FARSHELL_SERVER > 127.0.0.1:8022.
          FARSHELL_SERVER accepts host[:port].
          Batch delay: --output-batch-ms > FARSHELL_OUTPUT_BATCH_MS > 4.

        First use:
          FarShell.Client.exe --pair 192.168.1.110
          Verify the certificate, then approve the displayed request on the broker.
          FarShell.Client.exe --profile work 192.168.1.110

        File examples:
          FarShell.Client.exe --upload .\build.zip C:\Temp\build.zip
          FarShell.Client.exe --download C:\Temp\result.log .\result.log
          Inside a remote shell: FarShell.Broker.exe --send .\results\*.log

        A disconnected shell and ordinary child processes are terminated. Children
        explicitly broken away from the Windows Job Object may keep running.
        Transfers replace a destination only after completion. Exit codes: 0 success, 1 failure,
        2 invalid arguments; interactive sessions return the remote shell exit code.
        Documentation: https://github.com/vvladz/FarShell/tree/master/docs
        """;
}
