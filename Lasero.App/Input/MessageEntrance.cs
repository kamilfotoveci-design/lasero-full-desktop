using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Lasero.App.Controls.Motion;
using Lasero.App.ViewModels;

namespace Lasero.App.Input;

/// <summary>
/// Entrance for a chat message row: it rises a few pixels, scales up with a small overshoot (a soft
/// bounce) and fades in. Only a message that has just arrived animates; rows that are re-created while
/// the panel opens or a conversation is switched stay still. Nothing runs when animations are off
/// (Windows setting or the app's reduced-motion switch).
/// </summary>
public static class MessageEntrance
{
    private static readonly TimeSpan FreshWindow = TimeSpan.FromSeconds(4);

    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled", typeof(bool), typeof(MessageEntrance), new PropertyMetadata(false, OnIsEnabledChanged));

    public static bool GetIsEnabled(DependencyObject d) => (bool)d.GetValue(IsEnabledProperty);
    public static void SetIsEnabled(DependencyObject d, bool value) => d.SetValue(IsEnabledProperty, value);

    private static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement element) return;
        element.Loaded -= OnLoaded;
        if ((bool)e.NewValue) element.Loaded += OnLoaded;
    }

    private static void OnLoaded(object sender, RoutedEventArgs e)
    {
        var element = (FrameworkElement)sender;
        element.Loaded -= OnLoaded;
        if (!LaseroMotion.AnimationsEnabled) return;
        if (element.DataContext is not ChatMessageItem message || DateTimeOffset.Now - message.CreatedAt > FreshWindow) return;

        var scale = new ScaleTransform(0.94, 0.94);
        var shift = new TranslateTransform(0, 16);
        element.RenderTransformOrigin = new Point(0, 1);
        element.RenderTransform = new TransformGroup { Children = { scale, shift } };
        element.Opacity = 0;

        var bounce = new BackEase { Amplitude = 0.55, EasingMode = EasingMode.EaseOut };
        var duration = TimeSpan.FromMilliseconds(420);

        element.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220)));
        shift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(16, 0, duration) { EasingFunction = bounce });
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.94, 1, duration) { EasingFunction = bounce });
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.94, 1, duration) { EasingFunction = bounce });
    }
}
