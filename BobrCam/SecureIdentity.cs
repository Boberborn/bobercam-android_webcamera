using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace BobrCam;

public static class SecureIdentity
{
    private const string Password = "BobrCamLocalIdentity";

    public static X509Certificate2 GetOrCreate(string fileName, string commonName)
    {
        var path = Path.Combine(FileSystem.AppDataDirectory, fileName);
        if (File.Exists(path)) return new X509Certificate2(path, Password, X509KeyStorageFlags.Exportable);
        using var key = RSA.Create(2048);
        var request = new CertificateRequest($"CN={commonName}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        using var created = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(10));
        var pfx = created.Export(X509ContentType.Pfx, Password);
        File.WriteAllBytes(path, pfx);
        return new X509Certificate2(pfx, Password, X509KeyStorageFlags.Exportable);
    }

    public static string Fingerprint(X509Certificate2 certificate) => certificate.GetCertHashString(HashAlgorithmName.SHA256);

    public static byte[] GetOrCreatePairingToken()
    {
        var saved = Preferences.Default.Get("phone_pairing_token", string.Empty);
        if (!string.IsNullOrEmpty(saved))
        {
            try
            {
                var existing = Convert.FromBase64String(saved);
                if (existing.Length == 32)
                    return existing;
            }
            catch (FormatException)
            {
            }
        }
        var token = RandomNumberGenerator.GetBytes(32);
        Preferences.Default.Set("phone_pairing_token", Convert.ToBase64String(token));
        return token;
    }

    public static bool FixedTimeEquals(string left, string right)
    {
        try { return CryptographicOperations.FixedTimeEquals(Convert.FromHexString(left), Convert.FromHexString(right)); }
        catch { return false; }
    }
}
