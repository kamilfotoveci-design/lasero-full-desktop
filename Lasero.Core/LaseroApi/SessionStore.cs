using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Lasero.Core.LaseroApi;

/// <summary>
/// Persists only a DPAPI-protected refresh token in current-user scope. Writes
/// are atomic so an interrupted shutdown cannot destroy the last valid login.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class SessionStore
{
    private readonly string _path;

    public SessionStore() : this(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Lasero", "session.dat"))
    {
    }

    public SessionStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = Path.GetFullPath(path);
    }

    public void SaveRefreshToken(string refreshToken, string localId, string? email)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(refreshToken);
        ArgumentException.ThrowIfNullOrWhiteSpace(localId);
        var directory = Path.GetDirectoryName(_path)
            ?? throw new InvalidOperationException("Přihlášení nemá platnou cílovou složku.");
        Directory.CreateDirectory(directory);

        var json = JsonSerializer.Serialize(new StoredSession(refreshToken, localId, email));
        var protectedBytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(json), null, DataProtectionScope.CurrentUser);
        var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(_path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllBytes(temporaryPath, protectedBytes);
            File.Move(temporaryPath, _path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }

    public StoredSession? TryLoad()
    {
        if (!File.Exists(_path)) return null;
        try
        {
            var bytes = ProtectedData.Unprotect(File.ReadAllBytes(_path), null, DataProtectionScope.CurrentUser);
            var session = JsonSerializer.Deserialize<StoredSession>(Encoding.UTF8.GetString(bytes));
            return session is not null
                && !string.IsNullOrWhiteSpace(session.RefreshToken)
                && !string.IsNullOrWhiteSpace(session.LocalId)
                ? session
                : null;
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException or IOException)
        {
            PreserveCorruptSession();
            return null;
        }
    }

    public void Clear()
    {
        if (File.Exists(_path))
            File.Delete(_path);
    }

    private void PreserveCorruptSession()
    {
        try
        {
            var corruptPath = _path + ".corrupt-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss");
            File.Move(_path, corruptPath, overwrite: false);
        }
        catch
        {
            // The caller still receives a signed-out state; failure to preserve
            // diagnostic bytes must never block app startup.
        }
    }

    public sealed record StoredSession(string RefreshToken, string LocalId, string? Email);
}
