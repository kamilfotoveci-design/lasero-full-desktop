using System.IO;
using System.Text.Json;
using Lasero.Core.History;
using Serilog;

namespace Lasero.App;

/// <summary>Home dashboard's recent-projects list — thin JSON persistence, same shape as
/// AppSettingsStore (atomic temp-file-then-move save). Touch() is called right after a project is
/// saved or opened; the actual thumbnail PNG is written separately by SceneThumbnailRenderer.
///
/// The local cache file is account-scoped (see <see cref="SwitchAccount"/>) using the same
/// SHA256(uid) convention ChatStore established, so one Lasero account's recent-project names and
/// thumbnails never appear to another account signed into the same Windows profile. Project files
/// themselves are not moved or touched by this scoping — only this index/cache.</summary>
public sealed class RecentProjectsStore
{
    private const int MaxEntries = 12;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _accountsDirectory;
    private string? _path;
    private List<RecentProjectEntry> _entries;

    public RecentProjectsStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = Path.GetFullPath(path);
        _accountsDirectory = Path.Combine(Path.GetDirectoryName(_path) ?? Directory.GetCurrentDirectory(), "recent-projects");
        _entries = LoadFromDisk();
    }

    public static RecentProjectsStore CreateDefault() => new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Lasero", "recent-projects.json"));

    /// <summary>Points the store at the signed-in account's own local cache file and reloads from
    /// it. Same conservative no-auto-migration policy as MaterialPresetStore.SwitchAccount: the
    /// pre-account-scoping shared recent-projects.json is left on disk untouched rather than guessed
    /// into whichever account signs in first, since ownership can't be determined safely. Unlike
    /// materials, there is no cloud sync to repopulate this list — a freshly-scoped account simply
    /// starts with an empty recent list, which fills back in as they open/save projects again.</summary>
    public void SwitchAccount(string? userId)
    {
        _path = string.IsNullOrWhiteSpace(userId)
            ? null
            : Path.Combine(_accountsDirectory, AccountScopedStorage.FileNameFor(userId));
        _entries = LoadFromDisk();
        Changed?.Invoke();
    }

    public IReadOnlyList<RecentProjectEntry> Recent => _entries;

    public event Action? Changed;

    public void Touch(string path, string name, string? thumbnailPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _entries.RemoveAll(e => string.Equals(e.Path, path, StringComparison.OrdinalIgnoreCase));
        _entries.Insert(0, new RecentProjectEntry(path, name, DateTime.UtcNow, thumbnailPath));
        if (_entries.Count > MaxEntries)
            _entries.RemoveRange(MaxEntries, _entries.Count - MaxEntries);
        Save();
        Changed?.Invoke();
    }

    public void Remove(string path)
    {
        if (_entries.RemoveAll(e => string.Equals(e.Path, path, StringComparison.OrdinalIgnoreCase)) == 0) return;
        Save();
        Changed?.Invoke();
    }

    private List<RecentProjectEntry> LoadFromDisk()
    {
        if (_path is null || !File.Exists(_path)) return [];
        try
        {
            return JsonSerializer.Deserialize<List<RecentProjectEntry>>(File.ReadAllText(_path), JsonOptions) ?? [];
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            Log.Warning(ex, "Recent-projects file is invalid; starting with an empty list");
            return [];
        }
    }

    private void Save()
    {
        if (_path is null) return;
        var directory = Path.GetDirectoryName(_path)
            ?? throw new InvalidOperationException("Seznam nedávných projektů nemá platnou cílovou složku.");
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(_path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(_entries, JsonOptions));
            File.Move(temporaryPath, _path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }
}
