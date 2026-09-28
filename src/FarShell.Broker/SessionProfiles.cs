using System.Collections;
using System.Collections.Frozen;
using System.Text.Json;
using System.Text.RegularExpressions;
using FarShell.ConPTY;
using FarShell.Protocol;
using FarShell.Security;

namespace FarShell.Broker;

internal sealed record SessionLaunchProfile(string Name, ProcessLaunchSettings Launch);

internal sealed class SessionProfiles
{
    private readonly FrozenDictionary<string, SessionLaunchProfile> _profiles;
    private readonly string _default;

    private SessionProfiles(Dictionary<string, SessionLaunchProfile> profiles, string defaultProfile)
    {
        _profiles = profiles.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
        _default = defaultProfile;
    }

    internal SessionLaunchProfile Resolve(string? name)
        => _profiles.TryGetValue(string.IsNullOrEmpty(name) ? _default : name, out var profile)
            ? profile : throw new SessionOperationException($"Unknown profile: {name}.");

    internal static SessionProfiles Load(string? configPath = null, string? stateDirectory = null)
    {
        var startup = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            startup[(string)entry.Key] = (string)entry.Value!;
        }

        var path = configPath ?? Path.Combine(stateDirectory ?? PrivateStorage.DefaultDirectory, "config.json");
        if (configPath is null && !File.Exists(path))
        {
            var launch = new ProcessLaunchSettings(ResolveExecutable("pwsh.exe", startup),
                new[] { "-NoLogo" }, null, startup);
            return new SessionProfiles(new(StringComparer.OrdinalIgnoreCase)
            {
                ["default"] = new("default", launch),
            }, "default");
        }

        using var document = JsonDocument.Parse(File.ReadAllText(path));
        return Parse(document.RootElement, startup);
    }

    internal static SessionProfiles Parse(JsonElement root, IReadOnlyDictionary<string, string> startup)
    {
        RequireProperties(root, "defaultProfile", "profiles");
        var defaultProfile = RequiredString(root.GetProperty("defaultProfile"));
        var entries = root.GetProperty("profiles");
        if (entries.ValueKind != JsonValueKind.Object) { throw Invalid("profiles must be an object."); }
        var profiles = new Dictionary<string, SessionLaunchProfile>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries.EnumerateObject())
        {
            ProtocolPayloads.ValidateProfileName(entry.Name, allowEmpty: false);
            RequireProperties(entry.Value, "workingDirectory", "shell", "env");
            var shell = entry.Value.GetProperty("shell");
            RequireProperties(shell, "file", "args");
            var file = ResolveExecutable(Expand(RequiredString(shell.GetProperty("file")), startup), startup);
            var arguments = new List<string>();
            if (shell.TryGetProperty("args", out var args))
            {
                if (args.ValueKind != JsonValueKind.Array) { throw Invalid("shell.args must be an array."); }
                foreach (var argument in args.EnumerateArray())
                {
                    arguments.Add(RequiredString(argument, allowEmpty: true));
                }
            }

            string? directory = null;
            if (entry.Value.TryGetProperty("workingDirectory", out var working)
                && working.ValueKind != JsonValueKind.Null)
            {
                directory = Expand(RequiredString(working), startup);
                if (!Path.IsPathFullyQualified(directory) || !Directory.Exists(directory))
                {
                    throw Invalid($"Profile {entry.Name} requires an existing absolute workingDirectory.");
                }
            }

            var environment = new Dictionary<string, string>(startup, StringComparer.OrdinalIgnoreCase);
            if (entry.Value.TryGetProperty("env", out var env))
            {
                if (env.ValueKind != JsonValueKind.Object) { throw Invalid("env must be an object."); }
                var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var variable in env.EnumerateObject())
                {
                    if (variable.Name.Length == 0 || variable.Name.Contains('=') || variable.Name.Contains('\0')
                        || !names.Add(variable.Name))
                    {
                        throw Invalid($"Profile {entry.Name} contains an invalid or duplicate environment name.");
                    }

                    if (variable.Value.ValueKind == JsonValueKind.Null) { environment.Remove(variable.Name); }
                    else { environment[variable.Name] = Expand(RequiredString(variable.Value, true), startup); }
                }
            }

            if (!profiles.TryAdd(entry.Name, new(entry.Name, new(file, arguments, directory, environment))))
            {
                throw Invalid($"Duplicate profile: {entry.Name}.");
            }
        }

        if (profiles.Count == 0 || !profiles.ContainsKey(defaultProfile))
        {
            throw Invalid("defaultProfile must name an existing profile.");
        }

        return new SessionProfiles(profiles, defaultProfile);
    }

    private static string Expand(string value, IReadOnlyDictionary<string, string> environment)
        => Regex.Replace(value, "%([^%]+)%", match =>
            environment.TryGetValue(match.Groups[1].Value, out var expanded) ? expanded : match.Value);

    private static string ResolveExecutable(string file, IReadOnlyDictionary<string, string> environment)
    {
        if (Path.IsPathFullyQualified(file) && File.Exists(file)) { return Path.GetFullPath(file); }
        if (Path.GetFileName(file) == file && environment.TryGetValue("PATH", out var path))
        {
            foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                var basePath = directory.Trim('"');
                if (!Path.IsPathFullyQualified(basePath)) { continue; }
                foreach (var suffix in Path.HasExtension(file) ? new[] { "" } : new[] { ".exe", ".com" })
                {
                    var candidate = Path.Combine(basePath, file + suffix);
                    if (File.Exists(candidate)) { return Path.GetFullPath(candidate); }
                }
            }
        }

        throw Invalid("A profile shell executable could not be resolved from the broker startup PATH.");
    }

    private static void RequireProperties(JsonElement element, params string[] allowed)
    {
        if (element.ValueKind != JsonValueKind.Object) { throw Invalid("Expected a JSON object."); }
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (!allowed.Contains(property.Name, StringComparer.Ordinal) || !names.Add(property.Name))
            {
                throw Invalid($"Unknown or duplicate configuration property: {property.Name}.");
            }
        }
    }

    private static string RequiredString(JsonElement element, bool allowEmpty = false)
    {
        if (element.ValueKind != JsonValueKind.String) { throw Invalid("Expected a string."); }
        var value = element.GetString()!;
        if ((!allowEmpty && string.IsNullOrWhiteSpace(value)) || value.Contains('\0'))
        {
            throw Invalid("Configuration contains an empty or invalid string.");
        }

        return value;
    }

    private static ArgumentException Invalid(string message) => new($"Invalid profile configuration: {message}");
}
