using System.Security.Cryptography;
using System.Text;

namespace FarShell.Security;

public sealed record ClientCredentials(string Fingerprint, byte[]? Key)
{
    public static string Endpoint(string host, int port) => $"{host.ToLowerInvariant()}:{port}";
    private static string PathFor(string directory, string endpoint) => Path.Combine(directory, "servers",
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(endpoint))) + ".bin");

    public static ClientCredentials? Load(string directory, string endpoint)
    {
        var path = PathFor(directory, endpoint);
        return File.Exists(path) ? PrivateStorage.Read<ClientCredentials>(path) : null;
    }

    public void Save(string directory, string endpoint)
    {
        PrivateStorage.EnsureDirectory(directory);
        PrivateStorage.Write(PathFor(directory, endpoint), this);
    }
}
