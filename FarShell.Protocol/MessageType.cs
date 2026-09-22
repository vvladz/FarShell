namespace FarShell.Protocol;

public enum MessageType : byte
{
    DataIn = 1,
    DataOut = 2,
    Resize = 3,
    Ping = 4,
    Pong = 5,
    Hello = 16,
    HelloAck = 17,
    ListSessions = 18,
    SessionList = 19,
    CreateSession = 20,
    SessionCreated = 21,
    AttachSession = 22,
    SessionAttached = 23,
    DetachSession = 24,
    TerminateSession = 25,
    SessionTerminated = 26,
    SessionExited = 27,
    Error = 28,
}
