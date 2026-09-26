using System.ComponentModel;
using System.Runtime.InteropServices;
using FarShell.Protocol;

namespace FarShell.Client;

internal sealed class ConsoleInputPump : IDisposable
{
    private const int StandardInputHandle = -10;
    private const int ErrorOperationAborted = 995;
    private const int BufferSize = 4096;
    private readonly FrameWriter _writer;
    private readonly IntPtr _inputHandle;
    private readonly CancellationTokenSource _stop = new();
    private readonly CancellationToken _stopToken;
    private readonly ManualResetEventSlim _start = new();
    private readonly TaskCompletionSource<bool> _ready = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<bool> _completion = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Thread _thread;
    private ConsoleModeScope? _inputMode;
    private int _disposed;

    internal ConsoleInputPump(FrameWriter writer)
    {
        _writer = writer;
        _stopToken = _stop.Token;
        _inputHandle = NativeMethods.GetStdHandle(StandardInputHandle);
        if (_inputHandle == IntPtr.Zero || _inputHandle == new IntPtr(-1))
        {
            throw new Win32Exception();
        }

        _thread = new Thread(Run)
        {
            IsBackground = true,
            Name = "FarShell console input",
        };
        _thread.Start();
    }

    internal Task Ready => _ready.Task;

    internal Task Completion => _completion.Task;

    internal void StartForwarding() => _start.Set();

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _stop.Cancel();
        _start.Set();
        // Cancellation can race with the next ReadFile call, so retry until the
        // input thread has observed it. A bounded wait keeps disconnect responsive.
        for (var attempt = 0; attempt < 20 && _thread.IsAlive; attempt++)
        {
            _ = NativeMethods.CancelIoEx(_inputHandle, IntPtr.Zero);
            if (_thread.Join(50))
            {
                break;
            }
        }

        // Restore the original input mode even if the console read did not stop.
        Volatile.Read(ref _inputMode)?.Dispose();
        if (!_thread.IsAlive)
        {
            _start.Dispose();
            _stop.Dispose();
        }
    }

    private void Run()
    {
        try
        {
            using var inputMode = ConsoleModeScope.EnableRawVirtualTerminalInputMode();
            Volatile.Write(ref _inputMode, inputMode);
            _ready.TrySetResult(true);
            _start.Wait(_stopToken);

            var buffer = new byte[BufferSize];
            while (!_stopToken.IsCancellationRequested)
            {
                if (!NativeMethods.ReadFile(
                        _inputHandle,
                        buffer,
                        (uint)buffer.Length,
                        out var bytesRead,
                        IntPtr.Zero))
                {
                    var error = Marshal.GetLastWin32Error();
                    if (_stopToken.IsCancellationRequested && error == ErrorOperationAborted)
                    {
                        break;
                    }

                    throw new Win32Exception(error);
                }

                if (bytesRead == 0 || _stopToken.IsCancellationRequested)
                {
                    break;
                }

                _writer.WriteAsync(
                        MessageType.DataIn,
                        buffer.AsMemory(0, (int)bytesRead),
                        _stopToken)
                    .AsTask()
                    .GetAwaiter()
                    .GetResult();
            }

            _completion.TrySetResult(true);
        }
        catch (OperationCanceledException) when (_stopToken.IsCancellationRequested)
        {
            _completion.TrySetResult(true);
        }
        catch (Exception exception)
        {
            _ready.TrySetException(exception);
            _completion.TrySetException(exception);
        }
    }

    private static class NativeMethods
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern IntPtr GetStdHandle(int standardHandle);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool ReadFile(
            IntPtr handle,
            [Out] byte[] buffer,
            uint bytesToRead,
            out uint bytesRead,
            IntPtr overlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CancelIoEx(IntPtr handle, IntPtr overlapped);
    }
}
