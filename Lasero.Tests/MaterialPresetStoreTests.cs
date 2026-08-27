using System.IO;
using Lasero.App;
using Lasero.Core.Layers;
using Lasero.Core.Materials;

namespace Lasero.Tests;

public sealed class MaterialPresetStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "lasero-materials-tests", Guid.NewGuid().ToString("N"));

    public MaterialPresetStoreTests() => Directory.CreateDirectory(_directory);

    private string PathFor(string name) => Path.Combine(_directory, name);

    [Fact]
    public void FirstRun_StartsWithEmptyPersonalCatalog()
    {
        var path = PathFor("materials.json");

        var store = new MaterialPresetStore(path);

        Assert.False(File.Exists(path));
        Assert.Empty(store.Presets);
    }

    [Fact]
    public void Add_PersistsAcrossNewStoreInstances()
    {
        var path = PathFor("materials.json");
        var store = new MaterialPresetStore(path);
        var preset = MaterialPreset.Create("Test materiál", LayerMode.Cut, speed: 123, power: 45, passes: 2);

        store.Add(preset);
        var reloaded = new MaterialPresetStore(path);

        Assert.Contains(reloaded.Presets, p => p.Name == "Test materiál" && p.Speed == 123 && p.Power == 45 && p.Passes == 2);
    }

    [Fact]
    public void Update_PersistsEditedValues()
    {
        var path = PathFor("materials.json");
        var store = new MaterialPresetStore(path);
        var preset = MaterialPreset.Create("Pôvodný", LayerMode.Cut, speed: 100, power: 50, passes: 1);
        store.Add(preset);

        preset.Speed = 999;
        store.Update(preset);
        var reloaded = new MaterialPresetStore(path);

        Assert.Contains(reloaded.Presets, p => p.Id == preset.Id && p.Speed == 999);
    }

    [Fact]
    public void Remove_DeletesFromPersistedList()
    {
        var path = PathFor("materials.json");
        var store = new MaterialPresetStore(path);
        var preset = MaterialPreset.Create("Na zmazanie", LayerMode.Cut, speed: 100, power: 50, passes: 1);
        store.Add(preset);

        store.Remove(preset.Id);
        var reloaded = new MaterialPresetStore(path);

        Assert.DoesNotContain(reloaded.Presets, p => p.Id == preset.Id);
    }

    [Fact]
    public void Changed_FiresOnAddUpdateAndRemove()
    {
        var store = new MaterialPresetStore(PathFor("materials.json"));
        var fireCount = 0;
        store.Changed += () => fireCount++;

        var preset = MaterialPreset.Create("X", LayerMode.Cut, speed: 1, power: 1, passes: 1);
        store.Add(preset);
        preset.Speed = 2;
        store.Update(preset);
        store.Remove(preset.Id);

        Assert.Equal(3, fireCount);
    }

    [Fact]
    public void CorruptFile_FallsBackToEmptyListWithoutThrowing()
    {
        var path = PathFor("materials.json");
        File.WriteAllText(path, "{ not valid json");

        var store = new MaterialPresetStore(path);

        Assert.Empty(store.Presets);
    }

    [Fact]
    public void MaterialPreset_ReportsActionableValidationErrors()
    {
        var preset = MaterialPreset.Create("", LayerMode.Cut, speed: 0, power: 101, passes: 0);

        Assert.Contains("název", preset[nameof(MaterialPreset.Name)], StringComparison.OrdinalIgnoreCase);
        Assert.Contains("rychlost", preset[nameof(MaterialPreset.Speed)], StringComparison.OrdinalIgnoreCase);
        Assert.Contains("0 až 100", preset[nameof(MaterialPreset.Power)]);
        Assert.Contains("1 až 100", preset[nameof(MaterialPreset.Passes)]);
        Assert.False(string.IsNullOrWhiteSpace(preset.Error));

        preset.Name = "Březová překližka 3 mm";
        preset.Speed = 1200;
        preset.Power = 65;
        preset.Passes = 1;

        Assert.Empty(preset.Error);
    }

    [Fact]
    public void ExistingBuiltInSlovakName_IsMigratedToCzech()
    {
        var path = PathFor("materials.json");
        var store = new MaterialPresetStore(path);
        store.Add(MaterialPreset.Create("Preglejka 3 mm – Rez", LayerMode.Cut, 300, 95, 2));

        var reloaded = new MaterialPresetStore(path);

        Assert.Contains(reloaded.Presets, preset => preset.Name == "Překližka 3 mm – řez");
        Assert.DoesNotContain(reloaded.Presets, preset => preset.Name == "Preglejka 3 mm – Rez");
    }

    [Fact]
    public void SyncBaseline_PersistsDeletedIdsUntilNextSuccessfulSync()
    {
        var path = PathFor("materials.json");
        var first = MaterialPreset.Create("První", LayerMode.Cut, 300, 80, 1);
        var second = MaterialPreset.Create("Druhý", LayerMode.Fill, 1200, 30, 1);
        var store = new MaterialPresetStore(path);
        store.ReplaceFromSync([first, second], "revision-1");

        store.Remove(first.Id);
        var reloaded = new MaterialPresetStore(path);

        Assert.True(reloaded.HasLocalChanges);
        Assert.Contains(first.Id, reloaded.LastSyncedIds);
        Assert.Contains(second.Id, reloaded.LastSyncedIds);
        Assert.DoesNotContain(reloaded.Presets, preset => preset.Id == first.Id);

        reloaded.MarkSynchronized("revision-2");
        var synchronized = new MaterialPresetStore(path);
        Assert.DoesNotContain(first.Id, synchronized.LastSyncedIds);
        Assert.Contains(second.Id, synchronized.LastSyncedIds);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_directory))
                Directory.Delete(_directory, recursive: true);
        }
        catch
        {
        }
    }
}
