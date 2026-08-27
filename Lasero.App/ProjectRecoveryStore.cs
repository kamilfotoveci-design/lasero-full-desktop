using System.IO;

namespace Lasero.App;

public sealed class ProjectRecoveryStore
{
    private readonly string _snapshotPath;

    public ProjectRecoveryStore(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        _snapshotPath = Path.Combine(Path.GetFullPath(directory), "autosave.lasero");
    }

    public static ProjectRecoveryStore CreateDefault() => new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Lasero", "recovery"));

    public bool HasSnapshot => File.Exists(_snapshotPath);
    public DateTime? LastWriteTimeUtc => HasSnapshot ? File.GetLastWriteTimeUtc(_snapshotPath) : null;

    public void Save(LaseroProjectFile project)
    {
        ArgumentNullException.ThrowIfNull(project);
        Directory.CreateDirectory(Path.GetDirectoryName(_snapshotPath)!);
        ProjectFileSerializer.Save(_snapshotPath, project);
    }

    public LaseroProjectFile? TryLoad() => HasSnapshot ? ProjectFileSerializer.Load(_snapshotPath) : null;

    public void Discard()
    {
        if (File.Exists(_snapshotPath))
            File.Delete(_snapshotPath);
    }
}
