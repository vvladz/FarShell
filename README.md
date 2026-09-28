# FarShell

FarShell provides a remote Windows terminal and file transfer over TLS. The
broker starts shells inside its existing interactive Windows session, including
an Entra session. Shells inherit that account's permissions; no separate Windows
login is performed.

- Concurrent, independent ConPTY shells with broker-defined launch profiles.
- Pinned broker certificates and a shared, randomly generated API key.
- Client registration through explicit approval on the broker machine.
- Upload, download, and `--send` from the current remote shell directory.
- Adaptive output batching, synchronized-output support, and a `🛜` window title.

Each shell lasts for its connection. Disconnecting or stopping the broker ends
the shell and its ordinary child processes. Programs that explicitly request
Windows Job Object breakaway can keep their child processes running.

## Install

Use matching broker and client packages from [Releases](https://github.com/vvladz/FarShell/releases).
Windows 11 and Windows Terminal are recommended. The executables require the
.NET 10 x64 runtime; the default shell requires PowerShell 7 on the broker's PATH.
Keep all files in the broker archive together, including the bundled ConPTY files.

See [installation and upgrades](docs/getting-started.md) for requirements,
archive verification, source builds, and first connection instructions.

## First connection

On the broker machine, in the intended Windows/Entra desktop session:

```powershell
.\FarShell.Broker.exe
```

On the client machine:

```powershell
.\FarShell.Client.exe --pair 192.168.1.110
```

Compare the displayed certificate fingerprint with the broker's output before
approving it. The client then displays a one-time request ID. In another terminal
on the broker machine, under the same Windows user:

```powershell
.\FarShell.Broker.exe --approve <request-id>
```

After approval, connect normally:

```powershell
$env:FARSHELL_SERVER = '192.168.1.110:8022'
.\FarShell.Client.exe
.\FarShell.Client.exe --profile work
```

`--profile work` requires a configured `work` profile. Every paired client has
the broker account's shell and file access. Read [security and trust](docs/security.md)
before allowing remote access. The broker listens on all IPv4 interfaces; restrict
the firewall rule to the machines that should reach it.

## Documentation

- [Documentation index](docs/README.md)
- [CLI reference](docs/cli.md)
- [Profile configuration](docs/configuration.md)
- [File transfers](docs/file-transfer.md)
- [Security, pairing, and rotation](docs/security.md)
- [Architecture](docs/architecture.md) and [protocol](docs/protocol.md)
- [Troubleshooting](docs/troubleshooting.md)
- [Development and release validation](docs/development.md)

Both executables support `--help` and `--version`.

## Build

From the repository root with the .NET 10 SDK:

```powershell
dotnet build .\FarShell.sln -c Release
dotnet test .\FarShell.sln -c Release --no-build
```
