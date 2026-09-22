# FarShell

FarShell is a minimal Windows-only proof of concept for forwarding a Windows
Terminal session to a PowerShell process running inside a corporate
Windows user's existing interactive session.

```text
Windows Terminal
    -> FarShell.Client
    -> TCP binary protocol
    -> FarShell.Broker on 0.0.0.0:8022
    -> Windows ConPTY
    -> pwsh.exe
```

The broker does not perform a Windows login. Start it from the required
interactive Entra desktop session. `pwsh.exe` is then created by
`CreateProcessW` and inherits the broker's Windows security context.

## Scope

This PoC contains five projects:

- `FarShell.Client` — transparent local terminal input/output and resize
  forwarding.
- `FarShell.Broker` — concurrent connections and in-process shell sessions on
  all IPv4 interfaces on port 8022.
- `FarShell.Protocol` — binary framing and control payloads.
- `FarShell.ConPTY` — the small Windows API wrapper that owns the pseudoconsole
  and child process.
- `FarShell.Tests` — protocol and Windows end-to-end session lifecycle tests.

It intentionally has no SSH implementation, authentication, encryption,
cross-restart session persistence, Windows service, GUI, terminal emulator, or
file transfer.

## Requirements

- Windows 10 version 1809 (build 17763) or later; Windows 11 is recommended.
- .NET 10 x64 runtime for release binaries; the .NET 10 SDK for source builds.
- PowerShell 7 available as `pwsh.exe` on `PATH`.
- Windows Terminal for the intended interactive experience.

## Build

From the solution directory:

```powershell
dotnet build .\FarShell.sln
```

## ToolDock release

The release workflow builds framework-dependent, single-file Windows x64
executables. The target machine must have the .NET 10 x64 runtime installed.
Every successful push to `master` creates the next patch release automatically;
rerunning the workflow for the same commit reuses its existing tag. A pushed
`v*` tag can also publish a release. Each release contains these assets:

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
> The broker listens on all IPv4 interfaces and has no authentication or
> encryption. All connections currently share one anonymous session namespace.
> Restrict inbound TCP port 8022 to trusted clients.

## Run the local PoC

Use two Windows Terminal tabs on the same Windows machine first.

In the interactive Windows/Entra session that must own all shell processes:

```powershell
dotnet run --project .\FarShell.Broker
```

The broker prints its wildcard listening endpoint. In the second tab:

```powershell
dotnet run --project .\FarShell.Client
```

The client defaults to `127.0.0.1:8022`. An endpoint can be supplied explicitly:

```powershell
dotnet run --project .\FarShell.Client -- 127.0.0.1 8022
```

The default command creates and attaches to a new session. The client prints
the new session ID before terminal forwarding starts. Session management uses
the same optional host and port suffix:

```powershell
dotnet run --project .\FarShell.Client -- --list 127.0.0.1 8022
dotnet run --project .\FarShell.Client -- --attach <session-id> 127.0.0.1 8022
dotnet run --project .\FarShell.Client -- --terminate <session-id> 127.0.0.1 8022
```

Broker resource limits and the handshake timeout are configurable:

```powershell
dotnet run --project .\FarShell.Broker -- 8022 `
  --max-connections 32 --max-sessions 16 --handshake-timeout-seconds 10
```

For direct executable use after building:

```powershell
.\FarShell.Client\bin\Debug\net10.0-windows\FarShell.Client.exe 127.0.0.1 8022
```

Verify the local ConPTY round trip first. The broker also accepts direct remote
connections, but the protocol is neither authenticated nor encrypted. Limit
the Windows Firewall rule to trusted source addresses and do not expose the
broker to an untrusted network.

## Protocol

Every frame is:

```text
1 byte   message type
4 bytes  payload length as a little-endian signed Int32
N bytes  payload
```

The maximum payload is 16 MiB. Every connection starts with `HELLO` and
`HELLO_ACK` carrying protocol version `1` before it may perform a session
operation.

| Value | Message | Direction | Payload |
|---:|---|---|---|
| 1 | `DATA_IN` | client -> broker | raw terminal bytes |
| 2 | `DATA_OUT` | broker -> client | raw terminal bytes |
| 3 | `RESIZE` | client -> broker | columns and rows as two little-endian Int32 values |
| 4 | `PING` | either | empty |
| 5 | `PONG` | either | empty |
| 16 | `HELLO` | client -> broker | little-endian Int32 protocol version |
| 17 | `HELLO_ACK` | broker -> client | selected protocol version |
| 18–19 | `LIST_SESSIONS` / `SESSION_LIST` | request / response | visible session metadata |
| 20–21 | `CREATE_SESSION` / `SESSION_CREATED` | request / response | terminal size / session ID |
| 22–23 | `ATTACH_SESSION` / `SESSION_ATTACHED` | request / response | session ID and size / session ID |
| 24 | `DETACH_SESSION` | client -> broker | empty |
| 25–26 | `TERMINATE_SESSION` / `SESSION_TERMINATED` | request / response | session ID |
| 27 | `SESSION_EXITED` | broker -> client | session ID and little-endian Int32 exit code |
| 28 | `ERROR` | broker -> client | UTF-8 error text |

Terminal data is never converted to strings. Partial UTF-8 sequences and VT
escape sequences therefore cross the transport unchanged.

The client disables processed, line, and echo input, enables virtual-terminal
input/output, and uses UTF-8 console code pages while connected. Consequently,
`Ctrl+C` is read as terminal input (`0x03`) and sent as `DATA_IN`; it is not used
to terminate the client. Original console modes and code pages are restored on
exit. Terminal dimensions are checked every 200 ms and changes become
`RESIZE` frames handled by `ResizePseudoConsole`.

## Session lifecycle

1. The client negotiates protocol version 1 and requests create or attach.
2. A new session creates one ConPTY and starts `pwsh.exe -NoLogo`.
3. Client input and ConPTY output are proxied as raw bytes while attached.
4. Client disconnect detaches without terminating the shell. The broker keeps
   draining and discarding ConPTY output so the detached process cannot block
   on a full output pipe.
5. Attach reserves the session exclusively and resizes ConPTY to the new
   terminal dimensions. Detached output is not replayed.
6. Normal shell exit or explicit termination removes the session. Broker
   shutdown closes every session's Job Object and complete process tree.

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

Then verify the session lifecycle:

- [ ] two clients can use independent shells concurrently
- [ ] closing a client leaves its session visible as detached in `--list`
- [ ] `--attach` resumes input and output and rejects a competing attachment
- [ ] detached output is discarded without blocking the shell
- [ ] `--terminate` removes the session and its complete process tree
- [ ] broker shutdown removes every remaining session and process tree

## Known PoC boundaries

- Sessions exist only while the broker process remains alive.
- A session accepts one attachment at a time; multiple sessions and connections
  may run concurrently.
- Detached terminal output is discarded and cannot be replayed.
- All clients currently use one anonymous owner identity and can see the same
  session namespace.
- `PING`/`PONG` only proves the stream is responsive.
- Broker startup must remain in the intended interactive user session. Running
  it as `LocalSystem` or another account changes the execution identity and
  defeats the design.

The implemented multi-session lifecycle and future authentication boundaries
are documented in
[`SESSION_ARCHITECTURE.md`](SESSION_ARCHITECTURE.md).

The proposed broker-approved client identity flow is documented in
[`AUTH_PROPOSAL.md`](AUTH_PROPOSAL.md). It is explicitly not a secure
authentication protocol while the transport remains unencrypted.
