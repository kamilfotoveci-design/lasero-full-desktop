using System.ComponentModel;
using System.Windows;
using System.Windows.Shell;
using Lasero.App.ViewModels;

namespace Lasero.App;

public partial class PreviewWindow : Window
{
    private readonly MainViewModel _viewModel;

    public PreviewWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        Closing += OnClosing;
        StateChanged += (_, _) => UpdateMaximizeGlyph();
        UpdateMaximizeGlyph();
    }

    private void OnMinimizeClick(object sender, RoutedEventArgs e) => SystemCommands.MinimizeWindow(this);

    private void OnMaximizeClick(object sender, RoutedEventArgs e)
    {
        if (WindowState == WindowState.Maximized) SystemCommands.RestoreWindow(this);
        else SystemCommands.MaximizeWindow(this);
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_viewModel.GCode.CloseSimulationCommand.CanExecute(null))
            _viewModel.GCode.CloseSimulationCommand.Execute(null);
    }

    private void UpdateMaximizeGlyph()
    {
        MaximizeGlyph.IconData = (System.Windows.Media.Geometry)FindResource(
            WindowState == WindowState.Maximized ? "Glyph.Restore" : "Glyph.Maximize");
        MaximizeButton.ToolTip = WindowState == WindowState.Maximized ? "Obnovit velikost" : "Maximalizovat";
    }
}
