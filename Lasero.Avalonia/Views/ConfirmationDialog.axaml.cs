using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Media;

namespace Lasero.Avalonia.Views;

public sealed partial class ConfirmationDialog : Window
{
    public ConfirmationDialog()
    {
        AvaloniaXamlLoader.Load(this);
    }

    /// <summary>Shows a modal confirm/cancel prompt and returns true only if the user picked the
    /// primary (confirming) action. Mirrors LaseroDialogWindow's Show(...) == LaseroDialogChoice.Primary
    /// call sites in JogViewModel.SoftReset and ConsoleViewModel.Send.</summary>
    public static async Task<bool> ShowAsync(Window owner, string title, string message,
        string primaryText, string cancelText = "Zrušit", bool destructive = false)
    {
        var dialog = new ConfirmationDialog();
        dialog.FindControl<TextBlock>("TitleText")!.Text = title;
        dialog.FindControl<TextBlock>("MessageText")!.Text = message;
        dialog.FindControl<Button>("CancelButton")!.Content = cancelText;
        var primaryButton = dialog.FindControl<Button>("PrimaryButton")!;
        primaryButton.Content = primaryText;
        if (destructive && dialog.TryFindResource("Brush.Danger", out var danger) && danger is IBrush dangerBrush)
        {
            primaryButton.Background = dangerBrush;
            primaryButton.BorderBrush = dangerBrush;
        }

        var confirmed = false;
        primaryButton.Click += (_, _) => { confirmed = true; dialog.Close(); };
        dialog.FindControl<Button>("CancelButton")!.Click += (_, _) => dialog.Close();

        await dialog.ShowDialog(owner);
        return confirmed;
    }
}
