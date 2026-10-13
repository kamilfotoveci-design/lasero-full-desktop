using System.Runtime.Versioning;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Lasero.Avalonia.ViewModels;

namespace Lasero.Avalonia.Views;

/// <summary>Port of Lasero.App/RasterImportWindow.xaml.cs. WPF used Window.DialogResult; Avalonia has
/// no equivalent property, so ShowResultAsync below returns the same true/false/null contract via
/// ShowDialog&lt;bool?&gt;.</summary>
[SupportedOSPlatform("windows")]
public sealed partial class RasterImportWindow : Window
{
    public RasterImportViewModel ViewModel { get; }

    public RasterImportWindow(RasterImportViewModel viewModel)
    {
        AvaloniaXamlLoader.Load(this);
        ViewModel = viewModel;
        DataContext = viewModel;
        Closed += (_, _) => ViewModel.Dispose();
    }

    /// <summary>Shows the dialog and returns true only if the operator confirmed the import — mirrors
    /// WPF's <c>importWindow.ShowDialog() == true</c> call sites.</summary>
    public static Task<bool?> ShowResultAsync(Window owner, RasterImportViewModel viewModel)
    {
        var window = new RasterImportWindow(viewModel);
        return window.ShowDialog<bool?>(owner);
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close(false);

    private void OnImportClick(object? sender, RoutedEventArgs e)
    {
        if (!ViewModel.TryValidate(out var message))
        {
            ViewModel.StatusMessage = message;
            return;
        }

        Close(true);
    }
}
