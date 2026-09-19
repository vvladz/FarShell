# Client Identity Proposal

## Status

This document records the proposed first identity mechanism for FarShell. It is
a design proposal, not the current implementation.

> [!WARNING]
> **This is not a secure authentication protocol.** The transport is not
> encrypted, so an observer can read terminal traffic, capture an identity key,
> and replay it. The mechanism only prevents accidental mixing of sessions
> between a small number of known users on a trusted intranet. Do not expose it
> to an untrusted network or describe it as a security boundary.

## Goal

Give the broker a stable, broker-approved `OwnerId` for each connection so it
can:

- assign newly created sessions to the correct owner;
- list only sessions belonging to that owner;
- allow attach and terminate operations only for that owner's sessions;
- support several client installations belonging to the same owner;
- approve and revoke client installations without restarting the broker.

The proposal deliberately does not provide confidentiality, integrity, server
authentication, or protection from impersonation by a network observer.

## Identity model

The model has three distinct values:

- `IdentityKey` is a random bearer value generated and stored by a client.
- `KeyId` is a short fingerprint used to identify a key in administrative
  commands and messages. It is not a secret.
- `OwnerId` is the canonical session owner assigned by the broker administrator.

The client never supplies or selects its `OwnerId`. Multiple identity keys may
map to the same owner:

```text
home-laptop key  -> vvladz
work-laptop key  -> vvladz
another key      -> user2
```

Session code depends only on the resolved `OwnerId`, not on how the connection
obtained it.

## Key generation and storage

- Generate 32 random bytes with a cryptographically secure random generator.
- Encode the key as Base64Url for storage and transport.
- Generate one key per client installation by default.
- Persist the key in the current user's client configuration.
- Never accept a human-chosen password as an identity key.
- Never place the key in command-line arguments or logs.

The broker computes SHA-256 over the received key and stores only the complete
hash. `KeyId` is derived from a short prefix of that hash and displayed in a
human-friendly form such as `AB12-CD34`. Administrative commands must reject an
ambiguous short ID and require more characters.

Hashing avoids storing the original bearer value in the registry. It does not
make this protocol secure: the original value still crosses the network in
plaintext.

## Enrollment flow

1. The client loads its existing identity key or generates one on first use.
2. After protocol negotiation, it sends the key and an optional client label.
3. The broker hashes the key and checks the identity registry.
4. If approved, the broker resolves the associated `OwnerId` and places it in
   `ConnectionContext`.
5. If unknown, the broker upserts a pending enrollment request and rejects all
   session operations.
6. The broker returns the request's `KeyId`; the client prints it and exits.
7. An administrator allows the request and assigns an `OwnerId`.
8. The user retries the connection. No polling or suspended connection is
   required.

An unknown identity must not create a ConPTY, create a session, list sessions,
or reserve an attachment.

## Pending enrollment requests

A pending request contains:

```text
KeyHash
KeyId
ClientLabel
RemoteEndpoint
FirstSeen
LastSeen
AttemptCount
```

`ClientLabel` and `RemoteEndpoint` are informational and untrusted. The broker
deduplicates requests by the complete key hash. Repeated attempts update
`LastSeen` and `AttemptCount` instead of creating new entries.

To keep the pending store bounded:

- expire requests after seven days;
- retain at most 100 requests;
- evict expired requests before rejecting a new request because the limit was
  reached.

These are operational safeguards, not denial-of-service protection.

## Broker administration

The initial command surface may live in `FarShell.Broker.exe`; a separate
project is not required. Proposed commands are:

```text
FarShell.Broker.exe identities pending
FarShell.Broker.exe identities allow <key-id> --owner <owner-id>
FarShell.Broker.exe identities revoke <key-id>
FarShell.Broker.exe identities list
```

`pending` shows the key ID, client label, remote endpoint, first/last seen time,
and attempt count. `allow` marks the key as approved and uses the
administrator-supplied owner ID. `revoke` retains the key hash in a revoked
state and prevents subsequent connections using that key. A revoked key is not
automatically added back to pending on its next attempt.

Allowing a key does not create a session or keep the original connection open;
the client connects again. Revocation applies to new connections and does not
interrupt an already accepted connection in the initial implementation.

## Registry behavior

Approved identities must persist across broker restarts. Pending requests
should also persist so an administrator can process them later. This identity
metadata is independent of shell-session persistence: sessions still end when
the broker process ends.

A small local JSON registry is sufficient for the intended number of users.
Broker and administration commands must serialize registry mutations and
replace the file atomically so readers never observe a partial document. The
broker must observe allow and revoke changes on subsequent connection attempts
without requiring a restart.

Registry entries have `Pending`, `Approved`, or `Revoked` state. An unknown key
creates a pending entry, an approved key resolves an owner, and a revoked key is
rejected without creating a new pending request. Keeping the revoked hash also
lets the administrator approve the same installation again deliberately.

An approved record contains at least:

```text
KeyHash
KeyId
OwnerId
ClientLabel
ApprovedAt
```

## Protocol boundary

Identity resolution occurs after version negotiation and before every session
operation. Its result is a broker-owned connection identity:

```text
Presented IdentityKey
    -> identity provider
    -> ConnectionIdentity(OwnerId, KeyId)
    -> session authorization
```

`ShellSession` stores only its `OwnerId`. `SessionManager` compares that owner
with the identity in `ConnectionContext` for create, list, attach, and terminate
operations. It does not know about identity keys, pending requests, or registry
storage.

The protocol needs outcomes equivalent to:

- identity accepted;
- identity pending, with `KeyId`;
- identity rejected because the key is revoked or the registry is unavailable
  or full.

Detailed error responses must not echo the identity key or its complete hash.

## Future security upgrade

The identity provider is replaceable. TLS, certificate authentication, Windows
authentication, or another mechanism can later produce the same
`ConnectionIdentity` without changing session ownership or lifecycle code.

If transport encryption is added later, all keys previously sent over the
plaintext protocol must be considered exposed and rotated before treating them
as authentication credentials.

## Acceptance criteria

- A new client generates one persistent random key.
- Its first connection creates one bounded, deduplicated pending request and no
  shell session.
- The client and broker administration tool display the same `KeyId`.
- Allowing the request assigns an administrator-selected `OwnerId`.
- A subsequent connection resolves that owner without restarting the broker.
- Two keys can map to one owner and see the same session namespace.
- Different owners cannot list, attach to, or terminate each other's sessions.
- Revoking one key does not revoke other keys mapped to the same owner.
- A revoked key does not reappear as a new pending request.
- Raw identity keys and complete hashes never appear in logs or protocol error
  messages.
