using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace Lasero.App;

public enum LaseroDialogChoice
{
    Cancel,
    Secondary,
    Primary,
}

public enum LaseroDialogTone
{
    Information,
    Warning,
    Danger,
}

public sealed record LaseroDialogOptions(
    string Title,
    string Message,
    string PrimaryText,
    string? SecondaryText = null,
    string? CancelText = "Zrušit",
    LaseroDialogTone Tone = LaseroDialogTone.Information,
    bool DestructivePrimary = false);

public partial class LaseroDialogWindow : Window
{
    public LaseroDialogChoice Choice { get; private set; } = LaseroDialogChoice.Cancel;

    private LaseroDialogWindow(LaseroDialogOptions options)
    {
        InitializeComponent();
        Title = options.Title;
        TitleText.Text = options.Title;
        MessageText.Text = options.Message;
        PrimaryButton.Content = options.PrimaryText;
        PrimaryButton.Style = (Style)FindResource(options.DestructivePrimary ? "Button.Danger" : "Button.Primary");

        SecondaryButton.Content = options.SecondaryText ?? string.Empty;
        SecondaryButton.Visibility = string.IsNullOrWhiteSpace(options.SecondaryText) ? Visibility.Collapsed : Visibility.Visible;
        CancelButton.Content = options.CancelText ?? string.Empty;
        CancelButton.Visibility = string.IsNullOrWhiteSpace(options.CancelText) ? Visibility.Collapsed : Visibility.Visible;
        CancelButton.IsCancel = CancelButton.Visibility == Visibility.Visible;

        var iconKey = options.Tone == LaseroDialogTone.Information ? "Glyph.Info" : "Glyph.Warning";
        DialogIcon.IconData = (Geometry)FindResource(iconKey);
        DialogIcon.Foreground = (System.Windows.Media.Brush)FindResource(options.Tone switch
        {
            LaseroDialogTone.Danger => "Brush.Danger",
            LaseroDialogTone.Warning => "Brush.Warning",
            _ => "Brush.Info",
        });

        PreviewKeyDown += (_, eventArgs) =>
        {
            if (eventArgs.Key != Key.Escape) return;
            Choice = LaseroDialogChoice.Cancel;
            Close();
        };
    }

    public static LaseroDialogChoice Show(Window? owner, LaseroDialogOptions options)
    {
        var dialog = new LaseroDialogWindow(options);
        if (owner is not null) dialog.Owner = owner;
        dialog.ShowDialog();
        return dialog.Choice;
    }

    private void OnPrimaryClick(object sender, RoutedEventArgs e)
    {
        Choice = LaseroDialogChoice.Primary;
        Close();
    }

    private void OnSecondaryClick(object sender, RoutedEventArgs e)
    {
        Choice = LaseroDialogChoice.Secondary;
        Close();
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        Choice = LaseroDialogChoice.Cancel;
        Close();
    }
}
