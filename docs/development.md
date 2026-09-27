# Development and release validation

Use Windows, the .NET 10 SDK, and `pwsh.exe` on PATH. Run these commands from
the repository root:

```powershell
dotnet build .\FarShell.sln -c Release
dotnet test .\FarShell.sln -c Release --no-build
dotnet format .\FarShell.sln --verify-no-changes --no-restore
```

## Publish locally

```powershell
dotnet publish .\src\FarShell.Broker\FarShell.Broker.csproj -c Release -r win-x64 `
  --no-self-contained -p:PublishSingleFile=true -p:DebugType=None -p:DebugSymbols=false `
  -o .\artifacts\broker
dotnet publish .\src\FarShell.Client\FarShell.Client.csproj -c Release -r win-x64 `
  --no-self-contained -p:PublishSingleFile=true -p:DebugType=None -p:DebugSymbols=false `
  -o .\artifacts\client
```

Keep the broker's `conpty.dll` and matching `OpenConsole.exe` files. Confirm that
published `--help` and `--version` work and invalid arguments return 2.

## Releases

CI versions use `1.0.<GitHub Actions run number>` for both build and publish.
A successful push to `master` publishes the corresponding `v1.0.<run number>`
release. Rerunning the same workflow run reuses that version and release tag.
Pull requests and manual runs validate and upload artifacts without publishing
a release, so published version numbers can have gaps. Pushing a tag does not
start the workflow. Local builds default to `1.0.0` from `Directory.Build.props`;
use `-p:Version=1.0.123` to test a particular version locally.

## Manual terminal checks

Use the published executables in Windows Terminal:

- Pair using a verified certificate, connect with the configured default, then a
  selected profile; verify the initial directory and environment.
- Run Codex, PowerShell, Neovim, and Yazi where installed. Check typing, redraw,
  cursor visibility, Unicode, colors, alternate screen, resize, Ctrl+C, and scrolling.
- Verify `🛜` remains in the window title and Alt+Tab entry as applications change
  titles; verify restoration after normal shell exit and connection failure.
- Compare normal adaptive batching and explicit zero delay. Interrupt a
  synchronized update and confirm local output resumes.
- Check standalone upload/download and remote `--send`, including interruption.
- Close a client and stop the broker with active shells; verify process cleanup.
