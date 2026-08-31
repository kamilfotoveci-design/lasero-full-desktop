using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using Serilog;

namespace Lasero.App.ViewModels;

/// <summary>
/// The photo of the operator's own engraver, shown on Home and in the device card.
/// <para>
/// It is supplied, never inferred: nothing the app can read off a GRBL controller identifies a
/// product, so a stock render of somebody else's machine would be a picture of the wrong engraver.
/// Until a photo is chosen the surfaces show a placeholder that says so.
/// </para>
/// <para>
/// The chosen file is copied into the app's own data folder, so the picture survives the original
/// being moved off a memory card or renamed.
/// </para>
/// </summary>
public partial class MachinePhotoViewModel : ObservableObject
{
    private readonly AppSettingsStore _settingsStore;

    public MachinePhotoViewModel(AppSettingsStore settingsStore)
    {
        _settingsStore = settingsStore;
        _photoPath = Existing(settingsStore.Current.Machine.MachinePhotoPath);
    }

    [ObservableProperty] private string? _photoPath;

    public bool HasPhoto => !string.IsNullOrWhiteSpace(PhotoPath);

    partial void OnPhotoPathChanged(string? value) => OnPropertyChanged(nameof(HasPhoto));

    [RelayCommand]
    private void ChoosePhoto()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Vyberte fotku své gravírky",
            Filter = "Obrázky|*.png;*.jpg;*.jpeg;*.webp;*.bmp|Všechny soubory|*.*",
            CheckFileExists = true,
        };
        if (dialog.ShowDialog() != true) return;

        try
        {
            PhotoPath = CopyIntoAppData(dialog.FileName);
            Save();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Log.Warning(exception, "Could not store the machine photo from {Path}", dialog.FileName);
        }
    }

    [RelayCommand]
    private void RemovePhoto()
    {
        PhotoPath = null;
        Save();
    }

    private void Save()
    {
        _settingsStore.Current.Machine.MachinePhotoPath = PhotoPath;
        _settingsStore.Save();
    }

    /// <summary>A path that no longer resolves is treated as no photo. The alternative is a broken
    /// image box where a machine used to be.</summary>
    private static string? Existing(string? path) =>
        !string.IsNullOrWhiteSpace(path) && File.Exists(path) ? path : null;

    private static string CopyIntoAppData(string source)
    {
        var folder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Lasero");
        Directory.CreateDirectory(folder);
        var target = Path.Combine(folder, "machine-photo" + Path.GetExtension(source));
        File.Copy(source, target, overwrite: true);
        return target;
    }
}
