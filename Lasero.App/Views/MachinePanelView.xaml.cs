using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Lasero.App.ViewModels;

namespace Lasero.App.Views;

public partial class MachinePanelView : UserControl
{
    private INotifyCollectionChanged? _consoleLines;
    private bool _positioningLaserPressed;

    private Window? _hostWindow;

    public MachinePanelView()
    {
        InitializeComponent();

        // Hold-to-fire must end on every way the release can be missed, not only on mouse-up:
        // keyboard focus moving away (Space/Enter key-up is then delivered elsewhere), the button
        // becoming disabled, the panel being hidden, or the window being deactivated (Alt+Tab).
        PositioningLaserButton.LostKeyboardFocus += OnPositioningLaserInterrupted;
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
        if (_positioningLaserPressed ||
            DataContext is MainViewModel { Jog.IsPositioningLaserOn: true })
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

    private async void OnPositioningLaserPressed(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || DataContext is not MainViewModel viewModel ||
            !viewModel.Jog.CanUsePositioningLaser) return;

        _positioningLaserPressed = true;
        ((Button)sender).CaptureMouse();
        e.Handled = true;
        await viewModel.Jog.StartPositioningLaserAsync();
    }

    private async void OnPositioningLaserReleased(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || !_positioningLaserPressed) return;
        _positioningLaserPressed = false;
        ((Button)sender).ReleaseMouseCapture();
        e.Handled = true;
        await StopPositioningLaserAsync();
    }

    private async void OnPositioningLaserLostCapture(object sender, MouseEventArgs e)
    {
        if (!_positioningLaserPressed) return;
        _positioningLaserPressed = false;
        await StopPositioningLaserAsync();
    }

    private async void OnPositioningLaserKeyDown(object sender, KeyEventArgs e)
    {
        if (e.IsRepeat || e.Key is not (Key.Space or Key.Enter) ||
            DataContext is not MainViewModel viewModel || !viewModel.Jog.CanUsePositioningLaser) return;

        _positioningLaserPressed = true;
        e.Handled = true;
        await viewModel.Jog.StartPositioningLaserAsync();
    }

    private async void OnPositioningLaserKeyUp(object sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Space or Key.Enter) || !_positioningLaserPressed) return;
        _positioningLaserPressed = false;
        e.Handled = true;
        await StopPositioningLaserAsync();
    }

    // IsEnabledChanged and IsVisibleChanged also fire when the state becomes true; only the
    // transitions away from "usable" interrupt a hold.
    private void OnPositioningLaserPropertyInterrupted(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is true) return;
        OnPositioningLaserInterrupted(sender, EventArgs.Empty);
    }

    private async void OnPositioningLaserInterrupted(object? sender, EventArgs e)
    {
        if (!_positioningLaserPressed && DataContext is not MainViewModel { Jog.IsPositioningLaserOn: true }) return;

        if (PositioningLaserButton.IsMouseCaptured) PositioningLaserButton.ReleaseMouseCapture();
        await StopPositioningLaserAsync();
    }

    private async Task StopPositioningLaserAsync()
    {
        _positioningLaserPressed = false;
        if (DataContext is MainViewModel viewModel)
            await viewModel.Jog.StopPositioningLaserAsync();
    }
}
