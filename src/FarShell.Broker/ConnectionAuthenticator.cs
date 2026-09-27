using System.Security.Cryptography;
using FarShell.Protocol;

namespace FarShell.Broker;

internal sealed class ApiKeyConnectionAuthenticator(byte[] key)
{
    internal async ValueTask AuthenticateAsync(ConnectionContext connection,
        ProtocolFrame request, CancellationToken cancellationToken)
    {
        try
        {
            if (request.Type != MessageType.Authenticate || request.Payload.Length != 32
                || !CryptographicOperations.FixedTimeEquals(request.Payload, key))
            {
                throw new ProtocolException("Authentication failed. Run --pair to provision this client.");
            }
        }
        finally { CryptographicOperations.ZeroMemory(request.Payload); }

        await connection.Writer.WriteAsync(MessageType.Authenticated, ReadOnlyMemory<byte>.Empty, cancellationToken);
    }
}
