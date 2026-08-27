using System.IO;
using System.Text.Json;
using Lasero.Core.History;
using Serilog;

namespace Lasero.App;

/// <summary>Home dashboard's job-history log — thin JSON persistence, same shape as AppSettingsStore
/// (atomic temp-file-then-move save). Appended once per successfully completed job (never framing —
/// see GCodeViewModel.JobCompleted).</summary>
public sealed class JobHistoryStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _path;
    private List<JobHistoryEntry> _entries;

    public JobHistoryStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = Path.GetFullPath(path);
        _entries = LoadFromDisk();
    }

    public static JobHistoryStore CreateDefault() => new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Lasero", "job-history.json"));

    public IReadOnlyList<JobHistoryEntry> Entries => _entries;

    public event Action? Changed;

    public void Append(JobHistoryEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        _entries.Add(entry);
        Save();
        Changed?.Invoke();
    }

    private List<JobHistoryEntry> LoadFromDisk()
    {
        if (!File.Exists(_path)) return [];
        try
        {
            return JsonSerializer.Deserialize<List<JobHistoryEntry>>(File.ReadAllText(_path), JsonOptions) ?? [];
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            Log.Warning(ex, "Job-history file is invalid; starting with an empty log");
            return [];
        }
    }

    private void Save()
    {
        var directory = Path.GetDirectoryName(_path)
            ?? throw new InvalidOperationException("Historie úloh nemá platnou cílovou složku.");
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
