# Protocol 3

Remote connections start with TLS 1.2 or 1.3. Plaintext is rejected. Frames are
then exchanged inside the encrypted stream:

```text
1 byte   message type
4 bytes  payload length, signed Int32, little endian
N bytes  payload
```

The general maximum payload is 16 MiB. Before authentication, HELLO is limited
to 4 bytes and the authentication/pairing request to 32 bytes. Unknown message
types, malformed payloads, and invalid UTF-8 in structured text are protocol errors.
Terminal DATA_IN/DATA_OUT remains opaque bytes except for client
title transformation and synchronized-mode cleanup.

## Setup and authentication

1. Client verifies the pinned TLS certificate.
2. Client sends HELLO with Int32 version 3; broker replies HELLO_ACK with version 3.
3. Client sends AUTHENTICATE containing the 32-byte API key; broker compares it in
   constant time and replies with empty AUTHENTICATED.
4. Client sends CREATE_SESSION, UPLOAD_FILE, or DOWNLOAD_FILE.

A mismatched protocol version is rejected during HELLO negotiation.

The setup deadline starts when the accepted connection is handled and covers all
four steps. Active operations use their connection lifetime instead.

For pairing, step 3 is an empty PAIR_REQUEST. PAIR_PENDING contains a GUID request
ID. Local approval must occur within two minutes. PAIR_ACCEPTED contains the
32-byte API key, followed by connection closure. Additional client frames or a
disconnect cancel the pending request. At most eight approvals may be pending.

## Message values

| Value | Message | Payload |
| ---: | --- | --- |
| 1 | DATA_IN | Raw terminal input |
| 2 | DATA_OUT | Raw terminal output |
| 3 | RESIZE | Int32 columns, Int32 rows |
| 4 / 5 | PING / PONG | Empty |
| 16 / 17 | HELLO / HELLO_ACK | Int32 protocol version |
| 18 / 19 | AUTHENTICATE / AUTHENTICATED | 32-byte key / empty |
| 20 | CREATE_SESSION | Int32 columns, Int32 rows, Int32 name byte length, UTF-8 profile name |
| 21 | SESSION_CREATED | 16-byte GUID |
| 27 | SESSION_EXITED | GUID followed by Int32 exit code |
| 28 | ERROR | UTF-8 diagnostic, never a key or profile environment dump |
| 29 | UPLOAD_FILE | Int64 file length followed by UTF-8 destination path |
| 30 | UPLOAD_READY | Empty |
| 31 | DOWNLOAD_FILE | UTF-8 source path |
| 32 | FILE_METADATA | Int64 file length |
| 33 | FILE_DATA | File bytes, normally up to 64 KiB |
| 34 | FILE_COMPLETED | Empty |
| 35 | SESSION_FILE_START | GUID transfer ID, Int64 length, UTF-8 relative path |
| 36 | SESSION_FILE_STATUS | GUID, byte status (1 ready / 2 completed / 3 failed), UTF-8 message |
| 37 | SESSION_FILE_END | GUID transfer ID |
| 38 | SESSION_FILE_ABORT | Same encoding as SESSION_FILE_STATUS |
| 39 / 40 | SEND_FILES_REQUEST / SEND_FILES_COMPLETED | Local send-helper pipe messages; see payload codecs |
| 41 / 42 / 43 | PAIR_REQUEST / PAIR_PENDING / PAIR_ACCEPTED | Empty / GUID / 32-byte API key |

All numeric payload fields are little endian. GUIDs use .NET `Guid.TryWriteBytes`
and `new Guid(ReadOnlySpan<byte>)` layout. Names are empty to select the broker
default, or 1–64 ASCII bytes following the profile-name rules. The name length
must match the remaining payload exactly. Terminal dimensions range from 1 to
32767. File lengths cannot be negative; completion requires the advertised length.

## Local approval pipe

The local approval pipe uses a 16-byte GUID request and one byte for success/failure.
Access is restricted to the current Windows user.
