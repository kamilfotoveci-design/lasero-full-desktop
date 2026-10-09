using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Lasero.App;
using Lasero.App.ViewModels;
using Lasero.Core.Grbl;
using Lasero.Core.GCode;
using Lasero.Core.Jobs;
using Lasero.Core.Layers;
using Lasero.Core.Machines;
using Lasero.Core.Scene;
using Lasero.Core.Scene.Commands;

namespace Lasero.Tests;

/// <summary>
/// The job prepared on a worker thread from a snapshot must be line for line the job built from the live
/// scene, a stale result must never be applied, and starting a job must never use a document that does
/// not match the design.
/// </summary>
[Collection("WpfUi")]
public sealed class AsyncJobGenerationTests : IDisposable
{
    private static Dispatcher Ui => InlineTextEditorRenderTests.Ui;
    private static readonly RgbColor Red = new(255, 0, 0);
    private static readonly RgbColor Black = new(20, 20, 20);
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "lasero-asyncjob-" + Guid.NewGuid().ToString("N"));

    public AsyncJobGenerationTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try { Directory.Delete(_directory, true); } catch (IOException) { }
    }

    private static SceneViewModel MixedScene()
    {
        var vm = new SceneViewModel();
        var cut = LayerSettings.CreateDefault(Red, LayerMode.Cut, "Rez");
        var fill = LayerSettings.CreateDefault(Black, LayerMode.Fill, "Vypln");
        var random = new Random(5);
        for (var i = 0; i < 12; i++)
        {
            var o = new Position(random.NextDouble() * 200, random.NextDouble() * 200, 0);
            var obj = i % 2 == 0
                ? ScenePrimitiveFactory.CreateRectangle(o, new Position(o.X + 30, o.Y + 20, 0), Red, $"r{i}")
                : ScenePrimitiveFactory.CreateEllipse(o, new Position(o.X + 25, o.Y + 25, 0), Black, $"e{i}");
            obj.Transform = new ObjectTransform(i * 3, i, i * 11, 1 + i * 0.05, 1 + i * 0.05);
            vm.Execute(new AddObjectCommand(vm.Scene, obj, [i % 2 == 0 ? cut : fill]));
        }

        vm.Layers[1].FillLineIntervalMm = 1;
        return vm;
    }

    [Fact]
    public void JobBuiltFromASnapshotIsLineForLineTheJobBuiltFromTheLiveScene()
    {
        Ui.Invoke(() =>
        {
            var scene = MixedScene();
            var rasterPath = LayoutAuditRenderTests.WritePng(Path.Combine(_directory, "snapshot-raster.png"), (dc, width, height) =>
            {
                dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, width, height));
                dc.DrawRectangle(Brushes.Black, null, new Rect(12, 18, width - 24, height - 36));
            });
            scene.ImportRasterFile(rasterPath, new Lasero.Core.Import.RasterImportOptions
            {
                TargetWidthMm = 12,
                Dpi = 127,
                UseDithering = true,
            });

            var live = SceneJobBuilder.BuildLines(scene.Scene, 1000, 3.5, -2);
            var snapshot = SceneJobBuilder.BuildLines(SceneJobBuilder.Snapshot(scene.Scene), 1000, 3.5, -2);

            Assert.True(live.Count > 100);
            Assert.Equal(live, snapshot);
        });
    }

    [Fact]
    public void ASnapshotIsNotAffectedByEditsMadeAfterItWasTaken()
    {
        var vm = MixedScene();
        var scene = vm.Scene;
        var snapshot = SceneJobBuilder.Snapshot(scene);
        var before = SceneJobBuilder.BuildLines(snapshot, 1000, 0, 0);

        vm.Layers[0].Power = 7;
        vm.Layers[0].Speed = 99;
        scene.Objects[0].Transform = scene.Objects[0].Transform with { X = 500 };
        scene.Objects.RemoveAt(1);

        Assert.Equal(before, SceneJobBuilder.BuildLines(snapshot, 1000, 0, 0));
        Assert.NotEqual(before, SceneJobBuilder.BuildLines(scene, 1000, 0, 0));
    }

    [Fact]
    public void CancellingStopsTheBuild()
    {
        var scene = MixedScene().Scene;
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(() => SceneJobBuilder.BuildLines(scene, 1000, 0, 0, cts.Token));
    }

    [Fact]
    public void PreviewShowsRemainingTimeAndFlagsTheDocumentStaleAfterSceneChanges()
    {
        Ui.Invoke(() =>
        {
            var scene = new SceneViewModel();
            var viewModel = ViewModelFor(scene);
            var document = GCodeParser.Parse(["G1 X60 Y0 F60"], "one-minute-line");
            var setDocument = typeof(GCodeViewModel).GetMethod("SetDocument", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var applyState = typeof(GCodeViewModel).GetMethod("ApplySimulationState", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var sceneChanged = typeof(GCodeViewModel).GetMethod("OnSceneChanged", BindingFlags.Instance | BindingFlags.NonPublic)!;

            setDocument.Invoke(viewModel, [document, "one-minute-line"]);
            viewModel.IsSimulationActive = true;
            viewModel.IsSimulationPlaying = true;
            var estimate = JobTimeEstimator.Estimate(document, new MachineMotionProfile
            {
                DefaultWorkSpeedMmPerMinute = 60,
                CommandOverhead = TimeSpan.Zero,
            });
            applyState.Invoke(viewModel, [JobSimulator.GetStateAtTime(document, estimate, TimeSpan.FromSeconds(30))]);

            Assert.Equal("00:30", viewModel.RemainingTimeLabel);
            sceneChanged.Invoke(viewModel, null);
            Assert.True(viewModel.IsSimulationStale);
            Assert.False(viewModel.IsSimulationActive);
            Assert.False(viewModel.IsSimulationPlaying);

            setDocument.Invoke(viewModel, [document, "one-minute-line"]);
            Assert.False(viewModel.IsSimulationStale);
        });
    }

    private GCodeViewModel ViewModelFor(SceneViewModel scene)
    {
        var vm = new GCodeViewModel(new AsyncJobMachineBase(), scene, new AppSettingsStore(Path.Combine(_directory, "settings.json")));
        typeof(GCodeViewModel).GetField("_controllerMaximumS", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(vm, 1000d);
        return vm;
    }

    private static void Pump(Func<bool> done)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!done() && DateTime.UtcNow < deadline)
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background);
        Assert.True(done(), "the background job preparation did not finish");
    }

    [Fact]
    public void AsyncAndSynchronousRegenerationProduceTheSameDocument()
    {
        Ui.Invoke(() =>
        {
            var scene = MixedScene();
            var viewModel = ViewModelFor(scene);
            typeof(GCodeViewModel).GetMethod("RegenerateFromSceneNow", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(viewModel, null);
            var syncLines = viewModel.Document!.RawLines.ToList();
            var syncSegments = viewModel.Document.Segments.Count;
            viewModel.ClearDocument();

            var task = viewModel.RegenerateFromSceneCommand.ExecuteAsync(null);
            Pump(() => task.IsCompleted);

            Assert.False(viewModel.IsPreparingJob);
            Assert.Equal(syncLines, viewModel.Document!.RawLines.ToList());
            Assert.Equal(syncSegments, viewModel.Document.Segments.Count);
        });
    }

    [Fact]
    public void AResultPreparedFromAnOlderSceneIsDiscardedAndAStartCheckRebuildsSynchronously()
    {
        Ui.Invoke(() =>
        {
            var scene = MixedScene();
            var viewModel = ViewModelFor(scene);
            var task = viewModel.RegenerateFromSceneCommand.ExecuteAsync(null);

            // The design changes while the worker is still preparing the old one.
            var item = scene.Objects[1];
            scene.Execute(new TransformObjectCommand(item, item.Transform, item.Transform with { Y = 77 }));
            Pump(() => task.IsCompleted);

            Assert.Null(viewModel.Document);
            Assert.False(viewModel.IsPreparingJob);

            // Start rebuilds before evaluating preflight. This fake machine is disconnected, so the action exits
            // at the safety gate before opening a confirmation or sending any machine command.
            InvokeActionThroughPreflight(viewModel, "RunJob");
            Assert.NotNull(viewModel.Document);
            Assert.Equal(SceneJobBuilder.BuildLines(scene.Scene, 1000, 0, 0), viewModel.Document!.RawLines.ToList());

            item = scene.Objects[1];
            scene.Execute(new TransformObjectCommand(item, item.Transform, item.Transform with { X = 91 }));
            InvokeActionThroughPreflight(viewModel, "RunFraming");
            Assert.Equal(SceneJobBuilder.BuildLines(scene.Scene, 1000, 0, 0), viewModel.Document!.RawLines.ToList());
        });
    }

    private static void InvokeActionThroughPreflight(GCodeViewModel viewModel, string methodName)
    {
        var method = typeof(GCodeViewModel).GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic)!;
        var task = (Task)method.Invoke(viewModel, null)!;
        Assert.True(task.IsCompleted, $"{methodName} should rebuild synchronously before its preflight gate");
        task.GetAwaiter().GetResult();
    }
}
