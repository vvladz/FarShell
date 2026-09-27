using FarShell.Protocol;

namespace FarShell.Broker;

internal sealed class FileTransferHandler
{
    private const int BufferSize = 64 * 1024;

    internal async Task UploadAsync(
        ConnectionContext connection,
        UploadFileRequest request)
    {
        try
        {
            await UploadCoreAsync(connection, request);
        }
        catch (Exception exception) when (
            exception is not ProtocolException && IsFileException(exception))
        {
            throw new FileTransferException(
                $"Remote upload failed: {exception.Message}",
                exception);
        }
    }

    internal async Task DownloadAsync(ConnectionContext connection, string requestedPath)
    {
        try
        {
            await DownloadCoreAsync(connection, requestedPath);
        }
        catch (Exception exception) when (
            exception is not ProtocolException && IsFileException(exception))
        {
            throw new FileTransferException(
                $"Remote download failed: {exception.Message}",
                exception);
        }
    }

    private static async Task UploadCoreAsync(
        ConnectionContext connection,
        UploadFileRequest request)
    {
        var destinationPath = Path.GetFullPath(request.Path);
        var directory = Path.GetDirectoryName(destinationPath);
        var fileName = Path.GetFileName(destinationPath);
        if (string.IsNullOrEmpty(directory) || string.IsNullOrEmpty(fileName))
        {
            throw new ArgumentException(
                $"Remote destination must identify a file: {request.Path}");
        }

        if (!Directory.Exists(directory))
        {
            throw new DirectoryNotFoundException(
                $"Remote destination directory does not exist: {directory}");
        }

        var temporaryPath = Path.Combine(
            directory,
            $".{fileName}.{Guid.NewGuid():N}.farshell-upload");
        var committed = false;
        try
        {
            await using (var destination = new FileStream(
                temporaryPath,
                new FileStreamOptions
                {
                    Mode = FileMode.CreateNew,
                    Access = FileAccess.Write,
                    Share = FileShare.None,
                    Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
                }))
            {
                await connection.Writer.WriteAsync(
                    MessageType.UploadReady,
                    ReadOnlyMemory<byte>.Empty,
                    connection.CancellationToken);

                var remaining = request.Length;
                while (remaining > 0)
                {
                    var frame = await FrameCodec.ReadAsync(
                        connection.Transport,
                        connection.CancellationToken)
                        ?? throw new IOException(
                            "Client disconnected before upload completed.");
                    if (frame.Type != MessageType.FileData)
                    {
                        throw new ProtocolException(
                            $"Expected FILE_DATA during upload, received {frame.Type}.");
                    }

                    if (frame.Payload.Length == 0 || frame.Payload.Length > remaining)
                    {
                        throw new ProtocolException(
                            "FILE_DATA has an invalid upload length.");
                    }

                    await destination.WriteAsync(
                        frame.Payload,
                        connection.CancellationToken);
                    remaining -= frame.Payload.Length;
                }

                await destination.FlushAsync(connection.CancellationToken);
            }

            File.Move(temporaryPath, destinationPath, overwrite: true);
            committed = true;
            await connection.Writer.WriteAsync(
                MessageType.FileCompleted,
                ReadOnlyMemory<byte>.Empty,
                connection.CancellationToken);
        }
        finally
        {
            if (!committed)
            {
                TryDelete(temporaryPath);
            }
        }
    }

    private static async Task DownloadCoreAsync(
        ConnectionContext connection,
        string requestedPath)
    {
        var sourcePath = Path.GetFullPath(requestedPath);
        await using var source = new FileStream(
            sourcePath,
            new FileStreamOptions
            {
                Mode = FileMode.Open,
                Access = FileAccess.Read,
                Share = FileShare.Read,
                Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
            });
        var remaining = source.Length;
        await connection.Writer.WriteAsync(
            MessageType.FileMetadata,
            FileTransferPayloads.EncodeFileLength(remaining),
            connection.CancellationToken);

        var buffer = new byte[BufferSize];
        while (remaining > 0)
        {
            var bytesRead = await source.ReadAsync(
                buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)),
                connection.CancellationToken);
            if (bytesRead == 0)
            {
                throw new EndOfStreamException(
                    $"Remote file changed before download completed: {requestedPath}");
            }

            await connection.Writer.WriteAsync(
                MessageType.FileData,
                buffer.AsMemory(0, bytesRead),
                connection.CancellationToken);
            remaining -= bytesRead;
        }

        await connection.Writer.WriteAsync(
            MessageType.FileCompleted,
            ReadOnlyMemory<byte>.Empty,
            connection.CancellationToken);
    }

    private static bool IsFileException(Exception exception)
    {
        return exception is IOException
            or UnauthorizedAccessException
            or ArgumentException
            or NotSupportedException;
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch
        {
            // Keep the original transfer failure.
        }
    }
}
