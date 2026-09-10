namespace Lasero.Core.LaseroApi;

/// <summary>
/// Persists a single random, non-identifying per-install device id used only for passive device-
/// activation telemetry (see <see cref="DeviceActivationClient"/>). Generated once and reused for
/// the life of this install — deliberately NOT derived from any hardware serial, MAC address,
/// Windows username, or machine name, and not a hardware fingerprint of any kind. Losing this file
/// (reinstall, profile reset) simply results in a new random id being generated next time — that is
/// expected and acceptable, not an error condition.
/// </summary>
public sealed class DeviceIdStore
{
    private readonly string _path;
    private string? _cached;

    public DeviceIdStore() : this(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Lasero", "device-id.txt"))
    {
    }

    public DeviceIdStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = Path.GetFullPath(path);
    }

    /// <summary>Returns this install's device id, generating and persisting one on first call.
    /// Cached in memory after the first read so repeated calls within a process never re-touch disk.</summary>
    public string GetOrCreate()
    {
        if (_cached is not null) return _cached;

        if (File.Exists(_path))
        {
            var existing = SafeRead();
            if (existing is not null && Guid.TryParse(existing, out _))
                return _cached = existing;
        }

        var id = Guid.NewGuid().ToString();
        Save(id);
        return _cached = id;
    }

    private string? SafeRead()
    {
        try
        {
            return File.ReadAllText(_path).Trim();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private void Save(string id)
    {
        var directory = Path.GetDirectoryName(_path)
            ?? throw new InvalidOperationException("ID zařízení nemá platnou cílovou složku.");
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(_path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(temporaryPath, id);
            File.Move(temporaryPath, _path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }
}
