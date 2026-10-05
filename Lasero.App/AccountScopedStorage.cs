using System.Security.Cryptography;
using System.Text;

namespace Lasero.App;

/// <summary>The one deterministic, privacy-preserving per-account filename convention for local
/// caches that must never leak between different Lasero accounts signed into the same Windows
/// profile. ChatStore established this SHA256(uid)-truncated-to-24-hex-chars convention first;
/// every other account-scoped store reuses it here instead of inventing its own.</summary>
internal static class AccountScopedStorage
{
    public static string FileNameFor(string userId) => $"{KeyFor(userId)}.json";

    /// <summary>The same hash without the extension, for per-account entries kept inside one shared
    /// file (guidance progress in settings.json) rather than in a file of their own.</summary>
    public static string KeyFor(string userId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(userId)))[..24];
    }
}
