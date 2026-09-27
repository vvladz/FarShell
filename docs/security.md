# Security and trust

## Permissions

The broker uses one randomly generated 256-bit API key for all clients. Possessing
that key grants every broker operation: creating shells, choosing any configured
profile, and accessing files with the broker's Windows account permissions.
There are no per-client roles, Windows logins, profile ACLs, or file sandboxes.

The broker starts in the intended interactive Windows/Entra desktop session.
Its processes inherit that identity. TLS and the API key authenticate access to
FarShell; they do not create a different Windows security context.

## Transport and certificate trust

All remote connections require TLS 1.2 or TLS 1.3, followed by protocol negotiation
and API-key authentication. Shell creation and file operations require successful
authentication. There is no plaintext fallback.

First startup generates a self-signed RSA certificate. The client pins its exact
SHA-256 fingerprint for the chosen host and port. First use displays the fingerprint
and asks for `yes`; compare it with the broker's `--fingerprint` output through an
independent trusted channel. Trust on first use alone cannot identify an active
attacker on that first connection. A trusted pin replaces CA/name validation for
that endpoint; a certificate with a different fingerprint is rejected before the
API key is sent.

The first-use inspection connection closes before asking for approval. The client
then reconnects and verifies the stored pin, so operator input does not hold a
broker setup slot indefinitely. Noninteractive trust requires the exact expected
fingerprint with `--trust`; there is no accept-any-certificate switch.

## Registering clients

`FarShell.Client.exe --pair <host>` requests approval over a pinned TLS connection.
It receives an unpredictable request ID, not a key. The operator compares this ID
with the intended client's screen and runs `FarShell.Broker.exe --approve <id>`
under the broker's Windows user. The control pipe is local and current-user-only.

Requests expire after two minutes, disappear on disconnect, and can be approved
only once. At most eight requests may wait concurrently, within the ordinary
connection limit. An approved key is returned only on the original TLS connection.
The request ID cannot be used later to retrieve the key. A lost response requires
a new pairing request.

## Local storage

Default state directory: `%USERPROFILE%\.farshell` (`~/.farshell`). Both executables
accept `--state-dir`; client and broker directories may be independent. Broker
commands that administer a running broker must use its exact directory.

- `broker.bin` contains the certificate/private key and API key, encrypted with
  Windows DPAPI for the current user.
- `clients/<endpoint-hash>.bin` contains a certificate pin and optional API key,
  also protected with DPAPI.
- `broker.lock` prevents two brokers or an identity-rotation command from modifying
  the same live broker state.

Storage directories have protected ACLs granting access to the current user.
Secrets are not command-line arguments or log output. State is written to a
temporary file and replaced after a completed write. Invalid existing state fails
instead of silently generating a new identity. DPAPI files cannot be provisioned
to another Windows account by copying them; pair the new client instead.

The Windows TLS implementation loads private keys for the current Windows user.
No certificate-authority installation is required. See Microsoft's documentation
on [DPAPI](https://learn.microsoft.com/en-us/windows/win32/api/dpapi/nf-dpapi-cryptprotectdata)
and [Schannel certificate loading](https://learn.microsoft.com/en-us/dotnet/core/extensions/sslstream-troubleshooting).

## Rotate an API key

Stop the broker first; this ends its shells. Then run:

```powershell
.\FarShell.Broker.exe --rotate-key --confirm
```

Restart the broker. Every client must run `--pair` and receive fresh approval.
The certificate pin stays the same. This is the way to revoke the shared key;
individual client revocation is not supported.

## Replace a certificate

Stop the broker, then run:

```powershell
.\FarShell.Broker.exe --replace-certificate --confirm
```

This replaces both the certificate and the shared key. Restart the broker. On
each client, independently verify the new fingerprint, then run:

```powershell
.\FarShell.Client.exe --trust <new-SHA256-fingerprint> <host>
.\FarShell.Client.exe --pair <host>
```

A changed pin clears that endpoint's previous key. The client never silently
accepts a replacement. Both rotation commands fail while the broker holds its
state lock and require explicit `--confirm` because every client's access changes.

## Boundaries

An authenticated shell has the broker user's privileges and can change its files
or inspect its processes. Local malware or an administrator can bypass protections
available to a normal user process. FarShell is not an isolation boundary against
either. Restrict network reachability to intended clients and safeguard access to
the broker's desktop account.

For remote `--send`, the client confines destination paths beneath its startup
directory and rejects reparse points along the directory chain and at the final
file. Directory handles prevent renaming/retargeting during the transfer.
Links pointing inside the root are also rejected. Explicit local
`--download` destinations are chosen by the local operator and have no such root
restriction. See [file transfer](file-transfer.md).
