using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Portal.AiHost.Core.Auth;
using Portal.AiHost.Core.Settings;

namespace Portal.AiHost.Core.Remote;

/// <summary>
/// The host's TLS identity for LAN access: a self-signed ECDSA P-256 certificate, created once. The certificate and
/// the private key (protected for the current Windows user) sit in the host's own data folder; devices pin the
/// certificate's SHA-256 fingerprint when they pair. The key never leaves that folder.
/// </summary>
public static class HostCertificate
{
    public const string CertificateFileName = "remote-cert.der";
    public const string KeyFileName = "remote-key.bin";

    /// <summary>Opens the stored certificate, or creates it. A transient failure to read the key is not treated as damage.</summary>
    public static X509Certificate2 LoadOrCreate(string directory, IHostKeyProtector protector, TimeProvider? time = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(directory);
        ArgumentNullException.ThrowIfNull(protector);
        var certPath = Path.Combine(directory, CertificateFileName);
        var keyPath = Path.Combine(directory, KeyFileName);
        if (File.Exists(certPath) && File.Exists(keyPath))
        {
            byte[]? stored = null;
            try
            {
                stored = protector.Unprotect(File.ReadAllBytes(keyPath));
            }
            catch (HostKeyDamagedException)
            {
                // Another user's key or damaged data: a new identity replaces it (devices must pair again). Any
                // other failure is temporary and is not treated as damage.
            }

            if (stored is not null)
            {
                try
                {
                    return Open(File.ReadAllBytes(certPath), stored);
                }
                catch (Exception ex) when (ex is CryptographicException or ArgumentException)
                {
                    // The stored files do not belong together or cannot be read: replaced as above.
                }
            }
        }

        var now = (time ?? TimeProvider.System).GetUtcNow();
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=Portal AI Host", key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));
        using var created = request.CreateSelfSigned(now.AddDays(-1), now.AddYears(20));
        var der = created.RawData;
        var pkcs8 = key.ExportPkcs8PrivateKey();
        try
        {
            AtomicFile.WriteAllBytes(keyPath, protector.Protect(pkcs8));
            AtomicFile.WriteAllBytes(certPath, der);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(pkcs8);
        }

        return Open(der, key.ExportPkcs8PrivateKey());
    }

    private static X509Certificate2 Open(byte[] der, byte[] pkcs8)
    {
        try
        {
            using var key = ECDsa.Create();
            key.ImportPkcs8PrivateKey(pkcs8, out _);
            using var publicOnly = X509CertificateLoader.LoadCertificate(der);
            using var withKey = publicOnly.CopyWithPrivateKey(key);

            // Windows TLS (SChannel) cannot use a key that exists only in memory ("platform does not support
            // ephemeral keys"). Loading it again from a PFX gives a key SChannel accepts; Windows removes that key
            // when the certificate object is disposed.
            var pfx = withKey.Export(X509ContentType.Pkcs12);
            try
            {
                return X509CertificateLoader.LoadPkcs12(pfx, null, X509KeyStorageFlags.DefaultKeySet);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(pfx);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(pkcs8);
        }
    }

    /// <summary>SHA-256 of the certificate (32 bytes).</summary>
    public static byte[] Fingerprint(X509Certificate2 certificate) => SHA256.HashData(certificate.RawData);
}
