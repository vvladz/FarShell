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
    CreateSession = 20,
    SessionCreated = 21,
    SessionExited = 27,
    Error = 28,
    UploadFile = 29,
    UploadReady = 30,
    DownloadFile = 31,
    FileMetadata = 32,
    FileData = 33,
    FileCompleted = 34,
    SessionFileStart = 35,
    SessionFileStatus = 36,
    SessionFileEnd = 37,
    SessionFileAbort = 38,
    SendFilesRequest = 39,
    SendFilesCompleted = 40,
}
