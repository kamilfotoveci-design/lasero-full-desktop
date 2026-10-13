using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Lasero.Avalonia.ViewModels;

namespace Lasero.Avalonia.Views;

/// <summary>Port of Lasero.App/BitmapTraceWindow.xaml.cs.</summary>
public sealed partial class BitmapTraceWindow : Window
{
    public BitmapTraceViewModel ViewModel { get; }

    public BitmapTraceWindow(BitmapTraceViewModel viewModel)
    {
        AvaloniaXamlLoader.Load(this);
        ViewModel = viewModel;
        DataContext = viewModel;
        Closed += (_, _) => ViewModel.Dispose();
    }

    public static Task<bool?> ShowResultAsync(Window owner, BitmapTraceViewModel viewModel)
    {
        var window = new BitmapTraceWindow(viewModel);
        return window.ShowDialog<bool?>(owner);
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close(false);

    private void OnApplyClick(object? sender, RoutedEventArgs e)
    {
        if (!ViewModel.HasResult) return;
        Close(true);
    }
}
