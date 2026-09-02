using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Lasero.App.ViewModels;

namespace Lasero.App.Views.Kamil;

/// <summary>
/// The assistant's text entry. Shared by Quick Ask and the expanded panel; the only difference
/// between the two is the placeholder.
/// </summary>
public partial class KamilComposer : UserControl
{
    public static readonly DependencyProperty PlaceholderProperty = DependencyProperty.Register(
        nameof(Placeholder), typeof(string), typeof(KamilComposer), new PropertyMetadata(string.Empty));

    public string Placeholder
    {
        get => (string)GetValue(PlaceholderProperty);
        set => SetValue(PlaceholderProperty, value);
    }

    public KamilComposer()
    {
        InitializeComponent();
    }

    /// <summary>Puts the caret at the end of whatever is already typed, rather than selecting it —
    /// re-opening the assistant should let the operator carry on, not overwrite their draft.</summary>
    public void FocusInput()
    {
        Input.Focus();
        Input.CaretIndex = Input.Text.Length;
    }

    /// <summary>
    /// Enter sends, Shift+Enter breaks the line. PreviewKeyDown because the TextBox accepts returns:
    /// by the time KeyDown bubbles, the newline has already been inserted.
    /// </summary>
    private void OnInputPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) return;

        e.Handled = true;
        if (DataContext is KamilAssistantViewModel { Chat: { } chat } && chat.SendCommand.CanExecute(null))
            chat.SendCommand.Execute(null);
    }
}
