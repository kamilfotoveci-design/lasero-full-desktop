using Lasero.Avalonia.ViewModels;
using Lasero.Core.Layers;
using Lasero.Core.Materials;

namespace Lasero.Avalonia.Tests;

public sealed class MaterialCatalogViewModelTests
{
    [Fact]
    public void FiltersRecommendedRecipesAndSwatchCardsTogether()
    {
        TestApp.EnsureInitialized();

        var viewModel = new MaterialCatalogViewModel
        {
            SelectedTechnology = LaserTechnology.Diode,
            SelectedPowerWatts = 20,
            SelectedOperation = LayerMode.Fill,
            SearchText = "Překližka",
        };

        var recipe = Assert.Single(viewModel.RecommendedRecipes);
        Assert.Equal("plywood", recipe.MaterialId);
        Assert.Equal(20, recipe.PowerWatts);
        Assert.Equal(LayerMode.Fill, recipe.Mode);

        // Every recommended recipe gets its own swatch card, built from the same match set — not a
        // second, independently-filtered list.
        var card = Assert.Single(viewModel.SwatchCards);
        Assert.Equal("plywood", card.Material.Id);
    }

    [Fact]
    public void SelectingASwatchCellProducesARecipeWithTheCellsOwnSpeedAndPower()
    {
        TestApp.EnsureInitialized();

        var viewModel = new MaterialCatalogViewModel
        {
            SelectedTechnology = LaserTechnology.Diode,
            SelectedPowerWatts = 20,
            SelectedOperation = LayerMode.Fill,
            SearchText = "Překližka",
        };
        var card = Assert.Single(viewModel.SwatchCards);
        var cell = card.Cells[5]; // an arbitrary interior cell, not the recipe's own default

        viewModel.SelectSwatchCell.Execute(cell).Subscribe();

        Assert.NotNull(viewModel.SelectedRecipe);
        Assert.Equal(cell.Speed, viewModel.SelectedRecipe!.SpeedMmPerMinute);
        Assert.Equal(cell.Power, viewModel.SelectedRecipe.PowerPercent);
        Assert.True(card.HasSelection);
        Assert.Same(cell, card.SelectedCell);
    }

    [Fact]
    public void RecipesForReturnsTheCurrentProfileAtASpecificMode()
    {
        TestApp.EnsureInitialized();

        var viewModel = new MaterialCatalogViewModel
        {
            SelectedTechnology = LaserTechnology.Diode,
            SelectedPowerWatts = 20,
        };

        var cutRecipes = viewModel.RecipesFor(LayerMode.Cut).ToList();
        Assert.NotEmpty(cutRecipes);
        Assert.All(cutRecipes, r => Assert.Equal(LayerMode.Cut, r.Mode));
        Assert.All(cutRecipes, r => Assert.Equal(20, r.PowerWatts));
    }
}
