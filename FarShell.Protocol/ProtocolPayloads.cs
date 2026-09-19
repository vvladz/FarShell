using System.Buffers.Binary;

namespace FarShell.Protocol;

public readonly record struct TerminalSize(int Columns, int Rows)
{
    public TerminalSize Validate()
    {
        if (Columns is < 1 or > short.MaxValue)
        {
            throw new ProtocolException($"Invalid terminal width: {Columns}.");
        }

        if (Rows is < 1 or > short.MaxValue)
        {
            throw new ProtocolException($"Invalid terminal height: {Rows}.");
        }

        return this;
    }
}

public static class ProtocolPayloads
{
    private const int IntegerPayloadLength = sizeof(int);
    private const int ResizePayloadLength = IntegerPayloadLength * 2;

    public static byte[] EncodeResize(TerminalSize size)
    {
        size.Validate();
        var payload = new byte[ResizePayloadLength];
        BinaryPrimitives.WriteInt32LittleEndian(payload, size.Columns);
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(IntegerPayloadLength), size.Rows);
        return payload;
    }

    public static TerminalSize DecodeResize(ReadOnlySpan<byte> payload)
    {
        if (payload.Length != ResizePayloadLength)
        {
            throw new ProtocolException(
                $"RESIZE payload must be {ResizePayloadLength} bytes, not {payload.Length}.");
        }

        return new TerminalSize(
            BinaryPrimitives.ReadInt32LittleEndian(payload),
            BinaryPrimitives.ReadInt32LittleEndian(payload[IntegerPayloadLength..])).Validate();
    }

    public static byte[] EncodeExitCode(int exitCode)
    {
        var payload = new byte[IntegerPayloadLength];
        BinaryPrimitives.WriteInt32LittleEndian(payload, exitCode);
        return payload;
    }

    public static int DecodeExitCode(ReadOnlySpan<byte> payload)
    {
        if (payload.Length != IntegerPayloadLength)
        {
            throw new ProtocolException(
                $"EXIT payload must be {IntegerPayloadLength} bytes, not {payload.Length}.");
        }

        return BinaryPrimitives.ReadInt32LittleEndian(payload);
    }
}
