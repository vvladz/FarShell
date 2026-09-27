using System.Text;
using FarShell.Client;

namespace FarShell.Tests;

public sealed class TerminalStreamTests
{
    [Theory]
    [InlineData("0", "\a")]
    [InlineData("2", "\a")]
    [InlineData("0", "\u001b\\")]
    [InlineData("2", "\u001b\\")]
    public void RewritesOnlyTitlesAtEveryPacketBoundary(string command, string terminator)
    {
        var input = Encoding.UTF8.GetBytes($"text\u001b[31m\u001b]{command};PowerShell — рабочая{terminator}\u001b[0mend");
        var expected = Encoding.UTF8.GetBytes($"text\u001b[31m\u001b]{command};🛜 PowerShell — рабочая{terminator}\u001b[0mend");
        for (var split = 0; split <= input.Length; split++)
        {
            var rewriter = new TerminalTitleRewriter();
            var actual = rewriter.Transform(input.AsSpan(0, split))
                .Concat(rewriter.Transform(input.AsSpan(split))).Concat(rewriter.Complete()).ToArray();
            Assert.Equal(expected, actual);
        }
    }

    [Theory]
    [InlineData("plain\u001b[?2026h\u001b[1;1Hframe\u001b[?2026l")]
    [InlineData("\u001b]52;c;data\a")]
    [InlineData("\u001b]1;icon\u001b\\")]
    [InlineData("\u001b]0;unfinished")]
    [InlineData("\u001b]2;malformed\u001b[31m\a")]
    [InlineData("\u001bPdata\u001b]2;not-a-title\a\u001b\\")]
    public void PreservesUnrelatedMalformedAndIncompleteStreams(string input)
    {
        var rewriter = new TerminalTitleRewriter();
        using var output = new MemoryStream();
        foreach (var value in Encoding.UTF8.GetBytes(input)) { output.Write(rewriter.Transform(new[] { value })); }
        output.Write(rewriter.Complete());
        Assert.Equal(Encoding.UTF8.GetBytes(input), output.ToArray());
    }

    [Fact]
    public void OverlongTitlesPassThroughWithoutUnboundedBuffering()
    {
        var rewriter = new TerminalTitleRewriter();
        var input = Encoding.UTF8.GetBytes("\u001b]2;" + new string('x', TerminalTitleRewriter.MaximumTitleBytes * 3) + "\a");
        var output = rewriter.Transform(input);
        Assert.Equal(input, output);
        Assert.Empty(rewriter.Complete());
    }

    [Fact]
    public async Task SynchronizedFramesBypassAnOutstandingDelayAtEveryBoundary()
    {
        var bytes = "before\u001b[?2026hframe\u001b[?2026l"u8.ToArray();
        for (var split = 0; split <= bytes.Length; split++)
        {
            var output = new MemoryStream();
            var delay = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await using (var batcher = new TerminalOutputBatcher(output, TimeSpan.FromMilliseconds(4), _ => delay.Task))
            {
                await batcher.WriteAsync(bytes[..split]);
                await batcher.WriteAsync(bytes[split..]);
                for (var attempt = 0; attempt < 100 && output.Length != bytes.Length; attempt++) { await Task.Delay(5); }
                Assert.Equal(bytes, output.ToArray());
            }
        }
    }

    [Fact]
    public async Task UnclosedSynchronizationIsRestoredOnDisposal()
    {
        var output = new MemoryStream();
        await using (var batcher = new TerminalOutputBatcher(output, TimeSpan.Zero))
        {
            await batcher.WriteAsync("\u001b[?2026hframe"u8.ToArray());
        }
        Assert.Equal("\u001b[?2026hframe\u001b[?2026l", Encoding.UTF8.GetString(output.ToArray()));
    }

    [Fact]
    public void ModeMarkersInsideStringsDoNotChangeSynchronization()
    {
        var tracker = new SynchronizedOutputTracker();
        foreach (var value in "\u001b]2;\u001b[?2026h\a\u001bP\u001b[?2026h\u001b\\"u8)
        {
            Assert.False(tracker.Observe(value));
        }
        Assert.False(tracker.IsActive);
    }

    [Fact]
    public async Task TruncatedControlStringCannotSwallowSynchronizationRecovery()
    {
        var output = new MemoryStream();
        await using (var batcher = new TerminalOutputBatcher(output, TimeSpan.Zero))
        {
            await batcher.WriteAsync("\u001b[?2026hframe\u001b]2;unfinished"u8.ToArray());
        }
        Assert.Equal("\u001b[?2026hframe\u001b]2;unfinished\u0018\u001b[?2026l", Encoding.UTF8.GetString(output.ToArray()));
    }

    [Fact]
    public void CancelledControlStringsAllowSubsequentTitlesAndModeMarkers()
    {
        var rewriter = new TerminalTitleRewriter();
        var tracker = new SynchronizedOutputTracker();
        var input = "\u001b]2;cancelled\u0018\u001b]2;valid\a\u001bPcancelled\u001a\u001b[?2026h"u8.ToArray();
        Assert.Equal("\u001b]2;cancelled\u0018\u001b]2;🛜 valid\a\u001bPcancelled\u001a\u001b[?2026h", Encoding.UTF8.GetString(rewriter.Transform(input)));
        foreach (var value in input) { tracker.Observe(value); }
        Assert.True(tracker.IsActive);
    }

    [Fact]
    public async Task InputRefreshesTheAdaptiveWindowAndZeroRemainsImmediate()
    {
        var time = new TestTime();
        await using var adaptive = new TerminalOutputBatcher(new MemoryStream(), TimeSpan.FromMilliseconds(4), timeProvider: time);
        await using var immediate = new TerminalOutputBatcher(new MemoryStream(), TimeSpan.Zero, timeProvider: time);
        Assert.Equal(TimeSpan.FromMilliseconds(4), adaptive.CurrentDelay);
        adaptive.NotifyInput();
        immediate.NotifyInput();
        Assert.Equal(TimeSpan.FromMilliseconds(1), adaptive.CurrentDelay);
        Assert.Equal(TimeSpan.Zero, immediate.CurrentDelay);
        time.Timestamp = 200;
        adaptive.NotifyInput();
        time.Timestamp = 400;
        Assert.Equal(TimeSpan.FromMilliseconds(1), adaptive.CurrentDelay);
        time.Timestamp = 451;
        Assert.Equal(TimeSpan.FromMilliseconds(4), adaptive.CurrentDelay);
    }

    private sealed class TestTime : TimeProvider
    {
        internal long Timestamp { get; set; }
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => Timestamp;
    }
}
