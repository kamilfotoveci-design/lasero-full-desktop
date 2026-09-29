namespace Lasero.Core.BackgroundRemoval;

/// <summary>
/// Provider-level contract for producing a new transparent raster without changing the source.
/// Implementations may use an on-device model or an authenticated remote provider; UI code should
/// commit the returned asset to the scene only after this operation completes successfully.
/// </summary>
public interface IBackgroundRemovalService
{
    bool IsReady { get; }

    Task PrepareAsync(IProgress<double>? progress = null, CancellationToken cancellationToken = default);

    Task<string> RemoveBackgroundAsync(
        string sourceFilePath,
        string? destinationFilePath = null,
        CancellationToken cancellationToken = default);
}
