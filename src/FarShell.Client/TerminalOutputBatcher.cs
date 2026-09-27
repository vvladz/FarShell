using System.Threading.Channels;

namespace FarShell.Client;

internal sealed class TerminalOutputBatcher : IAsyncDisposable
{
    private const int ChannelCapacity = 16;
    private const int MaximumBatchBytes = 64 * 1024;

    private readonly Stream _output;
    private readonly TimeSpan _batchDelay;
    private readonly Func<TimeSpan, Task> _delayAsync;
    private readonly Channel<byte[]> _payloads;
    private readonly Task _pump;
    private readonly TimeProvider _time;
    private readonly SynchronizedOutputTracker _synchronization = new();
    private long _lastInput;
    private bool _inputSeen;
    private TaskCompletionSource _inputArrived = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _disposed;

    internal TerminalOutputBatcher(
        Stream output,
        TimeSpan batchDelay,
        Func<TimeSpan, Task>? delayAsync = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(output);
        if (batchDelay < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(batchDelay));
        }

        _output = output;
        _batchDelay = batchDelay;
        _delayAsync = delayAsync ?? Task.Delay;
        _time = timeProvider ?? TimeProvider.System;
        _payloads = Channel.CreateBounded<byte[]>(
            new BoundedChannelOptions(ChannelCapacity)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = true,
            });
        _pump = PumpAsync();
    }

    internal TimeSpan CurrentDelay => _batchDelay == TimeSpan.Zero ? TimeSpan.Zero
        : Volatile.Read(ref _inputSeen) && _time.GetElapsedTime(Volatile.Read(ref _lastInput)) <= TimeSpan.FromMilliseconds(250)
            ? TimeSpan.FromMilliseconds(Math.Min(1, _batchDelay.TotalMilliseconds)) : _batchDelay;

    internal void NotifyInput()
    {
        Volatile.Write(ref _lastInput, _time.GetTimestamp());
        Volatile.Write(ref _inputSeen, true);
        Volatile.Read(ref _inputArrived).TrySetResult();
    }

    internal async ValueTask WriteAsync(
        byte[] payload,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(payload);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (payload.Length == 0)
        {
            return;
        }

        await _payloads.Writer.WriteAsync(payload, cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _payloads.Writer.TryComplete();
        }

        await _pump;
    }

    private async Task PumpAsync()
    {
        using var buffer = new MemoryStream(MaximumBatchBytes);

        try
        {
            while (await _payloads.Reader.WaitToReadAsync())
            {
                if (!_payloads.Reader.TryRead(out var payload))
                {
                    continue;
                }

                await AppendAsync(buffer, payload);
                if (buffer.Length == 0) { continue; }
                if (CurrentDelay == TimeSpan.Zero)
                {
                    await FlushAsync(buffer);
                    continue;
                }

                await CollectBatchAsync(buffer);
            }

            await FlushAsync(buffer);
            if (_synchronization.IsActive)
            {
                if (_synchronization.IsInsideString)
                {
                    // End a truncated OSC/DCS before resetting synchronized output.
                    await _output.WriteAsync(new byte[] { 24 });
                }
                await _output.WriteAsync("\u001b[?2026l"u8.ToArray());
                await _output.FlushAsync();
            }
        }
        catch (Exception exception)
        {
            _payloads.Writer.TryComplete(exception);
            throw;
        }
    }

    private async Task CollectBatchAsync(MemoryStream buffer)
    {
        var delayTask = _delayAsync(CurrentDelay);
        var input = Volatile.Read(ref _inputArrived);
        if (input.Task.IsCompleted)
        {
            var next = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Interlocked.CompareExchange(ref _inputArrived, next, input);
            input = Volatile.Read(ref _inputArrived);
        }

        while (true)
        {
            while (_payloads.Reader.TryRead(out var payload))
            {
                await AppendAsync(buffer, payload);
                if (buffer.Length == 0) { return; }
            }

            if (delayTask.IsCompleted)
            {
                await delayTask;
                await FlushAsync(buffer);
                return;
            }

            var dataAvailableTask = _payloads.Reader.WaitToReadAsync().AsTask();
            var ready = await Task.WhenAny(delayTask, dataAvailableTask, input.Task);
            if (ready == input.Task)
            {
                await FlushAsync(buffer);
                return;
            }
            if (ready == delayTask)
            {
                await delayTask;
                await FlushAsync(buffer);
                return;
            }

            if (!await dataAvailableTask)
            {
                await FlushAsync(buffer);
                return;
            }
        }
    }

    private async Task AppendAsync(MemoryStream buffer, byte[] payload)
    {
        var start = 0;
        for (var index = 0; index < payload.Length; index++)
        {
            var marker = _synchronization.Observe(payload[index]);
            if (marker || buffer.Length + index - start + 1 >= MaximumBatchBytes)
            {
                buffer.Write(payload, start, index - start + 1);
                await FlushAsync(buffer);
                start = index + 1;
            }
        }

        buffer.Write(payload, start, payload.Length - start);
        if (_synchronization.IsActive || CurrentDelay == TimeSpan.Zero) { await FlushAsync(buffer); }
    }

    private async Task FlushAsync(MemoryStream buffer)
    {
        if (buffer.Length == 0)
        {
            return;
        }

        await _output.WriteAsync(
            buffer.GetBuffer().AsMemory(0, checked((int)buffer.Length)));
        await _output.FlushAsync();
        buffer.SetLength(0);
    }
}
