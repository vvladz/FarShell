using FarShell.Protocol;

namespace FarShell.Broker;

internal readonly record struct SendFile(string FullPath, string RelativePath);

internal static class SendFileResolver
{
    private const int MaximumFiles = 256;

    internal static IReadOnlyList<SendFile> Resolve(SendFilesRequest request)
    {
        var rootPath = Path.GetFullPath(request.RootPath);
        if (!Directory.Exists(rootPath))
        {
            throw new DirectoryNotFoundException(
                $"Remote send directory does not exist: {request.RootPath}");
        }

        var files = new Dictionary<string, SendFile>(StringComparer.OrdinalIgnoreCase);
        foreach (var pattern in request.Patterns)
        {
            ResolvePattern(rootPath, pattern, files);
        }

        return files.Values
            .OrderBy(file => file.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static void ResolvePattern(
        string rootPath,
        string pattern,
        Dictionary<string, SendFile> files)
    {
        if (Path.IsPathRooted(pattern) || ContainsParentSegment(pattern))
        {
            throw new FileTransferException(
                $"send paths must stay under the current remote directory: {pattern}",
                new ArgumentException("The path is rooted or contains a parent segment."));
        }

        var directoryPart = Path.GetDirectoryName(pattern);
        var filePattern = Path.GetFileName(pattern);
        if (string.IsNullOrEmpty(filePattern))
        {
            throw new FileTransferException(
                $"send path must identify one or more files: {pattern}",
                new ArgumentException("The path has no file name."));
        }

        if (ContainsWildcard(directoryPart))
        {
            throw new FileTransferException(
                $"Wildcards are supported only in the file name: {pattern}",
                new ArgumentException("The directory contains a wildcard."));
        }

        var directoryPath = Path.GetFullPath(
            Path.Combine(rootPath, directoryPart ?? string.Empty));
        EnsureUnderRoot(rootPath, directoryPath, pattern);

        if (!ContainsWildcard(filePattern))
        {
            AddLiteralFile(rootPath, Path.Combine(directoryPath, filePattern), pattern, files);
            return;
        }

        if (!Directory.Exists(directoryPath))
        {
            throw new DirectoryNotFoundException(
                $"Remote send directory does not exist: {directoryPart}");
        }

        var matches = Directory.EnumerateFiles(
                directoryPath,
                filePattern,
                SearchOption.TopDirectoryOnly)
            .ToArray();
        if (matches.Length == 0)
        {
            throw new FileNotFoundException($"No remote files match: {pattern}");
        }

        foreach (var match in matches)
        {
            AddFile(rootPath, match, pattern, files);
        }
    }

    private static void AddLiteralFile(
        string rootPath,
        string fullPath,
        string requestedPath,
        Dictionary<string, SendFile> files)
    {
        if (Directory.Exists(fullPath))
        {
            throw new FileTransferException(
                $"Directories are not supported by send: {requestedPath}",
                new IOException("The requested path is a directory."));
        }

        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException($"Remote file does not exist: {requestedPath}");
        }

        AddFile(rootPath, fullPath, requestedPath, files);
    }

    private static void AddFile(
        string rootPath,
        string fullPath,
        string requestedPath,
        Dictionary<string, SendFile> files)
    {
        var normalizedPath = Path.GetFullPath(fullPath);
        EnsureUnderRoot(rootPath, normalizedPath, requestedPath);
        var relativePath = Path.GetRelativePath(rootPath, normalizedPath);
        files.TryAdd(normalizedPath, new SendFile(normalizedPath, relativePath));
        if (files.Count > MaximumFiles)
        {
            throw new FileTransferException(
                $"send matched more than {MaximumFiles} files.",
                new IOException("The send file limit was exceeded."));
        }
    }

    private static void EnsureUnderRoot(
        string rootPath,
        string path,
        string requestedPath)
    {
        var relativePath = Path.GetRelativePath(rootPath, path);
        if (Path.IsPathRooted(relativePath)
            || relativePath == ".."
            || relativePath.StartsWith(
                $"..{Path.DirectorySeparatorChar}",
                StringComparison.Ordinal))
        {
            throw new FileTransferException(
                $"send paths must stay under the current remote directory: {requestedPath}",
                new ArgumentException("The path escapes the transfer root."));
        }
    }

    private static bool ContainsParentSegment(string path)
    {
        return path.Split(
                [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                StringSplitOptions.RemoveEmptyEntries)
            .Contains("..", StringComparer.Ordinal);
    }

    private static bool ContainsWildcard(string? path)
    {
        return path?.IndexOfAny(['*', '?']) >= 0;
    }
}
