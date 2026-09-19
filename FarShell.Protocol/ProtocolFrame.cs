namespace FarShell.Protocol;

public sealed record ProtocolFrame(MessageType Type, byte[] Payload);
