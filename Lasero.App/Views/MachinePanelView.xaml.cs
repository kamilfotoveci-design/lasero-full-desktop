using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Lasero.App.ViewModels;

namespace Lasero.App.Views;

public partial class MachinePanelView : UserControl
{
    private INotifyCollectionChanged? _consoleLines;
    private Window? _hostWindow;

    public MachinePanelView()
    {
        InitializeComponent();

        // Click mode remains bounded by the view-model watchdog. Leaving the machine panel or
        // deactivating the window still requests an immediate M5.
        PositioningLaserButton.IsEnabledChanged += OnPositioningLaserPropertyInterrupted;
        IsVisibleChanged += OnPositioningLaserPropertyInterrupted;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_hostWindow is null && Window.GetWindow(this) is { } window)
        {
            _hostWindow = window;
            _hostWindow.Deactivated += OnPositioningLaserInterrupted;
        }

        if (DataContext is not MainViewModel viewModel) return;
        _consoleLines = viewModel.Console.Lines;
        _consoleLines.CollectionChanged += OnConsoleLinesChanged;
        ConsoleScroll.ScrollToBottom();
    }

    private async void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_hostWindow is not null) _hostWindow.Deactivated -= OnPositioningLaserInterrupted;
        _hostWindow = null;
        if (_consoleLines is not null)
            _consoleLines.CollectionChanged -= OnConsoleLinesChanged;
        _consoleLines = null;
        if (DataContext is MainViewModel { Jog.IsPositioningLaserActiveOrStarting: true })
        {
            await StopPositioningLaserAsync();
        }
    }

    private void OnConsoleLinesChanged(object? sender, NotifyCollectionChangedEventArgs e) =>
        ConsoleScroll.ScrollToBottom();

    private void OnConsoleInputKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || DataContext is not MainViewModel viewModel) return;
        if (viewModel.Console.SendCommand.CanExecute(null))
            viewModel.Console.SendCommand.Execute(null);
        e.Handled = true;
    }

    private async void OnPositioningLaserClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel viewModel)
            await viewModel.Jog.TogglePositioningLaserAsync();
    }

    // Disabling or hiding the control requests M5 while the pointer is active.
    private void OnPositioningLaserPropertyInterrupted(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is true) return;
        OnPositioningLaserInterrupted(sender, EventArgs.Empty);
    }

    private async void OnPositioningLaserInterrupted(object? sender, EventArgs e)
    {
        if (DataContext is not MainViewModel { Jog.IsPositioningLaserActiveOrStarting: true }) return;
        await StopPositioningLaserAsync();
    }

    private async Task StopPositioningLaserAsync()
    {
        if (DataContext is MainViewModel viewModel)
            await viewModel.Jog.StopPositioningLaserAsync();
    }
}
