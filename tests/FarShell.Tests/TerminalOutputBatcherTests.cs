using System.Text;
using FarShell.Client;

namespace FarShell.Tests;

public sealed class TerminalOutputBatcherTests
{
    [Fact]
    public async Task CoalescesPendingOutputWithoutChangingBytes()
    {
        var output = new RecordingStream();
        var delayStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseDelay = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await using (var batcher = new TerminalOutputBatcher(
            output,
            TimeSpan.FromMilliseconds(4),
            _ =>
            {
                delayStarted.TrySetResult();
                return releaseDelay.Task;
            }))
        {
            await batcher.WriteAsync(Encoding.UTF8.GetBytes("first"));
            await delayStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
            await batcher.WriteAsync(Encoding.UTF8.GetBytes("-second"));
        }

        Assert.Single(output.Writes);
        Assert.Equal("first-second", Encoding.UTF8.GetString(output.Writes[0]));
        Assert.Equal(1, output.FlushCount);
    }

    [Fact]
    public async Task FlushesOutputWhenBatchDelayExpires()
    {
        var output = new RecordingStream();
        var releaseDelay = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var batcher = new TerminalOutputBatcher(
            output,
            TimeSpan.FromMilliseconds(4),
            _ => releaseDelay.Task);

        await batcher.WriteAsync(Encoding.UTF8.GetBytes("frame"));
        releaseDelay.TrySetResult();
        await output.FirstFlush.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Single(output.Writes);
        Assert.Equal("frame", Encoding.UTF8.GetString(output.Writes[0]));
        Assert.Equal(1, output.FlushCount);
    }

    [Fact]
    public async Task ZeroDelayFlushesEveryPayloadImmediately()
    {
        var output = new RecordingStream();

        await using (var batcher = new TerminalOutputBatcher(output, TimeSpan.Zero))
        {
            await batcher.WriteAsync(Encoding.UTF8.GetBytes("first"));
            await batcher.WriteAsync(Encoding.UTF8.GetBytes("second"));
        }

        Assert.Equal(2, output.Writes.Count);
        Assert.Equal("first", Encoding.UTF8.GetString(output.Writes[0]));
        Assert.Equal("second", Encoding.UTF8.GetString(output.Writes[1]));
        Assert.Equal(2, output.FlushCount);
    }

    private sealed class RecordingStream : MemoryStream
    {
        private readonly TaskCompletionSource _firstFlush = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        internal List<byte[]> Writes { get; } = [];

        internal int FlushCount { get; private set; }

        internal Task FirstFlush => _firstFlush.Task;

        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            Writes.Add(buffer.ToArray());
            return ValueTask.CompletedTask;
        }

        public override Task FlushAsync(CancellationToken cancellationToken)
        {
            FlushCount++;
            _firstFlush.TrySetResult();
            return Task.CompletedTask;
        }
    }
}
