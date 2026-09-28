using System.Diagnostics;
using System.Runtime.Versioning;
using FarShell.ConPTY;

namespace FarShell.Tests;

[SupportedOSPlatform("windows10.0.17763")]
public sealed class ConPtySessionTests
{
    [Fact(Timeout = 25_000)]
    public async Task ExplicitBreakawayChildSurvivesSessionTermination()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"farshell-breakaway-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var scriptPath = Path.Combine(directory, "launch.ps1");
        var pidPath = Path.Combine(directory, "child.pid");
        var errorPath = Path.Combine(directory, "error.txt");
        await File.WriteAllTextAsync(scriptPath, """
            $ErrorActionPreference = 'Stop'
            try {
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
            }
            '@
                $exe = (Get-Process -Id $PID).Path
                $commandLine = [Text.StringBuilder]::new(('"{0}" -NoLogo -NoProfile -NonInteractive -Command "Start-Sleep -Seconds 120"' -f $exe))
                $startupInfo = [BreakawayNative+StartupInfo]::new()
                $startupInfo.Size = [Runtime.InteropServices.Marshal]::SizeOf($startupInfo)
                $processInfo = [BreakawayNative+ProcessInformation]::new()
                $created = [BreakawayNative]::CreateProcess($exe, $commandLine, [IntPtr]::Zero, [IntPtr]::Zero, $false, [uint32]0x01000008, [IntPtr]::Zero, [Environment]::CurrentDirectory, [ref]$startupInfo, [ref]$processInfo)
                if (-not $created) {
                    throw [ComponentModel.Win32Exception]::new([Runtime.InteropServices.Marshal]::GetLastWin32Error())
                }
                [BreakawayNative]::CloseHandle($processInfo.Thread) | Out-Null
                [BreakawayNative]::CloseHandle($processInfo.Process) | Out-Null
                [IO.File]::WriteAllText($env:FARSHELL_BREAKAWAY_PID, [string]$processInfo.ProcessId)
                while ($true) { Start-Sleep -Seconds 1 }
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
                    ["FARSHELL_BREAKAWAY_ERROR"] = errorPath,
                });

            for (var i = 0; i < 200 && !File.Exists(pidPath) && !File.Exists(errorPath); i++)
            {
                await Task.Delay(50);
            }

            Assert.True(File.Exists(pidPath), File.Exists(errorPath)
                ? await File.ReadAllTextAsync(errorPath)
                : "The breakaway child was not started within 10 seconds.");

            using var child = Process.GetProcessById(int.Parse(await File.ReadAllTextAsync(pidPath)));
            session.Terminate();
            await session.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            child.Refresh();
            Assert.False(child.HasExited);
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
