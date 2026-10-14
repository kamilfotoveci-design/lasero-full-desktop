using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using Lasero.App;
using Lasero.App.Controls;
using Lasero.App.Converters;
using Lasero.App.ViewModels;
using Lasero.Core.GCode;
using Lasero.Core.Geometry;
using Lasero.Core.Grbl;
using Lasero.Core.Import;
using Lasero.Core.Jobs;
using Lasero.Core.Layers;
using Lasero.Core.Scene;
using Lasero.Core.Scene.Commands;
using Xunit;

namespace Lasero.Tests;

/// <summary>
/// Operator-facing layer workflows: selecting a layer's shapes, drawing into the current layer, the
/// palette, bitmap import into a hidden raster layer, layer duplication and deletion, and the warning
/// for a hidden layer that still burns.
/// </summary>
public sealed class LayerWorkflowTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "lasero-layer-workflow", Guid.NewGuid().ToString("N"));

    public LayerWorkflowTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private static SceneObject Rect(SceneViewModel scene, double x)
    {
        scene.DrawPrimitive(DesignerTool.Rectangle, new Position(x, 0, 0), new Position(x + 20, 10, 0));
        return scene.Objects[^1];
    }

    private static ImportedShape Contour(LayerSettings layer, double x) => new()
    {
        Points = [new Position(x, 0, 0), new Position(x + 3, 0, 0), new Position(x + 3, 3, 0), new Position(x, 3, 0)],
        IsClosed = true,
        LayerId = layer.Id,
        LayerColor = layer.Color,
        PreferredMode = layer.Mode,
    };

    private static SceneObject Mixed(LayerSettings first, LayerSettings second) => new()
    {
        Name = "smíšený",
        LocalShapes = [Contour(first, 0), Contour(second, 5)],
        LocalPivot = new Position(4, 1.5, 0),
        LocalBounds = new BoundingBox2D(0, 0, 8, 3),
    };

    private string Png(string name)
    {
        var path = Path.Combine(_dir, name);
        using var bitmap = new System.Drawing.Bitmap(2, 1);
        bitmap.Save(path, ImageFormat.Png);
        return path;
    }

    // Select in layer ------------------------------------------------------------------------------

    [Fact]
    public void SelectingALayersShapesSplitsAMixedObjectSoDeleteKeepsTheOtherLayersArt()
    {
        var scene = new SceneViewModel();
        var red = LayerSettings.CreateDefault(RgbColor.Red, LayerMode.Cut, "Červená");
        var black = LayerSettings.CreateDefault(RgbColor.Black, LayerMode.Fill, "Černá");
        scene.Layers.Add(red);
        scene.Layers.Add(black);
        var mixed = Mixed(red, black);
        scene.Objects.Add(mixed);

        scene.SelectObjectsInLayerCommand.Execute(red);

        var part = Assert.Single(scene.SelectedObjects);
        Assert.All(part.LocalShapes, shape => Assert.Equal(red.Id, shape.LayerId));
        Assert.Equal(2, scene.Objects.Count);
        var rest = scene.Objects.Single(item => item.Id == mixed.Id);
        Assert.All(rest.LocalShapes, shape => Assert.Equal(black.Id, shape.LayerId));
        Assert.Equal(mixed.WorldBounds().MinX, Math.Min(part.WorldBounds().MinX, rest.WorldBounds().MinX), 6);

        scene.DeleteCommand.Execute(null);

        Assert.Same(rest, Assert.Single(scene.Objects));
        Assert.Same(black, Assert.Single(scene.Layers));

        scene.UndoCommand.Execute(null); // the delete
        scene.UndoCommand.Execute(null); // the split
        Assert.Same(mixed, Assert.Single(scene.Objects));
        Assert.Equal(new[] { red, black }, scene.Layers.ToArray());
        Assert.False(scene.CanUndo);
    }

    [Fact]
    public void SelectingALayerWhoseObjectsAreWhollyOnItAddsNoHistory()
    {
        var scene = new SceneViewModel();
        var a = Rect(scene, 0);
        var b = Rect(scene, 30);
        var layer = Assert.Single(scene.Layers);
        scene.SelectedObjects.Clear();
        var undoBefore = scene.CommandRevision;

        scene.SelectObjectsInLayerCommand.Execute(layer);

        Assert.Equal(new[] { a, b }, scene.SelectedObjects.ToArray());
        Assert.Equal(undoBefore, scene.CommandRevision);
    }

    [Fact]
    public void ATextSharingTwoLayersIsLeftOutOfTheLayerSelectionWithAReason()
    {
        var scene = new SceneViewModel();
        var red = LayerSettings.CreateDefault(RgbColor.Red, LayerMode.Cut, "Červená");
        var black = LayerSettings.CreateDefault(RgbColor.Black, LayerMode.Fill, "Černá");
        scene.Layers.Add(red);
        scene.Layers.Add(black);
        var shape = Mixed(red, black);
        var mixed = new SceneObject
        {
            Name = "text",
            LocalShapes = shape.LocalShapes,
            LocalPivot = shape.LocalPivot,
            LocalBounds = shape.LocalBounds,
            Text = new TextSource { Text = "Ab", HeightMm = 5 },
        };
        scene.Objects.Add(mixed);
        string? reason = null;
        scene.VectorOperationRejected += message => reason = message;

        scene.SelectObjectsInLayerCommand.Execute(red);

        Assert.Empty(scene.SelectedObjects);
        Assert.Same(mixed, Assert.Single(scene.Objects));
        Assert.NotNull(reason);
    }

    // Draw into the current layer ------------------------------------------------------------------

    [Fact]
    public void ANewShapeGoesOnTheSelectedLayerAndIsSelected()
    {
        var scene = new SceneViewModel();
        var blue = LayerSettings.CreateDefault(new RgbColor(31, 95, 204), LayerMode.Fill, "Modrá");
        scene.Layers.Add(blue);
        scene.SelectedLayer = blue;

        var drawn = Rect(scene, 0);

        Assert.All(drawn.LocalShapes, shape => Assert.Equal(blue.Id, shape.LayerId));
        Assert.Same(blue, Assert.Single(scene.Layers));
        Assert.Same(drawn, Assert.Single(scene.SelectedObjects));
        Assert.True(scene.CanSelectObject(drawn));
    }

    [Fact]
    public void AnOpenPathDrawnOnAFillLayerGoesToThatColoursCutLayer()
    {
        var scene = new SceneViewModel();
        var blue = LayerSettings.CreateDefault(new RgbColor(31, 95, 204), LayerMode.Fill, "Modrá");
        scene.Layers.Add(blue);
        scene.SelectedLayer = blue;
        var path = new VectorPath
        {
            Subpaths = [new VectorSubpath { Nodes = [VectorNode.CornerAt(new Position(0, 0, 0)), VectorNode.CornerAt(new Position(10, 5, 0))], IsClosed = false }],
        };

        scene.AddVectorPath(path);

        var line = scene.Objects[^1];
        var layer = scene.Layers.Single(candidate => candidate.Id == line.LocalShapes[0].LayerId);
        Assert.Equal(LayerMode.Cut, layer.Mode);
        Assert.True(layer.Color.IsApproximately(blue.Color));
        Assert.NotEqual(blue.Name, layer.Name);
    }

    [Fact]
    public void DrawingWithAHiddenDefaultLayerIsRefusedInsteadOfAddingAnInvisibleShape()
    {
        var scene = new SceneViewModel();
        Rect(scene, 0);
        var layer = Assert.Single(scene.Layers);
        scene.ToggleLayerVisibilityCommand.Execute(layer);
        scene.SelectedObjects.Clear();
        string? reason = null;
        scene.VectorOperationRejected += message => reason = message;

        scene.DrawPrimitive(DesignerTool.Rectangle, new Position(30, 0, 0), new Position(50, 10, 0));

        Assert.Single(scene.Objects);
        Assert.NotNull(reason);
    }

    [Fact]
    public void DrawingOnAHiddenSelectedLayerFallsBackToAVisibleDefaultLayer()
    {
        var scene = new SceneViewModel();
        var hidden = LayerSettings.CreateDefault(RgbColor.Red, LayerMode.Cut, "Skrytá");
        scene.Layers.Add(hidden);
        scene.SelectedLayer = hidden;
        scene.ToggleLayerVisibilityCommand.Execute(hidden);

        var drawn = Rect(scene, 0);

        Assert.True(scene.CanSelectObject(drawn));
        Assert.Same(drawn, Assert.Single(scene.SelectedObjects));
        Assert.NotEqual(hidden.Id, drawn.LocalShapes[0].LayerId);
    }

    // Palette --------------------------------------------------------------------------------------

    [Fact]
    public void ASwatchWithNothingSelectedPicksTheDrawingLayerAndChangesNoObject()
    {
        var scene = new SceneViewModel();
        var a = Rect(scene, 0);
        var before = a.LocalShapes;
        var original = Assert.Single(scene.Layers);
        var originalColor = original.Color;
        scene.SelectedObjects.Clear();
        var blue = new RgbColor(31, 95, 204);

        scene.ChangeSelectedLayerColorCommand.Execute(blue);

        Assert.Same(before, a.LocalShapes);
        Assert.Equal(originalColor, original.Color);
        var current = scene.SelectedLayer!;
        Assert.Equal(blue, current.Color);
        Assert.Equal("Modrá", current.Name);

        // A second pick replaces the empty draft instead of piling up layers; drawing then uses it.
        scene.ChangeSelectedLayerColorCommand.Execute(new RgbColor(214, 42, 42));
        Assert.Equal(2, scene.Layers.Count);
        var drawn = Rect(scene, 30);
        Assert.Equal(scene.SelectedLayer!.Id, drawn.LocalShapes[0].LayerId);
        Assert.Equal("Červená", scene.SelectedLayer.Name);
    }

    [Fact]
    public void ASwatchWithNothingSelectedSelectsAnExistingLayerOfThatColourWithoutHistory()
    {
        var scene = new SceneViewModel();
        Rect(scene, 0);
        var existing = Assert.Single(scene.Layers);
        scene.SelectedObjects.Clear();
        var revision = scene.CommandRevision;

        scene.ChangeSelectedLayerColorCommand.Execute(new RgbColor(18, 18, 18));

        Assert.Same(existing, scene.SelectedLayer);
        Assert.Equal(revision, scene.CommandRevision);
    }

    [Fact]
    public void ASwatchMovingPartOfALayerCreatesALayerWithItsOwnName()
    {
        var scene = new SceneViewModel();
        Rect(scene, 0);
        var b = Rect(scene, 30);
        scene.SelectedObjects.ReplaceWith([b]);

        scene.ChangeSelectedLayerColorCommand.Execute(new RgbColor(0, 0, 255));

        Assert.Equal(2, scene.Layers.Count);
        Assert.Equal(scene.Layers.Count, scene.Layers.Select(layer => layer.Name).Distinct().Count());
    }

    [Fact]
    public void ASwatchMovingAWholeLayerKeepsItsNameAndSettings()
    {
        var scene = new SceneViewModel();
        var a = Rect(scene, 0);
        var b = Rect(scene, 30);
        var layer = Assert.Single(scene.Layers);
        layer.Name = "Obrys krabičky";
        scene.SelectObjectsInLayerCommand.Execute(layer);

        scene.ChangeSelectedLayerColorCommand.Execute(new RgbColor(31, 95, 204));

        Assert.Same(layer, Assert.Single(scene.Layers));
        Assert.Equal("Obrys krabičky", layer.Name);
        Assert.Equal(new RgbColor(31, 95, 204), layer.Color);
        Assert.All(new[] { a, b }.SelectMany(item => item.LocalShapes), shape => Assert.Equal(layer.Id, shape.LayerId));
    }

    [Fact]
    public void EverySwatchHasItsCzechColourName()
    {
        var converter = new RgbColorToNameConverter();
        var names = new SceneViewModel().LayerPalette
            .Select(color => (string)converter.Convert(color, typeof(string), null!, CultureInfo.InvariantCulture))
            .ToList();

        Assert.Equal(
            ["Černá", "Červená", "Tmavě zelená", "Žlutá", "Zelená", "Tyrkysová", "Modrá", "Fialová", "Purpurová", "Hnědá", "Šedá", "Světle šedá"],
            names);
    }

    // Bitmaps and the raster layer -----------------------------------------------------------------

    [Fact]
    public void ImportingABitmapWhileTheRasterLayerIsHiddenIsRefusedWithTheReason()
    {
        var path = Png("a.png");
        var scene = new SceneViewModel();
        var options = new RasterImportOptions { TargetWidthMm = 20, Dpi = 254 };
        scene.ImportRasterFile(path, options);
        var bitmapa = Assert.Single(scene.Layers);
        scene.ToggleLayerVisibilityCommand.Execute(bitmapa);
        var red = LayerSettings.CreateDefault(RgbColor.Red, LayerMode.Cut, "Červená");
        scene.Layers.Add(red);
        scene.SelectedLayer = red;
        string? reason = null;
        scene.RasterOperationRejected += message => reason = message;

        scene.ImportRasterFile(path, options);

        Assert.Single(scene.Objects);
        Assert.Contains("Bitmapa", reason);
    }

    [Fact]
    public void ImportingABitmapIgnoresAHiddenSelectedVectorLayer()
    {
        var path = Png("b.png");
        var scene = new SceneViewModel();
        var red = LayerSettings.CreateDefault(RgbColor.Red, LayerMode.Cut, "Červená");
        scene.Layers.Add(red);
        scene.SelectedLayer = red;
        scene.ToggleLayerVisibilityCommand.Execute(red);

        scene.ImportRasterFile(path, new RasterImportOptions { TargetWidthMm = 20, Dpi = 254 });

        var bitmap = Assert.Single(scene.Objects);
        Assert.True(scene.CanSelectObject(bitmap));
        Assert.Same(bitmap, Assert.Single(scene.SelectedObjects));
    }

    [Fact]
    public void TheRasterLayerCannotBeDuplicated()
    {
        var scene = new SceneViewModel();
        scene.ImportRasterFile(Png("c.png"), new RasterImportOptions { TargetWidthMm = 20, Dpi = 254 });
        scene.SelectedLayer = scene.Layers[0];

        Assert.False(scene.DuplicateSelectedLayerCommand.CanExecute(null));
        scene.DuplicateSelectedLayerCommand.Execute(null);

        Assert.Single(scene.Layers, layer => layer.IsRaster);
    }

    // Deleting a layer -----------------------------------------------------------------------------

    [Fact]
    public void DeletingALayerDetachesTheLiveOffsetOfAnObjectItRemovedAndUndoRelinksIt()
    {
        var scene = new SceneDocument();
        var red = LayerSettings.CreateDefault(RgbColor.Red, LayerMode.Cut, "Červená");
        var black = LayerSettings.CreateDefault(RgbColor.Black, LayerMode.Cut, "Černá");
        scene.Layers.Add(red);
        scene.Layers.Add(black);
        var source = new SceneObject { Name = "zdroj", LocalShapes = [Contour(red, 0)], LocalPivot = new Position(1.5, 1.5, 0), LocalBounds = new BoundingBox2D(0, 0, 3, 3) };
        var link = new OffsetLink { SourceIds = [source.Id], Options = new VectorOffsetOptions() };
        var output = new SceneObject { Name = "offset", LocalShapes = [Contour(black, 10)], LocalPivot = new Position(11.5, 1.5, 0), LocalBounds = new BoundingBox2D(10, 0, 13, 3), OffsetLink = link };
        var stale = new SceneObject { Name = "zastaralý", LocalShapes = [Contour(black, 20)], LocalPivot = new Position(21.5, 1.5, 0), LocalBounds = new BoundingBox2D(20, 0, 23, 3), OffsetLink = link with { IsSuspended = true } };
        scene.Objects.Add(source);
        scene.Objects.Add(output);
        scene.Objects.Add(stale);
        var stack = new SceneCommandStack();

        stack.Execute(new DeleteLayerCommand(scene, red));

        Assert.Same(output, Assert.Single(scene.Objects));
        Assert.Null(output.OffsetLink);
        stack.Undo();
        Assert.Equal(new[] { source, output, stale }, scene.Objects.ToArray());
        Assert.Same(link, output.OffsetLink);
        Assert.Equal(new[] { red, black }, scene.Layers.ToArray());
    }

    [Fact]
    public void TheDeleteLayerTooltipsSayTheShapesAreDeleted()
    {
        var xaml = File.ReadAllText(Path.Combine(RepoRoot(), "Lasero.App", "Views", "DesignerInspectorView.xaml"));

        Assert.DoesNotContain("Vrstva ani její objekty se neodstraní", xaml, StringComparison.Ordinal);
        Assert.Equal(2, xaml.Split("Odstraní vrstvu i tvary v ní.").Length - 1);
    }

    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Lasero.App", "Lasero.App.csproj")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root not found.");
    }

    // A hidden layer that still burns --------------------------------------------------------------

    [Fact]
    public void AHiddenLayerWithOutputOnIsAWarningThatDoesNotBlockStart()
    {
        var scene = new SceneViewModel();
        var red = LayerSettings.CreateDefault(RgbColor.Red, LayerMode.Cut, "Červená");
        var empty = LayerSettings.CreateDefault(RgbColor.Black, LayerMode.Cut, "Prázdná");
        scene.Layers.Add(red);
        scene.Layers.Add(empty);
        scene.Objects.Add(new SceneObject { Name = "x", LocalShapes = [Contour(red, 0)], LocalPivot = new Position(1.5, 1.5, 0), LocalBounds = new BoundingBox2D(0, 0, 3, 3) });
        scene.ToggleLayerVisibilityCommand.Execute(red);
        scene.ToggleLayerVisibilityCommand.Execute(empty);
        using var machine = new GrblConnection(new VirtualGrblTransport { ResponseDelay = TimeSpan.Zero });
        var gcode = new GCodeViewModel(machine, scene, new AppSettingsStore(Path.Combine(_dir, "settings.json")));

        var warnings = gcode.EvaluatePreflight().Issues.Where(issue => issue.Code == JobPreflight.HiddenOutputLayerCode).ToList();

        var warning = Assert.Single(warnings);
        Assert.Equal(PreflightSeverity.Warning, warning.Severity);
        Assert.Contains("Červená", warning.Message);

        scene.ToggleLayerOutputCommand.Execute(red);
        Assert.DoesNotContain(gcode.EvaluatePreflight().Issues, issue => issue.Code == JobPreflight.HiddenOutputLayerCode);
    }

    [Fact]
    public void JobPreflightTurnsHiddenOutputLayersIntoNonBlockingWarnings()
    {
        var result = JobPreflight.Evaluate(new JobPreflightContext
        {
            IsConnected = false,
            Document = null,
            MachineStatus = null,
            RequireFraming = false,
            HasFramedCurrentDocument = false,
            WorkAreaWidthMm = 400,
            WorkAreaHeightMm = 400,
            HiddenOutputLayerNames = ["Gravír"],
        });

        var issue = Assert.Single(result.Issues, item => item.Code == JobPreflight.HiddenOutputLayerCode);
        Assert.Equal(PreflightSeverity.Warning, issue.Severity);
        Assert.Equal("Skrytá vrstva „Gravír“ se vypálí. Pokud nemá, je potřeba jí vypnout výstup.", issue.Message);
    }

    [Fact]
    public void TheStartConfirmationListsPreflightWarnings()
    {
        var text = StartSummary.Build(new StartSummaryInput(
            "Úloha", 10, 10, "Laser", null, false, "Střed", "Výkon: 50 %", "1 min", true,
            Warnings: [JobPreflight.HiddenOutputLayerMessage("Gravír")]));

        Assert.Contains("Pozor: Skrytá vrstva „Gravír“ se vypálí.", text);
    }
}
