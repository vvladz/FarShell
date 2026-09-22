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

public readonly record struct AttachSessionRequest(Guid SessionId, TerminalSize Size);

public readonly record struct SessionExit(Guid SessionId, int ExitCode);

public sealed record SessionInfo(
    Guid SessionId,
    bool IsAttached,
    DateTimeOffset CreatedAt,
    TerminalSize Size);

public static class ProtocolPayloads
{
    public const int CurrentVersion = 1;

    private const int IntegerPayloadLength = sizeof(int);
    private const int LongPayloadLength = sizeof(long);
    private const int ResizePayloadLength = IntegerPayloadLength * 2;
    private const int SessionIdPayloadLength = 16;
    private const int AttachSessionPayloadLength = SessionIdPayloadLength + ResizePayloadLength;
    private const int SessionExitPayloadLength = SessionIdPayloadLength + IntegerPayloadLength;
    private const int SessionInfoPayloadLength =
        SessionIdPayloadLength + sizeof(byte) + LongPayloadLength + ResizePayloadLength;
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

    public static byte[] EncodeAttachSession(Guid sessionId, TerminalSize size)
    {
        size.Validate();
        var payload = new byte[AttachSessionPayloadLength];
        sessionId.TryWriteBytes(payload);
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(SessionIdPayloadLength), size.Columns);
        BinaryPrimitives.WriteInt32LittleEndian(
            payload.AsSpan(SessionIdPayloadLength + IntegerPayloadLength),
            size.Rows);
        return payload;
    }

    public static AttachSessionRequest DecodeAttachSession(ReadOnlySpan<byte> payload)
    {
        if (payload.Length != AttachSessionPayloadLength)
        {
            throw new ProtocolException(
                $"ATTACH_SESSION payload must be {AttachSessionPayloadLength} bytes, not {payload.Length}.");
        }

        var sessionId = new Guid(payload[..SessionIdPayloadLength]);
        var size = new TerminalSize(
            BinaryPrimitives.ReadInt32LittleEndian(payload[SessionIdPayloadLength..]),
            BinaryPrimitives.ReadInt32LittleEndian(
                payload[(SessionIdPayloadLength + IntegerPayloadLength)..])).Validate();
        return new AttachSessionRequest(sessionId, size);
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

    public static byte[] EncodeSessionList(IReadOnlyCollection<SessionInfo> sessions)
    {
        var payloadLength = checked(IntegerPayloadLength + (sessions.Count * SessionInfoPayloadLength));
        if (payloadLength > FrameCodec.MaxPayloadLength)
        {
            throw new ArgumentOutOfRangeException(
                nameof(sessions),
                "The session list exceeds the maximum protocol payload size.");
        }

        var payload = new byte[payloadLength];
        BinaryPrimitives.WriteInt32LittleEndian(payload, sessions.Count);

        var offset = IntegerPayloadLength;
        foreach (var session in sessions)
        {
            session.Size.Validate();
            session.SessionId.TryWriteBytes(payload.AsSpan(offset, SessionIdPayloadLength));
            offset += SessionIdPayloadLength;
            payload[offset++] = session.IsAttached ? (byte)1 : (byte)0;
            BinaryPrimitives.WriteInt64LittleEndian(
                payload.AsSpan(offset, LongPayloadLength),
                session.CreatedAt.ToUnixTimeMilliseconds());
            offset += LongPayloadLength;
            BinaryPrimitives.WriteInt32LittleEndian(
                payload.AsSpan(offset, IntegerPayloadLength),
                session.Size.Columns);
            offset += IntegerPayloadLength;
            BinaryPrimitives.WriteInt32LittleEndian(
                payload.AsSpan(offset, IntegerPayloadLength),
                session.Size.Rows);
            offset += IntegerPayloadLength;
        }

        return payload;
    }

    public static IReadOnlyList<SessionInfo> DecodeSessionList(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < IntegerPayloadLength)
        {
            throw new ProtocolException("SESSION_LIST payload is missing its item count.");
        }

        var count = BinaryPrimitives.ReadInt32LittleEndian(payload);
        if (count < 0
            || payload.Length != IntegerPayloadLength + ((long)count * SessionInfoPayloadLength))
        {
            throw new ProtocolException("SESSION_LIST payload has an invalid length.");
        }

        var sessions = new SessionInfo[count];
        var offset = IntegerPayloadLength;
        for (var index = 0; index < count; index++)
        {
            var sessionId = new Guid(payload.Slice(offset, SessionIdPayloadLength));
            offset += SessionIdPayloadLength;

            var attachedValue = payload[offset++];
            if (attachedValue is not 0 and not 1)
            {
                throw new ProtocolException("SESSION_LIST contains an invalid attachment state.");
            }

            var createdAt = DateTimeOffset.FromUnixTimeMilliseconds(
                BinaryPrimitives.ReadInt64LittleEndian(payload.Slice(offset, LongPayloadLength)));
            offset += LongPayloadLength;

            var size = new TerminalSize(
                BinaryPrimitives.ReadInt32LittleEndian(
                    payload.Slice(offset, IntegerPayloadLength)),
                BinaryPrimitives.ReadInt32LittleEndian(
                    payload.Slice(offset + IntegerPayloadLength, IntegerPayloadLength))).Validate();
            offset += ResizePayloadLength;

            sessions[index] = new SessionInfo(
                sessionId,
                attachedValue == 1,
                createdAt,
                size);
        }

        return sessions;
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
