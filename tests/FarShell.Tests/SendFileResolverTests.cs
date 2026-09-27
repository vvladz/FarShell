using FarShell.Broker;
using FarShell.Protocol;

namespace FarShell.Tests;

public sealed class SendFileResolverTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"farshell-send-resolver-{Guid.NewGuid():N}");

    public SendFileResolverTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "results"));
        File.WriteAllText(Path.Combine(_root, "root.txt"), "root");
        File.WriteAllText(Path.Combine(_root, "results", "one.bin"), "one");
        File.WriteAllText(Path.Combine(_root, "results", "two.txt"), "two");
    }

    [Fact]
    public void ResolvesWildcardsRelativeToTheRemoteRoot()
    {
        var files = SendFileResolver.Resolve(
            new SendFilesRequest(_root, ["*.txt", @"results\*.bin"]));

        Assert.Collection(
            files,
            file => Assert.Equal("results\\one.bin", file.RelativePath),
            file => Assert.Equal("root.txt", file.RelativePath));
    }

    [Theory]
    [InlineData(@"..\outside.txt")]
    [InlineData(@"results\..\root.txt")]
    [InlineData(@"C:\absolute.txt")]
    public void RejectsPathsOutsideTheRemoteRoot(string path)
    {
        Assert.Throws<FileTransferException>(
            () => SendFileResolver.Resolve(new SendFilesRequest(_root, [path])));
    }

    [Fact]
    public void RejectsDirectories()
    {
        Assert.Throws<FileTransferException>(
            () => SendFileResolver.Resolve(new SendFilesRequest(_root, ["results"])));
    }

    public void Dispose()
    {
        Directory.Delete(_root, recursive: true);
    }
}
