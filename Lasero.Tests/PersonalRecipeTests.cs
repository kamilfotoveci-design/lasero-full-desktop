using System.IO;
using System.Net.Http;
using Lasero.App;
using Lasero.App.ViewModels;
using Lasero.Core.LaseroApi;
using Lasero.Core.Layers;
using Lasero.Core.Materials;

namespace Lasero.Tests;

public sealed class PersonalRecipeTests
{
    private static MaterialsViewModel Create(TemporaryDirectory directory, int watts = 20)
    {
        var http = new HttpClient();
        var account = new AccountViewModel(
            new LaseroAuthClient(http), new LaseroAccountClient(http),
            new SessionStore(Path.Combine(directory.Root, "session.json")),
            new DeviceIdStore(Path.Combine(directory.Root, "device-id.txt")),
            new DeviceActivationClient(http));
        var settings = new AppSettingsStore(Path.Combine(directory.Root, "settings.json"));
        settings.Current.Machine.LaserPowerWatts = watts;
        var vm = new MaterialsViewModel(new MaterialPresetStore(Path.Combine(directory.Root, "materials.json")),
            settings, new MaterialSyncClient(http), account);
        vm.ConfirmDelete = _ => true;
        return vm;
    }

    [Theory]
    [InlineData(LayerMode.Cut, 20)]
    [InlineData(LayerMode.Fill, 20)]
    [InlineData(LayerMode.FillAndCut, 20)]
    [InlineData(LayerMode.Cut, 5)]
    [InlineData(LayerMode.Fill, 60)]
    public void DefaultsAreAlwaysInsideTheInspectorRanges(LayerMode mode, int watts)
    {
        var (speed, power, passes) = MaterialRecipeRules.DefaultsFor(mode, watts);
        Assert.InRange(speed, 10, 12000);
        Assert.InRange(power, 0, 100);
        Assert.InRange(passes, 1, 100);
    }

    [Fact]
    public void CuttingIsSlowerThanEngravingAndLowPowerLasersGetMorePasses()
    {
        var cut = MaterialRecipeRules.DefaultsFor(LayerMode.Cut, 10);
        var fill = MaterialRecipeRules.DefaultsFor(LayerMode.Fill, 10);
        Assert.True(cut.Speed < fill.Speed);
        Assert.True(MaterialRecipeRules.DefaultsFor(LayerMode.Cut, 5).Passes > MaterialRecipeRules.DefaultsFor(LayerMode.Cut, 40).Passes);
        Assert.True(MaterialRecipeRules.DefaultsFor(LayerMode.Cut, 40).Speed > cut.Speed);
    }

    [Fact]
    public void AddUsesTheMachinePowerClassAndAFreshName()
    {
        using var dir = new TemporaryDirectory();
        var vm = Create(dir, watts: 10);
        vm.AddCommand.Execute(null);
        vm.AddCommand.Execute(null);

        Assert.Equal("Nový materiál", vm.Presets[0].Name);
        Assert.Equal("Nový materiál 2", vm.Presets[1].Name);
        var expected = MaterialRecipeRules.DefaultsFor(vm.Presets[0].Mode, 10);
        Assert.Equal(expected.Speed, vm.Presets[0].Speed);
        Assert.Equal(expected.Power, vm.Presets[0].Power);
        Assert.Empty(vm.Presets[0].ValidationMessage);
    }

    [Fact]
    public void SwitchingAnUntouchedRecipeToCuttingSwapsInCuttingDefaults_ButEditedValuesStay()
    {
        using var dir = new TemporaryDirectory();
        var vm = Create(dir, watts: 20);
        vm.AddCommand.Execute(null);
        vm.AddCommand.Execute(null);
        vm.Presets[1].Speed = 1234;

        vm.Presets[0].Mode = LayerMode.Cut;
        vm.Presets[1].Mode = LayerMode.Cut;

        var cut = MaterialRecipeRules.DefaultsFor(LayerMode.Cut, 20);
        Assert.Equal(cut.Speed, vm.Presets[0].Speed);
        Assert.Equal(cut.Power, vm.Presets[0].Power);
        Assert.Equal(1234, vm.Presets[1].Speed);
    }

    [Theory]
    [InlineData(5, 50, 1, "Rychlost musí být mezi 10 a 12000 mm/min.")]
    [InlineData(20000, 50, 1, "Rychlost musí být mezi 10 a 12000 mm/min.")]
    [InlineData(300, 101, 1, "Výkon musí být mezi 0 a 100 %.")]
    [InlineData(300, 50, 0, "Počet průchodů musí být alespoň 1 a nejvýše 100.")]
    public void OutOfRangeValuesShowPlainMessages(double speed, double power, int passes, string message)
    {
        var preset = MaterialPreset.Create("Test", LayerMode.Cut, speed, power, passes);
        Assert.Equal(message, preset.ValidationMessage);
        Assert.True(preset.HasValidationMessage);
    }

    [Fact]
    public void InvalidValuesAreNotPersisted()
    {
        using var dir = new TemporaryDirectory();
        var vm = Create(dir);
        vm.AddCommand.Execute(null);
        vm.Presets[0].Passes = 0;

        var reloaded = new MaterialPresetStore(Path.Combine(dir.Root, "materials.json"));
        Assert.Equal(1, Assert.Single(reloaded.Presets).Passes);
    }

    [Fact]
    public void DuplicateNamesAreFlaggedOnTheLaterRecipeOnly()
    {
        using var dir = new TemporaryDirectory();
        var vm = Create(dir);
        vm.AddCommand.Execute(null);
        vm.AddCommand.Execute(null);
        vm.Presets[1].Name = " nový MATERIÁL ";

        Assert.False(vm.Presets[0].HasDuplicateName);
        Assert.True(vm.Presets[1].HasDuplicateName);
        Assert.Contains("už máte", vm.Presets[1].ValidationMessage);

        vm.Presets[1].Name = "Jiný";
        Assert.False(vm.Presets[1].HasDuplicateName);
        Assert.Empty(vm.Presets[1].ValidationMessage);
    }

    [Fact]
    public void DuplicateGetsAUniqueNameAndDeleteNeedsConfirmation()
    {
        using var dir = new TemporaryDirectory();
        var vm = Create(dir);
        vm.AddCommand.Execute(null);
        vm.DuplicateCommand.Execute(vm.Presets[0]);
        vm.DuplicateCommand.Execute(vm.Presets[0]);
        Assert.Equal(["Nový materiál", "Nový materiál (kopie)", "Nový materiál (kopie) 2"], vm.Presets.Select(p => p.Name));

        vm.ConfirmDelete = _ => false;
        vm.DeleteCommand.Execute(vm.Presets[0]);
        Assert.Equal(3, vm.Presets.Count);
        vm.ConfirmDelete = _ => true;
        vm.DeleteCommand.Execute(vm.Presets[0]);
        Assert.Equal(2, vm.Presets.Count);
    }

    [Fact]
    public void CutHintAppearsOnlyWhileARecipeCuts()
    {
        using var dir = new TemporaryDirectory();
        var vm = Create(dir);
        Assert.False(vm.HasCutPreset);
        vm.AddCommand.Execute(null);
        Assert.False(vm.HasCutPreset);
        vm.Presets[0].Mode = LayerMode.Cut;
        Assert.True(vm.HasCutPreset);
    }

    [Fact]
    public void ClampingUsesTheInspectorSliderRanges()
    {
        Assert.Equal(10, MaterialRecipeRules.ClampSpeed(-5));
        Assert.Equal(12000, MaterialRecipeRules.ClampSpeed(99999));
        Assert.Equal(10, MaterialRecipeRules.ClampSpeed(double.NaN));
        Assert.Equal(0, MaterialRecipeRules.ClampPower(-1));
        Assert.Equal(100, MaterialRecipeRules.ClampPower(400));
        Assert.Equal(1, MaterialRecipeRules.ClampPasses(0));
        Assert.Equal(100, MaterialRecipeRules.ClampPasses(1000));
    }

    // Source-level checks: labels, units, tooltips and text sizes stay in the recipe editor.
    [Fact]
    public void RecipeEditorLabelsTooltipsAndTextSizesArePresent()
    {
        var xaml = File.ReadAllText(Path.Combine(AppRoot(), "MaterialsWindow.xaml"));
        foreach (var label in new[] { "Název materiálu", "Rychlost (mm/min)", "Výkon (%)", "Počet průchodů", "Režim",
                     "Gravírování", "Řezání", "Přidat první recept", "Použít na operaci" })
            Assert.Contains(label, xaml);
        Assert.Contains("ToolTip=\"Duplikovat recept\"", xaml);
        Assert.Contains("ToolTip=\"Smazat recept\"", xaml);
        Assert.Contains("<WrapPanel", xaml);
        Assert.DoesNotContain("Odstranit recept", xaml);
        Assert.Contains("FontSize=\"14\" Height=\"{StaticResource Size.Control.Base}\"", xaml);
        Assert.DoesNotMatch(@"FontSize=""(9|10|11|12)""", xaml);
    }

    [Fact]
    public void RecipeTextFollowsTheBrandRules()
    {
        var core = Path.Combine(AppRoot(), "..", "Lasero.Core", "Materials");
        var text = File.ReadAllText(Path.Combine(AppRoot(), "MaterialsWindow.xaml"))
                 + File.ReadAllText(Path.Combine(core, "MaterialPreset.cs"))
                 + File.ReadAllText(Path.Combine(core, "MaterialRecipeRules.cs"));
        Assert.DoesNotContain("?\"", text);
        Assert.DoesNotContain("!\"", text);
        Assert.DoesNotContain("–", text);
    }

    private static string AppRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "Lasero.App"))) dir = dir.Parent;
        return Path.Combine(dir!.FullName, "Lasero.App");
    }
}
