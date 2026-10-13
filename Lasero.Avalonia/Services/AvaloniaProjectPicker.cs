using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using CSharpFunctionalExtensions;
using Lasero.Avalonia.Contracts;

namespace Lasero.Avalonia.Services;

public sealed class AvaloniaProjectPicker : IProjectPicker
{
    private static readonly FilePickerFileType ProjectType = new("Projekt Lasero")
    {
        Patterns = ["*.lasero"],
    };

    public async Task<Result<string?>> PickOpen(CancellationToken cancellationToken = default)
    {
        try
        {
            var window = (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)
                ?.MainWindow;
            if (window is null)
                return Result.Failure<string?>("Hlavní okno není dostupné.");

            var files = await window.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Otevřít projekt Lasero",
                AllowMultiple = false,
                FileTypeFilter = [ProjectType, FilePickerFileTypes.All],
            });

            cancellationToken.ThrowIfCancellationRequested();
            return Result.Success(files.Count == 0 ? null : files[0].TryGetLocalPath());
        }
        catch (OperationCanceledException)
        {
            return Result.Success<string?>(null);
        }
        catch (Exception exception)
        {
            return Result.Failure<string?>($"Výběr projektu selhal: {exception.Message}");
        }
    }
}
