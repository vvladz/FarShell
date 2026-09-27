using System.ComponentModel;
using System.Runtime.InteropServices;

namespace FarShell.Client;

internal sealed class ConsoleModeScope : IDisposable
{
    private const int StandardInputHandle = -10;
    private const int StandardOutputHandle = -11;
    private const uint Utf8CodePage = 65001;

    private const uint EnableProcessedInput = 0x0001;
    private const uint EnableLineInput = 0x0002;
    private const uint EnableEchoInput = 0x0004;
    private const uint EnableVirtualTerminalInput = 0x0200;

    private const uint EnableProcessedOutput = 0x0001;
    private const uint EnableVirtualTerminalProcessing = 0x0004;
    private const uint DisableNewlineAutoReturn = 0x0008;

    private readonly IntPtr _inputHandle;
    private readonly IntPtr _outputHandle;
    private readonly uint _inputMode;
    private readonly uint _outputMode;
    private readonly uint _inputCodePage;
    private readonly uint _outputCodePage;
    private readonly bool _hasInputConsole;
    private readonly bool _hasOutputConsole;
    private int _disposed;

    private ConsoleModeScope(
        IntPtr inputHandle,
        IntPtr outputHandle,
        uint inputMode,
        uint outputMode,
        uint inputCodePage,
        uint outputCodePage,
        bool hasInputConsole,
        bool hasOutputConsole)
    {
        _inputHandle = inputHandle;
        _outputHandle = outputHandle;
        _inputMode = inputMode;
        _outputMode = outputMode;
        _inputCodePage = inputCodePage;
        _outputCodePage = outputCodePage;
        _hasInputConsole = hasInputConsole;
        _hasOutputConsole = hasOutputConsole;
    }

    internal static ConsoleModeScope EnableRawVirtualTerminalInputMode() =>
        Enable(input: true, output: false);

    internal static ConsoleModeScope EnableVirtualTerminalOutputMode() =>
        Enable(input: false, output: true);

    private static ConsoleModeScope Enable(bool input, bool output)
    {
        var inputHandle = NativeMethods.GetStdHandle(StandardInputHandle);
        var outputHandle = NativeMethods.GetStdHandle(StandardOutputHandle);
        var inputMode = 0u;
        var outputMode = 0u;
        var hasInputConsole = input && NativeMethods.GetConsoleMode(inputHandle, out inputMode);
        var hasOutputConsole = output && NativeMethods.GetConsoleMode(outputHandle, out outputMode);

        var inputCodePage = hasInputConsole ? NativeMethods.GetConsoleCP() : 0;
        var outputCodePage = hasOutputConsole ? NativeMethods.GetConsoleOutputCP() : 0;

        var scope = new ConsoleModeScope(
            inputHandle,
            outputHandle,
            inputMode,
            outputMode,
            inputCodePage,
            outputCodePage,
            hasInputConsole,
            hasOutputConsole);

        try
        {
            if (hasInputConsole)
            {
                var rawInputMode = GetRawVirtualTerminalInputMode(inputMode);
                SetConsoleMode(inputHandle, rawInputMode);
                SetInputCodePage(Utf8CodePage);
            }

            if (hasOutputConsole)
            {
                var virtualTerminalOutputMode = outputMode
                    | EnableProcessedOutput
                    | EnableVirtualTerminalProcessing
                    | DisableNewlineAutoReturn;
                SetConsoleMode(outputHandle, virtualTerminalOutputMode);
                SetOutputCodePage(Utf8CodePage);
            }

            return scope;
        }
        catch
        {
            scope.Dispose();
            throw;
        }
    }

    internal static uint GetRawVirtualTerminalInputMode(uint inputMode)
    {
        inputMode &= ~(EnableProcessedInput | EnableLineInput | EnableEchoInput);
        return inputMode | EnableVirtualTerminalInput;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        if (_hasInputConsole)
        {
            _ = NativeMethods.SetConsoleMode(_inputHandle, _inputMode);
            if (_inputCodePage != 0)
            {
                _ = NativeMethods.SetConsoleCP(_inputCodePage);
            }
        }

        if (_hasOutputConsole)
        {
            _ = NativeMethods.SetConsoleMode(_outputHandle, _outputMode);
            if (_outputCodePage != 0)
            {
                _ = NativeMethods.SetConsoleOutputCP(_outputCodePage);
            }
        }
    }

    private static void SetConsoleMode(IntPtr handle, uint mode)
    {
        if (!NativeMethods.SetConsoleMode(handle, mode))
        {
            throw new Win32Exception();
        }
    }

    private static void SetInputCodePage(uint codePage)
    {
        if (!NativeMethods.SetConsoleCP(codePage))
        {
            throw new Win32Exception();
        }
    }

    private static void SetOutputCodePage(uint codePage)
    {
        if (!NativeMethods.SetConsoleOutputCP(codePage))
        {
            throw new Win32Exception();
        }
    }

    private static class NativeMethods
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern IntPtr GetStdHandle(int standardHandle);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetConsoleMode(IntPtr consoleHandle, out uint mode);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetConsoleMode(IntPtr consoleHandle, uint mode);

        [DllImport("kernel32.dll")]
        internal static extern uint GetConsoleCP();

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetConsoleCP(uint codePage);

        [DllImport("kernel32.dll")]
        internal static extern uint GetConsoleOutputCP();

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetConsoleOutputCP(uint codePage);
    }
}
