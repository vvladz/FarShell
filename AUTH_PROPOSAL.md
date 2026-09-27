# API Key and TLS Proposal

## Status

This is a design proposal, not current behavior. The broker currently accepts
plaintext, unauthenticated connections. Authentication and TLS must be
implemented and deployed together.

## Agreed direction

- Use one broker-wide random API key. There are no per-user identities,
  `OwnerId` values, or cross-client session permissions. Each shell exists only
  for its own live client connection.
- Wrap the protocol transport in TLS. The broker generates a certificate on
  first start if none exists and keeps its certificate and private key under
  the broker user's `~/.farshell` directory. Protect the private key and API key
  with permissions limited to that user.
- On first connection, the client shows the certificate fingerprint and asks
  the user to approve it, then pins it locally. Later connections must reject
  a changed certificate until the user explicitly approves a replacement.
- Provision the shared key through a one-time broker-approved request instead
  of manually copying a secret between machines. The exact approval command
  and request lifetime remain to be specified before implementation.

The API key is sent only inside an established TLS connection after the client
has accepted the broker certificate. Authenticate before creating a ConPTY or
allowing any file operation. Do not include the key in command-line arguments,
protocol errors, or logs. Generate it with a cryptographic random generator;
never derive it from a human password.

Certificate confirmation on first use is trust on first use, like SSH
`known_hosts`. It protects subsequent connections from an unnoticed broker
certificate change. The first connection needs an independently verified
fingerprint if an active network attacker is in scope.

Rotating the single API key invalidates every provisioned client. Replacing
the broker certificate requires clients to approve its new fingerprint. Both
events need explicit operator steps and tests when this proposal is
implemented.

## Implementation boundary

`BrokerServer` already calls an `IConnectionAuthenticator` placeholder after
protocol negotiation. TLS should be established before sending protocol
frames, and authentication should complete before dispatching create, upload,
or download requests. Session lifecycle and file-transfer payloads do not need
owner metadata.

The first implementation should verify that an unknown or wrong key creates
no shell and cannot transfer files; accepted clients can run concurrent,
independent shells; certificate mismatches stop before key submission; and
key or private-key material never appears in logs.
