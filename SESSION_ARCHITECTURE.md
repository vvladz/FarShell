# Session Architecture

## Status and lifecycle

FarShell supports multiple independent, active shells. Each interactive client
connection creates one ConPTY and one `pwsh.exe -NoLogo` process tree. A shell
belongs to that connection for its entire lifetime. Disconnecting the client
terminates the shell and its complete Windows Job Object process tree. Normal
shell exit reports `SESSION_EXITED` when the connection is still available.
Broker shutdown terminates all remaining shells.

There is no detach, attach, session listing, or remote termination operation.
Shells cannot be recovered after a connection is lost. Internal session IDs
identify exit notifications and file transfers within a live connection; they
are not handles for resuming a shell.

## Startup output

ConPTY may produce terminal control sequences before the broker has replied to
`CREATE_SESSION`. The broker starts ConPTY at the client's requested size and
leaves its output in the operating system pipe until `SESSION_CREATED` has been
sent and the connection is active. The output pump then drains that pipe and
forwards all bytes in order. There is no separate unbounded memory buffer.

An extra resize at activation is unnecessary because ConPTY already has the
requested size. Subsequent client `RESIZE` frames call `ResizePseudoConsole`.

## Components

- `BrokerServer` accepts concurrent connections, negotiates the protocol,
  authenticates through the current placeholder, and owns each connection's
  interactive request until disconnect or shell exit.
- `SessionManager` tracks live shells, enforces the active-session limit, and
  waits for cleanup on broker shutdown. It exposes no cross-connection session
  lookup.
- `ShellSession` owns ConPTY, the process tree, output pump, and one active
  connection. Its output pump waits for activation before reading ConPTY output.
- `SessionFileSendService` provides a randomized current-user-only named pipe
  for `FarShell.Broker.exe --send` and transfers files to the shell's client.
  An incomplete transfer is aborted when the connection ends.
- `ConnectionContext` owns one TCP transport, frame writer, and cancellation
  token. It does not contain a session owner ID.

The broker defaults to 32 concurrent connections, 16 active shells, and a
10-second handshake timeout. The process wait currently uses one blocked
ThreadPool worker per shell; a registered Windows handle wait would be more
appropriate if the supported session count grows substantially.

## Protocol and compatibility

Protocol version 2 requires `HELLO` / `HELLO_ACK` before an operation. An
interactive connection sends `CREATE_SESSION`, receives `SESSION_CREATED`, and
then exchanges `DATA_IN`, `DATA_OUT`, `RESIZE`, `PING`, `PONG`, and file-transfer
frames until the shell exits or the connection closes. Standalone `--upload`
and `--download` use separate connections and do not create shells.

Version 2 removes the version 1 list, attach, detach, and terminate operations.
The old numeric message values are not reused. Version 1 clients and version 2
brokers reject each other at handshake; update both binaries together.

The current transport is plaintext and accepts connections without
authentication. The proposed single API key and TLS design is recorded in
[`AUTH_PROPOSAL.md`](AUTH_PROPOSAL.md).
