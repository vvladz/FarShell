using System.Collections;
using System.Text.Json;
using FarShell.Broker;

namespace FarShell.Tests;

internal static class TestProfiles
{
    // Integration tests must not load the developer's FarShell or PowerShell profiles.
    internal static SessionProfiles Default { get; } = Create();

    private static SessionProfiles Create()
    {
        var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            environment[(string)entry.Key] = (string)entry.Value!;
        }

        using var document = JsonDocument.Parse("""
            {"defaultProfile":"default","profiles":{"default":{"shell":{"file":"pwsh.exe","args":["-NoLogo","-NoProfile"]}}}}
            """);
        return SessionProfiles.Parse(document.RootElement, environment);
    }
}
