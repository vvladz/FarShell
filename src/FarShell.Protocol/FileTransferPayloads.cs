using System.Buffers.Binary;
using System.Text;

namespace FarShell.Protocol;

public readonly record struct UploadFileRequest(string Path, long Length);

public readonly record struct SessionFileStart(
    Guid TransferId,
    long Length,
    string RelativePath);

public enum SessionFileStatusCode : byte
{
    Ready = 1,
    Completed = 2,
    Failed = 3,
}

public readonly record struct SessionFileStatus(
    Guid TransferId,
    SessionFileStatusCode Status,
    string Message);

public sealed record SendFilesRequest(string RootPath, IReadOnlyList<string> Patterns);

public static class FileTransferPayloads
{
    private const int IntegerPayloadLength = sizeof(int);
    private const int LongPayloadLength = sizeof(long);
    private const int SessionIdPayloadLength = 16;
    private const int SessionFilePrefixLength = SessionIdPayloadLength + LongPayloadLength;
    private const int SessionFileStatusPrefixLength = SessionIdPayloadLength + sizeof(byte);
    private const int MaximumSendPatterns = 256;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static byte[] EncodeUploadFile(string path, long length)
    {
        ValidateFileLength(length);
        var pathBytes = EncodeFilePath(path);
        var payloadLength = checked(LongPayloadLength + pathBytes.Length);
        if (payloadLength > FrameCodec.MaxPayloadLength)
        {
            throw new ArgumentOutOfRangeException(
                nameof(path),
                "The encoded file path exceeds the maximum protocol payload size.");
        }

        var payload = new byte[payloadLength];
        BinaryPrimitives.WriteInt64LittleEndian(payload, length);
        pathBytes.CopyTo(payload.AsSpan(LongPayloadLength));
        return payload;
    }

    public static UploadFileRequest DecodeUploadFile(ReadOnlySpan<byte> payload)
    {
        if (payload.Length <= LongPayloadLength)
        {
            throw new ProtocolException("UPLOAD_FILE payload is missing its file path.");
        }

        var length = BinaryPrimitives.ReadInt64LittleEndian(payload);
        ValidateFileLength(length);
        return new UploadFileRequest(
            DecodeFilePath(payload[LongPayloadLength..]),
            length);
    }

    public static byte[] EncodeFilePath(string path)
    {
        if (string.IsNullOrEmpty(path) || path.Contains('\0'))
        {
            throw new ArgumentException(
                "A file path must be non-empty and contain no null characters.",
                nameof(path));
        }

        var payload = StrictUtf8.GetBytes(path);
        if (payload.Length > FrameCodec.MaxPayloadLength)
        {
            throw new ArgumentOutOfRangeException(
                nameof(path),
                "The encoded file path exceeds the maximum protocol payload size.");
        }

        return payload;
    }

    public static string DecodeFilePath(ReadOnlySpan<byte> payload)
    {
        if (payload.IsEmpty)
        {
            throw new ProtocolException("File path payload must not be empty.");
        }

        try
        {
            var path = StrictUtf8.GetString(payload);
            if (path.Contains('\0'))
            {
                throw new ProtocolException("File path payload contains a null character.");
            }

            return path;
        }
        catch (DecoderFallbackException exception)
        {
            throw new ProtocolException(
                $"File path payload is not valid UTF-8: {exception.Message}");
        }
    }

    public static byte[] EncodeFileLength(long length)
    {
        ValidateFileLength(length);
        var payload = new byte[LongPayloadLength];
        BinaryPrimitives.WriteInt64LittleEndian(payload, length);
        return payload;
    }

    public static long DecodeFileLength(ReadOnlySpan<byte> payload)
    {
        if (payload.Length != LongPayloadLength)
        {
            throw new ProtocolException(
                $"FILE_METADATA payload must be {LongPayloadLength} bytes, not {payload.Length}.");
        }

        var length = BinaryPrimitives.ReadInt64LittleEndian(payload);
        ValidateFileLength(length);
        return length;
    }

    public static byte[] EncodeSessionFileStart(
        Guid transferId,
        long length,
        string relativePath)
    {
        ValidateFileLength(length);
        var pathBytes = EncodeFilePath(relativePath);
        var payloadLength = checked(SessionFilePrefixLength + pathBytes.Length);
        if (payloadLength > FrameCodec.MaxPayloadLength)
        {
            throw new ArgumentOutOfRangeException(
                nameof(relativePath),
                "The encoded relative path exceeds the maximum protocol payload size.");
        }

        var payload = new byte[payloadLength];
        transferId.TryWriteBytes(payload);
        BinaryPrimitives.WriteInt64LittleEndian(
            payload.AsSpan(SessionIdPayloadLength),
            length);
        pathBytes.CopyTo(payload.AsSpan(SessionFilePrefixLength));
        return payload;
    }

    public static SessionFileStart DecodeSessionFileStart(ReadOnlySpan<byte> payload)
    {
        if (payload.Length <= SessionFilePrefixLength)
        {
            throw new ProtocolException(
                "SESSION_FILE_START payload is missing its relative path.");
        }

        var length = BinaryPrimitives.ReadInt64LittleEndian(
            payload[SessionIdPayloadLength..]);
        ValidateFileLength(length);
        return new SessionFileStart(
            new Guid(payload[..SessionIdPayloadLength]),
            length,
            DecodeFilePath(payload[SessionFilePrefixLength..]));
    }

    public static byte[] EncodeSessionFileStatus(
        Guid transferId,
        SessionFileStatusCode status,
        string message = "")
    {
        ValidateSessionFileStatus(status, message);
        var messageBytes = StrictUtf8.GetBytes(message);
        var payloadLength = checked(SessionFileStatusPrefixLength + messageBytes.Length);
        if (payloadLength > FrameCodec.MaxPayloadLength)
        {
            throw new ArgumentOutOfRangeException(
                nameof(message),
                "The transfer status message exceeds the maximum protocol payload size.");
        }

        var payload = new byte[payloadLength];
        transferId.TryWriteBytes(payload);
        payload[SessionIdPayloadLength] = (byte)status;
        messageBytes.CopyTo(payload.AsSpan(SessionFileStatusPrefixLength));
        return payload;
    }

    public static SessionFileStatus DecodeSessionFileStatus(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < SessionFileStatusPrefixLength)
        {
            throw new ProtocolException(
                "SESSION_FILE_STATUS payload is missing required fields.");
        }

        var status = (SessionFileStatusCode)payload[SessionIdPayloadLength];
        string message;
        try
        {
            message = StrictUtf8.GetString(payload[SessionFileStatusPrefixLength..]);
        }
        catch (DecoderFallbackException exception)
        {
            throw new ProtocolException(
                $"SESSION_FILE_STATUS message is not valid UTF-8: {exception.Message}");
        }

        ValidateSessionFileStatus(status, message);
        return new SessionFileStatus(
            new Guid(payload[..SessionIdPayloadLength]),
            status,
            message);
    }

    public static byte[] EncodeSendFilesRequest(
        string rootPath,
        IReadOnlyCollection<string> patterns)
    {
        if (patterns.Count is < 1 or > MaximumSendPatterns)
        {
            throw new ArgumentOutOfRangeException(
                nameof(patterns),
                $"--send accepts 1 through {MaximumSendPatterns} path patterns.");
        }

        var rootBytes = EncodeFilePath(rootPath);
        var patternBytes = patterns.Select(EncodeFilePath).ToArray();
        var payloadLength = checked(
            IntegerPayloadLength
            + rootBytes.Length
            + IntegerPayloadLength
            + patternBytes.Sum(bytes => IntegerPayloadLength + bytes.Length));
        if (payloadLength > FrameCodec.MaxPayloadLength)
        {
            throw new ArgumentOutOfRangeException(
                nameof(patterns),
                "The send request exceeds the maximum protocol payload size.");
        }

        var payload = new byte[payloadLength];
        var offset = 0;
        WriteLengthPrefixedBytes(payload, ref offset, rootBytes);
        BinaryPrimitives.WriteInt32LittleEndian(
            payload.AsSpan(offset, IntegerPayloadLength),
            patternBytes.Length);
        offset += IntegerPayloadLength;
        foreach (var bytes in patternBytes)
        {
            WriteLengthPrefixedBytes(payload, ref offset, bytes);
        }

        return payload;
    }

    public static SendFilesRequest DecodeSendFilesRequest(ReadOnlySpan<byte> payload)
    {
        var offset = 0;
        var rootPath = ReadLengthPrefixedString(payload, ref offset, "send root path");
        if (payload.Length - offset < IntegerPayloadLength)
        {
            throw new ProtocolException(
                "SEND_FILES_REQUEST payload is missing its pattern count.");
        }

        var count = BinaryPrimitives.ReadInt32LittleEndian(
            payload.Slice(offset, IntegerPayloadLength));
        offset += IntegerPayloadLength;
        if (count is < 1 or > MaximumSendPatterns)
        {
            throw new ProtocolException($"Invalid send pattern count: {count}.");
        }

        var patterns = new string[count];
        for (var index = 0; index < patterns.Length; index++)
        {
            patterns[index] = ReadLengthPrefixedString(
                payload,
                ref offset,
                "send path pattern");
        }

        if (offset != payload.Length)
        {
            throw new ProtocolException(
                "SEND_FILES_REQUEST payload contains trailing data.");
        }

        return new SendFilesRequest(rootPath, patterns);
    }

    public static byte[] EncodeFileCount(int count)
    {
        if (count < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(count));
        }

        var payload = new byte[IntegerPayloadLength];
        BinaryPrimitives.WriteInt32LittleEndian(payload, count);
        return payload;
    }

    public static int DecodeFileCount(ReadOnlySpan<byte> payload)
    {
        if (payload.Length != IntegerPayloadLength)
        {
            throw new ProtocolException(
                $"SEND_FILES_COMPLETED payload must be {IntegerPayloadLength} bytes, not {payload.Length}.");
        }

        var count = BinaryPrimitives.ReadInt32LittleEndian(payload);
        if (count < 0)
        {
            throw new ProtocolException($"Invalid file count: {count}.");
        }

        return count;
    }

    private static void ValidateFileLength(long length)
    {
        if (length < 0)
        {
            throw new ProtocolException($"Invalid file length: {length}.");
        }
    }

    private static void ValidateSessionFileStatus(
        SessionFileStatusCode status,
        string message)
    {
        if (!Enum.IsDefined(status))
        {
            throw new ProtocolException($"Invalid session file status: {(byte)status}.");
        }

        if (status == SessionFileStatusCode.Failed)
        {
            if (string.IsNullOrWhiteSpace(message))
            {
                throw new ProtocolException(
                    "A failed session file status requires an error message.");
            }
        }
        else if (message.Length != 0)
        {
            throw new ProtocolException(
                $"{status} session file status must not contain a message.");
        }
    }

    private static void WriteLengthPrefixedBytes(
        Span<byte> destination,
        ref int offset,
        ReadOnlySpan<byte> value)
    {
        BinaryPrimitives.WriteInt32LittleEndian(
            destination.Slice(offset, IntegerPayloadLength),
            value.Length);
        offset += IntegerPayloadLength;
        value.CopyTo(destination[offset..]);
        offset += value.Length;
    }

    private static string ReadLengthPrefixedString(
        ReadOnlySpan<byte> payload,
        ref int offset,
        string fieldName)
    {
        if (payload.Length - offset < IntegerPayloadLength)
        {
            throw new ProtocolException(
                $"SEND_FILES_REQUEST is missing its {fieldName} length.");
        }

        var length = BinaryPrimitives.ReadInt32LittleEndian(
            payload.Slice(offset, IntegerPayloadLength));
        offset += IntegerPayloadLength;
        if (length <= 0 || length > payload.Length - offset)
        {
            throw new ProtocolException(
                $"SEND_FILES_REQUEST contains an invalid {fieldName} length.");
        }

        var value = DecodeFilePath(payload.Slice(offset, length));
        offset += length;
        return value;
    }
}
