using System.IO;

namespace Lasero.App;

public sealed class ProjectRecoveryStore
{
    private readonly string _directory;
    private string? _snapshotPath;

    public ProjectRecoveryStore(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        _directory = Path.GetFullPath(directory);
    }

    public static ProjectRecoveryStore CreateDefault() => new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Lasero", "recovery"));

    /// <summary>Only the signed-in account may access its recovery snapshot. The former shared
    /// autosave.lasero is intentionally left untouched: its owner cannot be inferred safely.</summary>
    public void SwitchAccount(string? userId)
    {
        _snapshotPath = string.IsNullOrWhiteSpace(userId)
            ? null
            : Path.Combine(_directory, "accounts",
                Path.ChangeExtension(AccountScopedStorage.FileNameFor(userId), ".lasero"));
    }

    public bool HasSnapshot => _snapshotPath is not null && File.Exists(_snapshotPath);
    public DateTime? LastWriteTimeUtc => HasSnapshot ? File.GetLastWriteTimeUtc(_snapshotPath!) : null;

    /// <summary>Sidecar remembering which project file the snapshot was taken from. Kept beside the
    /// snapshot rather than inside the project schema, so the .lasero format is unchanged.</summary>
    private string? OriginPath => _snapshotPath is null ? null : _snapshotPath + ".origin";

    public void Save(LaseroProjectFile project, string? originalProjectPath = null)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (_snapshotPath is null) return;
        Directory.CreateDirectory(Path.GetDirectoryName(_snapshotPath)!);
        ProjectFileSerializer.Save(_snapshotPath, project);
        WriteOrigin(originalProjectPath);
    }

    private void WriteOrigin(string? originalProjectPath)
    {
        var originPath = OriginPath;
        if (originPath is null) return;
        if (string.IsNullOrWhiteSpace(originalProjectPath))
        {
            if (File.Exists(originPath)) File.Delete(originPath);
            return;
        }
        File.WriteAllText(originPath, originalProjectPath);
    }

    /// <summary>The project file the snapshot was taken from, or null when it was never saved or that
    /// file no longer exists (Ctrl+S then asks for a location instead of writing somewhere stale).</summary>
    public string? TryLoadOriginalPath()
    {
        var originPath = OriginPath;
        if (originPath is null || !File.Exists(originPath)) return null;
        try
        {
            var path = File.ReadAllText(originPath).Trim();
            return path.Length > 0 && File.Exists(path) ? path : null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    public LaseroProjectFile? TryLoad() => HasSnapshot ? ProjectFileSerializer.Load(_snapshotPath!) : null;

    public void Discard()
    {
        if (HasSnapshot)
            File.Delete(_snapshotPath!);
        if (OriginPath is { } originPath && File.Exists(originPath))
            File.Delete(originPath);
    }
}
