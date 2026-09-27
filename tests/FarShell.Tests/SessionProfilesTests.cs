using System.Collections;
using System.Text.Json;
using FarShell.Broker;
using FarShell.Protocol;

namespace FarShell.Tests;

public sealed class SessionProfilesTests
{
    private static Dictionary<string, string> Startup()
    {
        var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables()) { environment[(string)entry.Key] = (string)entry.Value!; }
        return environment;
    }

    [Fact]
    public void ExpandsAgainstStartupAndAppliesOverridesAndRemovals()
    {
        var env = Startup();
        env["BASE"] = "startup";
        env["REMOVE"] = "present";
        var json = """
            {"defaultProfile":"Work","profiles":{"Work":{"shell":{"file":"pwsh.exe","args":["-NoLogo"]},"env":{"BASE":"changed","COPY":"%BASE%","REMOVE":null}}}}
            """;
        var profiles = SessionProfiles.Parse(JsonDocument.Parse(json).RootElement, env);
        var launch = profiles.Resolve("work").Launch;
        Assert.Equal("changed", launch.Environment["BASE"]);
        Assert.Equal("startup", launch.Environment["COPY"]);
        Assert.False(launch.Environment.ContainsKey("REMOVE"));
        Assert.Equal("Work", profiles.Resolve("").Name);
        Assert.Throws<SessionOperationException>(() => profiles.Resolve("missing"));
    }

    [Theory]
    [InlineData("{\"defaultProfile\":\"missing\",\"profiles\":{}}")]
    [InlineData("{\"defaultProfile\":\"a\",\"defaultProfile\":\"a\",\"profiles\":{}}")]
    [InlineData("{\"defaultProfile\":\"a\",\"typo\":true,\"profiles\":{}}")]
    [InlineData("{\"defaultProfile\":\"a\",\"profiles\":{\"a\":{\"shell\":{\"file\":\"missing-farshell-executable.exe\"}}}}")]
    [InlineData("{\"defaultProfile\":\"a\",\"profiles\":{\"a\":{\"shell\":{\"file\":\"pwsh.exe\"}},\"A\":{\"shell\":{\"file\":\"pwsh.exe\"}}}}")]
    [InlineData("{\"defaultProfile\":\"a\",\"profiles\":{\"a\":{\"workingDirectory\":\"relative\",\"shell\":{\"file\":\"pwsh.exe\"}}}}")]
    [InlineData("{\"defaultProfile\":\"a\",\"profiles\":{\"a\":{\"shell\":{\"file\":\"pwsh.exe\",\"argument\":[]}}}}")]
    [InlineData("{\"defaultProfile\":\"a\",\"profiles\":{\"a\":{\"shell\":{\"file\":\"pwsh.exe\"},\"env\":{\"X\":\"one\",\"x\":\"two\"}}}}")]
    public void RejectsInvalidConfiguration(string json)
        => Assert.Throws<ArgumentException>(() => SessionProfiles.Parse(JsonDocument.Parse(json).RootElement, Startup()));

    [Fact]
    public void ExplicitMissingConfigDoesNotFallBack()
        => Assert.Throws<FileNotFoundException>(() => SessionProfiles.Load(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json")));

    [Fact]
    public void CreatePayloadValidatesProfilesAndRejectsVersionTwoShape()
    {
        var request = new CreateSessionRequest(new(120, 30), "Work-1");
        Assert.Equal(request, ProtocolPayloads.DecodeCreateSession(ProtocolPayloads.EncodeCreateSession(request)));
        Assert.Throws<ProtocolException>(() => ProtocolPayloads.DecodeCreateSession(ProtocolPayloads.EncodeResize(new(80, 24))));
        Assert.Throws<ProtocolException>(() => ProtocolPayloads.EncodeCreateSession(request with { ProfileName = "../bad" }));
        var invalid = ProtocolPayloads.EncodeCreateSession(request);
        invalid[12] = 255;
        Assert.Throws<ProtocolException>(() => ProtocolPayloads.DecodeCreateSession(invalid));
        invalid[8] = 255;
        Assert.Throws<ProtocolException>(() => ProtocolPayloads.DecodeCreateSession(invalid));
    }
}
