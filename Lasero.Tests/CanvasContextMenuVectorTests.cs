using System.IO;
using Lasero.App.Controls;
using Lasero.App.ViewModels;
using Lasero.Core.GCode;
using Lasero.Core.Grbl;
using Lasero.Core.Import;
using Lasero.Core.Layers;
using Lasero.Core.Raster;
using Lasero.Core.Scene;
using Xunit;

namespace Lasero.Tests;

/// <summary>Right-click on a vector object (a traced logo, a pen path, a primitive, a group) gives the
/// same core menu as text, in the same order; "Převést na bitmapu" renders the selection into one
/// bitmap object as a single undo step.</summary>
public sealed class CanvasContextMenuVectorTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "lasero-bitmap-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true); }
        catch (IOException) { /* a leftover temp folder is not a test failure */ }
    }

    // --- helpers ----------------------------------------------------------------------------------

    private static IEnumerable<ContextMenuItemModel> Flatten(IEnumerable<ContextMenuItemModel> rows) =>
        rows.SelectMany(row => row.Kind == ContextMenuItemKind.Submenu ? Flatten(row.Children).Prepend(row) : [row]);

    private static ContextMenuItemModel? Find(IEnumerable<ContextMenuItemModel> rows, ContextAction action) =>
        Flatten(rows).FirstOrDefault(row => row.Kind == ContextMenuItemKind.Command && row.Action == action);

    /// <summary>Header and shortcut of every row up to and including "Zobrazit vlastnosti", "-" for a
    /// separator. Enabled or disabled is deliberately left out: it depends on the object, the order does not.</summary>
    private static List<string> Core(IEnumerable<ContextMenuItemModel> rows)
    {
        var core = new List<string>();
        foreach (var row in rows)
        {
            core.Add(row.IsSeparator ? "-" : $"{row.Header}|{row.Gesture}");
            if (row.Action == ContextAction.ShowProperties) break;
        }
        return core;
    }

    private static readonly string[] ExpectedCore =
    [
        "Vyjmout|Ctrl+X", "Kopírovat|Ctrl+C", "Vložit|Ctrl+V", "Duplikovat|Ctrl+D",
        "-",
        "Odstranit|Del",
        "-",
        "Vybrat vše|Ctrl+A", "Seskupit|Ctrl+G", "Odebrat ze skupiny|Ctrl+U", "Oddělit obrysy|", "Zamknout vybrané tvary|",
        "-",
        "Převést na dráhu|Ctrl+Shift+C", "Převést na bitmapu|Ctrl+Shift+B",
        "-",
        "Náhled|Alt+P",
        "-",
        "Zobrazit vlastnosti|",
    ];

    private static readonly CanvasContextMenuStates.Reach Reach = new() { HasPreview = true, HasProperties = true };

    private static IReadOnlyList<ContextMenuItemModel> MenuFor(SceneViewModel scene) =>
        CanvasContextMenuBuilder.ForObject(CanvasContextMenuStates.ForSelection(scene, Reach));

    private static VectorPath TwoSquares() => new()
    {
        Subpaths =
        [
            Square(0, 0, 10),
            Square(30, 0, 10),
        ],
    };

    private static VectorSubpath Square(double x, double y, double size) => new()
    {
        IsClosed = true,
        Nodes =
        [
            VectorNode.CornerAt(new Position(x, y, 0)),
            VectorNode.CornerAt(new Position(x + size, y, 0)),
            VectorNode.CornerAt(new Position(x + size, y + size, 0)),
            VectorNode.CornerAt(new Position(x, y + size, 0)),
        ],
    };

    private static SceneObject AddRectangle(SceneViewModel scene, double x)
    {
        scene.DrawPrimitive(DesignerTool.Rectangle, new Position(x, 0, 0), new Position(x + 20, 10, 0));
        return scene.Objects[^1];
    }

    private static void Select(SceneViewModel scene, params SceneObject[] objects)
    {
        scene.SelectedObjects.Clear();
        foreach (var item in objects) scene.SelectedObjects.Add(item);
    }

    private SceneViewModel NewScene() => new() { BitmapConversionDirectory = _directory };

    /// <summary>Two rectangles far apart, both on one fill layer, both selected.</summary>
    private (SceneViewModel Scene, SceneObject A, SceneObject B) FilledRectangles()
    {
        var scene = NewScene();
        var a = AddRectangle(scene, 0);
        var b = AddRectangle(scene, 60);
        var fill = LayerSettings.CreateDefault(new RgbColor(10, 120, 200), LayerMode.Fill, "Gravír");
        scene.Layers.Add(fill);
        Select(scene, a, b);
        scene.AssignSelectionToLayerCommand.Execute(fill);
        Select(scene, a, b);
        return (scene, a, b);
    }

    private static void AssertBounds(BoundingBox2D expected, BoundingBox2D actual)
    {
        Assert.Equal(expected.MinX, actual.MinX, 6);
        Assert.Equal(expected.MinY, actual.MinY, 6);
        Assert.Equal(expected.MaxX, actual.MaxX, 6);
        Assert.Equal(expected.MaxY, actual.MaxY, 6);
    }

    private static System.Drawing.Color Pixel(string path, int x, int y)
    {
        using var bitmap = new System.Drawing.Bitmap(path);
        return bitmap.GetPixel(x, y);
    }

    // --- one core menu for every kind of object ---------------------------------------------------

    [Fact]
    public void ATracedLogoGetsTheLightBurnCoreThenItsNodeEditingAfterIt()
    {
        var scene = NewScene();
        scene.AddVectorPath(TwoSquares());
        var rows = MenuFor(scene);

        Assert.Equal(ExpectedCore, Core(rows));

        var order = rows.Where(row => !row.IsSeparator).Select(row => row.Action).ToList();
        Assert.True(order.IndexOf(ContextAction.ShowProperties) < order.IndexOf(ContextAction.EditNodes));
        Assert.True(order.IndexOf(ContextAction.EditNodes) < order.IndexOf(ContextAction.Offset));
        Assert.Equal(ContextAction.EditNodes, rows[rows.ToList().FindIndex(row => row.Action == ContextAction.ShowProperties) + 2].Action);
    }

    [Fact]
    public void EveryKindOfObjectHasTheSameCoreInTheSameOrder()
    {
        var traced = NewScene();
        traced.AddVectorPath(TwoSquares());

        var text = NewScene();
        text.AddText("LASERO", new Position(10, 20, 0), 12);

        var primitive = NewScene();
        AddRectangle(primitive, 0);

        var two = NewScene();
        var first = AddRectangle(two, 0);
        var second = AddRectangle(two, 30);
        Select(two, first, second);

        var grouped = NewScene();
        var g1 = AddRectangle(grouped, 0);
        var g2 = AddRectangle(grouped, 30);
        Select(grouped, g1, g2);
        grouped.GroupSelectionCommand.Execute(null);

        foreach (var scene in new[] { traced, text, primitive, two, grouped })
            Assert.Equal(ExpectedCore, Core(MenuFor(scene)));
    }

    [Fact]
    public void ARectangleConvertsToAnEditablePathAndHasNoNodeRowYet()
    {
        var scene = NewScene();
        AddRectangle(scene, 0);
        var rows = MenuFor(scene);

        var convert = Find(rows, ContextAction.ConvertToCurves)!;
        Assert.Equal("Převést na dráhu", convert.Header);
        Assert.True(convert.IsEnabled);
        Assert.Null(Find(rows, ContextAction.EditNodes));
        Assert.Null(Find(rows, ContextAction.ConvertTextToPath));
    }

    [Fact]
    public void AnObjectThatIsAlreadyAPathShowsConvertDisabledWithTheReason()
    {
        var scene = NewScene();
        scene.AddVectorPath(TwoSquares());

        var convert = Find(MenuFor(scene), ContextAction.ConvertToCurves)!;

        Assert.False(convert.IsEnabled);
        Assert.Equal("Vybraný objekt již je dráha.", convert.Reason);
    }

    [Fact]
    public void ConvertedTextBecomesAnObjectThatCanBeConvertedToEditableCurves()
    {
        var scene = NewScene();
        scene.AddText("LASERO", new Position(10, 20, 0), 12);
        scene.ConvertSelectedTextToPathCommand.Execute(null);

        var state = CanvasContextMenuStates.ForSelection(scene, Reach);

        Assert.True(state.CanConvertToCurves);
        Assert.True(Find(CanvasContextMenuBuilder.ForObject(state), ContextAction.ConvertToCurves)!.IsEnabled);
    }

    [Fact]
    public void SeveralObjectsShowConvertToPathDisabledWithTheReason()
    {
        var scene = NewScene();
        var a = AddRectangle(scene, 0);
        var b = AddRectangle(scene, 30);
        Select(scene, a, b);

        var convert = Find(MenuFor(scene), ContextAction.ConvertToCurves)!;

        Assert.False(convert.IsEnabled);
        Assert.False(string.IsNullOrWhiteSpace(convert.Reason));
    }

    [Fact]
    public void GroupingRowsAreDisabledWithAReasonWhenTheyDoNotApply()
    {
        var scene = NewScene();
        AddRectangle(scene, 0);
        var rows = MenuFor(scene);

        var group = Find(rows, ContextAction.Group)!;
        var ungroup = Find(rows, ContextAction.Ungroup)!;
        Assert.False(group.IsEnabled);
        Assert.False(string.IsNullOrWhiteSpace(group.Reason));
        Assert.False(ungroup.IsEnabled);
        Assert.Equal("Vybraný objekt není skupina.", ungroup.Reason);
    }

    [Fact]
    public void AGroupCanBeReleasedAndSeveralObjectsCanBeGrouped()
    {
        var scene = NewScene();
        var a = AddRectangle(scene, 0);
        var b = AddRectangle(scene, 30);
        Select(scene, a, b);
        Assert.True(Find(MenuFor(scene), ContextAction.Group)!.IsEnabled);

        scene.GroupSelectionCommand.Execute(null);
        var rows = MenuFor(scene);

        Assert.True(Find(rows, ContextAction.Ungroup)!.IsEnabled);
        // Groups nest (regrouping keeps the earlier membership for Ungroup), so Group stays on.
        Assert.True(Find(rows, ContextAction.Group)!.IsEnabled);
    }

    [Fact]
    public void VectorExtrasFollowTheCoreInTheirOwnGroups()
    {
        var scene = NewScene();
        var a = AddRectangle(scene, 0);
        var b = AddRectangle(scene, 30);
        Select(scene, a, b);
        var rows = MenuFor(scene);

        var order = rows.Where(row => !row.IsSeparator).Select(row => row.Header).ToList();
        var properties = order.IndexOf("Zobrazit vlastnosti");
        foreach (var extra in new[] { "Booleovské operace", "Offset křivky…", "Zarovnat" })
            Assert.True(order.IndexOf(extra) > properties, extra);
    }

    // --- Převést na bitmapu: when it is offered ---------------------------------------------------

    [Fact]
    public void BitmapConversionIsRefusedWithAReasonForNothingLockedOrBitmapSelections()
    {
        var (scene, a, b) = FilledRectangles();
        Assert.Null(scene.ConvertSelectionToBitmapDisabledReason);

        Select(scene);
        Assert.NotNull(scene.ConvertSelectionToBitmapDisabledReason);
        Assert.False(scene.ConvertSelectionToBitmapCommand.CanExecute(null));

        Select(scene, a, b);
        a.IsLocked = true; // a locked object cannot be added to a selection, so lock it once selected
        Assert.Contains("Zamčené", scene.ConvertSelectionToBitmapDisabledReason);
        Assert.False(scene.ConvertSelectionToBitmapCommand.CanExecute(null));
        Assert.Null(scene.TryConvertSelectionToBitmap());
        Assert.Equal([a, b], scene.Objects);
        Assert.False(Directory.Exists(_directory), "a refused conversion must not write a file");
    }

    [Fact]
    public void ABitmapCannotBeConvertedToABitmapAndTheMenuSaysSo()
    {
        var (scene, _, _) = FilledRectangles();
        var raster = Assert.IsType<SceneObject>(scene.TryConvertSelectionToBitmap());

        Select(scene, raster);

        Assert.Contains("bitmap", scene.ConvertSelectionToBitmapDisabledReason, StringComparison.OrdinalIgnoreCase);
        Assert.False(scene.ConvertSelectionToBitmapCommand.CanExecute(null));
        var row = Find(CanvasContextMenuBuilder.ForObject(
            CanvasContextMenuStates.ForSelection(scene, Reach) with { Kind = SelectionKind.Mixed }), ContextAction.ConvertToBitmap)!;
        Assert.False(row.IsEnabled);
        Assert.Equal(scene.ConvertSelectionToBitmapDisabledReason, row.Reason);
    }

    // --- Převést na bitmapu: what it does ---------------------------------------------------------

    [Fact]
    public void FilledVectorsBecomeOneBlackOnWhiteBitmapOverTheSameRectangle()
    {
        var (scene, a, b) = FilledRectangles();
        var frame = BoundingBox2D.Empty;
        foreach (var item in new[] { a, b })
            frame = frame.Include(item.WorldBounds().MinX, item.WorldBounds().MinY).Include(item.WorldBounds().MaxX, item.WorldBounds().MaxY);
        var layersBefore = scene.Layers.Count;

        scene.ConvertSelectionToBitmapCommand.Execute(null);

        var raster = Assert.Single(scene.Objects);
        Assert.True(raster.IsRaster);
        Assert.Same(raster, Assert.Single(scene.SelectedObjects));
        AssertBounds(frame, raster.WorldBounds());

        Assert.StartsWith(_directory, raster.RasterFilePath);
        Assert.True(File.Exists(raster.RasterFilePath));
        Assert.Equal(254, raster.RasterOptions!.Dpi, 6);
        Assert.Equal(80, raster.RasterOptions.TargetWidthMm, 6);

        // 80 x 10 mm at 254 DPI is 800 x 100 pixels; world Y is up, image rows are down.
        using (var picture = new System.Drawing.Bitmap(raster.RasterFilePath!))
        {
            Assert.Equal(800, picture.Width);
            Assert.Equal(100, picture.Height);
        }
        Assert.True(Pixel(raster.RasterFilePath!, 100, 50).R < 32, "inside the first rectangle");
        Assert.True(Pixel(raster.RasterFilePath!, 700, 50).R < 32, "inside the second rectangle");
        Assert.True(Pixel(raster.RasterFilePath!, 400, 50).R > 224, "the gap between them stays white");

        // On the bitmap layer, which is created once and is the one the outline refers to.
        var rasterLayer = Assert.Single(scene.Layers, layer => layer.IsRaster);
        Assert.Equal(layersBefore + 1, scene.Layers.Count);
        Assert.All(raster.LocalShapes, shape => Assert.Equal(rasterLayer.Id, shape.LayerId));
    }

    [Fact]
    public void ALineLayerStaysAnOutlineInsteadOfBecomingASolidBlock()
    {
        var scene = NewScene();
        var rectangle = AddRectangle(scene, 0);

        scene.ConvertSelectionToBitmapCommand.Execute(null);

        var raster = Assert.Single(scene.Objects);
        Assert.NotSame(rectangle, raster);
        Assert.True(Pixel(raster.RasterFilePath!, 100, 50).R > 224, "the inside of a cut outline is not burned");
        Assert.True(Math.Min(Pixel(raster.RasterFilePath!, 0, 50).R, Pixel(raster.RasterFilePath!, 1, 50).R) < 200, "the outline is");
    }

    [Fact]
    public void TextBecomesABitmapWithInkOnItAndKeepsItsPlacement()
    {
        var scene = NewScene();
        scene.AddText("LASERO", new Position(10, 20, 0), 12);
        var text = scene.Objects[0];
        var frame = text.WorldBounds();

        scene.ConvertSelectionToBitmapCommand.Execute(null);

        var raster = Assert.Single(scene.Objects);
        Assert.True(raster.IsRaster);
        AssertBounds(frame, raster.WorldBounds());

        using var picture = new System.Drawing.Bitmap(raster.RasterFilePath!);
        var ink = 0;
        for (var y = 0; y < picture.Height; y++)
            for (var x = 0; x < picture.Width; x++)
                if (picture.GetPixel(x, y).R < 64) ink++;
        Assert.True(ink > 0);
        Assert.True(ink < picture.Width * picture.Height, "letters are not a solid block");
    }

    [Fact]
    public void ConvertingToABitmapIsOneUndoStepAndRedoBringsTheSameBitmapBack()
    {
        var (scene, a, b) = FilledRectangles();
        var undoDepthMarker = scene.Objects.ToList();
        var layersBefore = scene.Layers.ToList();

        scene.ConvertSelectionToBitmapCommand.Execute(null);
        var raster = Assert.Single(scene.Objects);
        var file = raster.RasterFilePath!;

        scene.UndoCommand.Execute(null);

        Assert.Equal(undoDepthMarker, scene.Objects);
        Assert.Same(a, scene.Objects[0]);
        Assert.Same(b, scene.Objects[1]);
        Assert.Equal(layersBefore, scene.Layers);
        Assert.DoesNotContain(scene.Objects, item => item.IsRaster);
        Assert.True(File.Exists(file), "redo needs the picture");

        scene.RedoCommand.Execute(null);

        Assert.Same(raster, Assert.Single(scene.Objects));
        Assert.Single(scene.Layers, layer => layer.IsRaster);
    }

    [Fact]
    public void AnExistingBitmapLayerIsReusedAndSurvivesUndo()
    {
        var (scene, a, b) = FilledRectangles();
        var first = scene.TryConvertSelectionToBitmap()!;
        var rasterLayer = Assert.Single(scene.Layers, layer => layer.IsRaster);
        var c = AddRectangle(scene, 200);
        Select(scene, c);

        var second = scene.TryConvertSelectionToBitmap()!;

        Assert.Equal(2, scene.Objects.Count(item => item.IsRaster));
        Assert.Single(scene.Layers, layer => layer.IsRaster);
        Assert.Equal(rasterLayer.Id, second.LocalShapes[0].LayerId);

        scene.UndoCommand.Execute(null);
        Assert.Single(scene.Layers, layer => layer.IsRaster);
        Assert.Contains(first, scene.Objects);
        Assert.Contains(c, scene.Objects);
        Assert.DoesNotContain(a, scene.Objects);
        Assert.DoesNotContain(b, scene.Objects);
    }

    [Fact]
    public void TheBitmapKeepsTheStackingPositionOfTheTopmostObjectItReplaces()
    {
        var scene = NewScene();
        var back = AddRectangle(scene, 0);
        var middle = AddRectangle(scene, 30);
        var front = AddRectangle(scene, 60);
        Select(scene, back, middle);

        scene.ConvertSelectionToBitmapCommand.Execute(null);

        Assert.Equal(2, scene.Objects.Count);
        Assert.True(scene.Objects[0].IsRaster);
        Assert.Same(front, scene.Objects[1]);
    }

    // --- the rasterizer itself --------------------------------------------------------------------

    [Fact]
    public void ResolutionFollowsTheLayerLineIntervalAndFallsBackTo254()
    {
        Assert.Equal(254, VectorRasterizer.DpiFromLineInterval(0.1), 6);
        Assert.Equal(508, VectorRasterizer.DpiFromLineInterval(0.05), 6);
        Assert.Equal(VectorRasterizer.DefaultDpi, VectorRasterizer.DpiFromLineInterval(0));
        Assert.Equal(VectorRasterizer.DefaultDpi, VectorRasterizer.DpiFromLineInterval(double.NaN));
        Assert.Equal(VectorRasterizer.MaxDpi, VectorRasterizer.DpiFromLineInterval(0.0001));
    }

    [Fact]
    public void AStraightLineStillGetsAPixelToLiveIn()
    {
        var line = new ImportedShape
        {
            Points = [new Position(0, 5, 0), new Position(20, 5, 0)],
            IsClosed = false,
            LayerColor = new RgbColor(0, 0, 0),
            PreferredMode = LayerMode.Cut,
        };

        var result = VectorRasterizer.Render(
            [line], new BoundingBox2D(0, 5, 20, 5), 254, _ => false, Path.Combine(_directory, "line.png"));

        Assert.True(File.Exists(result.FilePath));
        Assert.Equal(200, result.WidthPx);
        Assert.Equal(1, result.HeightPx);
        Assert.Equal(25.4 / 254, result.Frame.Height, 9);
    }

    [Fact]
    public void ASelectionTooLargeForOnePictureIsRenderedAtALowerResolution()
    {
        var normal = VectorRasterizer.PlanGrid(80, 10, 254);
        Assert.Equal((800, 100), (normal.WidthPx, normal.HeightPx));
        Assert.Equal(254, normal.Dpi, 6);

        var huge = VectorRasterizer.PlanGrid(2000, 2000, 254);
        Assert.True((long)huge.WidthPx * huge.HeightPx <= VectorRasterizer.MaxPixels * 1.01);
        Assert.True(huge.Dpi < 254);
        Assert.Equal(2000, huge.WidthMm);
    }

    // --- shortcuts the menu now advertises must be bound ------------------------------------------

    [Fact]
    public void TheShortcutsOfTheVectorMenuAreBound()
    {
        var scene = NewScene();
        var a = AddRectangle(scene, 0);
        var b = AddRectangle(scene, 30);
        Select(scene, a, b);

        var gestures = Flatten(MenuFor(scene)).Select(row => row.Gesture).Where(g => g is not null).ToHashSet();
        Assert.Equal(
            new HashSet<string?>
            {
                "Ctrl+X", "Ctrl+C", "Ctrl+V", "Ctrl+D", "Del", "Ctrl+A", "Ctrl+G", "Ctrl+U",
                "Ctrl+Shift+C", "Ctrl+Shift+B", "Alt+P", "Ctrl+Shift+U", "Ctrl+Shift+O",
            },
            gestures);
    }
}
