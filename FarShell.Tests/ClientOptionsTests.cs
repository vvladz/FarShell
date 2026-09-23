using FarShell.Client;

namespace FarShell.Tests;

public sealed class ClientOptionsTests
{
    [Fact]
    public void DefaultsToCreateOnLoopback()
    {
        var options = ClientOptions.Parse([]);

        Assert.Equal(ClientOperation.Create, options.Operation);
        Assert.Equal("127.0.0.1", options.Endpoint.Host);
        Assert.Equal(8022, options.Endpoint.Port);
        Assert.Equal(TimeSpan.FromMilliseconds(4), options.OutputBatchDelay);
    }

    [Fact]
    public void ParsesAttachWithEndpoint()
    {
        var sessionId = Guid.NewGuid();

        var options = ClientOptions.Parse(
            ["--attach", sessionId.ToString("N"), "server", "9000"]);

        Assert.Equal(ClientOperation.Attach, options.Operation);
        Assert.Equal(sessionId, options.SessionId);
        Assert.Equal("server", options.Endpoint.Host);
        Assert.Equal(9000, options.Endpoint.Port);
    }

    [Fact]
    public void ParsesUploadWithEndpoint()
    {
        var options = ClientOptions.Parse(
            ["--upload", @".\local.bin", @"C:\remote.bin", "server", "9000"]);

        Assert.Equal(ClientOperation.Upload, options.Operation);
        Assert.Equal(@".\local.bin", options.LocalPath);
        Assert.Equal(@"C:\remote.bin", options.RemotePath);
        Assert.Equal("server", options.Endpoint.Host);
        Assert.Equal(9000, options.Endpoint.Port);
    }

    [Fact]
    public void ParsesDownloadWithDefaultEndpoint()
    {
        var options = ClientOptions.Parse(
            ["--download", @"C:\remote.bin", @".\local.bin"],
            "server.example:9000");

        Assert.Equal(ClientOperation.Download, options.Operation);
        Assert.Equal(@".\local.bin", options.LocalPath);
        Assert.Equal(@"C:\remote.bin", options.RemotePath);
        Assert.Equal("server.example", options.Endpoint.Host);
        Assert.Equal(9000, options.Endpoint.Port);
    }

    [Fact]
    public void UsesDefaultServerFromEnvironmentValue()
    {
        var options = ClientOptions.Parse([], "server.example:9000");

        Assert.Equal("server.example", options.Endpoint.Host);
        Assert.Equal(9000, options.Endpoint.Port);
    }

    [Fact]
    public void ExplicitEndpointOverridesEnvironmentValue()
    {
        var options = ClientOptions.Parse(
            ["explicit.example", "9001"],
            "default.example:9000");

        Assert.Equal("explicit.example", options.Endpoint.Host);
        Assert.Equal(9001, options.Endpoint.Port);
    }

    [Fact]
    public void SessionOperationUsesDefaultServerFromEnvironmentValue()
    {
        var sessionId = Guid.NewGuid();

        var options = ClientOptions.Parse(
            ["--attach", sessionId.ToString("N")],
            "server.example:9000");

        Assert.Equal(ClientOperation.Attach, options.Operation);
        Assert.Equal(sessionId, options.SessionId);
        Assert.Equal("server.example", options.Endpoint.Host);
        Assert.Equal(9000, options.Endpoint.Port);
    }

    [Fact]
    public void UsesOutputBatchDelayFromEnvironmentValue()
    {
        var options = ClientOptions.Parse(
            [],
            defaultOutputBatchMilliseconds: "8");

        Assert.Equal(TimeSpan.FromMilliseconds(8), options.OutputBatchDelay);
    }

    [Fact]
    public void CommandLineOutputBatchDelayOverridesEnvironmentValue()
    {
        var sessionId = Guid.NewGuid();

        var options = ClientOptions.Parse(
            [
                "--attach",
                sessionId.ToString("N"),
                "--output-batch-ms",
                "2",
                "server",
                "9000",
            ],
            defaultOutputBatchMilliseconds: "8");

        Assert.Equal(ClientOperation.Attach, options.Operation);
        Assert.Equal(sessionId, options.SessionId);
        Assert.Equal("server", options.Endpoint.Host);
        Assert.Equal(9000, options.Endpoint.Port);
        Assert.Equal(TimeSpan.FromMilliseconds(2), options.OutputBatchDelay);
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("101")]
    [InlineData("invalid")]
    public void RejectsInvalidOutputBatchDelay(string value)
    {
        Assert.Throws<ArgumentException>(
            () => ClientOptions.Parse([], defaultOutputBatchMilliseconds: value));
    }

    [Fact]
    public void RejectsDuplicateOutputBatchDelayOptions()
    {
        Assert.Throws<ArgumentException>(
            () => ClientOptions.Parse(
                ["--output-batch-ms", "2", "--output-batch-ms", "4"]));
    }

    [Fact]
    public void RejectsInvalidDefaultServer()
    {
        Assert.Throws<ArgumentException>(() => ClientOptions.Parse([], "server.example:0"));
    }

    [Fact]
    public void RejectsUnknownOperation()
    {
        Assert.Throws<ArgumentException>(() => ClientOptions.Parse(["--unknown"]));
    }
}
