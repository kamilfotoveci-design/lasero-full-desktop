using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Lasero.App.ViewModels;

namespace Lasero.App.Views;

public partial class ChatView : UserControl
{
    private INotifyCollectionChanged? _messages;

    public ChatView() => InitializeComponent();

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel viewModel) return;
        _messages = viewModel.Chat.Messages;
        _messages.CollectionChanged += OnMessagesChanged;
        MessageScroll.ScrollToEnd();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_messages is not null) _messages.CollectionChanged -= OnMessagesChanged;
        _messages = null;
    }

    private void OnMessagesChanged(object? sender, NotifyCollectionChangedEventArgs e) =>
        Dispatcher.BeginInvoke(new Action(MessageScroll.ScrollToEnd));

    private void OnInputKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) return;
        if (DataContext is MainViewModel viewModel && viewModel.Chat.SendCommand.CanExecute(null))
            viewModel.Chat.SendCommand.Execute(null);
        e.Handled = true;
    }
}
