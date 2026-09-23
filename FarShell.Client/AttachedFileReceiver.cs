using FarShell.Protocol;

namespace FarShell.Client;

internal sealed class AttachedFileReceiver : IAsyncDisposable
{
    private readonly string _downloadRoot;
    private readonly FrameWriter _writer;
    private IncomingFileTransfer? _incoming;
    private Guid? _rejectedTransferId;

    internal AttachedFileReceiver(string downloadRoot, FrameWriter writer)
    {
        _downloadRoot = downloadRoot;
        _writer = writer;
    }

    internal async Task HandleAsync(
        ProtocolFrame frame,
        CancellationToken cancellationToken)
    {
        switch (frame.Type)
        {
            case MessageType.SessionFileStart:
                await StartAsync(
                    FileTransferPayloads.DecodeSessionFileStart(frame.Payload),
                    cancellationToken);
                return;

            case MessageType.FileData:
                await WriteAsync(frame.Payload, cancellationToken);
                return;

            case MessageType.SessionFileEnd:
                await CompleteAsync(
                    ProtocolPayloads.DecodeSessionId(frame.Payload),
                    cancellationToken);
                return;

            case MessageType.SessionFileAbort:
                await AbortAsync(
                    FileTransferPayloads.DecodeSessionFileStatus(frame.Payload));
                return;

            default:
                throw new ArgumentOutOfRangeException(
                    nameof(frame),
                    frame.Type,
                    "Unsupported attached file-transfer message.");
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_incoming is not null)
        {
            await _incoming.DisposeAsync();
        }
    }

    private async Task StartAsync(
        SessionFileStart start,
        CancellationToken cancellationToken)
    {
        if (_incoming is not null || _rejectedTransferId.HasValue)
        {
            throw new ProtocolException(
                "Broker started a second concurrent send transfer.");
        }

        try
        {
            _incoming = IncomingFileTransfer.Create(_downloadRoot, start);
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            _rejectedTransferId = start.TransferId;
            await SendStatusAsync(
                start.TransferId,
                SessionFileStatusCode.Failed,
                exception.Message,
                cancellationToken);
            return;
        }

        await SendStatusAsync(
            start.TransferId,
            SessionFileStatusCode.Ready,
            cancellationToken: cancellationToken);
    }

    private async Task WriteAsync(
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken)
    {
        if (_incoming is null)
        {
            if (!_rejectedTransferId.HasValue)
            {
                throw new ProtocolException(
                    "Received FILE_DATA without an active send transfer.");
            }

            return;
        }

        try
        {
            await _incoming.WriteAsync(payload, cancellationToken);
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            var transferId = _incoming.TransferId;
            await _incoming.DisposeAsync();
            _incoming = null;
            _rejectedTransferId = transferId;
            await SendStatusAsync(
                transferId,
                SessionFileStatusCode.Failed,
                exception.Message,
                cancellationToken);
        }
    }

    private async Task CompleteAsync(
        Guid transferId,
        CancellationToken cancellationToken)
    {
        if (_incoming is null)
        {
            if (_rejectedTransferId != transferId)
            {
                throw new ProtocolException(
                    "SESSION_FILE_END has no matching send transfer.");
            }

            // END may have been queued before the broker received the failure.
            // Keep discarding until its ABORT arrives.
            return;
        }

        if (_incoming.TransferId != transferId)
        {
            throw new ProtocolException(
                "SESSION_FILE_END identifies a different transfer.");
        }

        var completed = false;
        try
        {
            await _incoming.CompleteAsync(cancellationToken);
            completed = true;
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            await SendStatusAsync(
                transferId,
                SessionFileStatusCode.Failed,
                exception.Message,
                cancellationToken);
        }
        finally
        {
            await _incoming.DisposeAsync();
            _incoming = null;
        }

        if (completed)
        {
            await SendStatusAsync(
                transferId,
                SessionFileStatusCode.Completed,
                cancellationToken: cancellationToken);
        }
    }

    private async Task AbortAsync(SessionFileStatus status)
    {
        if (status.Status != SessionFileStatusCode.Failed)
        {
            throw new ProtocolException(
                "SESSION_FILE_ABORT must contain a failed status.");
        }

        if (_incoming?.TransferId == status.TransferId)
        {
            await _incoming.DisposeAsync();
            _incoming = null;
        }
        else if (_rejectedTransferId == status.TransferId)
        {
            _rejectedTransferId = null;
        }
        else
        {
            throw new ProtocolException(
                "SESSION_FILE_ABORT has no matching send transfer.");
        }
    }

    private ValueTask SendStatusAsync(
        Guid transferId,
        SessionFileStatusCode status,
        string message = "",
        CancellationToken cancellationToken = default)
    {
        return _writer.WriteAsync(
            MessageType.SessionFileStatus,
            FileTransferPayloads.EncodeSessionFileStatus(transferId, status, message),
            cancellationToken);
    }

    private static bool IsRecoverable(Exception exception)
    {
        return exception is IOException
            or UnauthorizedAccessException
            or ArgumentException
            or NotSupportedException;
    }
}
