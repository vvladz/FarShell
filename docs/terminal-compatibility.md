# Terminal compatibility notes

FarShell forwards terminal input as raw bytes and preserves non-title output.

## Bundled ConPTY

The broker uses the bundled Microsoft `conpty.dll` and matching
`OpenConsole.exe` binaries. Keep these files together when installing or
updating the broker.

## Console input

The client enables raw VT input mode and performs synchronous `ReadFile` on a
dedicated thread. A blocking console read needs its own thread, and this path
forwards both control keys and replies to terminal queries as unmodified
`DATA_IN` bytes. On shutdown, `CancelIoEx` interrupts the blocked read. Console
mode and code page changes are restored when the connection ends.

## Session startup

ConPTY may emit output before the broker has sent `SESSION_CREATED`. The output
pump waits until the connection is active, leaving those bytes in the operating
system pipe, then forwards them in order. There is no separate replay buffer.

Before rendering the first remote byte, the client sends `ESC[2J` and `ESC[H`
to clear its screen and move the cursor home. Some ConPTY instances do not emit
their own startup clear/home sequence, so otherwise the new shell can begin at
the local cursor position. ConPTY is created at the requested size; an extra
startup resize is unnecessary.

## Adaptive output and synchronized updates

The output queue is bounded to 16 payloads and writes batches no larger than
64 KiB. Idle batching uses the configured delay (4 ms by default). Each local
input read refreshes a 250 ms window using at most 1 ms. An explicit zero delay
stays immediate. Input itself is not batched.

The client recognizes exact `ESC[?2026h` and `ESC[?2026l` markers across frame
boundaries. Inside an application-provided synchronized update, it forwards
bytes immediately and lets Windows Terminal perform atomic presentation. Both
markers pass through unchanged; FarShell does not wrap arbitrary frames in its
own updates. Disconnect cleanup emits an end marker if the terminal was left in
synchronized mode. A truncated control string is canceled first so it cannot
swallow the recovery marker. The tracker ignores marker-like bytes inside control strings.

## Remote window title

The client initially marks the window `🛜 FarShell — <host>`. A streaming rewriter
prefixes only OSC 0 and OSC 2 titles with `🛜 `, preserving their text and BEL/ST
terminator. Other control sequences pass through unchanged. Candidate titles
are bounded to 4096 bytes; malformed, unsupported, overlong, or unfinished
sequences pass through conservatively. The previous local title is restored
after output cleanup when the client leaves the session.
