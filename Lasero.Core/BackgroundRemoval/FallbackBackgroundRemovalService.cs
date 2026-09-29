namespace Lasero.Core.BackgroundRemoval;

/// <summary>
/// Tries the primary (cloud) provider first and, when it is not usable, the on-device model - but
/// only if that model is already cached, so a fallback never silently starts a large download.
/// When the fallback is not ready the primary's own message is surfaced, so the operator always
/// learns what to fix instead of seeing nothing happen.
/// </summary>
public sealed class FallbackBackgroundRemovalService : IBackgroundRemovalService
{
    private readonly IBackgroundRemovalService _primary;
    private readonly IBackgroundRemovalService _fallback;

    public FallbackBackgroundRemovalService(IBackgroundRemovalService primary, IBackgroundRemovalService fallback)
    {
        _primary = primary ?? throw new ArgumentNullException(nameof(primary));
        _fallback = fallback ?? throw new ArgumentNullException(nameof(fallback));
    }

    /// <summary>True when the last successful run was produced on this computer, so nothing left it.</summary>
    public bool LastRunWasLocal { get; private set; }

    public bool IsReady => _primary.IsReady || _fallback.IsReady;

    public Task PrepareAsync(IProgress<double>? progress = null, CancellationToken cancellationToken = default) =>
        _primary.IsReady ? _primary.PrepareAsync(progress, cancellationToken) : _fallback.PrepareAsync(progress, cancellationToken);

    public async Task<string> RemoveBackgroundAsync(
        string sourceFilePath, string? destinationFilePath = null, CancellationToken cancellationToken = default)
    {
        LastRunWasLocal = false;
        BackgroundRemovalException primaryFailure;
        try
        {
            return await _primary.RemoveBackgroundAsync(sourceFilePath, destinationFilePath, cancellationToken).ConfigureAwait(false);
        }
        catch (BackgroundRemovalException exception) when (
            exception.Failure != BackgroundRemovalFailure.InvalidImage && _fallback.IsReady)
        {
            primaryFailure = exception;
        }

        try
        {
            var result = await _fallback.RemoveBackgroundAsync(sourceFilePath, destinationFilePath, cancellationToken).ConfigureAwait(false);
            LastRunWasLocal = true;
            return result;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new BackgroundRemovalException(BackgroundRemovalFailure.LocalFailed,
                "Odstranění pozadí v počítači se nezdařilo. " + primaryFailure.Message, exception);
        }
    }
}
