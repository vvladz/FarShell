using FarShell.Broker;

namespace FarShell.Tests;

public sealed class BrokerOptionsTests
{
    [Fact]
    public void RotationRequiresConfirmationAndCannotBeCombinedWithServingOptions()
    {
        Assert.Throws<ArgumentException>(() => BrokerOptions.Parse(["--rotate-key"]));
        Assert.Throws<ArgumentException>(() => BrokerOptions.Parse(["--confirm"]));
        Assert.Throws<ArgumentException>(() => BrokerOptions.Parse(["--rotate-key", "--confirm", "8022"]));
        Assert.Equal(BrokerOperation.RotateKey, BrokerOptions.Parse(["--rotate-key", "--confirm"]).Operation);
        Assert.Equal(BrokerOperation.ReplaceCertificate, BrokerOptions.Parse(["--replace-certificate", "--confirm"]).Operation);
    }
    [Fact]
    public void UsesBoundedDefaults()
    {
        var options = BrokerOptions.Parse([]);

        Assert.Equal(8022, options.Port);
        Assert.Equal(32, options.MaxConnections);
        Assert.Equal(16, options.MaxSessions);
        Assert.Equal(TimeSpan.FromSeconds(10), options.HandshakeTimeout);
    }

    [Fact]
    public void ParsesAllLimits()
    {
        var options = BrokerOptions.Parse(
            [
                "9000",
                "--max-connections",
                "12",
                "--max-sessions",
                "6",
                "--handshake-timeout-seconds",
                "4",
            ]);

        Assert.Equal(9000, options.Port);
        Assert.Equal(12, options.MaxConnections);
        Assert.Equal(6, options.MaxSessions);
        Assert.Equal(TimeSpan.FromSeconds(4), options.HandshakeTimeout);
    }

    [Fact]
    public void RejectsOutOfRangeLimits()
    {
        Assert.Throws<ArgumentException>(
            () => BrokerOptions.Parse(["--max-sessions", "0"]));
    }
}
