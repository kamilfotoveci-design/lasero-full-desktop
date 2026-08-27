using System.IO;
using System.Text.Json;
using Lasero.Core.Materials;
using Serilog;

namespace Lasero.App;

/// <summary>Materials nav panel's saved preset catalog — thin JSON persistence, same shape as
/// RecentProjectsStore (atomic temp-file-then-move save, Changed event, empty-list fallback on a
/// missing/corrupt file). This store contains only personal recipes. The immutable offline catalog
/// lives in MaterialCatalog and is never copied into or overwritten by account-owned data.</summary>
public sealed class MaterialPresetStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _path;
    private List<MaterialPreset> _presets;
    private HashSet<Guid> _lastSyncedIds = [];

    public MaterialPresetStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = Path.GetFullPath(path);
        _presets = LoadFromDisk();
    }

    public static MaterialPresetStore CreateDefault() => new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Lasero", "materials.json"));

    public IReadOnlyList<MaterialPreset> Presets => _presets;
    public IReadOnlySet<Guid> LastSyncedIds => _lastSyncedIds;
    public string? SyncRevision { get; private set; }
    public bool HasLocalChanges { get; private set; }

    public event Action? Changed;

    public void Add(MaterialPreset preset)
    {
        _presets.Add(preset);
        HasLocalChanges = true;
        Save();
        Changed?.Invoke();
    }

    public void Update(MaterialPreset preset)
    {
        var index = _presets.FindIndex(p => p.Id == preset.Id);
        if (index < 0) return;
        _presets[index] = preset;
        HasLocalChanges = true;
        Save();
        Changed?.Invoke();
    }

    public void Remove(Guid id)
    {
        if (_presets.RemoveAll(p => p.Id == id) == 0) return;
        HasLocalChanges = true;
        Save();
        Changed?.Invoke();
    }

    private List<MaterialPreset> LoadFromDisk()
    {
        if (!File.Exists(_path)) return [];

        try
        {
            var json = File.ReadAllText(_path);
            List<MaterialPreset> loaded;
            if (json.AsSpan().TrimStart().StartsWith("["))
            {
                loaded = JsonSerializer.Deserialize<List<MaterialPreset>>(json, JsonOptions) ?? [];
                HasLocalChanges = loaded.Count > 0;
            }
            else
            {
                var document = JsonSerializer.Deserialize<MaterialStoreDocument>(json, JsonOptions);
                loaded = document?.Items ?? [];
                SyncRevision = document?.SyncRevision;
                HasLocalChanges = document?.HasLocalChanges ?? false;
                _lastSyncedIds = document?.LastSyncedIds?.ToHashSet() ?? [];
            }
            if (MigrateBuiltInPresetNames(loaded))
            {
                _presets = loaded;
                Save();
            }
            return loaded;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            Log.Warning(ex, "Materials file is invalid; starting with an empty list");
            return [];
        }
    }

    public void ReplaceFromSync(IEnumerable<MaterialPreset> presets, string? revision)
    {
        _presets = presets.ToList();
        _lastSyncedIds = _presets.Select(preset => preset.Id).ToHashSet();
        SyncRevision = revision;
        HasLocalChanges = false;
        Save();
        Changed?.Invoke();
    }

    public void MarkSynchronized(string? revision)
    {
        _lastSyncedIds = _presets.Select(preset => preset.Id).ToHashSet();
        SyncRevision = revision;
        HasLocalChanges = false;
        Save();
    }

    private static bool MigrateBuiltInPresetNames(IEnumerable<MaterialPreset> presets)
    {
        var translations = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Preglejka 3 mm – Rez"] = "Překližka 3 mm – řez",
            ["Preglejka 3 mm – Gravír"] = "Překližka 3 mm – gravírování",
            ["Preglejka 6 mm – Rez"] = "Překližka 6 mm – řez",
            ["MDF 3 mm – Rez"] = "MDF 3 mm – řez",
            ["Akryl 3 mm – Rez"] = "Akryl 3 mm – řez",
            ["Balza 3 mm – Rez"] = "Balza 3 mm – řez",
            ["Kartón – Rez"] = "Karton – řez",
            ["Koža – Gravír"] = "Kůže – gravírování",
            ["Bridlica / kameň – Gravír"] = "Břidlice / kámen – gravírování",
        };

        var changed = false;
        foreach (var preset in presets)
        {
            if (!translations.TryGetValue(preset.Name, out var translated)) continue;
            preset.Name = translated;
            changed = true;
        }
        return changed;
    }

    private void Save()
    {
        var directory = Path.GetDirectoryName(_path)
            ?? throw new InvalidOperationException("Katalog materiálů nemá platnou cílovou složku.");
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(_path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            var document = new MaterialStoreDocument(1, SyncRevision, HasLocalChanges, _lastSyncedIds.ToList(), _presets);
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(document, JsonOptions));
            File.Move(temporaryPath, _path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }

    private sealed record MaterialStoreDocument(int Version, string? SyncRevision, bool HasLocalChanges,
        List<Guid>? LastSyncedIds, List<MaterialPreset> Items);
}
