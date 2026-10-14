using Lasero.App.ViewModels;
using Lasero.Core.GCode;
using Lasero.Core.Grbl;
using Lasero.Core.Import;
using Lasero.Core.Layers;
using Lasero.Core.Scene;
using Xunit;

namespace Lasero.Tests;

/// <summary>Boolean results on operands from different layers: Unite keeps the topmost layer,
/// the other operations give every region the layer of the operand it comes from.</summary>
public sealed class BooleanMixedLayerTests
{
    private sealed record Fixture(SceneViewModel ViewModel, SceneObject Back, SceneObject Front,
        LayerSettings BackLayer, LayerSettings FrontLayer);

    private static SceneObject Square(LayerSettings layer, double x, double width = 20, Guid? group = null) => new()
    {
        Name = $"Čtverec {x}",
        GroupId = group,
        LocalShapes =
        [
            new ImportedShape
            {
                GeometrySetId = Guid.NewGuid(),
                LayerId = layer.Id,
                LayerColor = layer.Color,
                PreferredMode = layer.Mode,
                IsClosed = true,
                Points =
                [
                    new Position(x, 0, 0), new Position(x + width, 0, 0), new Position(x + width, 10, 0),
                    new Position(x, 10, 0), new Position(x, 0, 0),
                ],
            },
        ],
        LocalPivot = Position.Zero,
        LocalBounds = new BoundingBox2D(x, 0, x + width, 10),
        Transform = ObjectTransform.Identity,
    };

    // Back square world x:[0,20] on a Cut layer, front square x:[10,30] on a Fill layer.
    private static Fixture MakeFixture(Guid? group = null)
    {
        var viewModel = new SceneViewModel();
        var backLayer = LayerSettings.CreateDefault(new RgbColor(200, 30, 30), LayerMode.Cut, "Řez");
        var frontLayer = LayerSettings.CreateDefault(new RgbColor(30, 30, 200), LayerMode.Fill, "Výplň");
        viewModel.Layers.Add(backLayer);
        viewModel.Layers.Add(frontLayer);
        var back = Square(backLayer, 0, group: group);
        var front = Square(frontLayer, 10, group: group);
        viewModel.Objects.Add(back);
        viewModel.Objects.Add(front);
        viewModel.SelectedObjects.Add(back);
        viewModel.SelectedObjects.Add(front);
        return new Fixture(viewModel, back, front, backLayer, frontLayer);
    }

    private static IEnumerable<Guid> LayerIds(SceneObject item) => item.LocalShapes.Select(shape => shape.LayerId);

    [Fact]
    public void UniteKeepsTheTopmostLayer()
    {
        var f = MakeFixture();

        f.ViewModel.UniteSelectionCommand.Execute(null);

        var result = Assert.Single(f.ViewModel.Objects);
        Assert.All(LayerIds(result), id => Assert.Equal(f.FrontLayer.Id, id));
    }

    [Fact]
    public void SubtractKeepsTheBackOperandsLayer()
    {
        var f = MakeFixture();

        f.ViewModel.SubtractSelectionCommand.Execute(null);

        var result = Assert.Single(f.ViewModel.Objects);
        Assert.All(LayerIds(result), id => Assert.Equal(f.BackLayer.Id, id));
    }

    [Fact]
    public void IntersectTakesTheTopmostLayer()
    {
        var f = MakeFixture();

        f.ViewModel.IntersectSelectionCommand.Execute(null);

        var result = Assert.Single(f.ViewModel.Objects);
        Assert.All(LayerIds(result), id => Assert.Equal(f.FrontLayer.Id, id));
    }

    [Fact]
    public void ExcludeGivesEachRegionItsOwnOperandsLayer()
    {
        var f = MakeFixture();

        f.ViewModel.ExcludeSelectionCommand.Execute(null);

        Assert.Equal(2, f.ViewModel.Objects.Count);
        var left = f.ViewModel.Objects.Single(item => item.WorldBounds().MinX < 5);
        var right = f.ViewModel.Objects.Single(item => item.WorldBounds().MaxX > 25);
        Assert.All(LayerIds(left), id => Assert.Equal(f.BackLayer.Id, id));
        Assert.All(LayerIds(right), id => Assert.Equal(f.FrontLayer.Id, id));
        Assert.Equal(LayerMode.Cut, left.LocalShapes[0].PreferredMode);
        Assert.Equal(LayerMode.Fill, right.LocalShapes[0].PreferredMode);
    }

    [Fact]
    public void DivideGivesEachRegionItsOwnOperandsLayer()
    {
        var f = MakeFixture();

        f.ViewModel.DivideSelectionCommand.Execute(null);

        Assert.Equal(3, f.ViewModel.Objects.Count);
        var left = f.ViewModel.Objects.Single(item => item.WorldBounds().MaxX < 11);
        var middle = f.ViewModel.Objects.Single(item => item.WorldBounds().MinX > 9 && item.WorldBounds().MaxX < 21);
        var right = f.ViewModel.Objects.Single(item => item.WorldBounds().MinX > 19);
        Assert.All(LayerIds(left), id => Assert.Equal(f.BackLayer.Id, id));
        Assert.All(LayerIds(middle), id => Assert.Equal(f.FrontLayer.Id, id));
        Assert.All(LayerIds(right), id => Assert.Equal(f.FrontLayer.Id, id));
    }

    [Fact]
    public void FractureGivesEachRegionItsOwnOperandsLayer()
    {
        var f = MakeFixture();

        f.ViewModel.FractureSelectionCommand.Execute(null);

        Assert.Equal(3, f.ViewModel.Objects.Count);
        var left = f.ViewModel.Objects.Single(item => item.WorldBounds().MaxX < 11);
        var right = f.ViewModel.Objects.Single(item => item.WorldBounds().MinX > 19);
        Assert.All(LayerIds(left), id => Assert.Equal(f.BackLayer.Id, id));
        Assert.All(LayerIds(right), id => Assert.Equal(f.FrontLayer.Id, id));
    }

    [Fact]
    public void MixedLayerBooleanIsOneUndoStep()
    {
        var f = MakeFixture();

        f.ViewModel.ExcludeSelectionCommand.Execute(null);
        f.ViewModel.UndoCommand.Execute(null);

        Assert.Equal(2, f.ViewModel.Objects.Count);
        Assert.Contains(f.Back, f.ViewModel.Objects);
        Assert.Contains(f.Front, f.ViewModel.Objects);
        Assert.False(f.ViewModel.CanUndo);
    }

    [Fact]
    public void PreviewExplainsTheLayerRuleWhenOperandsAreOnDifferentLayers()
    {
        var f = MakeFixture();
        VectorOperationPreview? seen = null;
        f.ViewModel.BooleanPreviewRequested += preview => seen = preview;

        f.ViewModel.UniteSelectionCommand.Execute(null);
        Assert.Contains("horního", seen!.LayerNotice);

        f.ViewModel.SelectedObjects.Clear();
        f.ViewModel.SelectedObjects.Add(f.Back);
        f.ViewModel.SelectedObjects.Add(f.Front);
        f.ViewModel.ExcludeSelectionCommand.Execute(null);
        Assert.Contains("vznikla", seen!.LayerNotice);
    }

    [Fact]
    public void ResultsKeepTheSharedGroupOfTheirSources()
    {
        var group = Guid.NewGuid();
        var f = MakeFixture(group);

        f.ViewModel.UniteSelectionCommand.Execute(null);

        var result = Assert.Single(f.ViewModel.Objects);
        Assert.Equal(group, result.GroupId);
    }
}
