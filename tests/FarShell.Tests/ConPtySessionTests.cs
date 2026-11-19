using System.Diagnostics;
using System.Runtime.Versioning;
using FarShell.ConPTY;

namespace FarShell.Tests;

[SupportedOSPlatform("windows10.0.17763")]
public sealed class ConPtySessionTests
{
    [Theory(Timeout = 60_000)]
    [InlineData(0)]
    [InlineData(8192)]
    public async Task ExplicitBreakawayChildSurvivesSessionTermination(int startupOutputLines)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"farshell-breakaway-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var scriptPath = Path.Combine(directory, "launch.ps1");
        var childScriptPath = Path.Combine(directory, "child.ps1");
        var pidPath = Path.Combine(directory, "child.pid");
        var readyPath = Path.Combine(directory, "child.ready");
        var errorPath = Path.Combine(directory, "error.txt");
        await File.WriteAllTextAsync(childScriptPath, """
            $ErrorActionPreference = 'Stop'
            [IO.File]::WriteAllText($env:FARSHELL_BREAKAWAY_READY, 'ready')
            Start-Sleep -Seconds 120
            """);
        await File.WriteAllTextAsync(scriptPath, """
            $ErrorActionPreference = 'Stop'
            try {
                [Console]::Write(("startup output`r`n" * [int]$env:FARSHELL_STARTUP_OUTPUT_LINES))
                Add-Type -TypeDefinition @'
            using System;
            using System.Runtime.InteropServices;
            using System.Text;

            public static class BreakawayNative
            {
                [StructLayout(LayoutKind.Sequential)]
                public struct StartupInfo
                {
                    public int Size;
                    public IntPtr Reserved;
                    public IntPtr Desktop;
                    public IntPtr Title;
                    public int X;
                    public int Y;
                    public int XSize;
                    public int YSize;
                    public int XCountChars;
                    public int YCountChars;
                    public int FillAttribute;
                    public int Flags;
                    public short ShowWindow;
                    public short Reserved2Size;
                    public IntPtr Reserved2;
                    public IntPtr StandardInput;
                    public IntPtr StandardOutput;
                    public IntPtr StandardError;
                }

                [StructLayout(LayoutKind.Sequential)]
                public struct ProcessInformation
                {
                    public IntPtr Process;
                    public IntPtr Thread;
                    public uint ProcessId;
                    public uint ThreadId;
                }

                [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "CreateProcessW", SetLastError = true)]
                public static extern bool CreateProcess(string application, StringBuilder commandLine,
                    IntPtr processAttributes, IntPtr threadAttributes, bool inheritHandles,
                    uint creationFlags, IntPtr environment, string currentDirectory,
                    ref StartupInfo startupInfo, out ProcessInformation processInformation);

                [DllImport("kernel32.dll", SetLastError = true)]
                public static extern bool CloseHandle(IntPtr handle);

                [DllImport("kernel32.dll", SetLastError = true)]
                public static extern bool GetExitCodeProcess(IntPtr process, out uint exitCode);
            }
            '@
                $exe = (Get-Process -Id $PID).Path
                $commandLine = [Text.StringBuilder]::new(('"{0}" -NoLogo -NoProfile -NonInteractive -File "{1}"' -f $exe, $env:FARSHELL_BREAKAWAY_SCRIPT))
                $startupInfo = [BreakawayNative+StartupInfo]::new()
                $startupInfo.Size = [Runtime.InteropServices.Marshal]::SizeOf($startupInfo)
                $startupInfo.Flags = 1 # STARTF_USESHOWWINDOW; ShowWindow defaults to SW_HIDE.
                $processInfo = [BreakawayNative+ProcessInformation]::new()
                # CREATE_BREAKAWAY_FROM_JOB | CREATE_NEW_CONSOLE keeps PowerShell independent of ConPTY.
                $created = [BreakawayNative]::CreateProcess($exe, $commandLine, [IntPtr]::Zero, [IntPtr]::Zero, $false, [uint32]0x01000010, [IntPtr]::Zero, [Environment]::CurrentDirectory, [ref]$startupInfo, [ref]$processInfo)
                if (-not $created) {
                    throw [ComponentModel.Win32Exception]::new([Runtime.InteropServices.Marshal]::GetLastWin32Error())
                }
                [BreakawayNative]::CloseHandle($processInfo.Thread) | Out-Null
                [IO.File]::WriteAllText(($env:FARSHELL_BREAKAWAY_PID + '.tmp'), [string]$processInfo.ProcessId)
                [IO.File]::Move(($env:FARSHELL_BREAKAWAY_PID + '.tmp'), $env:FARSHELL_BREAKAWAY_PID)
                while ($true) {
                    $childExitCode = [uint32]0
                    if (-not [BreakawayNative]::GetExitCodeProcess($processInfo.Process, [ref]$childExitCode)) {
                        throw [ComponentModel.Win32Exception]::new([Runtime.InteropServices.Marshal]::GetLastWin32Error())
                    }
                    if ($childExitCode -ne 259) { throw "Breakaway child exited with code $childExitCode." }
                    Start-Sleep -Milliseconds 100
                }
            }
            catch {
                [IO.File]::WriteAllText($env:FARSHELL_BREAKAWAY_ERROR, "exe=$exe; command=$commandLine; size=$($startupInfo.Size); error=$($_.ToString()); stack=$($_.ScriptStackTrace)")
                exit 1
            }
            """);

        try
        {
            using var session = ConPtySession.Start(
                80, 24, $"pwsh.exe -NoLogo -NoProfile -NonInteractive -File \"{scriptPath}\"",
                environmentVariables: new Dictionary<string, string>
                {
                    ["FARSHELL_BREAKAWAY_PID"] = pidPath,
                    ["FARSHELL_BREAKAWAY_READY"] = readyPath,
                    ["FARSHELL_BREAKAWAY_SCRIPT"] = childScriptPath,
                    ["FARSHELL_BREAKAWAY_ERROR"] = errorPath,
                    ["FARSHELL_STARTUP_OUTPUT_LINES"] = startupOutputLines.ToString(),
                });

            // Drain ConPTY while waiting: startup output can fill its pipe and block the launcher.
            using var outputReader = new StreamReader(session.Output, leaveOpen: true);
            var outputTask = outputReader.ReadToEndAsync();
            var exitTask = session.WaitForExitAsync();
            try
            {
                var startup = Stopwatch.StartNew();
                var startupTimeout = TimeSpan.FromSeconds(30);
                while (startup.Elapsed < startupTimeout
                    && (!File.Exists(pidPath) || !File.Exists(readyPath))
                    && !File.Exists(errorPath) && !exitTask.IsCompleted)
                {
                    await Task.Delay(50);
                }

                if (!File.Exists(pidPath) || !File.Exists(readyPath))
                {
                    var exitedBeforeReady = exitTask.IsCompleted;
                    session.Terminate();
                    var exitCode = await exitTask.WaitAsync(TimeSpan.FromSeconds(5));
                    var output = await outputTask.WaitAsync(TimeSpan.FromSeconds(5));
                    if (output.Length > 4096) { output = output[^4096..]; }
                    var reason = File.Exists(errorPath) ? await File.ReadAllTextAsync(errorPath)
                        : exitedBeforeReady ? $"The launcher exited with code {exitCode} before creating the breakaway child."
                        : $"The breakaway child was not started within {startupTimeout.TotalSeconds} seconds.";
                    Assert.Fail($"{reason}{Environment.NewLine}ConPTY output:{Environment.NewLine}{output}");
                }

                using var child = Process.GetProcessById(int.Parse(await File.ReadAllTextAsync(pidPath)));
                session.Terminate();
                await exitTask.WaitAsync(TimeSpan.FromSeconds(5));
                child.Refresh();
                Assert.False(child.HasExited);
            }
            finally
            {
                session.Terminate();
                await exitTask.WaitAsync(TimeSpan.FromSeconds(5));
                await outputTask.WaitAsync(TimeSpan.FromSeconds(5));
            }
        }
        finally
        {
            if (File.Exists(pidPath)
                && int.TryParse(await File.ReadAllTextAsync(pidPath), out var childId))
            {
                try
                {
                    using var child = Process.GetProcessById(childId);
                    if (!child.HasExited)
                    {
                        child.Kill();
                        await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                    }
                }
                catch (ArgumentException) { }
            }

            Directory.Delete(directory, recursive: true);
        }
    }
}
