using System.Collections.Concurrent;
using System.Runtime.Versioning;
using FarShell.Protocol;

namespace FarShell.Broker;

[SupportedOSPlatform("windows10.0.17763")]
internal sealed class SessionManager : IAsyncDisposable
{
    private readonly object _lifecycleLock = new();
    private readonly ConcurrentDictionary<Guid, ShellSession> _sessions = new();
    private readonly ConcurrentDictionary<Guid, Task> _monitors = new();
    private readonly SemaphoreSlim _sessionSlots;
    private readonly ISessionAuthorizer _authorizer;
    private bool _stopping;

    internal SessionManager(int maxSessions, ISessionAuthorizer authorizer)
    {
        _sessionSlots = new SemaphoreSlim(maxSessions, maxSessions);
        _authorizer = authorizer;
    }

    internal ShellSession Create(ConnectionIdentity identity, TerminalSize size)
    {
        if (!_sessionSlots.Wait(0))
        {
            throw new SessionOperationException("Active session limit reached.");
        }

        try
        {
            lock (_lifecycleLock)
            {
                if (_stopping)
                {
                    throw new SessionOperationException("Broker is shutting down.");
                }

                Guid sessionId;
                do
                {
                    sessionId = Guid.NewGuid();
                }
                while (_sessions.ContainsKey(sessionId));

                var session = new ShellSession(sessionId, identity.OwnerId, size);
                if (!_sessions.TryAdd(session.Id, session))
                {
                    throw new InvalidOperationException("Could not register a new session.");
                }

                var monitor = MonitorAsync(session);
                _monitors[session.Id] = monitor;
                _ = monitor.ContinueWith(
                    completed => _monitors.TryRemove(session.Id, out var _),
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
                return session;
            }
        }
        catch
        {
            _sessionSlots.Release();
            throw;
        }
    }

    internal IReadOnlyList<SessionInfo> List(ConnectionIdentity identity)
    {
        return _sessions.Values
            .Where(session => _authorizer.CanAccess(identity, session.OwnerId))
            .Select(session => session.GetInfo())
            .OrderBy(session => session.CreatedAt)
            .ToArray();
    }

    internal ShellSession Get(ConnectionIdentity identity, Guid sessionId)
    {
        if (!_sessions.TryGetValue(sessionId, out var session)
            || !_authorizer.CanAccess(identity, session.OwnerId))
        {
            throw new SessionOperationException("Session not found.");
        }

        return session;
    }

    internal async Task TerminateAsync(ConnectionIdentity identity, Guid sessionId)
    {
        var session = Get(identity, sessionId);
        session.Terminate();
        await session.Completion;
    }

    public async ValueTask DisposeAsync()
    {
        ShellSession[] sessions;
        lock (_lifecycleLock)
        {
            if (_stopping)
            {
                return;
            }

            _stopping = true;
            sessions = _sessions.Values.ToArray();
        }

        foreach (var session in sessions)
        {
            session.Terminate();
        }

        await Task.WhenAll(_monitors.Values.ToArray());
        _sessionSlots.Dispose();
    }

    private async Task MonitorAsync(ShellSession session)
    {
        try
        {
            await session.Completion;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Session {session.Id:N} error: {exception.Message}");
        }
        finally
        {
            if (_sessions.TryRemove(session.Id, out _))
            {
                _sessionSlots.Release();
            }

            await session.DisposeAsync();
        }
    }
}
