using System.IO;
using Lasero.App;
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

public sealed class LayerAndHistoryReliabilityTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "lasero-layer-reliability-tests", Guid.NewGuid().ToString("N"));

    public LayerAndHistoryReliabilityTests() => Directory.CreateDirectory(_directory);

    private sealed class CountingCommand : ISceneCommand
    {
        public int Value { get; private set; }
        public void Do() => Value++;
        public void Undo() => Value--;
    }

    private static SceneObject MakeObject(string name, RgbColor? color = null) => new()
    {
        Name = name,
        LocalShapes =
        [
            new ImportedShape
            {
                Points = [new Position(0, 0, 0), new Position(10, 0, 0), new Position(10, 10, 0)],
                IsClosed = false,
                LayerColor = color ?? RgbColor.Red,
                PreferredMode = LayerMode.Cut,
            },
        ],
        LocalPivot = new Position(5, 5, 0),
        LocalBounds = new BoundingBox2D(0, 0, 10, 10),
    };

    // ---- Command stack save point ------------------------------------------------------------

    [Fact]
    public void UndoingBackToTheSavedStateIsRecognised()
    {
        var stack = new SceneCommandStack();
        Assert.True(stack.IsAtSavePoint);

        stack.Execute(new CountingCommand());
        Assert.False(stack.IsAtSavePoint);

        stack.MarkSaved();
        Assert.True(stack.IsAtSavePoint);

        stack.Undo();
        Assert.False(stack.IsAtSavePoint);
        stack.Redo();
        Assert.True(stack.IsAtSavePoint);
    }

    [Fact]
    public void SavedStateIsLostOnceANewCommandDiscardsItsRedoBranch()
    {
        var stack = new SceneCommandStack();
        stack.Execute(new CountingCommand());
        stack.MarkSaved();
        stack.Undo();

        stack.Execute(new CountingCommand()); // same depth as the saved state, but different content

        Assert.False(stack.IsAtSavePoint);
        stack.Undo();
        Assert.False(stack.IsAtSavePoint);
    }

    [Fact]
    public void ClearingTheStackMakesTheLoadedDocumentTheSavedState()
    {
        var stack = new SceneCommandStack();
        stack.Execute(new CountingCommand());

        stack.Clear();

        Assert.True(stack.IsAtSavePoint);
    }

    // ---- Layers entering the scene -----------------------------------------------------------

    [Fact]
    public void FillAndCutInTheSameColourGetTwoLayersAndAreMatchedByModeOnReimport()
    {
        var scene = new SceneDocument();
        var fill = LayerSettings.CreateDefault(RgbColor.Black, LayerMode.Fill, "Gravír");
        var cut = LayerSettings.CreateDefault(RgbColor.Black, LayerMode.Cut, "Obrys");

        var added = scene.EnsureLayers([fill, cut]);
        Assert.Equal(2, added.Count);
        Assert.Equal(2, scene.Layers.Count);

        var again = scene.EnsureLayers(
        [
            LayerSettings.CreateDefault(RgbColor.Black, LayerMode.Cut, "Jiný obrys"),
            LayerSettings.CreateDefault(RgbColor.Black, LayerMode.Fill, "Jiná výplň"),
        ]);
        Assert.Empty(again);
        Assert.Equal(2, scene.Layers.Count);
    }

    [Fact]
    public void ARemodedLayerStillAbsorbsAnImportOfTheSameColour()
    {
        var scene = new SceneDocument();
        var red = LayerSettings.CreateDefault(RgbColor.Red, LayerMode.Cut, "Rez");
        scene.Layers.Add(red);
        red.Mode = LayerMode.Fill;

        var added = scene.EnsureLayers([LayerSettings.CreateDefault(RgbColor.Red, LayerMode.Cut, "Rez")]);

        Assert.Empty(added);
        Assert.Single(scene.Layers);
    }

    // ---- Deleting objects with linked offsets ------------------------------------------------

    private static OffsetLink LinkTo(SceneObject source, bool suspended = false) => new()
    {
        SourceIds = [source.Id],
        Options = new VectorOffsetOptions(),
        IsSuspended = suspended,
    };

    [Fact]
    public void DeletingASourceDetachesItsLiveOffsetOutputAndUndoRelinksIt()
    {
        var scene = new SceneDocument();
        var source = MakeObject("Zdroj");
        var output = MakeObject("Offset");
        output.OffsetLink = LinkTo(source);
        scene.Objects.Add(source);
        scene.Objects.Add(output);
        var stack = new SceneCommandStack();

        stack.Execute(new DeleteObjectsCommand(scene, [source]));

        Assert.Same(output, Assert.Single(scene.Objects));
        Assert.Null(output.OffsetLink);

        stack.Undo();

        Assert.Equal(2, scene.Objects.Count);
        Assert.NotNull(output.OffsetLink);
        Assert.Equal(source.Id, Assert.Single(output.OffsetLink!.SourceIds));
    }

    [Fact]
    public void DeletingASourceAlsoRemovesItsStaleSuspendedOutputAtItsOriginalPosition()
    {
        var scene = new SceneDocument();
        var first = MakeObject("První");
        var source = MakeObject("Zdroj");
        var stale = MakeObject("Zastaralý offset");
        stale.OffsetLink = LinkTo(source, suspended: true);
        stale.IsVisible = false;
        scene.Objects.Add(first);
        scene.Objects.Add(source);
        scene.Objects.Add(stale);
        var stack = new SceneCommandStack();

        stack.Execute(new DeleteObjectsCommand(scene, [source]));
        Assert.Same(first, Assert.Single(scene.Objects));

        stack.Undo();
        Assert.Equal(new[] { first, source, stale }, scene.Objects.ToArray());
        Assert.True(stale.OffsetLink!.IsSuspended);
    }

    [Fact]
    public void DeletingTheLastObjectOfALayerRemovesTheLayerEvenWhenItWasNotCreatedByATool()
    {
        var scene = new SceneDocument();
        var red = MakeObject("Červený", RgbColor.Red);
        var blue = MakeObject("Modrý", new RgbColor(0, 0, 255));
        scene.Objects.Add(red);
        scene.Objects.Add(blue);
        scene.EnsureLayers([LayerSettings.CreateDefault(RgbColor.Red, LayerMode.Cut, "Červená"),
            LayerSettings.CreateDefault(new RgbColor(0, 0, 255), LayerMode.Cut, "Modrá")], [red, blue]);
        var untouched = LayerSettings.CreateDefault(RgbColor.Black, LayerMode.Cut, "Nepoužitá");
        scene.Layers.Add(untouched);
        var stack = new SceneCommandStack();

        stack.Execute(new DeleteObjectsCommand(scene, [red]));

        Assert.DoesNotContain(scene.Layers, layer => layer.Name == "Červená");
        Assert.Contains(scene.Layers, layer => layer.Name == "Modrá");
        Assert.Contains(untouched, scene.Layers);

        stack.Undo();

        Assert.Contains(scene.Layers, layer => layer.Name == "Červená");
        Assert.Equal(3, scene.Layers.Count);
    }

    [Fact]
    public void ASwatchNearAnExistingLayerMovesEverySelectedObjectOntoThatLayer()
    {
        var viewModel = new SceneViewModel();
        var nearBlack = new RgbColor(10, 10, 10);
        var first = MakeObject("První", nearBlack);
        var second = MakeObject("Druhý", new RgbColor(200, 30, 30));
        viewModel.Scene.Objects.Add(first);
        viewModel.Scene.Objects.Add(second);
        viewModel.Scene.EnsureLayers(
            [LayerSettings.CreateDefault(nearBlack, LayerMode.Cut, "Gravír"),
             LayerSettings.CreateDefault(new RgbColor(200, 30, 30), LayerMode.Cut, "Řez")], [first, second]);
        var rejected = false;
        viewModel.VectorOperationRejected += _ => rejected = true;
        viewModel.SelectedObjects.Add(first);
        viewModel.SelectedObjects.Add(second);

        viewModel.ChangeSelectedLayerColorCommand.Execute(RgbColor.Black);

        Assert.False(rejected);
        var layerIds = viewModel.Objects.SelectMany(item => item.LocalShapes).Select(shape => shape.LayerId).Distinct().ToList();
        Assert.Single(layerIds);
    }

    [Fact]
    public void SelectAllThenASwatchLeavesExactlyOneLayerOfThatColourAndUndoRestoresAll()
    {
        var viewModel = new SceneViewModel();
        var colors = new[] { new RgbColor(200, 30, 30), new RgbColor(30, 200, 30), new RgbColor(30, 30, 200) };
        var objects = colors.Select((color, index) => MakeObject($"Objekt {index}", color)).ToList();
        foreach (var item in objects) viewModel.Scene.Objects.Add(item);
        viewModel.Scene.EnsureLayers(
            colors.Select((color, index) => LayerSettings.CreateDefault(color, LayerMode.Cut, $"Vrstva {index}")).ToList(), objects);
        foreach (var item in objects) viewModel.SelectedObjects.Add(item);
        var target = new RgbColor(240, 180, 0);

        viewModel.ChangeSelectedLayerColorCommand.Execute(target);

        var layer = Assert.Single(viewModel.Layers);
        Assert.True(layer.Color.IsApproximately(target));
        Assert.All(viewModel.Objects.SelectMany(item => item.LocalShapes), shape => Assert.Equal(layer.Id, shape.LayerId));

        viewModel.UndoCommand.Execute(null);

        Assert.Equal(3, viewModel.Layers.Count);
    }

    [Fact]
    public void DeletingBothSourceAndOutputDoesNotTouchTheLinkOfADeletedObject()
    {
        var scene = new SceneDocument();
        var source = MakeObject("Zdroj");
        var output = MakeObject("Offset");
        output.OffsetLink = LinkTo(source);
        scene.Objects.Add(source);
        scene.Objects.Add(output);

        var command = new DeleteObjectsCommand(scene, [source, output]);
        command.Do();
        Assert.Empty(scene.Objects);
        command.Undo();

        Assert.Equal(2, scene.Objects.Count);
        Assert.NotNull(output.OffsetLink);
    }

    // ---- Deleting a layer --------------------------------------------------------------------

    [Fact]
    public void DeletingALayerKeepsTheOffsetLinkOfTheContoursThatRemain()
    {
        var scene = new SceneDocument();
        var cut = LayerSettings.CreateDefault(RgbColor.Red, LayerMode.Cut, "Rez");
        var fill = LayerSettings.CreateDefault(RgbColor.Black, LayerMode.Fill, "Gravír");
        scene.Layers.Add(cut);
        scene.Layers.Add(fill);
        var source = MakeObject("Zdroj");
        var output = new SceneObject
        {
            Name = "Offset",
            LocalShapes =
            [
                new ImportedShape { Points = [new Position(0, 0, 0), new Position(10, 0, 0)], IsClosed = false, LayerColor = cut.Color, LayerId = cut.Id, PreferredMode = LayerMode.Cut },
                new ImportedShape { Points = [new Position(0, 5, 0), new Position(10, 5, 0)], IsClosed = false, LayerColor = fill.Color, LayerId = fill.Id, PreferredMode = LayerMode.Fill },
            ],
            LocalPivot = new Position(5, 2.5, 0),
            LocalBounds = new BoundingBox2D(0, 0, 10, 5),
            OffsetLink = LinkTo(source),
        };
        scene.Objects.Add(output);

        new DeleteLayerCommand(scene, cut).Do();

        var remaining = Assert.Single(scene.Objects);
        Assert.Equal(output.Id, remaining.Id);
        Assert.Single(remaining.LocalShapes);
        Assert.NotNull(remaining.OffsetLink);
    }

    // ---- Toolpath for orphaned geometry --------------------------------------------------------

    [Fact]
    public void GeometryOfALostLayerFallsBackToTheMatchingColourExactlyOnce()
    {
        var first = LayerSettings.CreateDefault(RgbColor.Red, LayerMode.Cut, "Rez");
        var second = LayerSettings.CreateDefault(RgbColor.Red, LayerMode.Cut, "Druhý rez");
        var document = new ImportedDocument
        {
            Shapes =
            [
                new ImportedShape
                {
                    LayerId = Guid.NewGuid(),
                    LayerColor = RgbColor.Red,
                    PreferredMode = LayerMode.Cut,
                    IsClosed = false,
                    Points = [new Position(0, 0, 0), new Position(10, 0, 0)],
                },
            ],
            Layers = [first, second],
            BoundingBox = new BoundingBox2D(0, 0, 10, 0),
        };

        var lines = ToolpathBuilder.BuildGCode(document, 1000);

        Assert.Single(lines, line => line.StartsWith("G1 X10"));
    }

    // ---- Loading a project with orphaned geometry ---------------------------------------------

    [Fact]
    public void LoadingGeometryWhoseLayerIsMissingCreatesALayerSoItIsNotJustDrawn()
    {
        var project = new LaseroProjectFile
        {
            Objects =
            [
                new ProjectObject
                {
                    Name = "Sirotek",
                    Shapes =
                    [
                        new ProjectShape
                        {
                            LayerId = Guid.NewGuid(),
                            LayerColor = RgbColor.Red,
                            PreferredMode = LayerMode.Cut,
                            Points = [new Position(0, 0, 0), new Position(10, 0, 0)],
                        },
                    ],
                    LocalPivot = new Position(5, 0, 0),
                    LocalBounds = new BoundingBox2D(0, 0, 10, 0),
                },
            ],
        };
        var viewModel = new SceneViewModel();

        viewModel.LoadProject(project);

        var layer = Assert.Single(viewModel.Layers);
        Assert.Equal(LayerMode.Cut, layer.Mode);
        Assert.Equal(layer.Id, Assert.Single(Assert.Single(viewModel.Objects).LocalShapes).LayerId);
    }

    // ---- SVG import through the scene ----------------------------------------------------------

    [Fact]
    public void ImportingAnSvgWithNothingToDrawIsRefusedInsteadOfAddingAnEmptyObject()
    {
        var path = Path.Combine(_directory, "empty.svg");
        File.WriteAllText(path, "<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 10 10\"><rect width=\"5\" height=\"5\" fill=\"none\"/></svg>");
        var viewModel = new SceneViewModel();

        var error = Assert.Throws<InvalidDataException>(() => viewModel.ImportSvgFile(path, 100));

        Assert.Contains("Nic nebylo naimportováno", error.Message);
        Assert.Empty(viewModel.Objects);
    }

    [Fact]
    public void ImportingAnSvgReturnsWhatWasLostAsWarnings()
    {
        var path = Path.Combine(_directory, "gradient.svg");
        File.WriteAllText(path,
            "<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 10 10\">" +
            "<defs><linearGradient id=\"g\"><stop offset=\"0\" stop-color=\"#ff0000\"/></linearGradient></defs>" +
            "<rect width=\"5\" height=\"5\" fill=\"url(#g)\"/></svg>");
        var viewModel = new SceneViewModel();

        var warnings = viewModel.ImportSvgFile(path, 100);

        Assert.Single(viewModel.Objects);
        Assert.Contains(warnings, warning => warning.Contains("Přechod"));
    }

    // ---- Personal data stores keep what they cannot read ----------------------------------------

    [Fact]
    public void CorruptJobHistoryIsKeptAsideInsteadOfBeingOverwritten()
    {
        var store = new JobHistoryStore(Path.Combine(_directory, "job-history.json"));
        var accountFile = Path.Combine(_directory, "job-history", AccountScopedStorage.FileNameFor("account-a"));
        Directory.CreateDirectory(Path.GetDirectoryName(accountFile)!);
        File.WriteAllText(accountFile, "{ not valid json");

        store.SwitchAccount("account-a");

        Assert.Empty(store.Entries);
        var kept = Assert.Single(Directory.GetFiles(Path.GetDirectoryName(accountFile)!, "*.corrupt-*"));
        Assert.Equal("{ not valid json", File.ReadAllText(kept));
    }

    [Fact]
    public void CorruptRecentProjectsAreKeptAsideInsteadOfBeingOverwritten()
    {
        var path = Path.Combine(_directory, "recent-projects.json");
        File.WriteAllText(path, "{ not valid json");

        var store = new RecentProjectsStore(path);

        Assert.Empty(store.Recent);
        Assert.Single(Directory.GetFiles(_directory, "recent-projects.json.corrupt-*"));
    }

    [Fact]
    public void CorruptMaterialPresetsAreKeptAsideInsteadOfBeingOverwritten()
    {
        var path = Path.Combine(_directory, "materials.json");
        File.WriteAllText(path, "{ not valid json");

        var store = new MaterialPresetStore(path);

        Assert.Empty(store.Presets);
        Assert.Single(Directory.GetFiles(_directory, "materials.json.corrupt-*"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }
}
