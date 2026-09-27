namespace FarShell.Broker;

internal interface IConnectionAuthenticator
{
    ValueTask AuthenticateAsync(
        ConnectionContext connection,
        CancellationToken cancellationToken);
}

internal sealed class AnonymousConnectionAuthenticator : IConnectionAuthenticator
{
    public ValueTask AuthenticateAsync(
        ConnectionContext connection,
        CancellationToken cancellationToken)
    {
        return ValueTask.CompletedTask;
    }
}
