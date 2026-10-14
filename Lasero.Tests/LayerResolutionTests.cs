using System.IO;
using System.Reflection;
using Lasero.App;
using Lasero.App.ViewModels;
using Lasero.Core.GCode;
using Lasero.Core.Grbl;
using Lasero.Core.Import;
using Lasero.Core.Layers;
using Lasero.Core.Scene;
using Xunit;

namespace Lasero.Tests;

/// <summary>
/// Which layer owns a contour: one rule (LayerResolver) for the canvas, selection, the inspector and the
/// job, by LayerId first and never by colour alone where two layers share a colour.
/// </summary>
public sealed class LayerResolutionTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "lasero-layer-resolution", Guid.NewGuid().ToString("N"));

    public LayerResolutionTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private static SceneObject Square(LayerSettings layer, double x = 0, Guid? layerId = null) => new()
    {
        Name = "čtverec",
        LocalShapes =
        [
            new ImportedShape
            {
                Points = [new Position(0, 0, 0), new Position(20, 0, 0), new Position(20, 10, 0), new Position(0, 10, 0)],
                IsClosed = true,
                LayerId = layerId ?? layer.Id,
                LayerColor = layer.Color,
                PreferredMode = layer.Mode,
            },
        ],
        LocalPivot = new Position(10, 5, 0),
        LocalBounds = new BoundingBox2D(0, 0, 20, 10),
        Transform = ObjectTransform.Identity with { X = x },
    };

    private List<string> SceneGCode(SceneViewModel scene)
    {
        using var machine = new GrblConnection(new VirtualGrblTransport { ResponseDelay = TimeSpan.Zero });
        var gcode = new GCodeViewModel(machine, scene, new AppSettingsStore(Path.Combine(_dir, $"{Guid.NewGuid():N}.json")));
        var method = typeof(GCodeViewModel).GetMethod("BuildSceneGCode", BindingFlags.Instance | BindingFlags.NonPublic)!;
        return (List<string>)method.Invoke(gcode, [0d, 0d, (double?)1000, null])!;
    }

    private static int CutMoves(IEnumerable<string> lines) => lines.Count(line => line.StartsWith("G1", StringComparison.Ordinal));

    // K1 -------------------------------------------------------------------------------------------

    [Fact]
    public void AGreyVectorLayerStaysAVectorLayerAfterSaveAndReload()
    {
        var svg = Path.Combine(_dir, "grey.svg");
        File.WriteAllText(svg, "<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 10 10\"><rect width=\"5\" height=\"5\" fill=\"none\" stroke=\"gray\"/></svg>");
        var scene = new SceneViewModel();
        scene.ImportSvgFile(svg, 50);
        var before = CutMoves(SceneGCode(scene));

        var reloaded = new SceneViewModel();
        reloaded.LoadProject(ProjectFileSerializer.Deserialize(ProjectFileSerializer.Serialize(scene.CreateProject())));

        Assert.True(before > 0);
        Assert.False(Assert.Single(reloaded.Layers).IsRaster);
        Assert.Equal(before, CutMoves(SceneGCode(reloaded)));
    }

    [Fact]
    public void AFileWithoutTheRasterFlagInfersItOnlyForALayerBitmapsUse()
    {
        var bitmapLayerId = Guid.NewGuid();
        var greyVectorLayerId = Guid.NewGuid();
        ProjectShape Outline(Guid layerId, RgbColor color) => new()
        {
            LayerId = layerId,
            LayerColor = color,
            PreferredMode = LayerMode.Cut,
            IsClosed = true,
            Points = [new Position(0, 0, 0), new Position(10, 0, 0), new Position(10, 10, 0)],
        };
        var project = new LaseroProjectFile
        {
            Layers =
            [
                new ProjectLayer { Id = bitmapLayerId, Color = SceneObjectFactory.RasterEngravingColor, Name = "Bitmapa", Mode = LayerMode.Fill, Speed = 3000, Power = 30, Passes = 1, FillLineIntervalMm = 0.1 },
                new ProjectLayer { Id = greyVectorLayerId, Color = new RgbColor(0x80, 0x80, 0x80), Name = "Šedá", Mode = LayerMode.Cut, Speed = 350, Power = 95, Passes = 1, FillLineIntervalMm = 0.1 },
            ],
            Objects =
            [
                new ProjectObject
                {
                    Name = "obrázek", RasterFilePath = Path.Combine(_dir, "missing.png"),
                    Shapes = [Outline(bitmapLayerId, SceneObjectFactory.RasterEngravingColor)],
                    LocalPivot = new Position(5, 5, 0), LocalBounds = new BoundingBox2D(0, 0, 10, 10),
                },
                new ProjectObject
                {
                    Name = "čára",
                    Shapes = [Outline(greyVectorLayerId, new RgbColor(0x80, 0x80, 0x80))],
                    LocalPivot = new Position(5, 5, 0), LocalBounds = new BoundingBox2D(0, 0, 10, 10),
                },
            ],
        };
        var scene = new SceneViewModel();

        scene.LoadProject(project);

        Assert.True(scene.Layers.Single(layer => layer.Id == bitmapLayerId).IsRaster);
        Assert.False(scene.Layers.Single(layer => layer.Id == greyVectorLayerId).IsRaster);
    }

    // K3 -------------------------------------------------------------------------------------------

    [Fact]
    public void SelectingAnObjectSelectsItsLayerByIdEvenWhenAnotherLayerHasTheSameColour()
    {
        var scene = new SceneViewModel();
        var cut = LayerSettings.CreateDefault(RgbColor.Black, LayerMode.Cut, "Černá čára");
        var fill = LayerSettings.CreateDefault(RgbColor.Black, LayerMode.Fill, "Černá výplň");
        scene.Layers.Add(cut);
        scene.Layers.Add(fill);
        var onCut = Square(cut);
        var onFill = Square(fill, 40);
        scene.Objects.Add(onCut);
        scene.Objects.Add(onFill);

        scene.SelectedObjects.ReplaceWith([onFill]);
        Assert.Same(fill, scene.SelectedLayer);

        scene.SelectedObjects.ReplaceWith([onCut]);
        Assert.Same(cut, scene.SelectedLayer);
    }

    [Fact]
    public void ASelectionOverSeveralLayersShowsNoSingleLayer()
    {
        var scene = new SceneViewModel();
        var cut = LayerSettings.CreateDefault(RgbColor.Black, LayerMode.Cut, "Černá čára");
        var fill = LayerSettings.CreateDefault(RgbColor.Black, LayerMode.Fill, "Černá výplň");
        scene.Layers.Add(cut);
        scene.Layers.Add(fill);
        var onCut = Square(cut);
        var onFill = Square(fill, 40);
        scene.Objects.Add(onCut);
        scene.Objects.Add(onFill);

        scene.SelectedObjects.ReplaceWith([onCut, onFill]);

        Assert.Null(scene.SelectedLayer);
        Assert.True(scene.IsMixedLayerSelection);
        Assert.False(scene.CanDeleteSelectedLayer);

        scene.SelectedLayer = fill; // the operator picks a row in the layer list
        Assert.False(scene.IsMixedLayerSelection);

        scene.SelectedObjects.ReplaceWith([onCut]);
        Assert.Same(cut, scene.SelectedLayer);
    }

    [Fact]
    public void ASwatchIsANoOpWhenTheSelectionIsAlreadyOnLayersOfThatColour()
    {
        var scene = new SceneViewModel();
        var cut = LayerSettings.CreateDefault(RgbColor.Black, LayerMode.Cut, "Černá čára");
        var fill = LayerSettings.CreateDefault(RgbColor.Black, LayerMode.Fill, "Černá výplň");
        scene.Layers.Add(cut);
        scene.Layers.Add(fill);
        var onCut = Square(cut);
        var onFill = Square(fill, 40);
        scene.Objects.Add(onCut);
        scene.Objects.Add(onFill);
        scene.SelectedObjects.ReplaceWith([onCut, onFill]);

        scene.ChangeSelectedLayerColorCommand.Execute(RgbColor.Black);

        Assert.False(scene.CanUndo);
        Assert.Equal(cut.Id, onCut.LocalShapes[0].LayerId);
        Assert.Equal(fill.Id, onFill.LocalShapes[0].LayerId);
    }

    // K4 -------------------------------------------------------------------------------------------

    [Fact]
    public void AnOutlineImportedAfterASameColourFillGetsACutLayer()
    {
        var fillSvg = Path.Combine(_dir, "fill.svg");
        var lineSvg = Path.Combine(_dir, "line.svg");
        File.WriteAllText(fillSvg, "<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 10 10\"><rect width=\"5\" height=\"5\" fill=\"#000000\"/></svg>");
        File.WriteAllText(lineSvg, "<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 10 10\"><rect width=\"5\" height=\"5\" fill=\"none\" stroke=\"#000000\"/></svg>");
        var scene = new SceneViewModel();

        scene.ImportSvgFile(fillSvg, 50);
        scene.ImportSvgFile(lineSvg, 50);

        Assert.Equal(2, scene.Layers.Count);
        var outlineLayer = scene.Layers.Single(layer => layer.Id == scene.Objects[1].LocalShapes[0].LayerId);
        Assert.Equal(LayerMode.Cut, outlineLayer.Mode);
        var fillLayer = scene.Layers.Single(layer => layer.Id == scene.Objects[0].LocalShapes[0].LayerId);
        Assert.Equal(LayerMode.Fill, fillLayer.Mode);
    }

    // K6 -------------------------------------------------------------------------------------------

    [Fact]
    public void AnOrphanedContourIsBurnedOnTheLayerTheCanvasShowsItOn()
    {
        var scene = new SceneViewModel();
        var red = LayerSettings.CreateDefault(RgbColor.Red, LayerMode.Cut, "Červená");
        scene.Layers.Add(red);
        var orphan = Square(red, layerId: Guid.NewGuid());
        scene.Objects.Add(orphan);
        red.IsVisible = false;

        // The canvas resolves it to the red layer (now hidden) and so does the job.
        Assert.False(scene.CanSelectObject(orphan));
        Assert.False(scene.IsLayerVisible(orphan.LocalShapes[0].LayerId, orphan.LocalShapes[0].LayerColor));
        red.IsVisible = true;
        var job = CutMoves(SceneGCode(scene));
        var direct = CutMoves(ToolpathBuilder.BuildGCode(scene.Scene.ToImportedDocument(), 1000));
        Assert.True(job > 0);
        Assert.Equal(direct, job);
    }

    [Fact]
    public void ALegacyContourMatchingTwoLayersIsEmittedOnceOnTheNearestColour()
    {
        var scene = new SceneViewModel();
        var far = LayerSettings.CreateDefault(new RgbColor(240, 0, 0), LayerMode.Cut, "Daleká");
        var near = LayerSettings.CreateDefault(new RgbColor(252, 0, 0), LayerMode.Cut, "Blízká");
        scene.Layers.Add(far);
        scene.Layers.Add(near);
        scene.Objects.Add(Square(LayerSettings.CreateDefault(RgbColor.Red, LayerMode.Cut, "legacy"), layerId: Guid.Empty));

        var lines = SceneGCode(scene);

        var single = CutMoves(ToolpathBuilder.BuildGCode(new ImportedDocument
        {
            Shapes = scene.Scene.ToImportedDocument().Shapes,
            Layers = [near],
            BoundingBox = BoundingBox2D.Empty,
        }, 1000));
        Assert.Equal(single, CutMoves(lines));
        Assert.Contains(lines, line => line.Contains("Blízká", StringComparison.Ordinal));
        Assert.DoesNotContain(lines, line => line.Contains("Daleká", StringComparison.Ordinal));
    }

    [Fact]
    public void LayerResolverPrefersTheContoursOwnModeBetweenTwoSameColourLayers()
    {
        var cut = LayerSettings.CreateDefault(RgbColor.Black, LayerMode.Cut, "Čára");
        var fill = LayerSettings.CreateDefault(RgbColor.Black, LayerMode.Fill, "Výplň");
        IReadOnlyList<LayerSettings> layers = [cut, fill];

        Assert.Same(fill, LayerResolver.Resolve(Guid.Empty, RgbColor.Black, LayerMode.Fill, layers));
        Assert.Same(cut, LayerResolver.Resolve(Guid.Empty, RgbColor.Black, LayerMode.Cut, layers));
        Assert.Same(fill, LayerResolver.Resolve(fill.Id, RgbColor.Red, LayerMode.Cut, layers));
        Assert.Null(LayerResolver.Resolve(Guid.Empty, new RgbColor(0, 0, 255), LayerMode.Cut, layers));
    }

    // L4 -------------------------------------------------------------------------------------------

    [Fact]
    public void UnitingTwoObjectsOfOneLayerWhoseStoredColourDriftedKeepsThemOnThatLayer()
    {
        var scene = new SceneViewModel();
        var red = LayerSettings.CreateDefault(RgbColor.Red, LayerMode.Cut, "Červená");
        scene.Layers.Add(red);
        var a = Square(red);
        var b = Square(red, 10);
        b.RestoreLocalShapes([b.LocalShapes[0] with { LayerColor = new RgbColor(250, 4, 4) }]);
        scene.Objects.Add(a);
        scene.Objects.Add(b);
        scene.SelectedObjects.ReplaceWith([a, b]);

        scene.UniteSelectionCommand.Execute(null);

        var united = Assert.Single(scene.Objects);
        Assert.All(united.LocalShapes, shape => Assert.Equal(red.Id, shape.LayerId));
        Assert.Same(red, Assert.Single(scene.Layers));
    }
}
