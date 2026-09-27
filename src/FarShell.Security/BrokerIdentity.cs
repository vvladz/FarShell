using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace FarShell.Security;

public sealed class BrokerIdentity : IDisposable
{
    public BrokerIdentity(X509Certificate2 certificate, byte[] key)
    {
        Certificate = certificate;
        Key = key;
        if (key.Length != 32 || !certificate.HasPrivateKey)
        {
            throw new ArgumentException("Invalid broker identity.");
        }
    }

    public X509Certificate2 Certificate { get; }
    public byte[] Key { get; }
    public string Fingerprint => GetFingerprint(Certificate);

    public static string GetFingerprint(X509Certificate certificate)
        => certificate.GetCertHashString(HashAlgorithmName.SHA256);

    public static BrokerIdentity Create()
    {
        using var rsa = RSA.Create(3072);
        var request = new CertificateRequest("CN=FarShell", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection { new("1.3.6.1.5.5.7.3.1") }, true));
        using var generated = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddYears(5));
        var pfx = generated.Export(X509ContentType.Pfx);
        try
        {
            return new BrokerIdentity(X509CertificateLoader.LoadPkcs12(pfx, null,
                X509KeyStorageFlags.UserKeySet | X509KeyStorageFlags.Exportable), RandomNumberGenerator.GetBytes(32));
        }
        finally { CryptographicOperations.ZeroMemory(pfx); }
    }

    public static BrokerIdentity LoadOrCreate(string stateDirectory)
    {
        var path = IdentityPath(stateDirectory);
        if (File.Exists(path)) { return Load(stateDirectory); }
        var identity = Create();
        try { identity.Save(stateDirectory); return identity; }
        catch { identity.Dispose(); throw; }
    }

    public static BrokerIdentity Load(string stateDirectory)
    {
        var stored = PrivateStorage.Read<StoredIdentity>(IdentityPath(stateDirectory));
        try
        {
            return new BrokerIdentity(X509CertificateLoader.LoadPkcs12(stored.Certificate, null,
                X509KeyStorageFlags.UserKeySet | X509KeyStorageFlags.Exportable), stored.Key);
        }
        finally { CryptographicOperations.ZeroMemory(stored.Certificate); }
    }

    public void Save(string stateDirectory)
    {
        var certificate = Certificate.Export(X509ContentType.Pfx);
        try { PrivateStorage.Write(IdentityPath(stateDirectory), new StoredIdentity(certificate, Key)); }
        finally { CryptographicOperations.ZeroMemory(certificate); }
    }

    public static FileStream AcquireLock(string directory)
    {
        PrivateStorage.EnsureDirectory(directory);
        try { return new FileStream(Path.Combine(directory, "broker.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException) { throw new IOException("The broker is running or its state directory is in use. Stop it before changing its identity."); }
    }

    private static string IdentityPath(string directory) => Path.Combine(directory, "broker.bin");
    private sealed record StoredIdentity(byte[] Certificate, byte[] Key);

    public void Dispose()
    {
        Certificate.Dispose();
        CryptographicOperations.ZeroMemory(Key);
    }
}
