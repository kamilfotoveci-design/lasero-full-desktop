using System.IO;
using Lasero.App;
using Lasero.App.ViewModels;
using Lasero.Core.BackgroundRemoval;
using Lasero.Core.GCode;
using Lasero.Core.Geometry;
using Lasero.Core.Grbl;
using Lasero.Core.Import;
using Lasero.Core.Layers;
using Lasero.Core.Scene;
using Lasero.Core.Trace;
using Xunit;

namespace Lasero.Tests;

/// <summary>Scene-level behaviour of "Odstranit pozadí": the result replaces the source as one
/// undo step, and every failure path leaves the document untouched with a short Czech message.</summary>
public sealed class BackgroundRemovalCoordinatorTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "lasero-bgc-" + Guid.NewGuid().ToString("N"));

    public BackgroundRemovalCoordinatorTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public async Task Success_ReplacesSourceInOneUndoStepAndUndoRestoresIt()
    {
        var (scene, source) = CreateScene();
        var result = Path.Combine(_directory, "photo.nobg.png");
        File.WriteAllText(result, "png");
        var coordinator = new BackgroundRemovalCoordinator(scene, new FakeService((_, _) => Task.FromResult(result)));

        var outcome = await coordinator.RunAsync(source);

        Assert.Equal(BackgroundRemovalOutcome.Committed, outcome);
        var replaced = Assert.Single(scene.Objects);
        Assert.Equal(result, replaced.RasterFilePath);
        Assert.Equal("photo.png", replaced.OriginalRasterFilePath);
        Assert.True(replaced.HasBackgroundRemoved);
        Assert.Same(replaced, scene.Selected);
        Assert.True(File.Exists(result));
        Assert.False(scene.IsRemovingBackground);
        Assert.Null(scene.BackgroundRemovalError);

        scene.UndoCommand.Execute(null);
        var restored = Assert.Single(scene.Objects);
        Assert.Equal("photo.png", restored.RasterFilePath);
        Assert.False(restored.HasBackgroundRemoved);
        Assert.False(scene.CanUndo);

        scene.RedoCommand.Execute(null);
        Assert.Equal(result, Assert.Single(scene.Objects).RasterFilePath);
    }

    [Fact]
    public async Task ServiceFailure_KeepsDocumentUnchangedAndShowsTheCzechReason()
    {
        var (scene, source) = CreateScene();
        var coordinator = new BackgroundRemovalCoordinator(scene, new FakeService((_, _) =>
            throw new BackgroundRemovalException(BackgroundRemovalFailure.RateLimited, "Služba je nyní vytížená.")));

        var outcome = await coordinator.RunAsync(source);

        Assert.Equal(BackgroundRemovalOutcome.Failed, outcome);
        Assert.Same(source, Assert.Single(scene.Objects));
        Assert.Equal("photo.png", source.RasterFilePath);
        Assert.False(scene.CanUndo);
        Assert.Equal("Služba je nyní vytížená.", scene.BackgroundRemovalError);
        Assert.False(scene.IsRemovingBackground);
        Assert.False(scene.BackgroundRemovalIsIndeterminate);
    }

    [Fact]
    public async Task UnexpectedException_NeverLeaksItsTextAndNeverCrashes()
    {
        var (scene, source) = CreateScene();
        var coordinator = new BackgroundRemovalCoordinator(scene, new FakeService((_, _) =>
            throw new InvalidOperationException("Secret internal Bearer abc123")));

        var outcome = await coordinator.RunAsync(source);

        Assert.Equal(BackgroundRemovalOutcome.Failed, outcome);
        var error = Assert.IsType<string>(scene.BackgroundRemovalError);
        Assert.Equal(BackgroundRemovalCoordinator.GenericFailureMessage, error);
        Assert.DoesNotContain('?', error);
        Assert.DoesNotContain('!', error);
        Assert.False(scene.CanUndo);
    }

    [Fact]
    public async Task Cancel_StopsTheRunLeavesNoErrorAndNoUndoStep()
    {
        var (scene, source) = CreateScene();
        var started = new TaskCompletionSource();
        var coordinator = new BackgroundRemovalCoordinator(scene, new FakeService(async (_, token) =>
        {
            started.SetResult();
            await Task.Delay(Timeout.Infinite, token);
            return "never";
        }));

        var run = coordinator.RunAsync(source);
        await started.Task;
        Assert.True(scene.IsRemovingBackground);
        scene.CancelBackgroundRemovalCommand.Execute(null);
        var outcome = await run;

        Assert.Equal(BackgroundRemovalOutcome.Cancelled, outcome);
        Assert.Null(scene.BackgroundRemovalError);
        Assert.Equal("Zrušeno", scene.BackgroundRemovalStatus);
        Assert.False(scene.IsRemovingBackground);
        Assert.False(scene.CanUndo);
        Assert.Equal("photo.png", Assert.Single(scene.Objects).RasterFilePath);
    }

    [Fact]
    public async Task ResultForAnObjectRemovedMeanwhile_IsRejectedAndDeleted()
    {
        var (scene, source) = CreateScene();
        var result = Path.Combine(_directory, "orphan.nobg.png");
        File.WriteAllText(result, "png");
        var coordinator = new BackgroundRemovalCoordinator(scene, new FakeService((_, _) =>
        {
            scene.Objects.Remove(source);
            return Task.FromResult(result);
        }));

        var outcome = await coordinator.RunAsync(source);

        Assert.Equal(BackgroundRemovalOutcome.Failed, outcome);
        Assert.False(File.Exists(result));
        Assert.NotNull(scene.BackgroundRemovalError);
        Assert.Empty(scene.Objects);
    }

    [Fact]
    public async Task SecondRequestWhileRunning_IsSkipped()
    {
        var (scene, source) = CreateScene();
        var gate = new TaskCompletionSource<string>();
        var started = new TaskCompletionSource();
        var calls = 0;
        var coordinator = new BackgroundRemovalCoordinator(scene, new FakeService((_, _) =>
        {
            calls++;
            started.TrySetResult();
            return gate.Task;
        }));

        var first = coordinator.RunAsync(source);
        await started.Task;
        var second = await coordinator.RunAsync(source);
        gate.SetException(new BackgroundRemovalException(BackgroundRemovalFailure.Network, "Bez připojení."));
        await first;

        Assert.Equal(BackgroundRemovalOutcome.Skipped, second);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task ImageWithoutFile_IsSkippedWithoutCallingTheService()
    {
        var (scene, source) = CreateScene(rasterPath: "");
        var calls = 0;
        var coordinator = new BackgroundRemovalCoordinator(scene, new FakeService((_, _) => { calls++; return Task.FromResult("x"); }));

        Assert.Equal(BackgroundRemovalOutcome.Skipped, await coordinator.RunAsync(source));
        Assert.Equal(0, calls);
    }

    [Fact]
    public void ConsentStore_PersistsAcceptanceAcrossInstancesAndSurvivesCorruption()
    {
        var path = Path.Combine(_directory, "consent.json");
        var store = new BackgroundRemovalConsentStore(path);
        Assert.False(store.HasConsent);

        store.RecordConsent();

        Assert.True(store.HasConsent);
        Assert.True(new BackgroundRemovalConsentStore(path).HasConsent);

        File.WriteAllText(path, "{ not json");
        Assert.False(new BackgroundRemovalConsentStore(path).HasConsent);
    }

    private static (SceneViewModel Scene, SceneObject Source) CreateScene(string rasterPath = "photo.png")
    {
        var shape = new ImportedShape
        {
            Points = [new Position(0, 0, 0), new Position(20, 0, 0), new Position(20, 10, 0), new Position(0, 10, 0), new Position(0, 0, 0)],
            IsClosed = true,
            LayerColor = RgbColor.Red,
            PreferredMode = LayerMode.Cut,
        };
        var source = new SceneObject
        {
            LocalShapes = [shape],
            LocalPivot = new Position(10, 5, 0),
            LocalBounds = new BoundingBox2D(0, 0, 20, 10),
            RasterFilePath = rasterPath,
            RasterOptions = new RasterImportOptions { TargetWidthMm = 20, TargetHeightMm = 10 },
        };
        var scene = new SceneViewModel();
        scene.Objects.Add(source);
        scene.SelectedObjects.Add(source);
        return (scene, source);
    }

    private sealed class FakeService(Func<string, CancellationToken, Task<string>> run) : IBackgroundRemovalService
    {
        public bool IsReady => true;
        public Task PrepareAsync(IProgress<double>? progress = null, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<string> RemoveBackgroundAsync(string sourceFilePath, string? destinationFilePath = null, CancellationToken cancellationToken = default) =>
            run(sourceFilePath, cancellationToken);
    }
}
