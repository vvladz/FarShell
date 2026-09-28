# Installation and first connection

## Requirements

- Windows 10 version 1809 or later as enforced by FarShell; Windows 11 is the
  recommended environment. Install a .NET 10 runtime supported by your Windows version.
- .NET 10 x64 runtime for downloaded binaries; .NET 10 SDK for source builds.
- PowerShell 7 (`pwsh.exe` on the broker's startup PATH) for the built-in profile.
  A configured profile may select another executable.
- Windows Terminal for the intended interactive experience.

The broker must run in the interactive Windows session whose permissions the
remote processes should inherit. For Entra, start it from that user's desktop.
Running it as another user or LocalSystem changes the execution identity.

## Release packages

Download both packages from the same [release](https://github.com/vvladz/FarShell/releases):

- `farshell-win-x64.zip`: broker, `conpty.dll`, matching `x64/OpenConsole.exe`
  and `arm64/OpenConsole.exe`, plus documentation.
- `farshell-client-win-x64.zip`: client and documentation.
- Each ZIP has a corresponding SHA-256 checksum file.

Compare `Get-FileHash .\farshell-win-x64.zip -Algorithm SHA256` with the published
checksum before extracting. Extract each package to a stable directory. Keep the
broker files together. The executables are framework-dependent single-file
applications; the .NET runtime is installed separately.

## Pair a client

1. Run `.\FarShell.Broker.exe` on the broker machine. First startup creates its
   protected certificate and API key under `~/.farshell` and prints the certificate
   SHA-256 fingerprint. The default listener is `0.0.0.0:8022`.
2. Permit inbound TCP 8022 from the intended clients in Windows Firewall according
   to your environment's access policy.
3. Run `.\FarShell.Client.exe --pair <broker-host>` on the client. Compare its
   fingerprint with the broker's console or `.\FarShell.Broker.exe --fingerprint`,
   using an independent trusted channel. Type `yes` to pin it.
4. The client displays a request ID. Within two minutes, run
   `.\FarShell.Broker.exe --approve <request-id>` in another terminal on the
   broker machine under the same Windows user. Check that the ID matches the
   client's request. For custom storage, add the broker's `--state-dir` value.
5. After the client reports success, run `.\FarShell.Client.exe <broker-host>`.

For a previously verified fingerprint, `--trust <SHA256-fingerprint> <broker-host>`
pins it without a prompt. It contacts the server and refuses a mismatch. Run
`--pair` afterwards to obtain the key. There is no option to skip certificate
verification or submit the API key as a command-line argument.

Pins and keys belong to a host/port pair. Using a DNS name and using its IP
address create separate entries. Keep using the address you paired.

## Everyday use

```powershell
$env:FARSHELL_SERVER = '192.168.1.110:8022'
.\FarShell.Client.exe
.\FarShell.Client.exe --profile work
.\FarShell.Client.exe --upload .\package.zip C:\Temp\package.zip
```

The environment example affects the current terminal. Use your preferred
Windows environment management to persist it. Closing the connection terminates
its shell and child processes. There is no detach or reconnect operation.

## Upgrade and rollback

Stop the broker, finish or disconnect active sessions, and replace both packages
with binaries from the same release. Keep the private state directory and profile
configuration. Start the broker again and check both `--version` outputs.

To roll back, stop the broker and restore both previous packages together.
Shells are never preserved across broker restarts. Back up configuration and
protected state before identity changes; DPAPI-protected files belong to the
Windows user that created them.

When upgrading a client from a version that stored credentials in `clients/`,
move its `.bin` files to `servers/` within the client state directory before
running the updated client. Otherwise, trust and pair each endpoint again.

## Run from source

```powershell
dotnet build .\FarShell.sln -c Release
dotnet run --project .\src\FarShell.Broker -- --help
dotnet run --project .\src\FarShell.Broker
```

In a second terminal, pair and connect using
`dotnet run --project .\src\FarShell.Client -- --pair 127.0.0.1`, then the same
command without `--pair`. See [development](development.md) for publishing.
