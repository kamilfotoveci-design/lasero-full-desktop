using System.IO.Compression;
using System.Text;
using Lasero.Avalonia.Services;

namespace Lasero.Avalonia.Tests;

public sealed class ProjectPackageInspectorTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), $"lasero-avalonia-{Guid.NewGuid():N}");

    public ProjectPackageInspectorTests() => Directory.CreateDirectory(directory);

    [Fact]
    public void ReadsPackagedProjectManifest()
    {
        var path = Path.Combine(directory, "sample.lasero");
        using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            var entry = archive.CreateEntry("project.json");
            using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
            writer.Write("""{"Name":"Cedulka","Version":7,"Objects":[{},{}],"Layers":[{}]}""");
        }

        var result = new ProjectPackageInspector().Inspect(path);

        Assert.True(result.IsSuccess, result.IsFailure ? result.Error : string.Empty);
        Assert.Equal("Cedulka", result.Value.Name);
        Assert.Equal(7, result.Value.Version);
        Assert.Equal(2, result.Value.ObjectCount);
        Assert.Equal(1, result.Value.LayerCount);
        Assert.True(result.Value.IsPackaged);
    }

    [Fact]
    public void ReadsLegacyJsonProject()
    {
        var path = Path.Combine(directory, "legacy.lasero");
        File.WriteAllText(path, """{"Name":"Starší","Version":5,"Objects":[],"Layers":[{},{}]}""");

        var result = new ProjectPackageInspector().Inspect(path);

        Assert.True(result.IsSuccess, result.IsFailure ? result.Error : string.Empty);
        Assert.Equal("Starší", result.Value.Name);
        Assert.Equal(2, result.Value.LayerCount);
        Assert.False(result.Value.IsPackaged);
    }

    [Fact]
    public void ReturnsFailureForInvalidProject()
    {
        var path = Path.Combine(directory, "broken.lasero");
        File.WriteAllText(path, "not a project");

        var result = new ProjectPackageInspector().Inspect(path);

        Assert.True(result.IsFailure);
        Assert.Contains("nepodařilo přečíst", result.Error);
    }

    public void Dispose()
    {
        if (Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
    }
}
