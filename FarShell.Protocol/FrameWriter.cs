namespace FarShell.Protocol;

public sealed class FrameWriter : IDisposable
{
    private readonly Stream _stream;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public FrameWriter(Stream stream)
    {
        _stream = stream;
    }

    public async ValueTask WriteAsync(
        MessageType type,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await FrameCodec.WriteAsync(_stream, type, payload, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        _gate.Dispose();
    }
}
