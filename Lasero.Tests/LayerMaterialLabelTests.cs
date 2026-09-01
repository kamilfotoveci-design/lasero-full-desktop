using Lasero.Core.Layers;
using Xunit;

namespace Lasero.Tests;

/// <summary>
/// The material name on a layer is a claim about where its numbers came from. These tests hold the
/// one rule that keeps the claim true: the label survives only as long as the numbers do.
/// </summary>
public class LayerMaterialLabelTests
{
    [Fact]
    public void ApplyRecipe_RecordsTheMaterialAlongsideTheNumbers()
    {
        var layer = new LayerSettings();

        layer.ApplyRecipe(LayerMode.Cut, 350, 100, 1, 0.1, "Překližka 3 mm");

        Assert.Equal(LayerMode.Cut, layer.Mode);
        Assert.Equal(350, layer.Speed);
        Assert.Equal(100, layer.Power);
        Assert.Equal(1, layer.Passes);
        Assert.Equal(0.1, layer.FillLineIntervalMm);
        Assert.Equal("Překližka 3 mm", layer.MaterialLabel);
        Assert.Equal("Překližka 3 mm", layer.MaterialDisplayLabel);
    }

    [Fact]
    public void EditingAnyNumberByHandDropsTheMaterialName()
    {
        // Otherwise the inspector reads "Překližka 3 mm" off a layer whose power was just halved,
        // and the operator trusts a figure the catalogue never gave them.
        foreach (var edit in new Action<LayerSettings>[]
                 {
                     layer => layer.Speed = 500,
                     layer => layer.Power = 50,
                     layer => layer.Passes = 3,
                     layer => layer.FillLineIntervalMm = 0.2,
                 })
        {
            var layer = new LayerSettings();
            layer.ApplyRecipe(LayerMode.Cut, 350, 100, 1, 0.1, "Překližka 3 mm");

            edit(layer);

            Assert.Null(layer.MaterialLabel);
            Assert.Equal("Vlastní nastavení", layer.MaterialDisplayLabel);
        }
    }

    [Fact]
    public void SwitchingModeDropsTheMaterialName()
    {
        // A cutting recipe says nothing about the same material being filled, and the mode change
        // itself migrates Speed and Power away from the recipe's values.
        var layer = new LayerSettings();
        layer.ApplyRecipe(LayerMode.Cut, 350, 95, 1, 0.1, "Překližka 3 mm");

        layer.Mode = LayerMode.Fill;

        Assert.Null(layer.MaterialLabel);
    }

    [Fact]
    public void ReapplyingARecipeAfterAHandEditRestoresTheName()
    {
        var layer = new LayerSettings();
        layer.Speed = 1234;
        Assert.Null(layer.MaterialLabel);

        layer.ApplyRecipe(LayerMode.Fill, 3000, 30, 1, 0.1, "MDF 3 mm");

        Assert.Equal("MDF 3 mm", layer.MaterialLabel);
    }
}
