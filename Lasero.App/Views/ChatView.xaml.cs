using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Lasero.App.ViewModels;

namespace Lasero.App.Views;

public partial class ChatView : UserControl
{
    private const double ScrollBottomThreshold = 56;
    private INotifyCollectionChanged? _messages;
    private bool _stickToBottom = true;

    public ChatView() => InitializeComponent();

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel viewModel) return;
        _messages = viewModel.Chat.Messages;
        _messages.CollectionChanged += OnMessagesChanged;
        _stickToBottom = true;
        Dispatcher.BeginInvoke(new Action(MessageScroll.ScrollToEnd), DispatcherPriority.Loaded);
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_messages is not null) _messages.CollectionChanged -= OnMessagesChanged;
        _messages = null;
    }

    private void OnMessagesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        var shouldScroll = _stickToBottom;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (shouldScroll)
                MessageScroll.ScrollToEnd();
            else
                ScrollToBottomButton.Visibility = Visibility.Visible;
        }), DispatcherPriority.Background);
    }

    private void OnMessageScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        var distanceFromBottom = e.ExtentHeight - e.ViewportHeight - e.VerticalOffset;
        _stickToBottom = distanceFromBottom <= ScrollBottomThreshold;
        if (_stickToBottom)
            ScrollToBottomButton.Visibility = Visibility.Collapsed;
        else if (e.ExtentHeightChange != 0 || e.ViewportHeightChange != 0 || e.VerticalChange != 0)
            ScrollToBottomButton.Visibility = Visibility.Visible;
    }

    private void OnScrollToBottom(object sender, RoutedEventArgs e)
    {
        _stickToBottom = true;
        MessageScroll.ScrollToEnd();
        ScrollToBottomButton.Visibility = Visibility.Collapsed;
    }

    private void OnCopyMessage(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string text } && !string.IsNullOrWhiteSpace(text))
            Clipboard.SetText(text);
    }

    private void OnInputKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) return;
        if (DataContext is MainViewModel viewModel && viewModel.Chat.SendCommand.CanExecute(null))
            viewModel.Chat.SendCommand.Execute(null);
        e.Handled = true;
    }
}
