using FarShell.Protocol;

namespace FarShell.Client;

internal sealed class IncomingFileTransfer : IAsyncDisposable
{
    private readonly FileStream _destination;
    private readonly string _destinationPath;
    private readonly string _temporaryPath;
    private readonly DownloadPathGuard _pathGuard;
    private long _remaining;
    private bool _committed;

    private IncomingFileTransfer(
        SessionFileStart start,
        string destinationPath,
        string temporaryPath,
        FileStream destination,
        DownloadPathGuard pathGuard)
    {
        TransferId = start.TransferId;
        RelativePath = start.RelativePath;
        _remaining = start.Length;
        _destinationPath = destinationPath;
        _temporaryPath = temporaryPath;
        _destination = destination;
        _pathGuard = pathGuard;
    }

    internal Guid TransferId { get; }

    internal string RelativePath { get; }

    internal static IncomingFileTransfer Create(string downloadRoot, SessionFileStart start)
    {
        if (string.IsNullOrEmpty(start.RelativePath) || Path.IsPathRooted(start.RelativePath)
            || start.RelativePath.Contains(':'))
        {
            throw new ProtocolException("SESSION_FILE_START path must be relative.");
        }

        var rootPath = Path.GetFullPath(downloadRoot);
        var destinationPath = Path.GetFullPath(
            Path.Combine(rootPath, start.RelativePath));
        EnsureUnderRoot(rootPath, destinationPath);
        var directory = Path.GetDirectoryName(destinationPath);
        if (string.IsNullOrEmpty(directory)
            || string.IsNullOrEmpty(Path.GetFileName(destinationPath)))
        {
            throw new ProtocolException(
                "SESSION_FILE_START path must identify a local file.");
        }

        var pathGuard = DownloadPathGuard.Acquire(rootPath, destinationPath);
        var temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(destinationPath)}.{Guid.NewGuid():N}.farshell-send");
        try
        {
            var destination = new FileStream(
                temporaryPath,
                new FileStreamOptions
                {
                    Mode = FileMode.CreateNew,
                    Access = FileAccess.Write,
                    Share = FileShare.None,
                    Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
                });
            return new IncomingFileTransfer(
                start,
                destinationPath,
                temporaryPath,
                destination,
                pathGuard);
        }
        catch
        {
            pathGuard.Dispose();
            throw;
        }
    }

    internal async Task WriteAsync(
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken)
    {
        if (payload.IsEmpty || payload.Length > _remaining)
        {
            throw new ProtocolException("FILE_DATA has an invalid send length.");
        }

        await _destination.WriteAsync(payload, cancellationToken);
        _remaining -= payload.Length;
    }

    internal async Task CompleteAsync(CancellationToken cancellationToken)
    {
        if (_remaining != 0)
        {
            throw new ProtocolException(
                $"send ended with {_remaining} bytes still expected.");
        }

        await _destination.FlushAsync(cancellationToken);
        await _destination.DisposeAsync();
        DownloadPathGuard.RejectReparseFile(_destinationPath);
        File.Move(_temporaryPath, _destinationPath, overwrite: true);
        _committed = true;
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await _destination.DisposeAsync();
            if (_committed)
            {
                return;
            }

            try
            {
                File.Delete(_temporaryPath);
            }
            catch
            {
                // Keep the original transfer failure.
            }
        }
        finally
        {
            _pathGuard.Dispose();
        }
    }

    private static void EnsureUnderRoot(string rootPath, string destinationPath)
    {
        var relativePath = Path.GetRelativePath(rootPath, destinationPath);
        if (relativePath == "." || Path.IsPathRooted(relativePath)
            || relativePath == ".."
            || relativePath.StartsWith(
                $"..{Path.DirectorySeparatorChar}",
                StringComparison.Ordinal))
        {
            throw new ProtocolException(
                "SESSION_FILE_START path escapes the local download directory.");
        }
    }
}
