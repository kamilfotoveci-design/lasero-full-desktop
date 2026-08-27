using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Lasero.App.ViewModels;

namespace Lasero.App.Views;

public partial class MachinePanelView : UserControl
{
    private INotifyCollectionChanged? _consoleLines;

    public MachinePanelView() => InitializeComponent();

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
}
