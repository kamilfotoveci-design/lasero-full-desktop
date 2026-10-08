using System.IO;
using System.Reflection;
using System.Windows.Threading;
using Lasero.App;
using Lasero.App.ViewModels;
using Lasero.Core.Grbl;
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
        var scene = MixedScene().Scene;

        var live = SceneJobBuilder.BuildLines(scene, 1000, 3.5, -2);
        var snapshot = SceneJobBuilder.BuildLines(SceneJobBuilder.Snapshot(scene), 1000, 3.5, -2);

        Assert.True(live.Count > 100);
        Assert.Equal(live, snapshot);
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

            // What Start, Frame and preview-before-run call: it rebuilds on the spot from the design as it is now.
            typeof(GCodeViewModel).GetMethod("EnsureSceneDocumentCurrent", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(viewModel, [false]);
            Assert.NotNull(viewModel.Document);
            Assert.Equal(SceneJobBuilder.BuildLines(scene.Scene, 1000, 0, 0), viewModel.Document!.RawLines.ToList());
        });
    }
}
