using System.Globalization;
using System.IO;
using System.Net.Http;
using Lasero.App;
using Lasero.App.ViewModels;
using Lasero.Core.LaseroApi;
using Lasero.Core.Layers;
using Lasero.Core.Materials;

namespace Lasero.Tests;

public sealed class MaterialSwatchViewModelTests
{
    [Fact]
    public void SwatchCardBuildsTheSameFourByFourMatrixAsLaseroApp()
    {
        var material = new MaterialDefinition("wood", "Dřevo", "Masivní dřevo");
        var card = new MaterialSwatchCardViewModel(material, Recipe(material, speed: 5000, power: 50));

        Assert.Equal([30, 55, 80, 100], card.Powers);
        Assert.Equal([10000d, 6250d, 3500d, 1750d], card.Speeds);
        Assert.Equal(16, card.Cells.Count);
        Assert.Equal((10000d, 30), (card.Cells[0].Speed, card.Cells[0].Power));
        Assert.Equal((1750d, 100), (card.Cells[^1].Speed, card.Cells[^1].Power));
    }

    [Fact]
    public void SelectingAnotherCellKeepsExactlyOneCellSelected()
    {
        var material = new MaterialDefinition("slate", "Břidlice", "Černá břidlice");
        var card = new MaterialSwatchCardViewModel(material, Recipe(material, speed: 3000, power: 80));

        card.SelectedCell = card.Cells[2];
        card.SelectedCell = card.Cells[9];

        Assert.Same(card.Cells[9], card.SelectedCell);
        Assert.Single(card.Cells, cell => cell.IsSelected);
    }

    [Fact]
    public void MorePowerAndLessSpeedAlwaysBurnHarder()
    {
        var material = new MaterialDefinition("wood", "Dřevo", "Masivní dřevo");
        var card = new MaterialSwatchCardViewModel(material, Recipe(material, speed: 4000, power: 60));

        for (var row = 0; row < 4; row++)
        {
            for (var column = 1; column < 4; column++)
            {
                var cell = card.Cells[row * 4 + column];
                var weaker = card.Cells[row * 4 + column - 1];
                Assert.True(cell.Intensity > weaker.Intensity,
                    $"{cell.Power} % must burn harder than {weaker.Power} % at {cell.Speed:0} mm/min");
            }
        }

        for (var row = 1; row < 4; row++)
        {
            for (var column = 0; column < 4; column++)
            {
                var cell = card.Cells[row * 4 + column];
                var faster = card.Cells[(row - 1) * 4 + column];
                Assert.True(cell.Intensity > faster.Intensity,
                    $"{cell.Speed:0} mm/min must burn harder than {faster.Speed:0} mm/min at {cell.Power} %");
            }
        }

        Assert.Equal(1, card.Cells[^1].Intensity, 3);
    }

    /// <summary>The whole point of the grid is that sixteen cells look like sixteen different results.
    /// This pins the readability that the first version lost: every mark has to separate from its own
    /// surface, and the corners of the grid have to be obviously different from each other.</summary>
    [Theory]
    [InlineData("wood")]
    [InlineData("acrylic")]
    [InlineData("anodized")]
    [InlineData("slate")]
    [InlineData("rubber")]
    public void EveryCellIsVisiblyDifferentFromItsSurfaceAndFromTheOtherCorner(string materialId)
    {
        var material = new MaterialDefinition(materialId, materialId, materialId);
        var card = new MaterialSwatchCardViewModel(material, Recipe(material, speed: 4000, power: 60));

        foreach (var cell in card.Cells)
        {
            Assert.True(Distance(cell.SurfaceColor, cell.MarkColor) > 24,
                $"{materialId} at {cell.Speed:0} mm/min / {cell.Power} % leaves no visible mark");
        }

        var lightest = card.Cells[0];
        var heaviest = card.Cells[^1];
        Assert.True(Distance(lightest.MarkColor, heaviest.MarkColor) > 60,
            $"{materialId} marks the weakest and strongest cell almost identically");
        Assert.True(Distance(lightest.SurfaceColor, heaviest.SurfaceColor) > 4,
            $"{materialId} tiles do not darken across the grid at all");
    }

    [Fact]
    public void PickingACellKeepsThatCellSelectedAndPublishesItsNumbers()
    {
        using var temporary = new TemporaryDirectory();
        var viewModel = CreateMaterialsViewModel(temporary);
        viewModel.SelectedTechnology = LaserTechnology.Diode;
        viewModel.SelectedPowerWatts = 20;
        viewModel.SelectedOperation = LayerMode.Fill;

        var card = Assert.IsType<MaterialSwatchCardViewModel>(viewModel.SwatchCards.First());
        var cell = card.Cells[6];
        viewModel.SelectSwatchCellCommand.Execute(cell);

        Assert.Same(cell, card.SelectedCell);
        Assert.NotNull(viewModel.SelectedRecipe);
        Assert.Equal(cell.Speed, viewModel.SelectedRecipe!.SpeedMmPerMinute);
        Assert.Equal(cell.Power, viewModel.SelectedRecipe.PowerPercent);
        Assert.Equal(cell.MaterialId, viewModel.SelectedRecipe.MaterialId);
    }

    private static MaterialsViewModel CreateMaterialsViewModel(TemporaryDirectory directory)
    {
        var http = new HttpClient();
        var account = new AccountViewModel(
            new LaseroAuthClient(http),
            new LaseroAccountClient(http),
            new SessionStore(Path.Combine(directory.Root, "session.json")),
            new DeviceIdStore(Path.Combine(directory.Root, "device-id.txt")),
            new DeviceActivationClient(http));
        return new MaterialsViewModel(
            new MaterialPresetStore(Path.Combine(directory.Root, "materials.json")),
            new AppSettingsStore(Path.Combine(directory.Root, "settings.json")),
            new MaterialSyncClient(http),
            account);
    }

    private static MaterialRecipe Recipe(MaterialDefinition material, double speed, double power) => new(
        $"{material.Id}-test",
        material.Id,
        material.Name,
        LaserTechnology.Diode,
        20,
        LayerMode.Fill,
        speed,
        power,
        1,
        300);

    private static double Distance(string first, string second)
    {
        var (r1, g1, b1) = Channels(first);
        var (r2, g2, b2) = Channels(second);
        return Math.Sqrt(Math.Pow(r1 - r2, 2) + Math.Pow(g1 - g2, 2) + Math.Pow(b1 - b2, 2));
    }

    private static (int R, int G, int B) Channels(string hex) => (
        int.Parse(hex.AsSpan(1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
        int.Parse(hex.AsSpan(3, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
        int.Parse(hex.AsSpan(5, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture));

    private sealed class TemporaryDirectory : IDisposable
    {
        public string Root { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), $"lasero-swatch-{Guid.NewGuid():N}");

        public TemporaryDirectory() => Directory.CreateDirectory(Root);

        public void Dispose()
        {
            try
            {
                Directory.Delete(Root, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}
