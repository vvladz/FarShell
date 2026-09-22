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
    public void RejectsUnknownOperation()
    {
        Assert.Throws<ArgumentException>(() => ClientOptions.Parse(["--unknown"]));
    }
}
