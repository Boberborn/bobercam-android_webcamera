using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace BobrCam;

public static class SecureIdentity
{
    private const string LegacyCertificatePassword = "BobrCamLocalIdentity";
    private const string CertificatePasswordKey = "receiver_identity_password";
    private const string PairingTokenKey = "phone_pairing_token";
    private const string ReceiverFingerprintKey = "paired_receiver";
    private const string TrustedPhoneTokenKey = "trusted_phone_token";
    private static readonly SemaphoreSlim TrustedPhoneGate = new(1, 1);

    public static async Task<X509Certificate2> GetOrCreateAsync(
        string fileName,
        string commonName)
    {
        var path = Path.Combine(FileSystem.AppDataDirectory, fileName);
        var password = await SecureStorage.Default.GetAsync(CertificatePasswordKey);
        if (File.Exists(path))
        {
            if (!string.IsNullOrEmpty(password))
                return LoadCertificate(path, password);

            using var legacy = X509CertificateLoader.LoadPkcs12FromFile(
                path,
                LegacyCertificatePassword,
                X509KeyStorageFlags.Exportable | X509KeyStorageFlags.UserKeySet);
            password = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
            await SecureStorage.Default.SetAsync(CertificatePasswordKey, password);
            File.WriteAllBytes(path, legacy.Export(X509ContentType.Pfx, password));
            return LoadCertificate(path, password);
        }

        password = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        await SecureStorage.Default.SetAsync(CertificatePasswordKey, password);
        using var key = RSA.Create(2048);
        var request = new CertificateRequest($"CN={commonName}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        using var created = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(10));
        var pfx = created.Export(X509ContentType.Pfx, password);
        File.WriteAllBytes(path, pfx);
        return LoadCertificate(path, password);
    }

    public static string Fingerprint(X509Certificate2 certificate) => certificate.GetCertHashString(HashAlgorithmName.SHA256);

    public static async Task<byte[]> GetOrCreatePairingTokenAsync()
    {
        var saved = await SecureStorage.Default.GetAsync(PairingTokenKey);
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
        await SecureStorage.Default.SetAsync(
            PairingTokenKey,
            Convert.ToBase64String(token));
        return token;
    }

    public static Task<string?> GetPairedReceiverFingerprintAsync() =>
        SecureStorage.Default.GetAsync(ReceiverFingerprintKey);

    public static Task SetPairedReceiverFingerprintAsync(string fingerprint) =>
        SecureStorage.Default.SetAsync(ReceiverFingerprintKey, fingerprint);

    public static bool ForgetPairedReceiver() =>
        SecureStorage.Default.Remove(ReceiverFingerprintKey);

    public static async Task<bool> TrustPhoneAsync(ReadOnlyMemory<byte> token)
    {
        await TrustedPhoneGate.WaitAsync();
        try
        {
            var saved = await SecureStorage.Default.GetAsync(TrustedPhoneTokenKey);
            if (string.IsNullOrEmpty(saved))
            {
                await SecureStorage.Default.SetAsync(
                    TrustedPhoneTokenKey,
                    Convert.ToBase64String(token.Span));
                return true;
            }

            try
            {
                return CryptographicOperations.FixedTimeEquals(
                    Convert.FromBase64String(saved),
                    token.Span);
            }
            catch (FormatException)
            {
                return false;
            }
        }
        finally
        {
            TrustedPhoneGate.Release();
        }
    }

    public static bool ForgetTrustedPhone() =>
        SecureStorage.Default.Remove(TrustedPhoneTokenKey);

    public static bool FixedTimeEquals(string left, string right)
    {
        try { return CryptographicOperations.FixedTimeEquals(Convert.FromHexString(left), Convert.FromHexString(right)); }
        catch { return false; }
    }

    private static X509Certificate2 LoadCertificate(string path, string password) =>
        X509CertificateLoader.LoadPkcs12FromFile(
            path,
            password,
            X509KeyStorageFlags.PersistKeySet |
            X509KeyStorageFlags.UserKeySet);
}
