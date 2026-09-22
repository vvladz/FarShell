namespace FarShell.Broker;

internal sealed class SessionOperationException : InvalidOperationException
{
    internal SessionOperationException(string message)
        : base(message)
    {
    }
}
