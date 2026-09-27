using System.Buffers.Binary;
using System.Text;

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

public readonly record struct SessionExit(Guid SessionId, int ExitCode);

public static class ProtocolPayloads
{
    public const int CurrentVersion = 2;

    private const int IntegerPayloadLength = sizeof(int);
    private const int ResizePayloadLength = IntegerPayloadLength * 2;
    private const int SessionIdPayloadLength = 16;
    private const int SessionExitPayloadLength = SessionIdPayloadLength + IntegerPayloadLength;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static byte[] EncodeVersion(int version)
    {
        var payload = new byte[IntegerPayloadLength];
        BinaryPrimitives.WriteInt32LittleEndian(payload, version);
        return payload;
    }

    public static int DecodeVersion(ReadOnlySpan<byte> payload)
    {
        return DecodeInteger(payload, "HELLO");
    }

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

    public static byte[] EncodeSessionId(Guid sessionId)
    {
        var payload = new byte[SessionIdPayloadLength];
        if (!sessionId.TryWriteBytes(payload))
        {
            throw new InvalidOperationException("Could not encode the session ID.");
        }

        return payload;
    }

    public static Guid DecodeSessionId(ReadOnlySpan<byte> payload)
    {
        if (payload.Length != SessionIdPayloadLength)
        {
            throw new ProtocolException(
                $"Session ID payload must be {SessionIdPayloadLength} bytes, not {payload.Length}.");
        }

        return new Guid(payload);
    }

    public static byte[] EncodeSessionExit(Guid sessionId, int exitCode)
    {
        var payload = new byte[SessionExitPayloadLength];
        sessionId.TryWriteBytes(payload);
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(SessionIdPayloadLength), exitCode);
        return payload;
    }

    public static SessionExit DecodeSessionExit(ReadOnlySpan<byte> payload)
    {
        if (payload.Length != SessionExitPayloadLength)
        {
            throw new ProtocolException(
                $"SESSION_EXITED payload must be {SessionExitPayloadLength} bytes, not {payload.Length}.");
        }

        return new SessionExit(
            new Guid(payload[..SessionIdPayloadLength]),
            BinaryPrimitives.ReadInt32LittleEndian(payload[SessionIdPayloadLength..]));
    }

    public static byte[] EncodeError(string message)
    {
        return StrictUtf8.GetBytes(message);
    }

    public static string DecodeError(ReadOnlySpan<byte> payload)
    {
        try
        {
            return StrictUtf8.GetString(payload);
        }
        catch (DecoderFallbackException exception)
        {
            throw new ProtocolException($"ERROR payload is not valid UTF-8: {exception.Message}");
        }
    }

    public static void RequireEmpty(ProtocolFrame frame)
    {
        if (frame.Payload.Length != 0)
        {
            throw new ProtocolException($"{frame.Type} payload must be empty.");
        }
    }

    private static int DecodeInteger(ReadOnlySpan<byte> payload, string messageName)
    {
        if (payload.Length != IntegerPayloadLength)
        {
            throw new ProtocolException(
                $"{messageName} payload must be {IntegerPayloadLength} bytes, not {payload.Length}.");
        }

        return BinaryPrimitives.ReadInt32LittleEndian(payload);
    }

}
