using System.Drawing.Imaging;
using System.IO;
using Lasero.App.ViewModels;
using Lasero.Core.GCode;
using Lasero.Core.Geometry;
using Lasero.Core.Grbl;
using Lasero.Core.Import;
using Lasero.Core.Layers;
using Lasero.Core.Scene;
using Lasero.Core.Scene.Commands;
using Xunit;

namespace Lasero.Tests;

/// <summary>
/// One layer-lifetime rule (EmptiedLayerPruner): a command that moves or removes objects removes, in
/// the same undo step, every layer that had users before it and has none after it. Layers that were
/// already empty stay. Undo puts every layer back at its original position.
/// </summary>
public sealed class EmptiedLayerPruningTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "lasero-layer-pruning", Guid.NewGuid().ToString("N"));

    public EmptiedLayerPruningTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private static SceneObject Square(LayerSettings layer, double x = 0) => new()
    {
        Name = layer.Name,
        LocalShapes =
        [
            new ImportedShape
            {
                Points = [new Position(0, 0, 0), new Position(20, 0, 0), new Position(20, 10, 0), new Position(0, 10, 0)],
                IsClosed = true,
                LayerId = layer.Id,
                LayerColor = layer.Color,
                PreferredMode = layer.Mode,
            },
        ],
        LocalPivot = new Position(10, 5, 0),
        LocalBounds = new BoundingBox2D(0, 0, 20, 10),
        Transform = ObjectTransform.Identity with { X = x },
    };

    /// <summary>Layers A, B, C, D (D empty from the start) with one object on each of A, B, C.</summary>
    private static (SceneViewModel Scene, LayerSettings[] Layers, SceneObject[] Objects) FourLayers()
    {
        var scene = new SceneViewModel();
        var layers = new[]
        {
            LayerSettings.CreateDefault(new RgbColor(200, 0, 0), LayerMode.Cut, "A"),
            LayerSettings.CreateDefault(new RgbColor(0, 200, 0), LayerMode.Cut, "B"),
            LayerSettings.CreateDefault(new RgbColor(0, 0, 200), LayerMode.Cut, "C"),
            LayerSettings.CreateDefault(new RgbColor(200, 200, 0), LayerMode.Cut, "D"),
        };
        foreach (var layer in layers) scene.Layers.Add(layer);
        var objects = new[] { Square(layers[0]), Square(layers[1], 30), Square(layers[2], 60) };
        foreach (var item in objects) scene.Objects.Add(item);
        return (scene, layers, objects);
    }

    [Fact]
    public void DeletingObjectsRemovesEveryLayerItEmptiedAndUndoRestoresTheOrder()
    {
        var (scene, layers, objects) = FourLayers();
        scene.SelectedObjects.ReplaceWith([objects[0], objects[2]]);

        scene.DeleteCommand.Execute(null);

        Assert.Equal(new[] { layers[1], layers[3] }, scene.Layers.ToArray());
        scene.UndoCommand.Execute(null);
        Assert.Equal(layers, scene.Layers.ToArray());
        scene.RedoCommand.Execute(null);
        Assert.Equal(new[] { layers[1], layers[3] }, scene.Layers.ToArray());
    }

    [Fact]
    public void AssigningToALayerRemovesTheSourceLayerItEmptied()
    {
        var (scene, layers, objects) = FourLayers();
        scene.SelectedObjects.ReplaceWith([objects[0]]);

        scene.AssignSelectionToLayerCommand.Execute(layers[1]);

        Assert.Equal(new[] { layers[1], layers[2], layers[3] }, scene.Layers.ToArray());
        Assert.True(scene.CanUndo);
        scene.UndoCommand.Execute(null);
        Assert.Equal(layers, scene.Layers.ToArray());
        Assert.Equal(layers[0].Id, objects[0].LocalShapes[0].LayerId);
    }

    [Fact]
    public void AssigningOntoTheAlreadyEmptyLayerKeepsItAndRemovesTheSource()
    {
        var (scene, layers, objects) = FourLayers();
        scene.SelectedObjects.ReplaceWith([objects[2]]);

        scene.AssignSelectionToLayerCommand.Execute(layers[3]);

        Assert.Equal(new[] { layers[0], layers[1], layers[3] }, scene.Layers.ToArray());
    }

    [Fact]
    public void UnitingAcrossLayersRemovesTheLayerOfTheConsumedOperand()
    {
        var (scene, layers, objects) = FourLayers();
        objects[1].Transform = objects[1].Transform with { X = 10 };
        scene.SelectedObjects.ReplaceWith([objects[0], objects[1]]);

        scene.UniteSelectionCommand.Execute(null);

        Assert.DoesNotContain(layers[0], scene.Layers);
        Assert.Contains(layers[3], scene.Layers); // already empty before: stays
        scene.UndoCommand.Execute(null);
        Assert.Equal(layers, scene.Layers.ToArray());
    }

    [Fact]
    public void ASwatchRemovesTheLayersItEmptiedAndKeepsAlreadyEmptyOnes()
    {
        var (scene, layers, objects) = FourLayers();
        scene.SelectedObjects.ReplaceWith([objects[0], objects[1]]);

        scene.ChangeSelectedLayerColorCommand.Execute(new RgbColor(0, 0, 205)); // C's colour

        Assert.Equal(new[] { layers[2], layers[3] }, scene.Layers.ToArray());
        scene.UndoCommand.Execute(null);
        Assert.Equal(layers, scene.Layers.ToArray());
    }

    [Fact]
    public void ACommandThatEmptiesNothingRemovesNoLayer()
    {
        var (scene, layers, objects) = FourLayers();
        scene.SelectedObjects.ReplaceWith([objects[0]]);

        scene.DuplicateCommand.Execute(null);
        scene.DeleteCommand.Execute(null); // deletes the duplicate, A still has its original

        Assert.Equal(layers, scene.Layers.ToArray());
    }

    [Fact]
    public void TheRasterLayerLeavesOnlyWithItsLastBitmap()
    {
        var path = Path.Combine(_dir, "a.png");
        using (var bitmap = new System.Drawing.Bitmap(2, 1)) bitmap.Save(path, ImageFormat.Png);
        var scene = new SceneViewModel();
        var options = new RasterImportOptions { TargetWidthMm = 20, Dpi = 254 };
        scene.ImportRasterFiles([path, path], options);
        var rasterLayer = Assert.Single(scene.Layers);

        scene.SelectedObjects.ReplaceWith([scene.Objects[0]]);
        scene.DeleteCommand.Execute(null);
        Assert.Contains(rasterLayer, scene.Layers);

        scene.SelectedObjects.ReplaceWith([scene.Objects[0]]);
        scene.DeleteCommand.Execute(null);
        Assert.Empty(scene.Layers);

        scene.UndoCommand.Execute(null);
        Assert.Same(rasterLayer, Assert.Single(scene.Layers));
    }

    [Fact]
    public void ReplaceObjectsCommandPrunesAndRestoresAtCoreLevel()
    {
        var scene = new SceneDocument();
        var red = LayerSettings.CreateDefault(RgbColor.Red, LayerMode.Cut, "Červená");
        var blue = LayerSettings.CreateDefault(new RgbColor(0, 0, 255), LayerMode.Cut, "Modrá");
        scene.Layers.Add(red);
        scene.Layers.Add(blue);
        var source = Square(red);
        scene.Objects.Add(source);
        scene.Objects.Add(Square(blue, 30));
        var stack = new SceneCommandStack();

        stack.Execute(new ReplaceObjectsCommand(scene, [source], [Square(blue, 60)]));

        Assert.Same(blue, Assert.Single(scene.Layers));
        stack.Undo();
        Assert.Equal(new[] { red, blue }, scene.Layers.ToArray());
        Assert.Same(source, scene.Objects[0]);
    }
}
