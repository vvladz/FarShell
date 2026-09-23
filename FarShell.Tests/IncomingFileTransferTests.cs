using FarShell.Client;
using FarShell.Protocol;

namespace FarShell.Tests;

public sealed class IncomingFileTransferTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"farshell-send-client-{Guid.NewGuid():N}");

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        return Task.CompletedTask;
    }

    [Fact]
    public async Task CommitsACompletedTransferUnderTheDownloadRoot()
    {
        var content = new byte[100_000];
        Random.Shared.NextBytes(content);
        var transfer = IncomingFileTransfer.Create(
            _root,
            new SessionFileStart(
                Guid.NewGuid(),
                content.Length,
                @"results\report.bin"));
        await using (transfer)
        {
            await transfer.WriteAsync(content.AsMemory(0, 40_000), CancellationToken.None);
            await transfer.WriteAsync(content.AsMemory(40_000), CancellationToken.None);
            await transfer.CompleteAsync(CancellationToken.None);
        }

        Assert.Equal(
            content,
            await File.ReadAllBytesAsync(
                Path.Combine(_root, "results", "report.bin")));
    }

    [Fact]
    public async Task InterruptedTransferPreservesAnExistingFile()
    {
        var destination = Path.Combine(_root, "report.txt");
        await File.WriteAllTextAsync(destination, "original");
        var transfer = IncomingFileTransfer.Create(
            _root,
            new SessionFileStart(Guid.NewGuid(), 10, "report.txt"));
        await transfer.WriteAsync(new byte[] { 1, 2, 3 }, CancellationToken.None);

        await transfer.DisposeAsync();

        Assert.Equal("original", await File.ReadAllTextAsync(destination));
        Assert.Empty(Directory.EnumerateFiles(_root, "*.farshell-send"));
    }

    [Fact]
    public void RejectsAPathOutsideTheDownloadRoot()
    {
        Assert.Throws<ProtocolException>(
            () => IncomingFileTransfer.Create(
                _root,
                new SessionFileStart(Guid.NewGuid(), 1, @"..\outside.bin")));
    }

    public Task DisposeAsync()
    {
        Directory.Delete(_root, recursive: true);
        return Task.CompletedTask;
    }
}
