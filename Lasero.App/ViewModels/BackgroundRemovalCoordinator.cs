using System.IO;
using Lasero.Core.BackgroundRemoval;
using Lasero.Core.Scene;
using Serilog;

namespace Lasero.App.ViewModels;

public enum BackgroundRemovalOutcome
{
    /// <summary>The transparent raster replaced the source as one undo step.</summary>
    Committed,
    Cancelled,
    /// <summary>A short Czech reason is in <see cref="SceneViewModel.BackgroundRemovalError"/>.</summary>
    Failed,
    /// <summary>Another run is active or the image has no file; nothing happened.</summary>
    Skipped,
}

/// <summary>
/// Runs one background-removal request for a raster and reports progress through the scene view
/// model. The service only produces a new file; the scene is touched exactly once, by
/// <see cref="SceneViewModel.CommitBackgroundRemoval"/>, after success - so cancel, timeout and every
/// error leave the document exactly as it was, and a result that could not be committed is deleted.
/// </summary>
public sealed class BackgroundRemovalCoordinator
{
    public const string GenericFailureMessage = "Odstranění pozadí se nezdařilo. Lze to zkusit znovu.";

    private readonly SceneViewModel _scene;
    private readonly IBackgroundRemovalService _service;
    private CancellationTokenSource? _cancellation;

    public BackgroundRemovalCoordinator(SceneViewModel scene, IBackgroundRemovalService service)
    {
        _scene = scene ?? throw new ArgumentNullException(nameof(scene));
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _scene.BackgroundRemovalCancelRequested += Cancel;
    }

    public bool IsRunning => _cancellation is not null;

    public void Cancel() => _cancellation?.Cancel();

    public async Task<BackgroundRemovalOutcome> RunAsync(SceneObject source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (string.IsNullOrWhiteSpace(source.RasterFilePath) || _scene.IsRemovingBackground || IsRunning)
            return BackgroundRemovalOutcome.Skipped;

        var cancellation = new CancellationTokenSource();
        _cancellation = cancellation;
        var cancellationToken = cancellation.Token;
        string? uncommittedResultPath = null;

        try
        {
            _scene.BackgroundRemovalError = null;
            _scene.IsRemovingBackground = true;
            _scene.BackgroundRemovalIsIndeterminate = true;
            _scene.BackgroundRemovalProgress = 0.5;
            _scene.BackgroundRemovalStatus = "Odstraňuji pozadí…";

            var resultPath = await _service.RemoveBackgroundAsync(source.RasterFilePath, cancellationToken: cancellationToken);
            uncommittedResultPath = resultPath;
            cancellationToken.ThrowIfCancellationRequested();
            if (!_scene.CommitBackgroundRemoval(source, resultPath))
            {
                _scene.BackgroundRemovalError = "Původní obrázek už není v dokumentu. Výsledek nebyl použit.";
                return BackgroundRemovalOutcome.Failed;
            }

            uncommittedResultPath = null;
            return BackgroundRemovalOutcome.Committed;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _scene.BackgroundRemovalStatus = "Zrušeno";
            return BackgroundRemovalOutcome.Cancelled;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Background removal failed for {RasterPath}", source.RasterFilePath);
            _scene.BackgroundRemovalError = DescribeFailure(ex);
            return BackgroundRemovalOutcome.Failed;
        }
        finally
        {
            if (uncommittedResultPath is not null)
            {
                try
                {
                    File.Delete(uncommittedResultPath);
                }
                catch (Exception cleanupException)
                {
                    Log.Warning(cleanupException, "Failed to clean uncommitted background-removal result {ResultPath}", uncommittedResultPath);
                }
            }

            _scene.IsRemovingBackground = false;
            _scene.BackgroundRemovalIsIndeterminate = false;
            if (ReferenceEquals(_cancellation, cancellation)) _cancellation = null;
            cancellation.Dispose();
        }
    }

    /// <summary>Only messages we wrote ourselves reach the operator; anything else (framework or
    /// provider text) becomes the generic sentence.</summary>
    public static string DescribeFailure(Exception exception) =>
        exception is BackgroundRemovalException { Message.Length: > 0 } known ? known.Message : GenericFailureMessage;
}
