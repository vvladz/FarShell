namespace FarShell.Broker;

internal readonly record struct ConnectionIdentity(string OwnerId)
{
    internal static ConnectionIdentity Anonymous { get; } = new("anonymous");
}

internal interface IConnectionAuthenticator
{
    ValueTask<ConnectionIdentity> AuthenticateAsync(
        ConnectionContext connection,
        CancellationToken cancellationToken);
}

internal sealed class AnonymousConnectionAuthenticator : IConnectionAuthenticator
{
    public ValueTask<ConnectionIdentity> AuthenticateAsync(
        ConnectionContext connection,
        CancellationToken cancellationToken)
    {
        return ValueTask.FromResult(ConnectionIdentity.Anonymous);
    }
}

internal interface ISessionAuthorizer
{
    bool CanAccess(ConnectionIdentity identity, string ownerId);
}

internal sealed class OwnerSessionAuthorizer : ISessionAuthorizer
{
    public bool CanAccess(ConnectionIdentity identity, string ownerId)
    {
        return string.Equals(identity.OwnerId, ownerId, StringComparison.Ordinal);
    }
}
