using FarShell.Protocol;

namespace FarShell.Tests;

public sealed class ProtocolPayloadsTests
{
    [Fact]
    public void SessionListRoundTrips()
    {
        var sessions = new[]
        {
            new SessionInfo(
                Guid.NewGuid(),
                true,
                DateTimeOffset.FromUnixTimeMilliseconds(1_789_000_000_123),
                new TerminalSize(120, 30)),
            new SessionInfo(
                Guid.NewGuid(),
                false,
                DateTimeOffset.FromUnixTimeMilliseconds(1_789_000_100_456),
                new TerminalSize(80, 24)),
        };

        var decoded = ProtocolPayloads.DecodeSessionList(
            ProtocolPayloads.EncodeSessionList(sessions));

        Assert.Equal(sessions, decoded);
    }

    [Fact]
    public void AttachSessionRoundTrips()
    {
        var expected = new AttachSessionRequest(
            Guid.NewGuid(),
            new TerminalSize(132, 43));

        var decoded = ProtocolPayloads.DecodeAttachSession(
            ProtocolPayloads.EncodeAttachSession(expected.SessionId, expected.Size));

        Assert.Equal(expected, decoded);
    }

    [Fact]
    public void SessionListRejectsInvalidAttachmentState()
    {
        var payload = ProtocolPayloads.EncodeSessionList(
            new[]
            {
                new SessionInfo(
                    Guid.NewGuid(),
                    false,
                    DateTimeOffset.UtcNow,
                    new TerminalSize(80, 24)),
            });
        payload[20] = 2;

        Assert.Throws<ProtocolException>(() => ProtocolPayloads.DecodeSessionList(payload));
    }
}
