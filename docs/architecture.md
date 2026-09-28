# Architecture

The broker runs in the interactive Windows session and starts shells under
the same Windows account.

| Project | Responsibility |
| --- | --- |
| `FarShell.Client` | CLI, certificate approval, raw console input, output batching/title marking, local transfers |
| `FarShell.Broker` | TLS listener, authentication, local pairing approval, profiles, active shells, remote transfers |
| `FarShell.Protocol` | Versioned framing and payload validation; independent of Windows process creation |
| `FarShell.ConPTY` | Native pseudoconsole/process handles, argument quoting, Unicode environment block, Job Object |
| `FarShell.Security` | Shared certificate identity, DPAPI storage, ACLs, endpoint credentials |
| `FarShell.Tests` | Protocol, CLI, streaming, security, configuration, Windows integration regression tests |

## Connection lifecycle

```text
TCP connection slot
  -> TLS
  -> HELLO / HELLO_ACK, protocol 3
  -> AUTHENTICATE / AUTHENTICATED
  -> first operation
     -> create profile shell: terminal and attached --send until disconnect/exit
     -> upload or download: transfer then close
```

TLS, negotiation, authentication, and the first operation share one bounded setup
deadline. Once an operation is read, its lifetime uses the connection cancellation
token instead. Pairing replaces authentication with an explicitly bounded approval
request and closes after the key is delivered; it creates no shell.

`BrokerServer` owns accepted connection handlers and waits for them on shutdown.
`ConnectionContext` owns TLS, serialized writes, and connection cancellation.
`SessionManager` enforces shell limits and observes cleanup. `ShellSession` owns
one ConPTY, output pump, send helper, and live attachment. IDs correlate protocol
notifications; they cannot be used for reconnecting.

## Process lifecycle

A client owns its shell for the connection's entire lifetime. Closing the client
or stopping the broker terminates the shell and processes still in its Windows
Job Object. Normal shell exit sends `SESSION_EXITED` with the exit code. There is
no FarShell detach, attach, session listing, remote termination command, or
session persistence across restart.

The process starts suspended, joins a `KILL_ON_JOB_CLOSE | BREAKAWAY_OK` Job
Object, and then resumes. Ordinary descendants remain in the job and are
terminated with it. A program that explicitly creates a child with
`CREATE_BREAKAWAY_FROM_JOB` can leave that child running after disconnect or
broker shutdown; FarShell no longer manages it. ConPTY output waits in the OS
pipe until `SESSION_CREATED` is sent and the connection becomes active. There
is no unbounded replay buffer. Output writes use the attachment cancellation
token so a client that stops reading cannot prevent job cleanup.

Profiles are validated before the listener opens and resolved before acquiring a
shell slot. Immutable, generic process-launch settings flow through
`SessionManager` and `ShellSession` to `ConPtySession`. The native layer does not
parse JSON or know profile names. The complete Unicode environment is allocated
for `CreateProcessW` and released on success or failure.

## File and terminal paths

`FileTransferHandler` performs standalone transfers. `SessionFileSendService`
accepts local helper requests over a current-user-only pipe. `AttachedFileReceiver`
and `IncomingFileTransfer` handle the remote-to-local stream; `DownloadPathGuard`
retains non-reparse directory handles through commit and cleanup.

The client input thread forwards raw bytes immediately. Output passes through a
bounded OSC-title rewriter and a bounded batching queue. The title recognizer and
mode-2026 tracker are narrow streaming state machines, not terminal emulators.
See [terminal compatibility](terminal-compatibility.md) and [protocol](protocol.md).
