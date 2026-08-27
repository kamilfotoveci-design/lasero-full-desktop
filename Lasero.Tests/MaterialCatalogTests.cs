using Lasero.Core.Layers;
using Lasero.Core.Materials;

namespace Lasero.Tests;

public sealed class MaterialCatalogTests
{
    [Fact]
    public void Catalog_ContainsEveryWebAppRecipe()
    {
        Assert.Equal(12, MaterialCatalog.Materials.Count);
        Assert.Equal(300, MaterialCatalog.Recipes.Count);
        Assert.Equal(100, MaterialCatalog.Recipes.Count(recipe => recipe.Technology == LaserTechnology.Diode));
        Assert.Equal(100, MaterialCatalog.Recipes.Count(recipe => recipe.Technology == LaserTechnology.Infrared));
        Assert.Equal(100, MaterialCatalog.Recipes.Count(recipe => recipe.Technology == LaserTechnology.Co2));
        Assert.Equal(MaterialCatalog.Recipes.Count, MaterialCatalog.Recipes.Select(recipe => recipe.Id).Distinct().Count());
    }

    [Theory]
    [InlineData(LaserTechnology.Diode, 20, "wood", LayerMode.Fill, 5000, 50, 1, 300)]
    [InlineData(LaserTechnology.Diode, 20, "plywood", LayerMode.Cut, 280, 100, 3, null)]
    [InlineData(LaserTechnology.Infrared, 20, "metal", LayerMode.Fill, 1500, 100, 1, 200)]
    [InlineData(LaserTechnology.Co2, 40, "acrylic", LayerMode.Cut, 500, 75, 1, null)]
    public void Catalog_MatchesCanonicalWebValues(LaserTechnology technology, int watts, string materialId,
        LayerMode mode, double speed, double power, int passes, int? dpi)
    {
        var recipe = Assert.Single(MaterialCatalog.Find(technology, watts, mode, materialId));

        Assert.Equal(speed, recipe.SpeedMmPerMinute);
        Assert.Equal(power, recipe.PowerPercent);
        Assert.Equal(passes, recipe.Passes);
        Assert.Equal(dpi, recipe.Dpi);
    }

    [Fact]
    public void EveryRecipe_HasSafeValidParametersAndKnownMaterial()
    {
        var materialIds = MaterialCatalog.Materials.Select(material => material.Id).ToHashSet();
        Assert.All(MaterialCatalog.Recipes, recipe =>
        {
            Assert.Contains(recipe.MaterialId, materialIds);
            Assert.True(recipe.SpeedMmPerMinute > 0);
            Assert.InRange(recipe.PowerPercent, 0, 100);
            Assert.InRange(recipe.Passes, 1, 100);
            if (recipe.Mode == LayerMode.Fill) Assert.True(recipe.Dpi > 0);
        });
    }
}
