using System.IO;
using System.Net.Http;
using System.Text.Json;
using Lasero.App;
using Lasero.App.ViewModels;
using Lasero.Core.Grbl;
using Lasero.Core.Import;
using Lasero.Core.LaseroApi;
using Lasero.Core.Scene;

namespace Lasero.Tests;

/// <summary>
/// Kamil's action chips are derived from live scene state and bound to the scene's own commands, and
/// the workspace snapshot is a bounded, path-free read of the editor. Neither may ever be built from
/// what the model says.
/// </summary>
public sealed class KamilActionChipsAndContextTests : IDisposable
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "lasero-kamil-chip-tests", Guid.NewGuid().ToString("N"));

    public KamilActionChipsAndContextTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public void NoSelectionOffersNoChips()
    {
        var (kamil, _) = Create();

        Assert.Empty(kamil.ActionChips);
        Assert.False(kamil.HasActionChips);
    }

    [Fact]
    public void SelectedVectorOffersOffsetBoundToTheSceneCommand()
    {
        var (kamil, scene) = Create();
        AddPath(scene);

        var chip = Assert.Single(kamil.ActionChips);
        Assert.StartsWith("Offset", chip.Label, StringComparison.Ordinal);
        Assert.Same(scene.OffsetSelectionCommand, chip.Command);
    }

    [Fact]
    public void SelectedRasterOffersTraceOnly()
    {
        var (kamil, scene) = Create();
        SelectRaster(scene);

        var chip = Assert.Single(kamil.ActionChips);
        Assert.StartsWith("Trasovat", chip.Label, StringComparison.Ordinal);
        Assert.Same(scene.TraceSelectedRasterCommand, chip.Command);
    }

    [Fact]
    public void ChipExecutionOnlyRaisesTheSceneRequestAndDoesNotChangeGeometry()
    {
        var (kamil, scene) = Create();
        SelectRaster(scene);
        var requested = 0;
        scene.TraceRasterRequested += _ => requested++;
        var before = scene.Objects.Count;

        kamil.ActionChips[0].Command.Execute(null);

        Assert.Equal(1, requested);
        Assert.Equal(before, scene.Objects.Count);
    }

    [Fact]
    public void ChipsFollowTheSelection()
    {
        var (kamil, scene) = Create();
        AddPath(scene);
        Assert.True(kamil.HasActionChips);

        scene.SelectedObjects.Clear();

        Assert.False(kamil.HasActionChips);
    }

    [Fact]
    public void SnapshotWithoutSelectionIsEmptyAndHasNoInventedMachine()
    {
        var (kamil, _) = Create();

        var context = kamil.BuildContext();
        var workspace = context.Workspace!;

        Assert.Equal(0, workspace.SelectedCount);
        Assert.Empty(workspace.SelectedObjects);
        Assert.Equal("disconnected", workspace.Machine.Connection);
        Assert.Null(workspace.Machine.FirmwareBanner);
        Assert.Null(context.PowerWatts);
        Assert.Null(context.LaserType);
    }

    [Fact]
    public void SnapshotDescribesRasterWithoutPathOrPixels()
    {
        var (kamil, scene) = Create();
        SelectRaster(scene);

        var workspace = kamil.BuildContext().Workspace!;
        var json = JsonSerializer.Serialize(workspace, Web);

        var selected = Assert.Single(workspace.SelectedObjects);
        Assert.Equal("raster", selected.Kind);
        Assert.DoesNotContain("secret", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SnapshotBoundsSelectionAndReportsOmissions()
    {
        var (kamil, scene) = Create();
        for (var i = 0; i < 5; i++) AddPath(scene);
        scene.SelectedObjects.Clear();
        foreach (var item in scene.Objects) scene.SelectedObjects.Add(item);

        var workspace = kamil.BuildContext().Workspace!;

        Assert.Equal(5, workspace.SelectedCount);
        Assert.Equal(3, workspace.SelectedObjects.Count);
        Assert.Equal(2, workspace.OmittedSelectedCount);
    }

    [Fact]
    public void SnapshotNeverTransmitsNonFiniteNumbers()
    {
        var (kamil, scene) = Create();
        AddPath(scene);
        scene.Layers[0].Speed = double.NaN;

        var json = JsonSerializer.Serialize(kamil.BuildContext().Workspace!, Web);

        Assert.DoesNotContain("NaN", json, StringComparison.Ordinal);
    }

    [Fact]
    public void ApplyTooltipWarnsWhenTheOperationHasNoMaterial()
    {
        var (kamil, scene) = Create();
        AddPath(scene);
        scene.SelectedLayer = scene.Layers[0];
        scene.Layers[0].MaterialLabel = null;

        Assert.Contains("odřezku", kamil.ApplyRecommendationTooltip, StringComparison.Ordinal);
    }

    private (KamilAssistantViewModel Kamil, SceneViewModel Scene) Create()
    {
        var http = new HttpClient();
        var account = new AccountViewModel(
            new LaseroAuthClient(http),
            new LaseroAccountClient(http),
            new SessionStore(Path.Combine(_directory, $"session-{Guid.NewGuid():N}.json")),
            new DeviceIdStore(Path.Combine(_directory, $"device-id-{Guid.NewGuid():N}.txt")),
            new DeviceActivationClient(http));
        var chat = new ChatViewModel(
            new LaseroChatClient(http), account, new ChatStore(Path.Combine(_directory, $"chat-{Guid.NewGuid():N}")));
        var machine = new GrblConnection(new VirtualGrblTransport { ResponseDelay = TimeSpan.Zero });
        var connection = new ConnectionViewModel(machine, new AppSettingsStore(Path.Combine(_directory, $"s-{Guid.NewGuid():N}.json")));
        var scene = new SceneViewModel();
        return (new KamilAssistantViewModel(chat, scene, connection), scene);
    }

    private static void AddPath(SceneViewModel scene)
    {
        scene.AddVectorPath(new VectorPath
        {
            Subpaths =
            [
                new VectorSubpath
                {
                    Nodes = [VectorNode.CornerAt(new Position(0, 0, 0)), VectorNode.CornerAt(new Position(10, 0, 0))],
                },
            ],
        });
        scene.SelectedObjects.Clear();
        scene.SelectedObjects.Add(scene.Objects[^1]);
    }

    private static void SelectRaster(SceneViewModel scene)
    {
        AddPath(scene);
        var vector = scene.Objects[^1];
        var raster = new SceneObject
        {
            LocalShapes = vector.LocalShapes,
            LocalPivot = vector.LocalPivot,
            LocalBounds = vector.LocalBounds,
            RasterFilePath = Path.Combine("C:", "secret", "photo.png"),
            RasterOptions = new RasterImportOptions { TargetWidthMm = 20, TargetHeightMm = 10 },
        };
        scene.Objects.Clear();
        scene.Objects.Add(raster);
        scene.SelectedObjects.Clear();
        scene.SelectedObjects.Add(raster);
    }
}
