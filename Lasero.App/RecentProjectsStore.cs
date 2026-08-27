using System.IO;
using System.Text.Json;
using Lasero.Core.History;
using Serilog;

namespace Lasero.App;

/// <summary>Home dashboard's recent-projects list — thin JSON persistence, same shape as
/// AppSettingsStore (atomic temp-file-then-move save). Touch() is called right after a project is
/// saved or opened; the actual thumbnail PNG is written separately by SceneThumbnailRenderer.</summary>
public sealed class RecentProjectsStore
{
    private const int MaxEntries = 12;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _path;
    private List<RecentProjectEntry> _entries;

    public RecentProjectsStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = Path.GetFullPath(path);
        _entries = LoadFromDisk();
    }

    public static RecentProjectsStore CreateDefault() => new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Lasero", "recent-projects.json"));

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
        if (!File.Exists(_path)) return [];
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
