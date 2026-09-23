using System.Runtime.Versioning;
using FarShell.ConPTY;
using FarShell.Protocol;

namespace FarShell.Broker;

[SupportedOSPlatform("windows10.0.17763")]
internal sealed class ShellSession : IAsyncDisposable
{
    private readonly object _stateLock = new();
    private readonly ConPtySession _conPty;
    private readonly SessionFileSendService _fileSender;
    private readonly Task<int> _completion;
    private SessionAttachment? _attachment;
    private TerminalSize _size;
    private bool _terminating;
    private bool _exited;
    private bool _disposed;

    internal ShellSession(Guid id, string ownerId, TerminalSize size)
    {
        Id = id;
        OwnerId = ownerId;
        CreatedAt = DateTimeOffset.UtcNow;
        _size = size.Validate();
        _fileSender = new SessionFileSendService(id, GetActiveAttachment);
        _conPty = ConPtySession.Start(
            size.Columns,
            size.Rows,
            environmentVariables: _fileSender.CreateProcessEnvironment());
        _fileSender.Start();
        _completion = RunAsync();
    }

    internal Guid Id { get; }

    internal string OwnerId { get; }

    internal DateTimeOffset CreatedAt { get; }

    internal Task<int> Completion => _completion;

    internal SessionInfo GetInfo()
    {
        lock (_stateLock)
        {
            return new SessionInfo(Id, _attachment is not null, CreatedAt, _size);
        }
    }

    internal SessionAttachment ReserveAttachment(ConnectionContext connection)
    {
        lock (_stateLock)
        {
            if (_terminating || _exited)
            {
                throw new SessionOperationException("Session has already exited.");
            }

            if (_attachment is not null)
            {
                throw new SessionOperationException("Session is already attached.");
            }

            _attachment = new SessionAttachment(connection);
            return _attachment;
        }
    }

    internal void Activate(SessionAttachment attachment, TerminalSize size)
    {
        size.Validate();
        lock (_stateLock)
        {
            RequireCurrentAttachment(attachment);
            _size = size;
            attachment.IsActive = true;
        }

        try
        {
            _conPty.Resize(size.Columns, size.Rows);
        }
        catch
        {
            Detach(attachment);
            throw;
        }
    }

    internal async ValueTask WriteInputAsync(
        SessionAttachment attachment,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken)
    {
        lock (_stateLock)
        {
            RequireActiveAttachment(attachment);
        }

        await _conPty.Input.WriteAsync(payload, cancellationToken);
        await _conPty.Input.FlushAsync(cancellationToken);
    }

    internal void Resize(SessionAttachment attachment, TerminalSize size)
    {
        size.Validate();
        lock (_stateLock)
        {
            RequireActiveAttachment(attachment);
            _size = size;
        }

        _conPty.Resize(size.Columns, size.Rows);
    }

    internal void HandleFileStatus(
        SessionAttachment attachment,
        SessionFileStatus status)
    {
        lock (_stateLock)
        {
            RequireActiveAttachment(attachment);
        }

        _fileSender.HandleStatus(attachment, status);
    }

    internal void Detach(SessionAttachment attachment)
    {
        var removed = false;
        lock (_stateLock)
        {
            if (ReferenceEquals(_attachment, attachment))
            {
                _attachment = null;
                removed = true;
            }
        }

        if (removed)
        {
            attachment.Dispose();
        }
    }

    internal void Terminate()
    {
        lock (_stateLock)
        {
            if (_terminating)
            {
                return;
            }

            _terminating = true;
        }

        _conPty.Terminate();
    }

    public async ValueTask DisposeAsync()
    {
        lock (_stateLock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        Terminate();
        await IgnoreCompletionAsync(_completion);
        SessionAttachment? attachment;
        lock (_stateLock)
        {
            attachment = _attachment;
            _attachment = null;
        }

        attachment?.Dispose();
        _conPty.Dispose();
        await _fileSender.DisposeAsync();
    }

    private async Task<int> RunAsync()
    {
        var exitTask = _conPty.WaitForExitAsync();
        var buffer = new byte[32 * 1024];

        try
        {
            while (true)
            {
                int bytesRead;
                try
                {
                    bytesRead = await _conPty.Output.ReadAsync(buffer);
                }
                catch (Exception) when (IsTerminating())
                {
                    break;
                }

                if (bytesRead == 0)
                {
                    break;
                }

                await ForwardOutputAsync(buffer.AsMemory(0, bytesRead));
            }

            var exitCode = await exitTask;
            SessionAttachment? attachment;
            lock (_stateLock)
            {
                _exited = true;
                attachment = _attachment is { IsActive: true } ? _attachment : null;
            }

            if (attachment is not null)
            {
                try
                {
                    await attachment.Connection.Writer.WriteAsync(
                        MessageType.SessionExited,
                        ProtocolPayloads.EncodeSessionExit(Id, exitCode),
                        attachment.CancellationToken);
                }
                catch (Exception exception) when (IsConnectionFailure(exception))
                {
                    Detach(attachment);
                }
            }

            return exitCode;
        }
        finally
        {
            lock (_stateLock)
            {
                _exited = true;
            }
        }
    }

    private async ValueTask ForwardOutputAsync(ReadOnlyMemory<byte> payload)
    {
        SessionAttachment? attachment;
        lock (_stateLock)
        {
            attachment = _attachment is { IsActive: true } ? _attachment : null;
        }

        if (attachment is null)
        {
            return;
        }

        try
        {
            await attachment.Connection.Writer.WriteAsync(
                MessageType.DataOut,
                payload,
                attachment.CancellationToken);
        }
        catch (Exception exception) when (IsConnectionFailure(exception))
        {
            Detach(attachment);
        }
    }

    private bool IsTerminating()
    {
        lock (_stateLock)
        {
            return _terminating;
        }
    }

    private SessionAttachment? GetActiveAttachment()
    {
        lock (_stateLock)
        {
            return _attachment is { IsActive: true } ? _attachment : null;
        }
    }

    private void RequireCurrentAttachment(SessionAttachment attachment)
    {
        if (!ReferenceEquals(_attachment, attachment))
        {
            throw new SessionOperationException("Connection is no longer attached to the session.");
        }
    }

    private void RequireActiveAttachment(SessionAttachment attachment)
    {
        RequireCurrentAttachment(attachment);
        if (!attachment.IsActive)
        {
            throw new SessionOperationException("Session attachment is not active.");
        }
    }

    private static bool IsConnectionFailure(Exception exception)
    {
        return exception is IOException or ObjectDisposedException or OperationCanceledException;
    }

    private static async Task IgnoreCompletionAsync(Task task)
    {
        try
        {
            await task;
        }
        catch
        {
            // Session cleanup must release native resources after a failed pump.
        }
    }
}

internal sealed class SessionAttachment : IDisposable
{
    private readonly CancellationTokenSource _lifetime;

    internal SessionAttachment(ConnectionContext connection)
    {
        Connection = connection;
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(connection.CancellationToken);
    }

    internal ConnectionContext Connection { get; }

    internal CancellationToken CancellationToken => _lifetime.Token;

    internal bool IsActive { get; set; }

    public void Dispose()
    {
        _lifetime.Cancel();
        _lifetime.Dispose();
    }
}
