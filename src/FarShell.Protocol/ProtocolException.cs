namespace FarShell.Protocol;

public sealed class ProtocolException : IOException
{
    public ProtocolException(string message)
        : base(message)
    {
    }
}
