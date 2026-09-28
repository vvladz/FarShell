# Troubleshooting

Start with both executables' `--version` output and ensure they came from the
same release. Use `--help` to check the syntax without contacting the server.

| Symptom | Check or action |
| --- | --- |
| Connection refused or setup timed out | Broker is running, address/port are correct, and the firewall permits this client. Both peers must use TLS/protocol 3. |
| Certificate approval needs a terminal | Independently verify the fingerprint, then use `--trust <SHA256>` with the same host/port. |
| Broker certificate changed | Compare with the broker's `--fingerprint` through a trusted channel. Use `--trust` only for an intended replacement, then pair again. |
| Client is not paired / authentication failed | Run `--pair`; approve the new request on the broker. Key rotation invalidates all old clients. |
| Approval cannot connect to the broker | Run it under the broker Windows user and use the broker's `--state-dir` if customized. |
| Pairing request expired or unknown | Start a new `--pair`; approve that ID within two minutes while the client stays connected. |
| State directory is in use | Stop the owning broker before key/certificate replacement. Different broker instances need distinct state directories. |
| Invalid profile configuration | Correct the indicated setting. An existing invalid file does not fall back to defaults. All configured paths must exist at startup. |
| Unknown profile | Check the broker's config; names are case-insensitive. Restart the broker after changing config. |
| Missing `pwsh.exe` or another executable | Put it on the broker's startup PATH or use an absolute path in the profile. Profile PATH overrides do not resolve the executable. |
| `--send` requires a FarShell session | Run it in the remote shell created by FarShell, with its inherited control-pipe environment. |
| Download path is a reparse point | Start the client in an ordinary directory with no redirected ancestors; avoid junctions, symlinks, and other reparse points. |
| Transfer cannot secure a directory | Wait for other directory changes/renames to finish, then retry. Ensure the current user can read its attributes. |
| Existing transfer destination remains unchanged | The transfer did not complete. Check space, permissions, and connectivity, then retry. |
| Missing `conpty.dll` / helper files | Extract the complete broker archive and preserve its directory layout. |

## Rendering and input

The default idle batch is 4 ms, reduced to 1 ms for 250 ms after input. Try
`--output-batch-ms 0` to compare immediate rendering. Applications that emit
DEC mode 2026 are forwarded immediately inside their synchronized update.

The title marker is `🛜`. If it appears as an empty box, check the Windows/terminal
font's support for that glyph. FarShell restores the previous local title on
normal cleanup. Applications can still choose their own title text after the marker.

Test resize, control keys, mouse-wheel behavior, and alternate-screen applications
in Windows Terminal. The client preserves local mouse and QuickEdit
settings. Do not run the interactive client with redirected stdin as a substitute
for a terminal; standalone transfer commands work without interactive console input.

Closing the connection ends the remote shell and its ordinary child processes.
Explicitly broken-away children may continue running. Start a new connection
for a new shell; there is no detached FarShell session to recover.

## Useful diagnostics

Record versions, the command shape, broker error messages, and whether a problem
occurs on loopback as well as remotely. Include the selected profile name when
relevant. Avoid including state files, API keys, or environment values in reports.
Certificate fingerprints and one-time request IDs are identifiers rather than
the shared API key.
