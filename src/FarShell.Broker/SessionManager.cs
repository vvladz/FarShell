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
    private bool _stopping;

    internal SessionManager(int maxSessions)
    {
        _sessionSlots = new SemaphoreSlim(maxSessions, maxSessions);
    }

    internal ShellSession Create(TerminalSize size, SessionLaunchProfile profile)
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

                var session = new ShellSession(sessionId, size, profile);
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
