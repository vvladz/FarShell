using System.Buffers.Binary;

namespace FarShell.Protocol;

public static class FrameCodec
{
    public const int HeaderLength = 5;
    public const int MaxPayloadLength = 16 * 1024 * 1024;

    public static async ValueTask<ProtocolFrame?> ReadAsync(
        Stream stream,
        CancellationToken cancellationToken = default)
    {
        var header = new byte[HeaderLength];
        var bytesRead = await stream.ReadAsync(header.AsMemory(0, 1), cancellationToken);
        if (bytesRead == 0)
        {
            return null;
        }

        await stream.ReadExactlyAsync(header.AsMemory(1), cancellationToken);

        var type = (MessageType)header[0];
        if (!Enum.IsDefined(type))
        {
            throw new ProtocolException($"Unknown message type: {header[0]}.");
        }

        var payloadLength = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(1));
        if (payloadLength is < 0 or > MaxPayloadLength)
        {
            throw new ProtocolException(
                $"Invalid payload length {payloadLength}; maximum is {MaxPayloadLength} bytes.");
        }

        var payload = payloadLength == 0 ? Array.Empty<byte>() : new byte[payloadLength];
        if (payload.Length > 0)
        {
            await stream.ReadExactlyAsync(payload, cancellationToken);
        }

        return new ProtocolFrame(type, payload);
    }

    public static async ValueTask WriteAsync(
        Stream stream,
        MessageType type,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken = default)
    {
        if (payload.Length > MaxPayloadLength)
        {
            throw new ArgumentOutOfRangeException(
                nameof(payload),
                payload.Length,
                $"Payload exceeds {MaxPayloadLength} bytes.");
        }

        var header = new byte[HeaderLength];
        header[0] = (byte)type;
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(1), payload.Length);

        await stream.WriteAsync(header, cancellationToken);
        if (!payload.IsEmpty)
        {
            await stream.WriteAsync(payload, cancellationToken);
        }
    }
}
