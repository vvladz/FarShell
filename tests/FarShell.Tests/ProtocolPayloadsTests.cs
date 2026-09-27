using FarShell.Protocol;

namespace FarShell.Tests;

public sealed class ProtocolPayloadsTests
{
    [Fact]
    public void SessionExitRoundTrips()
    {
        var expected = new SessionExit(Guid.NewGuid(), -1);
        var decoded = ProtocolPayloads.DecodeSessionExit(
            ProtocolPayloads.EncodeSessionExit(expected.SessionId, expected.ExitCode));
        Assert.Equal(expected, decoded);
    }

    [Fact]
    public void TerminalSizeRoundTrips()
    {
        var expected = new TerminalSize(132, 43);
        var decoded = ProtocolPayloads.DecodeResize(ProtocolPayloads.EncodeResize(expected));
        Assert.Equal(expected, decoded);
    }

    [Fact]
    public void SessionExitRejectsInvalidLength()
    {
        Assert.Throws<ProtocolException>(() => ProtocolPayloads.DecodeSessionExit(new byte[19]));
    }

    [Fact]
    public void UploadFileRoundTripsUnicodePathAndLength()
    {
        var expected = new UploadFileRequest(
            @"C:\данные\архив.bin",
            9_876_543_210);

        var decoded = FileTransferPayloads.DecodeUploadFile(
            FileTransferPayloads.EncodeUploadFile(expected.Path, expected.Length));

        Assert.Equal(expected, decoded);
    }

    [Fact]
    public void FilePathRoundTripsUnicode()
    {
        const string expected = @"C:\данные\результат.txt";

        var decoded = FileTransferPayloads.DecodeFilePath(
            FileTransferPayloads.EncodeFilePath(expected));

        Assert.Equal(expected, decoded);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(long.MinValue)]
    public void FileLengthRejectsNegativeValues(long length)
    {
        Assert.Throws<ProtocolException>(() => FileTransferPayloads.EncodeFileLength(length));
    }

    [Fact]
    public void SessionFileStartRoundTrips()
    {
        var expected = new SessionFileStart(
            Guid.NewGuid(),
            123_456_789,
            @"results\report.bin");

        var decoded = FileTransferPayloads.DecodeSessionFileStart(
            FileTransferPayloads.EncodeSessionFileStart(
                expected.TransferId,
                expected.Length,
                expected.RelativePath));

        Assert.Equal(expected, decoded);
    }

    [Theory]
    [InlineData(SessionFileStatusCode.Ready, "")]
    [InlineData(SessionFileStatusCode.Completed, "")]
    [InlineData(SessionFileStatusCode.Failed, "Disk is full.")]
    public void SessionFileStatusRoundTrips(
        SessionFileStatusCode status,
        string message)
    {
        var expected = new SessionFileStatus(Guid.NewGuid(), status, message);

        var decoded = FileTransferPayloads.DecodeSessionFileStatus(
            FileTransferPayloads.EncodeSessionFileStatus(
                expected.TransferId,
                expected.Status,
                expected.Message));

        Assert.Equal(expected, decoded);
    }

    [Fact]
    public void SendFilesRequestRoundTrips()
    {
        const string rootPath = @"C:\work";
        string[] patterns = ["*.txt", @"results\*.bin"];

        var decoded = FileTransferPayloads.DecodeSendFilesRequest(
            FileTransferPayloads.EncodeSendFilesRequest(rootPath, patterns));

        Assert.Equal(rootPath, decoded.RootPath);
        Assert.Equal(patterns, decoded.Patterns);
    }
}
