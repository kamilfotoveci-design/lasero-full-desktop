using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

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
        Loaded += OnLoaded;
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

    /// <summary>Reused by every dialog raised through this shell: a short opacity/scale/
    /// translate entrance on the dialog surface itself, following the same reduced-motion check
    /// (SystemParameters.ClientAreaAnimation + RenderCapability.Tier) that Lasero.App.Views.Kamil
    /// .KamilAssistantHost and Lasero.App.Views.DeviceSetup.DeviceWizardOverlay already use. The Window
    /// itself is not AllowsTransparency, so Window.Opacity would be a no-op - the fade runs on
    /// DialogSurface instead, which is fully opaque background anyway.</summary>
    private static bool AnimationsEnabled => SystemParameters.ClientAreaAnimation
        && RenderCapability.Tier > 0;

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (!AnimationsEnabled)
        {
            DialogSurface.Opacity = 1;
            DialogScale.ScaleX = 1;
            DialogScale.ScaleY = 1;
            DialogOffset.Y = 0;
            return;
        }

        var duration = (Duration)FindResource("Motion.Panel");
        var ease = (CubicEase)FindResource("Ease.Out");

        DialogSurface.BeginAnimation(OpacityProperty, new DoubleAnimation
        {
            From = 0, To = 1, Duration = duration, EasingFunction = ease, FillBehavior = FillBehavior.HoldEnd,
        });
        DialogScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation
        {
            From = 0.985, To = 1, Duration = duration, EasingFunction = ease, FillBehavior = FillBehavior.HoldEnd,
        });
        DialogScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation
        {
            From = 0.985, To = 1, Duration = duration, EasingFunction = ease, FillBehavior = FillBehavior.HoldEnd,
        });
        DialogOffset.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation
        {
            From = 4, To = 0, Duration = duration, EasingFunction = ease, FillBehavior = FillBehavior.HoldEnd,
        });
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
