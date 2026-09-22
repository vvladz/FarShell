# Session Architecture

## Status

Multi-session support, in-process session persistence, protocol negotiation,
and the anonymous authorization boundary described here are implemented.
Client identity enrollment and security-grade authentication remain future
work.

## Goals

- Serve multiple independent shell sessions concurrently.
- Keep a shell session alive when its client disconnects.
- Allow a client to list, attach to, and terminate available sessions.
- Keep session ownership independent of the eventual authentication method.
- Preserve the existing raw-byte terminal transport and ConPTY process model.

## Non-goals

- Recover sessions after the broker stops or crashes.
- Persist ConPTY handles, processes, or session metadata across broker restarts.
- Buffer or replay terminal output produced while a session is detached.
- Reconstruct terminal screens with a VT parser or terminal emulator.
- Provide security-grade authentication or encryption in this phase.
- Expose the broker directly to an untrusted network.

## Agreed session semantics

- The broker process owns every session.
- Broker shutdown or failure ends all sessions. Closing each session's Windows
  Job Object terminates its complete process tree.
- A session has at most one active client attachment.
- A client disconnect detaches from the session; it does not terminate it.
- An explicit terminate operation closes the session and its process tree.
- A normal shell exit ends the session and records its final exit code long
  enough to notify an attached client and remove the session from the registry.
- The ConPTY output pipe is always drained. Output is forwarded while a client
  is attached and discarded while the session is detached.
- Detached output is never replayed after attachment.
- Session identifiers are random, opaque, and impractical to guess.

## Attach and redraw behavior

Attachment does not restore output generated while the session was detached.
The broker performs the following sequence:

1. Validate that the connection may attach and reserve the session attachment.
2. Send the attach success response.
3. Activate the new output destination.
4. Unconditionally resize the ConPTY to the attaching client's columns and
   rows.
5. Start forwarding subsequent terminal output.

When the dimensions change, a well-behaved TUI will normally redraw in response
to the ConPTY resize. A resize to the same dimensions is not guaranteed to
produce a redraw. Full screen restoration is explicitly best effort; forcing an
intermediate fake size or injecting application-specific redraw input is out of
scope.

## Broker structure

### `ConnectionContext`

Owns one accepted transport connection and contains:

- the TCP client, stream, and serialized frame writer;
- the remote endpoint;
- protocol version and negotiated capabilities;
- cancellation and connection lifetime;
- the authenticated client identity when authentication is added.

It does not own a ConPTY session.

### `ShellSession`

Owns one persistent runtime session:

- session ID and owner ID;
- creation time and current terminal size;
- ConPTY, shell process, and Windows Job Object;
- continuous input/output pumps;
- optional current attachment;
- attached, detached, exiting, and exited state transitions.

The output pump must remain active while detached and discard bytes rather than
allowing the ConPTY pipe to fill.

### `SessionManager`

Owns the concurrent session registry and is responsible for:

- create, list, attach, detach, and terminate operations;
- enforcing one attachment per session;
- filtering sessions by owner identity;
- session-count and resource limits;
- coordinated broker shutdown and completion of all session tasks.

### `BrokerServer`

The accept loop creates a `ConnectionContext` and dispatches it without waiting
for earlier clients to disconnect. A connection supervisor tracks all handler
tasks, owns each accepted client, applies connection limits, and awaits clean
shutdown. Session lifetime is delegated to `SessionManager`.

## Protocol

Every connection performs a versioned handshake before any session operation or
ConPTY process creation. Protocol version 1 contains:

- `HELLO` / `HELLO_ACK`;
- `LIST_SESSIONS` / `SESSION_LIST`;
- `CREATE_SESSION` / `SESSION_CREATED`;
- `ATTACH_SESSION` / `SESSION_ATTACHED`;
- `DETACH_SESSION`;
- `TERMINATE_SESSION` / `SESSION_TERMINATED`;
- `SESSION_EXITED`.
- `ERROR`.

`DATA_IN`, `DATA_OUT`, `RESIZE`, `PING`, and `PONG` remain data-plane messages
for an attached session. Disconnect is treated as detach. Shell exit and an
explicit terminate request are distinct from detach.

This is a breaking protocol revision. Client and broker are updated and
deployed together; compatibility with the current unversioned PoC protocol is
not required.

## CLI

```text
FarShell.Client [host] [port]
FarShell.Client --list [host] [port]
FarShell.Client --attach <session-id> [host] [port]
FarShell.Client --terminate <session-id> [host] [port]
```

- The default command creates and attaches to a new session.
- `--list` prints sessions visible to the current client identity.
- `--attach` attaches exclusively to an existing detached session.
- `--terminate` terminates a session and its complete process tree.

`--list` includes the session ID, attached/detached state, creation time, and
terminal dimensions.

## Identity and authentication boundary

The proposed initial client identity and enrollment mechanism is documented in
[`AUTH_PROPOSAL.md`](AUTH_PROPOSAL.md). It intentionally prevents accidental
session-owner mixing but is not a secure authentication protocol. Session code
must not depend on that specific mechanism.

- Identity resolution occurs after protocol negotiation and before list,
  create, attach, or terminate operations.
- Identity resolution produces a broker-owned client identity.
- The broker derives `OwnerId` from that resolved identity. A client never
  supplies or selects its own owner ID.
- Authorization checks create, list, attach, and terminate operations against
  the identity in `ConnectionContext`.
- A secure authenticator may later provide the identity without
  changing `ShellSession` or `SessionManager`.

Until client identity is implemented, all connections use one anonymous
identity and therefore share one visible session namespace. The plaintext
identity proposal must not be treated as protection against a network attacker.

## Limits and failure handling

The broker provides configurable bounds for:

- concurrent connections;
- active shell sessions;
- handshake duration.

Defaults are 32 concurrent connections, 16 active sessions, and a 10-second
handshake timeout. No detached-session expiry policy is enabled; one may be
added later if operational use requires it.

There is no detached-output memory or disk quota because detached output is
discarded. A failed connection only detaches its session. A failed session only
closes its own attachment and process tree.

The current process wait uses one blocked ThreadPool worker per session. This is
acceptable for a small number of sessions, but it should be replaced with a
registered Windows handle wait before targeting high session counts.

## Remaining sequence

1. Integrate the client identity enrollment described in `AUTH_PROPOSAL.md`
   without changing session ownership or lifecycle semantics.
2. Replace the identity provider later if security-grade authentication is
   required.

## Acceptance criteria

- Multiple clients can run independent shells concurrently.
- One client failure does not interrupt other connections or sessions.
- Disconnecting a client leaves its shell and process tree running.
- Detached sessions continue running even when they produce more output than a
  pipe buffer can hold; that output is discarded.
- `--list` reports the sessions visible to the current identity.
- A detached session can be attached once and rejects a competing attachment.
- Attach applies the new client dimensions before normal interaction resumes.
- Commands can be entered successfully after reattachment.
- Normal shell exit removes the session and reports the exit code when possible.
- Explicit termination and broker shutdown remove the complete process tree.
- Repeated connect, detach, attach, terminate, and shutdown races leave no
  unobserved tasks, blocked handles, or orphaned processes.
