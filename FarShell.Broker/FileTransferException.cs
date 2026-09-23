namespace FarShell.Broker;

internal sealed class FileTransferException : IOException
{
    internal FileTransferException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
