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

    public void Save(LaseroProjectFile project)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (_snapshotPath is null) return;
        Directory.CreateDirectory(Path.GetDirectoryName(_snapshotPath)!);
        ProjectFileSerializer.Save(_snapshotPath, project);
    }

    public LaseroProjectFile? TryLoad() => HasSnapshot ? ProjectFileSerializer.Load(_snapshotPath!) : null;

    public void Discard()
    {
        if (HasSnapshot)
            File.Delete(_snapshotPath!);
    }
}
