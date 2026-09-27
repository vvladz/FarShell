using FarShell.Security;
using FarShell.Protocol;

namespace FarShell.Client;

internal sealed class RemoteTerminalClient
{
    private const int FileBufferSize = 64 * 1024;
    private static readonly byte[] InitialTerminalReset = "\u001b[2J\u001b[H"u8.ToArray();
    private static readonly TimeSpan ResizePollInterval = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan PingInterval = TimeSpan.FromSeconds(30);
    private readonly string _host;
    private readonly int _port;
    private readonly TimeSpan _outputBatchDelay;
    private readonly string _downloadRoot;
    private readonly string _profile;
    private readonly string _stateDirectory;

    internal RemoteTerminalClient(string host, int port, TimeSpan outputBatchDelay, string profile = "", string? stateDirectory = null)
    {
        _host = host;
        _profile = profile;
        _stateDirectory = stateDirectory ?? PrivateStorage.DefaultDirectory;
        _port = port;
        _outputBatchDelay = outputBatchDelay;
        _downloadRoot = Path.GetFullPath(Environment.CurrentDirectory);
    }

    internal Task<int> CreateAsync()
    {
        return RunInteractiveAsync();
    }

    internal async Task<int> PairAsync()
    {
        using var connection = await ClientConnection.ConnectAsync(_host, _port, _stateDirectory, authenticate: false);
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2) + TimeSpan.FromSeconds(5));
        await connection.Writer.WriteAsync(MessageType.PairRequest, ReadOnlyMemory<byte>.Empty, timeout.Token);
        var pending = await connection.ReadAsync(timeout.Token);
        if (pending.Type != MessageType.PairPending) { throw new ProtocolException("Expected PAIR_PENDING."); }
        var request = ProtocolPayloads.DecodeSessionId(pending.Payload);
        Console.WriteLine($"Pairing request: {request:N}\nOn the broker machine, under the broker's Windows user, run:\n  FarShell.Broker.exe --approve {request:N}\nApproval expires in 2 minutes.");
        var accepted = await connection.ReadAsync(timeout.Token);
        if (accepted.Type != MessageType.PairAccepted || accepted.Payload.Length != 32)
        {
            throw new ProtocolException("Invalid pairing response.");
        }

        try
        {
            new ClientCredentials(connection.Credentials.Fingerprint, accepted.Payload)
                .Save(_stateDirectory, ClientCredentials.Endpoint(_host, _port));
        }
        finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(accepted.Payload); }
        Console.WriteLine("Client paired. You can now connect or transfer files.");
        return 0;
    }

    internal async Task<int> UploadAsync(string localPath, string remotePath)
    {
        await using var source = OpenLocalSource(localPath);
        var length = source.Length;
        using var connection = await ConnectAsync();
        await connection.Writer.WriteAsync(
            MessageType.UploadFile,
            FileTransferPayloads.EncodeUploadFile(remotePath, length));

        var ready = await ReadRequiredAsync(connection.Transport);
        if (ready.Type != MessageType.UploadReady)
        {
            throw new ProtocolException($"Expected UPLOAD_READY, received {ready.Type}.");
        }

        ProtocolPayloads.RequireEmpty(ready);
        var remaining = length;
        var buffer = new byte[FileBufferSize];
        while (remaining > 0)
        {
            var bytesRead = await source.ReadAsync(
                buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)));
            if (bytesRead == 0)
            {
                throw new IOException($"Local file changed before upload completed: {localPath}");
            }

            await connection.Writer.WriteAsync(
                MessageType.FileData,
                buffer.AsMemory(0, bytesRead));
            remaining -= bytesRead;
        }

        var completed = await ReadRequiredAsync(connection.Transport);
        if (completed.Type != MessageType.FileCompleted)
        {
            throw new ProtocolException($"Expected FILE_COMPLETED, received {completed.Type}.");
        }

        ProtocolPayloads.RequireEmpty(completed);
        Console.WriteLine($"Uploaded {length} bytes to {remotePath}.");
        return 0;
    }

    internal async Task<int> DownloadAsync(string remotePath, string localPath)
    {
        var destinationPath = ResolveLocalDestination(localPath);
        using var connection = await ConnectAsync();
        await connection.Writer.WriteAsync(
            MessageType.DownloadFile,
            FileTransferPayloads.EncodeFilePath(remotePath));

        var metadata = await ReadRequiredAsync(connection.Transport);
        if (metadata.Type != MessageType.FileMetadata)
        {
            throw new ProtocolException($"Expected FILE_METADATA, received {metadata.Type}.");
        }

        var length = FileTransferPayloads.DecodeFileLength(metadata.Payload);
        var temporaryPath = CreateLocalTemporaryPath(destinationPath);
        var committed = false;
        try
        {
            await ReceiveDownloadAsync(
                connection.Transport,
                temporaryPath,
                length);
            File.Move(temporaryPath, destinationPath, overwrite: true);
            committed = true;
        }
        finally
        {
            if (!committed)
            {
                TryDelete(temporaryPath);
            }
        }

        Console.WriteLine($"Downloaded {length} bytes to {destinationPath}.");
        return 0;
    }

    private async Task<int> RunInteractiveAsync()
    {
        using var connection = await ConnectAsync();
        using var title = new TerminalTitleScope(_host);
        var standardOutput = Console.OpenStandardOutput();
        await using var outputBatcher = new TerminalOutputBatcher(standardOutput, _outputBatchDelay);
        using var inputPump = new ConsoleInputPump(connection.Writer, outputBatcher.NotifyInput);
        await inputPump.Ready;
        var initialSize = GetTerminalSize();
        await connection.Writer.WriteAsync(
            MessageType.CreateSession,
            ProtocolPayloads.EncodeCreateSession(new(initialSize, _profile)));

        var response = await ReadRequiredAsync(connection.Transport);
        if (response.Type != MessageType.SessionCreated)
        {
            throw new ProtocolException(
                $"Expected {MessageType.SessionCreated}, received {response.Type}.");
        }

        var sessionId = ProtocolPayloads.DecodeSessionId(response.Payload);

        // A remote ConPTY can start without clearing or homing the local terminal.
        // Align the terminal with its new screen before rendering any shell output.
        await standardOutput.WriteAsync(InitialTerminalReset);
        await standardOutput.FlushAsync();

        inputPump.StartForwarding();
        using var sessionCancellation = new CancellationTokenSource();
        var receiveTask = ReceiveAsync(
            connection.Transport,
            connection.Writer,
            sessionId,
            outputBatcher,
            sessionCancellation.Token);
        var inputTask = inputPump.Completion;
        var resizeTask = MonitorResizeAsync(
            connection.Writer,
            initialSize,
            sessionCancellation.Token);
        var pingTask = SendPingsAsync(connection.Writer, sessionCancellation.Token);

        try
        {
            var firstCompleted = await Task.WhenAny(receiveTask, inputTask);
            if (firstCompleted == inputTask)
            {
                await inputTask;
                return 0;
            }

            return await receiveTask;
        }
        finally
        {
            sessionCancellation.Cancel();
            inputPump.Dispose();
            try { await receiveTask; } catch { /* Preserve the first operation's outcome. */ }
            Observe(inputTask);
            Observe(resizeTask);
            Observe(pingTask);
        }
    }

    private static async Task ReceiveDownloadAsync(
        Stream transport,
        string temporaryPath,
        long length)
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
            var remaining = length;
            while (remaining > 0)
            {
                var frame = await ReadRequiredAsync(transport);
                if (frame.Type != MessageType.FileData)
                {
                    throw new ProtocolException(
                        $"Expected FILE_DATA during download, received {frame.Type}.");
                }

                if (frame.Payload.Length == 0 || frame.Payload.Length > remaining)
                {
                    throw new ProtocolException("FILE_DATA has an invalid download length.");
                }

                await destination.WriteAsync(frame.Payload);
                remaining -= frame.Payload.Length;
            }

            await destination.FlushAsync();
        }

        var completed = await ReadRequiredAsync(transport);
        if (completed.Type != MessageType.FileCompleted)
        {
            throw new ProtocolException($"Expected FILE_COMPLETED, received {completed.Type}.");
        }

        ProtocolPayloads.RequireEmpty(completed);
    }

    private static FileStream OpenLocalSource(string path)
    {
        return new FileStream(
            Path.GetFullPath(path),
            new FileStreamOptions
            {
                Mode = FileMode.Open,
                Access = FileAccess.Read,
                Share = FileShare.Read,
                Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
            });
    }

    private static string ResolveLocalDestination(string path)
    {
        var destinationPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(destinationPath);
        if (string.IsNullOrEmpty(directory)
            || string.IsNullOrEmpty(Path.GetFileName(destinationPath)))
        {
            throw new ArgumentException(
                $"Local destination must identify a file: {path}",
                nameof(path));
        }

        if (!Directory.Exists(directory))
        {
            throw new DirectoryNotFoundException(
                $"Local destination directory does not exist: {directory}");
        }

        return destinationPath;
    }

    private static string CreateLocalTemporaryPath(string destinationPath)
    {
        var directory = Path.GetDirectoryName(destinationPath)!;
        var fileName = Path.GetFileName(destinationPath);
        return Path.Combine(
            directory,
            $".{fileName}.{Guid.NewGuid():N}.farshell-download");
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

    private Task<ClientConnection> ConnectAsync() => ClientConnection.ConnectAsync(_host, _port, _stateDirectory);

    private async Task<int> ReceiveAsync(
        Stream transport,
        FrameWriter writer,
        Guid sessionId,
        TerminalOutputBatcher outputBatcher,
        CancellationToken cancellationToken)
    {
        var titles = new TerminalTitleRewriter();
        await using var attachedFileReceiver = new AttachedFileReceiver(
            _downloadRoot,
            writer);

        try
        {
            while (true)
            {
                var frame = await ReadRequiredAsync(transport, cancellationToken);
                switch (frame.Type)
                {
                    case MessageType.DataOut:
                        await outputBatcher.WriteAsync(titles.Transform(frame.Payload), cancellationToken);
                        break;

                    case MessageType.Ping:
                        ProtocolPayloads.RequireEmpty(frame);
                        await writer.WriteAsync(
                            MessageType.Pong,
                            ReadOnlyMemory<byte>.Empty,
                            cancellationToken);
                        break;

                    case MessageType.Pong:
                        ProtocolPayloads.RequireEmpty(frame);
                        break;

                    case MessageType.SessionExited:
                        var sessionExit = ProtocolPayloads.DecodeSessionExit(frame.Payload);
                        if (sessionExit.SessionId != sessionId)
                        {
                            throw new ProtocolException(
                                "Broker reported exit for a different session.");
                        }

                        return sessionExit.ExitCode;

                    case MessageType.SessionFileStart:
                    case MessageType.FileData:
                    case MessageType.SessionFileEnd:
                    case MessageType.SessionFileAbort:
                        await attachedFileReceiver.HandleAsync(frame, cancellationToken);
                        break;

                    default:
                        throw new ProtocolException(
                            $"Unsupported broker message while attached: {frame.Type}.");
                }
            }
        }
        finally { await outputBatcher.WriteAsync(titles.Complete()); }
    }

    private static async Task MonitorResizeAsync(
        FrameWriter writer,
        TerminalSize initialSize,
        CancellationToken cancellationToken)
    {
        var lastSize = initialSize;
        while (true)
        {
            await Task.Delay(ResizePollInterval, cancellationToken);
            var currentSize = GetTerminalSize();
            if (currentSize == lastSize)
            {
                continue;
            }

            await writer.WriteAsync(
                MessageType.Resize,
                ProtocolPayloads.EncodeResize(currentSize),
                cancellationToken);
            lastSize = currentSize;
        }
    }

    private static async Task SendPingsAsync(
        FrameWriter writer,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            await Task.Delay(PingInterval, cancellationToken);
            await writer.WriteAsync(
                MessageType.Ping,
                ReadOnlyMemory<byte>.Empty,
                cancellationToken);
        }
    }

    private static async Task<ProtocolFrame> ReadRequiredAsync(
        Stream transport,
        CancellationToken cancellationToken = default)
    {
        var frame = await FrameCodec.ReadAsync(transport, cancellationToken);
        if (frame is null)
        {
            throw new IOException("Broker closed the connection.");
        }

        if (frame.Type == MessageType.Error)
        {
            throw new ProtocolException(ProtocolPayloads.DecodeError(frame.Payload));
        }

        return frame;
    }

    private static TerminalSize GetTerminalSize()
    {
        try
        {
            var columns = Console.WindowWidth;
            var rows = Console.WindowHeight;
            if (columns > 0 && rows > 0)
            {
                return new TerminalSize(columns, rows);
            }
        }
        catch (IOException)
        {
            // Redirected stdio has no terminal dimensions.
        }

        return new TerminalSize(120, 30);
    }

    private static void Observe(Task task)
    {
        if (!task.IsCompleted)
        {
            _ = task.ContinueWith(
                completed => _ = completed.Exception,
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
    }

}
