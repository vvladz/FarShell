using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using FarShell.Protocol;

namespace FarShell.Broker;

internal sealed class PairingService(string stateDirectory, TimeSpan? requestLifetime = null) : IAsyncDisposable
{
    internal static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(2);
    private readonly ConcurrentDictionary<Guid, PendingRequest> _pending = new();
    private readonly SemaphoreSlim _slots = new(8, 8);
    private readonly CancellationTokenSource _stop = new();
    private Task? _adminTask;

    private static string PipeName(string directory) => "FarShell.Approval." + Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory)).ToUpperInvariant())));

    internal void Start()
    {
        _adminTask = RunAdminAsync();
        if (_adminTask.IsFaulted) { _adminTask.GetAwaiter().GetResult(); }
    }

    internal bool Approve(Guid id) => _pending.TryGetValue(id, out var pending)
        && DateTimeOffset.UtcNow < pending.Expires && pending.Approved.TrySetResult();

    internal async Task RequestAsync(ConnectionContext connection, byte[] key)
    {
        if (!_slots.Wait(0)) { throw new ProtocolException("Too many pending pairing requests."); }
        var id = Guid.NewGuid();
        var approved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = new(approved, DateTimeOffset.UtcNow + (requestLifetime ?? Lifetime));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(connection.CancellationToken, _stop.Token);
        timeout.CancelAfter(requestLifetime ?? Lifetime);
        try
        {
            await connection.Writer.WriteAsync(MessageType.PairPending, ProtocolPayloads.EncodeSessionId(id), timeout.Token);
            Console.WriteLine($"Pairing request {id:N} from {connection.RemoteEndpoint}; expires in 2 minutes.");
            using var readStop = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
            var disconnected = FrameCodec.ReadAsync(connection.Transport, readStop.Token, 0).AsTask();
            try
            {
                var approval = approved.Task.WaitAsync(timeout.Token);
                if (await Task.WhenAny(approval, disconnected) == disconnected)
                {
                    await disconnected;
                    return;
                }

                await approval;
            }
            finally
            {
                readStop.Cancel();
                try { await disconnected; } catch (OperationCanceledException) { }
            }

            await connection.Writer.WriteAsync(MessageType.PairAccepted, key, timeout.Token);
        }
        catch (OperationCanceledException) when (!connection.CancellationToken.IsCancellationRequested && !_stop.IsCancellationRequested)
        {
            throw new ProtocolException("Pairing request expired.");
        }
        finally
        {
            _pending.TryRemove(id, out _);
            _slots.Release();
        }
    }

    private sealed record PendingRequest(TaskCompletionSource Approved, DateTimeOffset Expires);

    internal static async Task ApproveAsync(string directory, Guid requestId)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var pipe = new NamedPipeClientStream(".", PipeName(directory), PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await pipe.ConnectAsync(timeout.Token);
        await pipe.WriteAsync(requestId.ToByteArray(), timeout.Token);
        var response = new byte[1];
        await pipe.ReadExactlyAsync(response, timeout.Token);
        if (response[0] != 1) { throw new ArgumentException("Pairing request is unknown, expired, or already approved."); }
    }

    private async Task RunAdminAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            using var pipe = new NamedPipeServerStream(PipeName(stateDirectory), PipeDirection.InOut, 1,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            try
            {
                await pipe.WaitForConnectionAsync(_stop.Token);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                timeout.CancelAfter(TimeSpan.FromSeconds(3));
                var request = new byte[16];
                await pipe.ReadExactlyAsync(request, timeout.Token);
                await pipe.WriteAsync(new byte[] { Approve(new Guid(request)) ? (byte)1 : (byte)0 }, timeout.Token);
            }
            catch (Exception exception) when (exception is IOException or OperationCanceledException)
            {
                // Each local administrative request has its own bounded lifetime.
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        if (_adminTask is not null) { await _adminTask; }
        _stop.Dispose();
        _slots.Dispose();
    }
}
