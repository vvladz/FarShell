using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace FarShell.ConPTY;

internal static class NativeMethods
{
    internal const uint ExtendedStartupInfoPresent = 0x00080000;
    internal const uint CreateUnicodeEnvironment = 0x00000400;
    internal const uint CreateSuspended = 0x00000004;
    internal const int ProcThreadAttributePseudoConsole = 0x00020016;
    internal const int JobObjectExtendedLimitInformation = 9;
    internal const uint JobObjectLimitKillOnJobClose = 0x00002000;
    internal const uint Infinite = 0xFFFFFFFF;
    internal const uint WaitFailed = 0xFFFFFFFF;
    internal const uint ResumeThreadFailed = 0xFFFFFFFF;

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CreatePipe(
        out SafeFileHandle readPipe,
        out SafeFileHandle writePipe,
        IntPtr pipeAttributes,
        int size);

    [DllImport("kernel32.dll")]
    internal static extern int CreatePseudoConsole(
        Coord size,
        IntPtr input,
        IntPtr output,
        uint flags,
        out IntPtr pseudoConsole);

    [DllImport("kernel32.dll")]
    internal static extern int ResizePseudoConsole(IntPtr pseudoConsole, Coord size);

    [DllImport("kernel32.dll")]
    internal static extern void ClosePseudoConsole(IntPtr pseudoConsole);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool InitializeProcThreadAttributeList(
        IntPtr attributeList,
        int attributeCount,
        int flags,
        ref nuint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool UpdateProcThreadAttribute(
        IntPtr attributeList,
        uint flags,
        IntPtr attribute,
        IntPtr value,
        nuint size,
        IntPtr previousValue,
        IntPtr returnSize);

    [DllImport("kernel32.dll")]
    internal static extern void DeleteProcThreadAttributeList(IntPtr attributeList);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CreateProcessW(
        string? applicationName,
        StringBuilder commandLine,
        IntPtr processAttributes,
        IntPtr threadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandles,
        uint creationFlags,
        IntPtr environment,
        string? currentDirectory,
        ref StartupInfoEx startupInfo,
        out ProcessInformation processInformation);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern SafeFileHandle CreateJobObjectW(
        IntPtr jobAttributes,
        string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetInformationJobObject(
        SafeFileHandle job,
        int informationClass,
        ref JobObjectExtendedLimitInformation information,
        uint informationLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool AssignProcessToJobObject(
        SafeFileHandle job,
        SafeFileHandle process);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern uint ResumeThread(IntPtr thread);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetExitCodeProcess(IntPtr process, out uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool TerminateProcess(IntPtr process, uint exitCode);
}

[StructLayout(LayoutKind.Sequential)]
internal readonly struct Coord
{
    internal Coord(short columns, short rows)
    {
        X = columns;
        Y = rows;
    }

    internal readonly short X;

    internal readonly short Y;
}

[StructLayout(LayoutKind.Sequential)]
internal struct StartupInfo
{
    internal int Size;
    internal IntPtr Reserved;
    internal IntPtr Desktop;
    internal IntPtr Title;
    internal int X;
    internal int Y;
    internal int XSize;
    internal int YSize;
    internal int XCountChars;
    internal int YCountChars;
    internal int FillAttribute;
    internal int Flags;
    internal short ShowWindow;
    internal short Reserved2Size;
    internal IntPtr Reserved2;
    internal IntPtr StandardInput;
    internal IntPtr StandardOutput;
    internal IntPtr StandardError;
}

[StructLayout(LayoutKind.Sequential)]
internal struct StartupInfoEx
{
    internal StartupInfo StartupInfo;
    internal IntPtr AttributeList;
}

[StructLayout(LayoutKind.Sequential)]
internal struct ProcessInformation
{
    internal IntPtr Process;
    internal IntPtr Thread;
    internal uint ProcessId;
    internal uint ThreadId;
}

[StructLayout(LayoutKind.Sequential)]
internal struct JobObjectBasicLimitInformation
{
    internal long PerProcessUserTimeLimit;
    internal long PerJobUserTimeLimit;
    internal uint LimitFlags;
    internal UIntPtr MinimumWorkingSetSize;
    internal UIntPtr MaximumWorkingSetSize;
    internal uint ActiveProcessLimit;
    internal UIntPtr Affinity;
    internal uint PriorityClass;
    internal uint SchedulingClass;
}

[StructLayout(LayoutKind.Sequential)]
internal struct IoCounters
{
    internal ulong ReadOperationCount;
    internal ulong WriteOperationCount;
    internal ulong OtherOperationCount;
    internal ulong ReadTransferCount;
    internal ulong WriteTransferCount;
    internal ulong OtherTransferCount;
}

[StructLayout(LayoutKind.Sequential)]
internal struct JobObjectExtendedLimitInformation
{
    internal JobObjectBasicLimitInformation BasicLimitInformation;
    internal IoCounters IoInfo;
    internal UIntPtr ProcessMemoryLimit;
    internal UIntPtr JobMemoryLimit;
    internal UIntPtr PeakProcessMemoryUsed;
    internal UIntPtr PeakJobMemoryUsed;
}
