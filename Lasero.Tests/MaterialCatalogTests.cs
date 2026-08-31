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

    /// <summary>Pins the values the desktop hands to the machine. Every row still mirrors the
    /// lasero.net database except diode 20 W plywood cutting, which was measured on the shop's own
    /// machine and deliberately diverges until the web catalog is corrected to match.</summary>
    [Theory]
    [InlineData(LaserTechnology.Diode, 20, "wood", LayerMode.Fill, 5000, 50, 1, 300)]
    [InlineData(LaserTechnology.Diode, 20, "plywood", LayerMode.Cut, 350, 100, 1, null)]
    [InlineData(LaserTechnology.Infrared, 20, "metal", LayerMode.Fill, 1500, 100, 1, 200)]
    [InlineData(LaserTechnology.Co2, 40, "acrylic", LayerMode.Cut, 500, 75, 1, null)]
    public void Catalog_MatchesTheAgreedRecipeValues(LaserTechnology technology, int watts, string materialId,
        LayerMode mode, double speed, double power, int passes, int? dpi)
    {
        var recipe = Assert.Single(MaterialCatalog.Find(technology, watts, mode, materialId));

        Assert.Equal(speed, recipe.SpeedMmPerMinute);
        Assert.Equal(power, recipe.PowerPercent);
        Assert.Equal(passes, recipe.Passes);
        Assert.Equal(dpi, recipe.Dpi);
    }

    /// <summary>A stronger laser must never be told to spend more energy on the same cut than a weaker
    /// one. The catalog broke this rule before the plywood and MDF rows were re-based: 20 W came out
    /// hotter than 40 W. Dose is power x passes / speed, which is what the machine actually delivers.</summary>
    [Fact]
    public void AStrongerDiodeNeverNeedsMoreDoseForTheSameCut()
    {
        foreach (var materialId in MaterialCatalog.Materials.Select(material => material.Id))
        {
            var rows = MaterialCatalog.PowerClasses
                .Select(watts => new
                {
                    Watts = watts,
                    Recipe = MaterialCatalog.Find(LaserTechnology.Diode, watts, LayerMode.Cut, materialId).SingleOrDefault(),
                })
                .Where(row => row.Recipe is not null)
                .ToArray();

            for (var i = 1; i < rows.Length; i++)
            {
                var weaker = rows[i - 1];
                var stronger = rows[i];
                Assert.True(Dose(stronger.Recipe!) < Dose(weaker.Recipe!),
                    $"{materialId}: {stronger.Watts} W asks for {Dose(stronger.Recipe!):0.###} dose, " +
                    $"more than {weaker.Watts} W at {Dose(weaker.Recipe!):0.###}");
            }
        }

        static double Dose(MaterialRecipe recipe) =>
            recipe.PowerPercent * recipe.Passes / recipe.SpeedMmPerMinute;
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
