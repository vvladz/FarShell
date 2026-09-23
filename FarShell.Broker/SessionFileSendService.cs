using System.IO.Pipes;
using FarShell.Protocol;

namespace FarShell.Broker;

internal sealed class SessionFileSendService : IAsyncDisposable
{
    private const int BufferSize = 64 * 1024;
    private readonly object _stateLock = new();
    private readonly Func<SessionAttachment?> _getAttachment;
    private readonly CancellationTokenSource _lifetime = new();
    private Task? _serverTask;
    private PendingSessionFile? _pending;

    internal SessionFileSendService(
        Guid sessionId,
        Func<SessionAttachment?> getAttachment)
    {
        _getAttachment = getAttachment;
        PipeName = $"FarShell.Send.{sessionId:N}.{Guid.NewGuid():N}";
    }

    internal string PipeName { get; }

    internal void Start()
    {
        _serverTask = RunServerAsync();
    }

    internal IReadOnlyDictionary<string, string> CreateProcessEnvironment()
    {
        var helperPath = Path.Combine(AppContext.BaseDirectory, "FarShell.Broker.exe");
        if (!File.Exists(helperPath))
        {
            throw new FileNotFoundException(
                "Could not locate the FarShell send command.",
                helperPath);
        }

        var helperDirectory = Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);
        var inheritedPath = Environment.GetEnvironmentVariable("PATH");
        var path = string.IsNullOrEmpty(inheritedPath)
            ? helperDirectory
            : $"{helperDirectory}{Path.PathSeparator}{inheritedPath}";
        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [SendCommand.PipeEnvironmentVariable] = PipeName,
            ["PATH"] = path,
        };
    }

    internal void HandleStatus(
        SessionAttachment attachment,
        SessionFileStatus status)
    {
        lock (_stateLock)
        {
            if (_pending is null
                || !ReferenceEquals(_pending.Attachment, attachment)
                || _pending.TransferId != status.TransferId)
            {
                throw new ProtocolException(
                    "SESSION_FILE_STATUS does not match the active transfer.");
            }

            _pending.Accept(status);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _lifetime.Cancel();
        try
        {
            if (_serverTask is not null)
            {
                await _serverTask;
            }
        }
        catch (OperationCanceledException)
        {
            // Session shutdown cancels the pending pipe accept or transfer.
        }
        finally
        {
            _lifetime.Dispose();
        }
    }

    private async Task RunServerAsync()
    {
        while (!_lifetime.IsCancellationRequested)
        {
            using var pipe = new NamedPipeServerStream(
                PipeName,
                PipeDirection.InOut,
                maxNumberOfServerInstances: 1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            try
            {
                await pipe.WaitForConnectionAsync(_lifetime.Token);
                await HandleRequestAsync(pipe, _lifetime.Token);
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                await TrySendErrorAsync(pipe, exception.Message);
            }
        }
    }

    private async Task HandleRequestAsync(
        Stream pipe,
        CancellationToken cancellationToken)
    {
        var frame = await FrameCodec.ReadAsync(pipe, cancellationToken)
            ?? throw new ProtocolException("send command sent an empty request.");
        if (frame.Type != MessageType.SendFilesRequest)
        {
            throw new ProtocolException(
                $"Expected SEND_FILES_REQUEST, received {frame.Type}.");
        }

        var files = SendFileResolver.Resolve(
            FileTransferPayloads.DecodeSendFilesRequest(frame.Payload));
        var attachment = _getAttachment()
            ?? throw new SessionOperationException(
                "--send requires an attached FarShell client.");
        foreach (var file in files)
        {
            await SendFileAsync(attachment, file, cancellationToken);
        }

        await FrameCodec.WriteAsync(
            pipe,
            MessageType.SendFilesCompleted,
            FileTransferPayloads.EncodeFileCount(files.Count),
            cancellationToken);
    }

    private async Task SendFileAsync(
        SessionAttachment attachment,
        SendFile file,
        CancellationToken cancellationToken)
    {
        await using var source = new FileStream(
            file.FullPath,
            new FileStreamOptions
            {
                Mode = FileMode.Open,
                Access = FileAccess.Read,
                Share = FileShare.Read,
                Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
            });
        var transferId = Guid.NewGuid();
        var pending = new PendingSessionFile(attachment, transferId);
        lock (_stateLock)
        {
            if (_pending is not null)
            {
                throw new InvalidOperationException("Another send transfer is already active.");
            }

            _pending = pending;
        }

        using var transferCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            attachment.CancellationToken);
        var token = transferCancellation.Token;
        var started = false;
        try
        {
            await attachment.Connection.Writer.WriteAsync(
                MessageType.SessionFileStart,
                FileTransferPayloads.EncodeSessionFileStart(
                    transferId,
                    source.Length,
                    file.RelativePath),
                token);
            started = true;

            var ready = await pending.Ready.Task.WaitAsync(token);
            ThrowIfFailed(ready);

            var remaining = source.Length;
            var buffer = new byte[BufferSize];
            while (remaining > 0)
            {
                if (pending.Completion.Task.IsCompleted)
                {
                    ThrowIfFailed(await pending.Completion.Task);
                }

                var bytesRead = await source.ReadAsync(
                    buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)),
                    token);
                if (bytesRead == 0)
                {
                    throw new EndOfStreamException(
                        $"Remote file changed during send: {file.RelativePath}");
                }

                await attachment.Connection.Writer.WriteAsync(
                    MessageType.FileData,
                    buffer.AsMemory(0, bytesRead),
                    token);
                remaining -= bytesRead;
            }

            await attachment.Connection.Writer.WriteAsync(
                MessageType.SessionFileEnd,
                ProtocolPayloads.EncodeSessionId(transferId),
                token);
            var completed = await pending.Completion.Task.WaitAsync(token);
            ThrowIfFailed(completed);
            if (completed.Status != SessionFileStatusCode.Completed)
            {
                throw new ProtocolException(
                    $"Unexpected final session file status: {completed.Status}.");
            }
        }
        catch (Exception exception)
        {
            if (started)
            {
                await TrySendAbortAsync(attachment, transferId, exception.Message);
            }

            throw;
        }
        finally
        {
            lock (_stateLock)
            {
                if (ReferenceEquals(_pending, pending))
                {
                    _pending = null;
                }
            }
        }
    }

    private static void ThrowIfFailed(SessionFileStatus status)
    {
        if (status.Status == SessionFileStatusCode.Failed)
        {
            throw new FileTransferException(
                status.Message,
                new IOException("The attached client rejected the transfer."));
        }
    }

    private static async Task TrySendAbortAsync(
        SessionAttachment attachment,
        Guid transferId,
        string message)
    {
        try
        {
            await attachment.Connection.Writer.WriteAsync(
                MessageType.SessionFileAbort,
                FileTransferPayloads.EncodeSessionFileStatus(
                    transferId,
                    SessionFileStatusCode.Failed,
                    string.IsNullOrWhiteSpace(message) ? "send transfer failed." : message),
                attachment.CancellationToken);
        }
        catch
        {
            // The original transfer failure remains the useful diagnostic.
        }
    }

    private static async Task TrySendErrorAsync(Stream pipe, string message)
    {
        if (pipe is not PipeStream { IsConnected: true })
        {
            return;
        }

        try
        {
            await FrameCodec.WriteAsync(
                pipe,
                MessageType.Error,
                ProtocolPayloads.EncodeError(
                    string.IsNullOrWhiteSpace(message) ? "send failed." : message));
        }
        catch
        {
            // The helper may have disconnected after the original failure.
        }
    }

    private sealed class PendingSessionFile
    {
        private PendingPhase _phase = PendingPhase.AwaitingReady;

        internal PendingSessionFile(SessionAttachment attachment, Guid transferId)
        {
            Attachment = attachment;
            TransferId = transferId;
        }

        internal SessionAttachment Attachment { get; }

        internal Guid TransferId { get; }

        internal TaskCompletionSource<SessionFileStatus> Ready { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource<SessionFileStatus> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal void Accept(SessionFileStatus status)
        {
            switch (_phase, status.Status)
            {
                case (PendingPhase.AwaitingReady, SessionFileStatusCode.Ready):
                    _phase = PendingPhase.AwaitingCompletion;
                    Ready.SetResult(status);
                    return;

                case (PendingPhase.AwaitingReady, SessionFileStatusCode.Failed):
                    _phase = PendingPhase.Finished;
                    Ready.SetResult(status);
                    return;

                case (PendingPhase.AwaitingCompletion, SessionFileStatusCode.Completed):
                case (PendingPhase.AwaitingCompletion, SessionFileStatusCode.Failed):
                    _phase = PendingPhase.Finished;
                    Completion.SetResult(status);
                    return;

                default:
                    throw new ProtocolException(
                        $"Unexpected {status.Status} status for the active session file transfer.");
            }
        }

        private enum PendingPhase
        {
            AwaitingReady,
            AwaitingCompletion,
            Finished,
        }
    }
}
