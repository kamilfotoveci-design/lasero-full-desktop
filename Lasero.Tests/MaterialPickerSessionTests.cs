using System.IO;
using System.Net.Http;
using Lasero.App;
using Lasero.App.ViewModels;
using Lasero.Core.LaseroApi;
using Lasero.Core.Layers;
using Lasero.Core.Materials;

namespace Lasero.Tests;

/// <summary>
/// The Vzorník materiálů as a picker for one operation: Aplikovat stays disabled until a swatch is
/// chosen, applying goes through LayerSettings.ApplyRecipe on the target layer only, and cancelling
/// leaves the layer exactly as it was.
/// </summary>
public sealed class MaterialPickerSessionTests
{
    private static MaterialsViewModel Create(string root) => Create(root, out _);

    private static MaterialsViewModel Create(string root, out AppSettingsStore settings,
        LaserTechnology technology = LaserTechnology.Diode, int watts = 5)
    {
        var http = new HttpClient();
        var account = new AccountViewModel(
            new LaseroAuthClient(http), new LaseroAccountClient(http),
            new SessionStore(Path.Combine(root, "session.json")),
            new DeviceIdStore(Path.Combine(root, "device-id.txt")),
            new DeviceActivationClient(http));
        settings = new AppSettingsStore(Path.Combine(root, "settings.json"));
        settings.Current.Machine.LaserTechnology = technology;
        settings.Current.Machine.LaserPowerWatts = watts;
        return new MaterialsViewModel(new MaterialPresetStore(Path.Combine(root, "materials.json")),
            settings, new MaterialSyncClient(http), account);
    }

    private static string NewRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), $"lasero-picker-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        return root;
    }

    private static void Cleanup(string root)
    {
        try { Directory.Delete(root, recursive: true); }
        catch (IOException) { }
    }

    // Speed, Power and Passes are assigned after Mode so the mode-change migration cannot alter them.
    private static LayerSettings Layer(LayerMode mode) =>
        new() { Name = "Operace 1", Mode = mode, Speed = 3000, Power = 30, Passes = 1, FillLineIntervalMm = 0.1 };

    private static void PickFirstSwatch(MaterialsViewModel materials)
    {
        Assert.NotEmpty(materials.SwatchCards);
        materials.SelectSwatchCellCommand.Execute(materials.SwatchCards[0].Cells[5]);
        Assert.NotNull(materials.SelectedRecipe);
    }

    [Fact]
    public void ApplyIsDisabledWithAReasonUntilASwatchIsChosen()
    {
        var root = NewRoot();
        try
        {
            var materials = Create(root);
            using var session = new MaterialPickerSession(materials, Layer(LayerMode.Fill));

            Assert.False(session.CanApply);
            Assert.False(session.ApplyCommand.CanExecute(null));
            Assert.Equal("Nejdřív je potřeba zvolit odstín ve vzorníku", session.ApplyHint);

            PickFirstSwatch(materials);

            Assert.True(session.CanApply);
            Assert.True(session.ApplyCommand.CanExecute(null));
            Assert.Contains("Operace 1", session.ApplyHint);
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void ApplyWritesTheChosenNumbersToTheTargetLayerThroughApplyRecipe()
    {
        var root = NewRoot();
        try
        {
            var materials = Create(root);
            var layer = Layer(LayerMode.Fill);
            var other = Layer(LayerMode.Fill);
            using var session = new MaterialPickerSession(materials, layer);
            var finished = 0;
            session.Finished += (_, _) => finished++;

            PickFirstSwatch(materials);
            var recipe = materials.SelectedRecipe!;
            session.ApplyCommand.Execute(null);

            Assert.True(session.Applied);
            Assert.Equal(1, finished);
            Assert.Equal(recipe.SpeedMmPerMinute, layer.Speed);
            Assert.Equal(recipe.PowerPercent, layer.Power);
            Assert.Equal(recipe.Passes, layer.Passes);
            Assert.Equal(recipe.FillLineIntervalMm, layer.FillLineIntervalMm);
            Assert.Equal(recipe.Mode, layer.Mode);
            // ApplyRecipe records the material as one unit, so the row can show its name.
            Assert.Equal(recipe.MaterialName, layer.MaterialLabel);
            Assert.Equal(recipe.MaterialName, layer.MaterialDisplayLabel);
            Assert.Equal(3000, other.Speed); // another operation is never touched
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void CancelLeavesTheLayerUnchangedAndFinishesWithoutApplying()
    {
        var root = NewRoot();
        try
        {
            var materials = Create(root);
            var layer = Layer(LayerMode.Fill);
            using var session = new MaterialPickerSession(materials, layer);
            var finished = 0;
            session.Finished += (_, _) => finished++;

            PickFirstSwatch(materials);
            session.CancelCommand.Execute(null);

            Assert.False(session.Applied);
            Assert.Equal(1, finished);
            Assert.Equal(3000, layer.Speed);
            Assert.Equal(30, layer.Power);
            Assert.Equal(1, layer.Passes);
            Assert.Equal(LayerMode.Fill, layer.Mode);
            Assert.Null(layer.MaterialLabel);
            Assert.Equal("Vlastní nastavení", layer.MaterialDisplayLabel);
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void PickingASwatchAloneDoesNotTouchTheLayer()
    {
        var root = NewRoot();
        try
        {
            var materials = Create(root);
            var layer = Layer(LayerMode.Fill);
            using var session = new MaterialPickerSession(materials, layer);

            PickFirstSwatch(materials);

            Assert.Equal(3000, layer.Speed);
            Assert.Null(layer.MaterialLabel);
        }
        finally { Cleanup(root); }
    }

    [Theory]
    [InlineData(LayerMode.Cut, LayerMode.Cut)]
    [InlineData(LayerMode.Fill, LayerMode.Fill)]
    public void OpeningPreselectsOperationFromTheLayerAndClearsSearchAndSelection(LayerMode layerMode, LayerMode expected)
    {
        var root = NewRoot();
        try
        {
            var materials = Create(root);
            materials.SelectedOperation = layerMode == LayerMode.Cut ? LayerMode.Fill : LayerMode.Cut;
            materials.SearchText = "dřevo";
            using var session = new MaterialPickerSession(materials, Layer(layerMode));

            Assert.Equal(expected, materials.SelectedOperation);
            Assert.Null(materials.SelectedRecipe);
            Assert.Equal(string.Empty, materials.SearchText);
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void OpeningReadsLaserTypeAndPowerFromTheCurrentMachineSettings()
    {
        var root = NewRoot();
        try
        {
            var materials = Create(root, out var settings);
            // The Device screen changed the machine after this view model was created.
            settings.Current.Machine.LaserTechnology = LaserTechnology.Infrared;
            settings.Current.Machine.LaserPowerWatts = 20;

            using var session = new MaterialPickerSession(materials, Layer(LayerMode.Fill));

            Assert.Equal(LaserTechnology.Infrared, materials.SelectedTechnology);
            Assert.Equal(20, materials.SelectedPowerWatts);
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void AMixedFillAndCutLayerLeavesTheOperationFilterAlone()
    {
        var root = NewRoot();
        try
        {
            var materials = Create(root);
            materials.SelectedOperation = LayerMode.Cut;
            using var session = new MaterialPickerSession(materials, Layer(LayerMode.FillAndCut));

            Assert.Equal(LayerMode.Cut, materials.SelectedOperation);
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void ADifferentOperationModeIsAnnouncedBeforeItIsApplied()
    {
        var root = NewRoot();
        try
        {
            var materials = Create(root);
            var layer = Layer(LayerMode.Cut);
            using var session = new MaterialPickerSession(materials, layer);
            Assert.False(session.HasModeChange);

            materials.SelectedOperation = LayerMode.Fill;
            PickFirstSwatch(materials);

            Assert.True(session.HasModeChange);
            Assert.Contains("Řezání", session.ModeChangeNote);
            Assert.Contains("Gravírování", session.ModeChangeNote);
            Assert.Equal(LayerMode.Cut, layer.Mode); // announced, not applied
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void ApplyingASavedRecipeUsesTheSameApplyRecipePath()
    {
        var root = NewRoot();
        try
        {
            var materials = Create(root);
            var layer = Layer(LayerMode.Fill);
            var preset = MaterialPreset.Create("Moje překližka", LayerMode.Cut, 420, 88, 2, 0.1);
            using var session = new MaterialPickerSession(materials, layer);

            session.ApplyPresetCommand.Execute(preset);

            Assert.True(session.Applied);
            Assert.Equal(LayerMode.Cut, layer.Mode);
            Assert.Equal(420, layer.Speed);
            Assert.Equal(88, layer.Power);
            Assert.Equal(2, layer.Passes);
            Assert.Equal("Moje překližka", layer.MaterialDisplayLabel);
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void DisposingTheSessionClearsTheSharedSelection()
    {
        var root = NewRoot();
        try
        {
            var materials = Create(root);
            var session = new MaterialPickerSession(materials, Layer(LayerMode.Fill));
            PickFirstSwatch(materials);

            session.Dispose();

            Assert.Null(materials.SelectedRecipe);
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void LineIntervalIsShownForFillRecipesOnly()
    {
        var root = NewRoot();
        try
        {
            var materials = Create(root);
            using var session = new MaterialPickerSession(materials, Layer(LayerMode.Fill));
            Assert.False(materials.HasSelectedLineInterval);

            PickFirstSwatch(materials);
            Assert.True(materials.HasSelectedLineInterval);
            Assert.EndsWith(" mm", materials.SelectedLineIntervalText);

            materials.SelectedOperation = LayerMode.Cut;
            PickFirstSwatch(materials);
            Assert.False(materials.HasSelectedLineInterval);
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void InspectorMaterialRowOffersVybratAndTheWindowSwapsItsBottomBar()
    {
        var inspector = File.ReadAllText(Path.Combine(AppRoot(), "Views", "DesignerInspectorView.xaml"));
        Assert.Contains("Content=\"Vybrat\"", inspector);
        Assert.Contains("OnPickMaterialClick", inspector);
        Assert.DoesNotContain("Změnit →", inspector);
        Assert.DoesNotContain("OnApplyMaterialClick", inspector);

        var window = File.ReadAllText(Path.Combine(AppRoot(), "MaterialsWindow.xaml"));
        Assert.Contains("Text=\"Hotovo\"", window);
        Assert.Contains("Text=\"Aplikovat\"", window);
        Assert.Contains("Content=\"Zrušit\"", window);
        Assert.Contains(MaterialPickerSession.ScrapNote, window);
    }

    private static string AppRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "Lasero.App"))) dir = dir.Parent;
        return Path.Combine(dir!.FullName, "Lasero.App");
    }
}
