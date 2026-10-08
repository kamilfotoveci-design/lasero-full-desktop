using System.Windows;
using System.Windows.Controls;
using Lasero.App.ViewModels;
using Microsoft.Win32;

namespace Lasero.App;

public partial class RasterImportWindow : Window
{
    public RasterImportViewModel ViewModel { get; }

    public RasterImportWindow(RasterImportViewModel viewModel)
    {
        InitializeComponent();
        ViewModel = viewModel;
        DataContext = viewModel;
        Closed += (_, _) => ViewModel.Dispose();
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void OnImportClick(object sender, RoutedEventArgs e)
    {
        if (!ViewModel.TryValidate(out var message))
        {
            ViewModel.StatusMessage = message;
            return;
        }

        DialogResult = true;
        Close();
    }

    private void OnZoomInClick(object sender, RoutedEventArgs e) => ViewModel.SetZoom(ViewModel.PreviewZoom * 1.2);
    private void OnZoomOutClick(object sender, RoutedEventArgs e) => ViewModel.SetZoom(ViewModel.PreviewZoom / 1.2);
    private void OnResetZoomClick(object sender, RoutedEventArgs e) => ViewModel.SetZoom(1);

    private void OnBuiltInPresetClick(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is string name) ViewModel.ApplyBuiltInPreset(name);
    }

    private void OnUserPresetSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (UserPresetCombo.SelectedItem is RasterImagePreset preset) ViewModel.ApplyUserPreset(preset);
    }

    private void OnSavePresetClick(object sender, RoutedEventArgs e)
    {
        var name = Microsoft.VisualBasic.Interaction.InputBox("Název předvolby úprav obrazu:", "Uložit předvolbu", "Moje předvolba");
        if (string.IsNullOrWhiteSpace(name)) return;
        try
        {
            var preset = ViewModel.SavePreset(name);
            ViewModel.ReloadPresets();
            UserPresetCombo.SelectedItem = ViewModel.UserPresets.FirstOrDefault(item => item.Id == preset.Id);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Předvolbu nelze uložit", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnDeletePresetClick(object sender, RoutedEventArgs e)
    {
        if (UserPresetCombo.SelectedItem is not RasterImagePreset preset) return;
        ViewModel.DeletePreset(preset.Id);
        UserPresetCombo.SelectedItem = null;
    }

    private void OnExportPresetClick(object sender, RoutedEventArgs e)
    {
        if (UserPresetCombo.SelectedItem is not RasterImagePreset preset) return;
        var dialog = new SaveFileDialog { Filter = "Předvolba obrazu (*.json)|*.json", FileName = $"{preset.Name}.json" };
        if (dialog.ShowDialog(this) != true) return;
        try { System.IO.File.WriteAllText(dialog.FileName, ViewModel.PresetStore.Export(preset.Id)); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Export se nepodařil", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private void OnImportPresetClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "Předvolba obrazu (*.json)|*.json" };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            ViewModel.PresetStore.Import(System.IO.File.ReadAllText(dialog.FileName));
            ViewModel.ReloadPresets();
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Import se nepodařil", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }
}
