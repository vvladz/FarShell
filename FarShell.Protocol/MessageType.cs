namespace FarShell.Protocol;

public enum MessageType : byte
{
    DataIn = 1,
    DataOut = 2,
    Resize = 3,
    Ping = 4,
    Pong = 5,
    Exit = 6,
}
