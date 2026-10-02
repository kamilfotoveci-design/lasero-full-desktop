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
        Assert.Contains("0 a 100", preset[nameof(MaterialPreset.Power)]);
        Assert.Contains("alespoň 1 a nejvýše 100", preset[nameof(MaterialPreset.Passes)]);
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

    [Fact]
    public void SwitchAccount_IsolatesTwoDistinctAccountsFromEachOther()
    {
        var store = new MaterialPresetStore(PathFor("materials.json"));

        store.SwitchAccount("account-a");
        store.Add(MaterialPreset.Create("A's materiál", LayerMode.Cut, speed: 100, power: 50, passes: 1));

        store.SwitchAccount("account-b");
        Assert.Empty(store.Presets);
        store.Add(MaterialPreset.Create("B's materiál", LayerMode.Cut, speed: 200, power: 60, passes: 1));

        store.SwitchAccount("account-a");
        Assert.Single(store.Presets);
        Assert.Equal("A's materiál", store.Presets[0].Name);

        store.SwitchAccount("account-b");
        Assert.Single(store.Presets);
        Assert.Equal("B's materiál", store.Presets[0].Name);
    }

    [Fact]
    public void SwitchAccount_FiresChangedSoSubscribersRefresh()
    {
        var store = new MaterialPresetStore(PathFor("materials.json"));
        var fireCount = 0;
        store.Changed += () => fireCount++;

        store.SwitchAccount("account-a");

        Assert.Equal(1, fireCount);
    }

    [Fact]
    public void SwitchAccount_ReloadsFromDiskAcrossStoreInstances()
    {
        var path = PathFor("materials.json");
        var first = new MaterialPresetStore(path);
        first.SwitchAccount("account-a");
        first.Add(MaterialPreset.Create("Perzistentní", LayerMode.Cut, speed: 100, power: 50, passes: 1));

        var second = new MaterialPresetStore(path);
        second.SwitchAccount("account-a");

        Assert.Contains(second.Presets, p => p.Name == "Perzistentní");
    }

    [Fact]
    public void SwitchAccount_DoesNotCarrySyncStateFromPreviousAccount()
    {
        var store = new MaterialPresetStore(PathFor("materials.json"));

        store.SwitchAccount("account-a");
        store.ReplaceFromSync([MaterialPreset.Create("Sync", LayerMode.Cut, 100, 50, 1)], "revision-a");
        Assert.True(store.LastSyncedIds.Count > 0);
        Assert.Equal("revision-a", store.SyncRevision);

        store.SwitchAccount("account-b");

        Assert.Empty(store.Presets);
        Assert.Empty(store.LastSyncedIds);
        Assert.Null(store.SyncRevision);
        Assert.False(store.HasLocalChanges);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void SwitchAccount_NullOrEmptyUserId_ClearsStateWithoutThrowing(string? userId)
    {
        var store = new MaterialPresetStore(PathFor("materials.json"));
        store.SwitchAccount("account-a");
        store.Add(MaterialPreset.Create("A's materiál", LayerMode.Cut, speed: 100, power: 50, passes: 1));

        store.SwitchAccount(userId);
        Assert.Empty(store.Presets);

        // Must not throw and must not persist anywhere with no account active.
        store.Add(MaterialPreset.Create("Bez účtu", LayerMode.Cut, speed: 1, power: 1, passes: 1));
        Assert.Single(store.Presets);

        store.SwitchAccount("account-a");
        Assert.Single(store.Presets);
        Assert.Equal("A's materiál", store.Presets[0].Name);
    }

    [Fact]
    public void SwitchAccount_NeverReadsOrWritesTheLegacySharedFile()
    {
        var legacyPath = PathFor("materials.json");
        File.WriteAllText(legacyPath, """[{"Id":"11111111-1111-1111-1111-111111111111","Name":"Legacy sdílený","Mode":0,"Speed":100,"Power":50,"Passes":1,"FillLineIntervalMm":0.1}]""");
        var legacyContentsBefore = File.ReadAllText(legacyPath);

        var store = new MaterialPresetStore(legacyPath);
        store.SwitchAccount("account-a");

        Assert.Empty(store.Presets);
        Assert.Equal(legacyContentsBefore, File.ReadAllText(legacyPath));

        store.Add(MaterialPreset.Create("Nový u A", LayerMode.Cut, speed: 1, power: 1, passes: 1));
        Assert.Equal(legacyContentsBefore, File.ReadAllText(legacyPath));
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
