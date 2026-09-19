using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace FarShell.ConPTY;

[SupportedOSPlatform("windows10.0.17763")]
public sealed class ConPtySession : IDisposable
{
    private readonly object _stateLock = new();
    private readonly SafePseudoConsoleHandle _pseudoConsole;
    private readonly SafeFileHandle _process;
    private readonly SafeFileHandle _job;
    private readonly Task<int> _exitTask;
    private bool _terminated;
    private bool _disposed;

    private ConPtySession(
        SafePseudoConsoleHandle pseudoConsole,
        SafeFileHandle process,
        SafeFileHandle job,
        Stream input,
        Stream output)
    {
        _pseudoConsole = pseudoConsole;
        _process = process;
        _job = job;
        Input = input;
        Output = output;
        _exitTask = WaitForExitCoreAsync();
    }

    public Stream Input { get; }

    public Stream Output { get; }

    public static ConPtySession Start(
        int columns,
        int rows,
        string commandLine = "pwsh.exe -NoLogo",
        string? currentDirectory = null)
    {
        var size = ToCoord(columns, rows);

        if (!NativeMethods.CreatePipe(out var pseudoConsoleInput, out var hostInput, IntPtr.Zero, 0))
        {
            throw new Win32Exception();
        }

        if (!NativeMethods.CreatePipe(out var hostOutput, out var pseudoConsoleOutput, IntPtr.Zero, 0))
        {
            pseudoConsoleInput.Dispose();
            hostInput.Dispose();
            throw new Win32Exception();
        }

        SafePseudoConsoleHandle? pseudoConsole = null;
        SafeFileHandle? process = null;
        SafeFileHandle? job = null;
        Stream? input = null;
        Stream? output = null;

        try
        {
            var result = NativeMethods.CreatePseudoConsole(
                size,
                pseudoConsoleInput.DangerousGetHandle(),
                pseudoConsoleOutput.DangerousGetHandle(),
                0,
                out var pseudoConsoleHandle);
            ThrowIfFailed(result);

            pseudoConsole = new SafePseudoConsoleHandle(pseudoConsoleHandle);
            pseudoConsoleInput.Dispose();
            pseudoConsoleOutput.Dispose();

            (process, job) = StartProcess(pseudoConsole, commandLine, currentDirectory);
            input = new FileStream(hostInput, FileAccess.Write, bufferSize: 4096, isAsync: false);
            output = new FileStream(hostOutput, FileAccess.Read, bufferSize: 4096, isAsync: false);

            return new ConPtySession(pseudoConsole, process, job, input, output);
        }
        catch
        {
            input?.Dispose();
            output?.Dispose();
            hostInput.Dispose();
            hostOutput.Dispose();
            process?.Dispose();
            job?.Dispose();
            pseudoConsole?.Dispose();
            pseudoConsoleInput.Dispose();
            pseudoConsoleOutput.Dispose();
            throw;
        }
    }

    public void Resize(int columns, int rows)
    {
        lock (_stateLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ObjectDisposedException.ThrowIf(_pseudoConsole.IsClosed, this);
            var result = NativeMethods.ResizePseudoConsole(
                _pseudoConsole.DangerousGetHandle(),
                ToCoord(columns, rows));
            ThrowIfFailed(result);
        }
    }

    public Task<int> WaitForExitAsync()
    {
        return _exitTask;
    }

    public void ClosePseudoConsole()
    {
        lock (_stateLock)
        {
            if (!_pseudoConsole.IsClosed)
            {
                _pseudoConsole.Dispose();
            }
        }
    }

    public void Terminate()
    {
        lock (_stateLock)
        {
            TerminateCore();
        }
    }

    public void Dispose()
    {
        lock (_stateLock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            TerminateCore();

            Input.Dispose();
            Output.Dispose();
            _process.Dispose();
            _job.Dispose();
        }
    }

    private Task<int> WaitForExitCoreAsync()
    {
        var referenceAdded = false;
        _process.DangerousAddRef(ref referenceAdded);

        try
        {
            return Task.Run(() =>
            {
                try
                {
                    var waitResult = NativeMethods.WaitForSingleObject(
                        _process.DangerousGetHandle(),
                        NativeMethods.Infinite);
                    if (waitResult == NativeMethods.WaitFailed)
                    {
                        throw new Win32Exception();
                    }

                    if (!NativeMethods.GetExitCodeProcess(
                            _process.DangerousGetHandle(),
                            out var exitCode))
                    {
                        throw new Win32Exception();
                    }

                    return unchecked((int)exitCode);
                }
                finally
                {
                    ClosePseudoConsole();
                    if (referenceAdded)
                    {
                        _process.DangerousRelease();
                    }
                }
            });
        }
        catch
        {
            if (referenceAdded)
            {
                _process.DangerousRelease();
            }

            throw;
        }
    }

    private void TerminateCore()
    {
        if (_terminated)
        {
            return;
        }

        _terminated = true;
        _job.Dispose();
        if (!_pseudoConsole.IsClosed)
        {
            _pseudoConsole.Dispose();
        }
    }

    private static (SafeFileHandle Process, SafeFileHandle Job) StartProcess(
        SafePseudoConsoleHandle pseudoConsole,
        string commandLine,
        string? currentDirectory)
    {
        nuint attributeListSize = 0;
        _ = NativeMethods.InitializeProcThreadAttributeList(
            IntPtr.Zero,
            1,
            0,
            ref attributeListSize);

        if (attributeListSize == 0)
        {
            throw new Win32Exception();
        }

        var job = CreateKillOnCloseJob();
        SafeFileHandle? process = null;
        var attributeList = IntPtr.Zero;
        var attributeListInitialized = false;
        try
        {
            attributeList = Marshal.AllocHGlobal(checked((nint)attributeListSize));
            if (!NativeMethods.InitializeProcThreadAttributeList(
                    attributeList,
                    1,
                    0,
                    ref attributeListSize))
            {
                throw new Win32Exception();
            }

            attributeListInitialized = true;

            if (!NativeMethods.UpdateProcThreadAttribute(
                    attributeList,
                    0,
                    (IntPtr)NativeMethods.ProcThreadAttributePseudoConsole,
                    pseudoConsole.DangerousGetHandle(),
                    (nuint)IntPtr.Size,
                    IntPtr.Zero,
                    IntPtr.Zero))
            {
                throw new Win32Exception();
            }

            var startupInfo = new StartupInfoEx
            {
                StartupInfo = new StartupInfo
                {
                    Size = Marshal.SizeOf<StartupInfoEx>(),
                },
                AttributeList = attributeList,
            };

            var mutableCommandLine = new StringBuilder(commandLine);
            if (!NativeMethods.CreateProcessW(
                    null,
                    mutableCommandLine,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    inheritHandles: false,
                    NativeMethods.ExtendedStartupInfoPresent
                        | NativeMethods.CreateUnicodeEnvironment
                        | NativeMethods.CreateSuspended,
                    IntPtr.Zero,
                    currentDirectory,
                    ref startupInfo,
                    out var processInformation))
            {
                throw new Win32Exception();
            }

            using var thread = new SafeFileHandle(processInformation.Thread, ownsHandle: true);
            process = new SafeFileHandle(processInformation.Process, ownsHandle: true);

            if (!NativeMethods.AssignProcessToJobObject(job, process))
            {
                throw new Win32Exception();
            }

            if (NativeMethods.ResumeThread(thread.DangerousGetHandle())
                == NativeMethods.ResumeThreadFailed)
            {
                throw new Win32Exception();
            }

            return (process, job);
        }
        catch
        {
            if (process is not null && !process.IsInvalid)
            {
                _ = NativeMethods.TerminateProcess(process.DangerousGetHandle(), 1);
            }

            process?.Dispose();
            job.Dispose();
            throw;
        }
        finally
        {
            if (attributeListInitialized)
            {
                NativeMethods.DeleteProcThreadAttributeList(attributeList);
            }

            if (attributeList != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(attributeList);
            }
        }
    }

    private static SafeFileHandle CreateKillOnCloseJob()
    {
        var job = NativeMethods.CreateJobObjectW(IntPtr.Zero, null);
        if (job.IsInvalid)
        {
            job.Dispose();
            throw new Win32Exception();
        }

        var information = new JobObjectExtendedLimitInformation
        {
            BasicLimitInformation = new JobObjectBasicLimitInformation
            {
                LimitFlags = NativeMethods.JobObjectLimitKillOnJobClose,
            },
        };

        if (!NativeMethods.SetInformationJobObject(
                job,
                NativeMethods.JobObjectExtendedLimitInformation,
                ref information,
                checked((uint)Marshal.SizeOf<JobObjectExtendedLimitInformation>())))
        {
            job.Dispose();
            throw new Win32Exception();
        }

        return job;
    }

    private static Coord ToCoord(int columns, int rows)
    {
        if (columns is < 1 or > short.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(columns));
        }

        if (rows is < 1 or > short.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(rows));
        }

        return new Coord((short)columns, (short)rows);
    }

    private static void ThrowIfFailed(int result)
    {
        if (result != 0)
        {
            Marshal.ThrowExceptionForHR(result);
        }
    }
}
