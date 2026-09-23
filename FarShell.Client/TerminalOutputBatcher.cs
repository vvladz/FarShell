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
    private int _disposed;

    internal TerminalOutputBatcher(
        Stream output,
        TimeSpan batchDelay,
        Func<TimeSpan, Task>? delayAsync = null)
    {
        ArgumentNullException.ThrowIfNull(output);
        if (batchDelay < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(batchDelay));
        }

        _output = output;
        _batchDelay = batchDelay;
        _delayAsync = delayAsync ?? Task.Delay;
        _payloads = Channel.CreateBounded<byte[]>(
            new BoundedChannelOptions(ChannelCapacity)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = true,
            });
        _pump = PumpAsync();
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

                buffer.Write(payload);
                if (_batchDelay == TimeSpan.Zero || buffer.Length >= MaximumBatchBytes)
                {
                    await FlushAsync(buffer);
                    continue;
                }

                await CollectBatchAsync(buffer);
            }

            await FlushAsync(buffer);
        }
        catch (Exception exception)
        {
            _payloads.Writer.TryComplete(exception);
            throw;
        }
    }

    private async Task CollectBatchAsync(MemoryStream buffer)
    {
        var delayTask = _delayAsync(_batchDelay);

        while (true)
        {
            while (_payloads.Reader.TryRead(out var payload))
            {
                buffer.Write(payload);
                if (buffer.Length >= MaximumBatchBytes)
                {
                    await FlushAsync(buffer);
                    return;
                }
            }

            if (delayTask.IsCompleted)
            {
                await delayTask;
                await FlushAsync(buffer);
                return;
            }

            var dataAvailableTask = _payloads.Reader.WaitToReadAsync().AsTask();
            if (await Task.WhenAny(delayTask, dataAvailableTask) == delayTask)
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
