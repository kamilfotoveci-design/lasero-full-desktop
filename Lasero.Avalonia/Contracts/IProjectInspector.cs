using CSharpFunctionalExtensions;

namespace Lasero.Avalonia.Contracts;

public sealed record ProjectSummary(
    string Name,
    string Path,
    int Version,
    int ObjectCount,
    int LayerCount,
    bool IsPackaged);

public interface IProjectInspector
{
    Result<ProjectSummary> Inspect(string path);
}
