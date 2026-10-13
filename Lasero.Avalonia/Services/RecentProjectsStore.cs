using System.IO;
using System.Text.Json;
using Lasero.Core.History;

namespace Lasero.Avalonia.Services;

/// <summary>Home dashboard's recent-projects list — ported 1:1 from Lasero.App/RecentProjectsStore.cs
/// (thin JSON persistence, atomic temp-file-then-move save). Shares the same on-disk file as the WPF
/// app, so a project touched in one build shows up as recent in the other.
/// Unlike the WPF original this doesn't log parse failures through Serilog (not yet a dependency of
/// this project) — an invalid file is silently treated as an empty list, same end behavior.</summary>
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
