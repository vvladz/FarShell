# CLI reference

Run `FarShell.Client.exe --help` or `FarShell.Broker.exe --help` for offline
command help. `-h` is an alias. `--version` reports application and protocol
versions without connecting, reading configuration, or generating an identity.

## Client

```text
FarShell.Client.exe [options] [host] [port]
FarShell.Client.exe --upload <local-file> <remote-file> [options] [host] [port]
FarShell.Client.exe --download <remote-file> <local-file> [options] [host] [port]
FarShell.Client.exe --pair [options] [host] [port]
FarShell.Client.exe --trust <SHA256-fingerprint> [options] [host] [port]
```

The command without an operation creates a new shell. Choose one operation per
invocation. Quote paths containing spaces.

| Option | Meaning and default |
| --- | --- |
| `--profile <name>` | Select a broker profile for a new shell; omission uses its default |
| `--output-batch-ms <0-100>` | Idle output delay, default 4 ms; 0 is immediate |
| `--state-dir <directory>` | Client private storage, default `~/.farshell` |
| `--pair` | Request broker-local approval and receive the shared API key over TLS |
| `--trust <fingerprint>` | Pin an independently verified SHA-256 certificate fingerprint; a changed pin clears the old key |

`--profile` cannot be combined with file transfer, pairing, or trust commands.
Repeated options and incompatible operations are errors. The trust fingerprint
is 64 hexadecimal digits; colons between digits are accepted.

Server precedence is explicit CLI host/port, then `FARSHELL_SERVER`, then
`127.0.0.1:8022`. An explicit host without a port uses 8022. CLI host and port
are separate arguments; `FARSHELL_SERVER` uses `host[:port]`.

Output delay precedence is `--output-batch-ms`, then
`FARSHELL_OUTPUT_BATCH_MS`, then 4. After each input read, the client uses at most
1 ms for 250 ms. Application-supplied synchronized updates bypass batching.
Zero always disables the delay. Terminal input is forwarded immediately.

```powershell
$env:FARSHELL_SERVER = 'workstation.example:8022'
.\FarShell.Client.exe --profile work
.\FarShell.Client.exe --output-batch-ms 0 workstation.example 9000
.\FarShell.Client.exe --download 'C:\Build Results\result.log' .\result.log
```

## Broker

```text
FarShell.Broker.exe [port] [server-options]
FarShell.Broker.exe --approve <request-id> [--state-dir <directory>]
FarShell.Broker.exe --fingerprint [--state-dir <directory>]
FarShell.Broker.exe --rotate-key --confirm [--state-dir <directory>]
FarShell.Broker.exe --replace-certificate --confirm [--state-dir <directory>]
FarShell.Broker.exe --send <relative-file-or-pattern> [more-files...]
```

| Server option | Default |
| --- | --- |
| Positional port | 8022; range 1–65535 |
| `--max-connections <count>` | 32; range 1–1024 |
| `--max-sessions <count>` | 16; range 1–1024 |
| `--handshake-timeout-seconds <seconds>` | 10; range 1–300 |
| `--config <path>` | `%LOCALAPPDATA%\FarShell\config.json` |
| `--state-dir <directory>` | `~/.farshell` |

The listener binds all IPv4 interfaces. The setup timeout covers TLS,
HELLO, authentication, and receipt of the first operation. It does not limit
an established shell or file transfer. Explicit pairing requests have their
own two-minute lifetime, with at most eight pending approvals.

`--approve` connects to a local control pipe restricted to the broker's Windows
user. It needs the running broker and its state directory. The request ID is
32 hexadecimal digits displayed by the client. `--fingerprint` reads the existing
identity. Rotation commands require a stopped broker and `--confirm`; see
[security](security.md) for their effect on every client.

`--send` runs inside a connected remote shell. It uses that shell's current
directory and an automatically injected control pipe; see [file transfer](file-transfer.md).

## Output and exit codes

Help, version, transfer results, broker status, and pairing instructions go to
stdout. Errors and certificate confirmation prompts go to stderr. Secrets are
never accepted as arguments or printed. Interactive stdout carries terminal data.

| Code | Meaning |
| --- | --- |
| 0 | Successful command or normal broker cancellation |
| 1 | Connection, authentication, configuration, or operation failure |
| 2 | Invalid command-line arguments (including a missing rotation confirmation) |
| Remote exit code | An interactive client returns the remote shell's exit code |

Profile configuration validation may report code 2 for invalid values. Shell exit
codes may overlap the client's own failure codes; use the accompanying diagnostic
when distinguishing them in scripts.
