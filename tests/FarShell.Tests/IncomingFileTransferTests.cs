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

    [Theory]
    [InlineData(".")]
    [InlineData("")]
    [InlineData("nested\\..")]
    [InlineData("file.txt:stream")]
    public void RejectsRootAndAlternateStreamDestinations(string path)
        => Assert.Throws<ProtocolException>(() => IncomingFileTransfer.Create(_root,
            new SessionFileStart(Guid.NewGuid(), 1, path)));

    [Fact]
    public async Task RejectsAJunctionWithoutReplacingTheOutsideFile()
    {
        var outside = Path.Combine(_root, "outside");
        var download = Path.Combine(_root, "download");
        Directory.CreateDirectory(outside);
        Directory.CreateDirectory(download);
        var target = Path.Combine(outside, "keep.txt");
        await File.WriteAllTextAsync(target, "original");
        var link = Path.Combine(download, "redirect");
        var start = new System.Diagnostics.ProcessStartInfo("pwsh.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-Command");
        start.ArgumentList.Add($"New-Item -ItemType Junction -Path '{link.Replace("'", "''")}' -Target '{outside.Replace("'", "''")}' -ErrorAction Stop | Out-Null");
        using var process = System.Diagnostics.Process.Start(start)!;
        await process.WaitForExitAsync();
        Assert.Equal(0, process.ExitCode);
        try
        {
            Assert.Throws<ProtocolException>(() => IncomingFileTransfer.Create(download,
                new SessionFileStart(Guid.NewGuid(), 1, @"redirect\keep.txt")));
            Assert.Equal("original", await File.ReadAllTextAsync(target));
            Assert.Single(Directory.GetFiles(outside));
        }
        finally
        {
            Directory.Delete(link);
        }
    }

    [Fact]
    public async Task HoldsDirectoriesUntilTheTransferHasBeenCleanedUp()
    {
        var directory = Path.Combine(_root, "nested");
        await using (var transfer = IncomingFileTransfer.Create(_root,
            new SessionFileStart(Guid.NewGuid(), 1, @"nested\file.txt")))
        {
            Assert.Throws<IOException>(() => Directory.Move(directory, directory + "-moved"));
            await transfer.WriteAsync(new byte[] { 42 }, CancellationToken.None);
            await transfer.CompleteAsync(CancellationToken.None);
        }

        Directory.Move(directory, directory + "-moved");
    }

    public Task DisposeAsync()
    {
        Directory.Delete(_root, recursive: true);
        return Task.CompletedTask;
    }
}
