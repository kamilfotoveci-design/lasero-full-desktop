using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Lasero.App.ViewModels;

namespace Lasero.App.Views;

public partial class DeviceView : UserControl
{
    private INotifyCollectionChanged? _consoleLines;

    public DeviceView()
    {
        InitializeComponent();
        IsVisibleChanged += OnIsVisibleChanged;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel viewModel) return;
        _consoleLines = viewModel.Console.Lines;
        _consoleLines.CollectionChanged += OnConsoleLinesChanged;
        ConsoleScroll.ScrollToBottom();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_consoleLines is not null)
            _consoleLines.CollectionChanged -= OnConsoleLinesChanged;
        _consoleLines = null;
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

    /// <summary>The settings draft is filled from the saved values every time the screen is entered, so an edit that was
    /// never saved does not come back after leaving the screen. Switching tabs inside the screen does not pass through here.</summary>
    private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is true) SettingsPage.Refresh();
    }
}
