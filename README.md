# FarShell

FarShell is a minimal Windows-only proof of concept for forwarding a local
Windows Terminal session to a PowerShell process running inside a corporate
Windows user's existing interactive session.

```text
Windows Terminal
    -> FarShell.Client
    -> TCP binary protocol
    -> FarShell.Broker on 127.0.0.1:8022
    -> Windows ConPTY
    -> pwsh.exe
```

The broker does not perform a Windows login. Start it from the required
interactive Entra desktop session. `pwsh.exe` is then created by
`CreateProcessW` and inherits the broker's Windows security context.

## Scope

This PoC contains exactly four projects:

- `FarShell.Client` — transparent local terminal input/output and resize
  forwarding.
- `FarShell.Broker` — one active client at a time on loopback port 8022.
- `FarShell.Protocol` — binary framing and control payloads.
- `FarShell.ConPTY` — the small Windows API wrapper that owns the pseudoconsole
  and child process.

It intentionally has no SSH implementation, authentication, encryption,
reconnect, persistent sessions, Windows service, GUI, terminal emulator, or
file transfer.

## Requirements

- Windows 10 version 1809 (build 17763) or later; Windows 11 is recommended.
- .NET 8 x64 runtime for release binaries; the .NET 8 SDK or a newer SDK
  capable of targeting .NET 8 for source builds.
- PowerShell 7 available as `pwsh.exe` on `PATH`.
- Windows Terminal for the intended interactive experience.

## Build

From the solution directory:

```powershell
dotnet build .\FarShell.sln
```

## ToolDock release

The release workflow builds framework-dependent, single-file Windows x64
executables. The target machine must have the .NET 8 x64 runtime installed.
Every `v*` tag publishes these GitHub Release assets:

- `farshell-win-x64.zip` contains `FarShell.Broker.exe` at its root and is the
  asset managed by ToolDock;
- `farshell-client-win-x64.zip` contains `FarShell.Client.exe` at its root for
  machines initiating terminal connections;
- a matching `.sha256` file is published for each archive.

Use this ToolDock catalog entry for the broker:

```json
{
  "tools": {
    "farshell": {
      "repo": "vvladz/FarShell",
      "asset": "farshell-win-x64.zip",
      "executable": "FarShell.Broker.exe",
      "enabled": true,
      "autostart": true,
      "restart": true
    }
  }
}
```

ToolDock starts and supervises the broker; it does not start the interactive
client. The client archive is installed or copied separately on the machine
from which the connection is initiated.

> [!IMPORTANT]
> Packaging FarShell for ToolDock does not change the current PoC boundaries.
> The broker still listens on loopback, serves one client at a time, and has no
> authentication or encryption. The session and identity documents describe
> planned behavior, not functionality included in this release.

## Run the local PoC

Use two Windows Terminal tabs on the same Windows machine first.

In the interactive Windows/Entra session that must own all shell processes:

```powershell
dotnet run --project .\FarShell.Broker
```

The broker prints its loopback endpoint. In the second tab:

```powershell
dotnet run --project .\FarShell.Client
```

The client defaults to `127.0.0.1:8022`. An endpoint can be supplied explicitly:

```powershell
dotnet run --project .\FarShell.Client -- 127.0.0.1 8022
```

For direct executable use after building:

```powershell
.\FarShell.Client\bin\Debug\net8.0-windows10.0.17763.0\FarShell.Client.exe 127.0.0.1 8022
```

Only after the local ConPTY round trip passes should an external encrypted
carrier be considered. The broker is deliberately bound to `127.0.0.1`; do not
expose this unauthenticated PoC directly to a network.

## Protocol

Every frame is:

```text
1 byte   message type
4 bytes  payload length as a little-endian signed Int32
N bytes  payload
```

The maximum payload is 16 MiB.

| Value | Message | Direction | Payload |
|---:|---|---|---|
| 1 | `DATA_IN` | client -> broker | raw terminal bytes |
| 2 | `DATA_OUT` | broker -> client | raw terminal bytes |
| 3 | `RESIZE` | client -> broker | columns and rows as two little-endian Int32 values |
| 4 | `PING` | either | empty |
| 5 | `PONG` | either | empty |
| 6 | `EXIT` | either | empty from client; broker sends a little-endian Int32 process exit code |

Terminal data is never converted to strings. Partial UTF-8 sequences and VT
escape sequences therefore cross the transport unchanged.

The client disables processed, line, and echo input, enables virtual-terminal
input/output, and uses UTF-8 console code pages while connected. Consequently,
`Ctrl+C` is read as terminal input (`0x03`) and sent as `DATA_IN`; it is not used
to terminate the client. Original console modes and code pages are restored on
exit. Terminal dimensions are checked every 200 ms and changes become
`RESIZE` frames handled by `ResizePseudoConsole`.

## Session lifecycle

1. The client connects and sends the initial terminal size.
2. The broker creates one ConPTY and starts `pwsh.exe -NoLogo`.
3. Client input and ConPTY output are proxied as raw bytes.
4. Client disconnect or client `EXIT` disposes the ConPTY and closes a Windows
   Job Object, terminating the shell and its complete child process tree.
5. Normal shell exit is returned to the client in a broker `EXIT` frame.
6. The broker then waits for the next client.

## Acceptance checklist

Run these first and confirm they match a normal PowerShell started locally in
the same corporate interactive desktop session:

- [ ] `whoami`
- [ ] `$env:USERPROFILE`
- [ ] `git status`

Then run `codex` and verify:

- [ ] normal typing, Enter, and Backspace
- [ ] arrows, Home, and End
- [ ] `Ctrl+C` reaches the remote program and does not terminate the client
- [ ] `Ctrl+L`, other Ctrl combinations, and Alt combinations
- [ ] large paste
- [ ] terminal resize and TUI redraw
- [ ] scrolling and alternate screen
- [ ] Unicode and colors/truecolor
- [ ] clean exit from Codex and successful repeated launch

Finally run Copilot CLI and repeat the interactive checks. The acceptance
criterion is that remote Codex/Copilot behavior is materially equivalent to
running each tool locally in Windows Terminal on the corporate laptop.

## Known PoC boundaries

- A single active client is handled at a time.
- There is no recovery after a broken connection; the child session is ended.
- `PING`/`PONG` only proves the stream is responsive; it is not reconnect logic.
- Broker startup must remain in the intended interactive user session. Running
  it as `LocalSystem` or another account changes the execution identity and
  defeats the design.

The agreed plan for multi-session support, detach/attach persistence, session
listing, and future authentication boundaries is documented in
[`SESSION_ARCHITECTURE.md`](SESSION_ARCHITECTURE.md).

The proposed broker-approved client identity flow is documented in
[`AUTH_PROPOSAL.md`](AUTH_PROPOSAL.md). It is explicitly not a secure
authentication protocol while the transport remains unencrypted.
