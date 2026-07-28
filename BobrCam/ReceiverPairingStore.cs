using System.Security.Cryptography;

namespace BobrCam;

internal sealed class ReceiverPairingStore
{
    private const string PreferenceKey = "paired_phone_token_hashes_v2";
    private const int MaximumPairedPhones = 16;
    private readonly object _gate = new();
    private HashSet<string>? _tokenHashes;

    public int Count
    {
        get
        {
            lock (_gate)
                return GetHashes().Count;
        }
    }

    public bool IsKnown(ReadOnlySpan<byte> pairingToken)
    {
        var candidate = Hash(pairingToken);
        lock (_gate)
        {
            foreach (var saved in GetHashes())
            {
                if (CryptographicOperations.FixedTimeEquals(
                        Convert.FromBase64String(saved),
                        Convert.FromBase64String(candidate)))
                {
                    return true;
                }
            }
        }
        return false;
    }

    public bool Add(ReadOnlySpan<byte> pairingToken)
    {
        var candidate = Hash(pairingToken);
        lock (_gate)
        {
            var hashes = GetHashes();
            if (hashes.Contains(candidate))
                return false;
            if (hashes.Count >= MaximumPairedPhones)
                throw new InvalidOperationException(
                    "The paired-phone limit is reached. Forget paired phones before adding another.");
            hashes.Add(candidate);
            Save(hashes);
            return true;
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            Preferences.Default.Remove(PreferenceKey);
            _tokenHashes = [];
        }
    }

    private HashSet<string> GetHashes()
    {
        if (_tokenHashes is not null)
            return _tokenHashes;

        var saved = Preferences.Default.Get(PreferenceKey, string.Empty);
        _tokenHashes = saved
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(IsValidHash)
            .ToHashSet(StringComparer.Ordinal);
        return _tokenHashes;
    }

    private static string Hash(ReadOnlySpan<byte> token) =>
        Convert.ToBase64String(SHA256.HashData(token));

    private static bool IsValidHash(string value)
    {
        try
        {
            return Convert.FromBase64String(value).Length == 32;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static void Save(IEnumerable<string> hashes) =>
        Preferences.Default.Set(PreferenceKey, string.Join(';', hashes));
}
