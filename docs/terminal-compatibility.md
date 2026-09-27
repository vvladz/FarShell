# Terminal compatibility notes

FarShell forwards terminal input and output as raw bytes. The following choices
address behavior observed with Windows console and ConPTY implementations.

## Bundled ConPTY

The broker ships a tested Microsoft `conpty.dll` together with its matching
`OpenConsole.exe` binaries instead of relying on the ConPTY version installed
with Windows. The installed version on a tested machine handled terminal-query
replies differently. Keeping the pair at a known version makes that behavior
consistent across broker machines.

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
