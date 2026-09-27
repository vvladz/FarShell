using System.Collections.ObjectModel;
using System.Text;

namespace FarShell.ConPTY;

public sealed class ProcessLaunchSettings
{
    public ProcessLaunchSettings(string file, IEnumerable<string> arguments,
        string? workingDirectory, IReadOnlyDictionary<string, string> environment)
    {
        File = file;
        Arguments = Array.AsReadOnly(arguments.ToArray());
        WorkingDirectory = workingDirectory;
        Environment = new ReadOnlyDictionary<string, string>(
            new Dictionary<string, string>(environment, StringComparer.OrdinalIgnoreCase));
    }

    public string File { get; }
    public IReadOnlyList<string> Arguments { get; }
    public string? WorkingDirectory { get; }
    public IReadOnlyDictionary<string, string> Environment { get; }

    internal string CommandLine => string.Join(' ', new[] { File }.Concat(Arguments).Select(Quote));

    internal static string Quote(string argument)
    {
        if (argument.Contains('\0')) { throw new ArgumentException("Arguments cannot contain NUL."); }
        var result = new StringBuilder("\"");
        var slashes = 0;
        foreach (var character in argument)
        {
            if (character == '\\') { slashes++; continue; }
            result.Append('\\', character == '"' ? slashes * 2 + 1 : slashes);
            result.Append(character);
            slashes = 0;
        }

        return result.Append('\\', slashes * 2).Append('"').ToString();
    }
}
