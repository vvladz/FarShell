# File transfer

All remote operations require a paired client over TLS. The API key authorizes
file access under the broker Windows account; there is no per-client file ACL.

## Upload and download

```powershell
.\FarShell.Client.exe --upload .\package.zip 'C:\Temp\package.zip' server.example
.\FarShell.Client.exe --download 'C:\Temp\result.log' .\result.log server.example
```

Each operation opens a separate connection and creates no shell. Local relative
paths use the client's working directory; remote relative paths use the broker's
startup directory. These commands do not use shell profiles. The destination
directory must exist. Use absolute remote paths to avoid ambiguity.

File data uses bounded 64 KiB chunks. A temporary destination is replaced into
place only after the expected content and completion have arrived. Interrupting a
transfer preserves any previous destination. A completed transfer replaces an
existing file; there is no overwrite prompt.

## Send from the current remote shell

Start the client in the local directory where files should arrive. In the remote
shell, change to the source directory and run:

```powershell
FarShell.Broker.exe --send report.zip
FarShell.Broker.exe --send '*.log' 'results\*.json'
```

The helper resolves paths from its own current directory and sends to the client
attached to that shell. Relative subdirectories are preserved and created locally.
The local root is fixed when the client starts. No shell profile script or
PowerShell function needs to be installed.

Paths must be relative, cannot contain `..`, and must select files. `*` and `?`
wildcards are supported in the final path component. One call may send at most
256 files. Successfully completed files remain if a later file fails. An active
connection is required; transfers are not resumed after disconnect.

The broker injects the helper directory into the shell's effective PATH and sets
`FARSHELL_SEND_PIPE` after profile overrides. The helper uses a randomized local
control pipe restricted to the current Windows user.

## Download-root protection

Remote `--send` requests cannot use absolute paths, alternate data streams, the
download root itself, or lexical paths escaping that root. The client also rejects
junctions, symlinks, and other reparse points in the directory chain or at the
destination. It checks the real opened directories and holds handles until commit
or cleanup, preventing a directory from being replaced while data is arriving.

Links pointing inside the root are also rejected.
Choose an ordinary local directory if a redirected or cloud-managed directory is
rejected. Windows may report a sharing error if an ancestor directory is being
renamed or changed concurrently; retry after that operation finishes.
