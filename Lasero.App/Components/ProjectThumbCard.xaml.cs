using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Lasero.App.Components;

/// <summary>One recent-project card on the Home dashboard. The commands live on the owning view model
/// (HomeViewModel), so they arrive as dependency properties while the DataContext stays the row.</summary>
public partial class ProjectThumbCard : UserControl
{
    public static readonly DependencyProperty OpenCommandProperty = DependencyProperty.Register(
        nameof(OpenCommand), typeof(ICommand), typeof(ProjectThumbCard), new PropertyMetadata(null));

    public static readonly DependencyProperty RemoveCommandProperty = DependencyProperty.Register(
        nameof(RemoveCommand), typeof(ICommand), typeof(ProjectThumbCard), new PropertyMetadata(null));

    private readonly SolidColorBrush _surfaceBorderBrush = new(Colors.Transparent);

    public ICommand? OpenCommand
    {
        get => (ICommand?)GetValue(OpenCommandProperty);
        set => SetValue(OpenCommandProperty, value);
    }

    public ICommand? RemoveCommand
    {
        get => (ICommand?)GetValue(RemoveCommandProperty);
        set => SetValue(RemoveCommandProperty, value);
    }

    public ProjectThumbCard()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    /// <summary>Same reduced-motion check KamilAssistantHost and DeviceWizardOverlay already
    /// established, applied here rather than inventing a second one.</summary>
    private static bool AnimationsEnabled => SystemParameters.ClientAreaAnimation
        && RenderCapability.Tier > 0;

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _surfaceBorderBrush.Color = ResolveColor("Brush.PanelBorder", Color.FromRgb(0xE4, 0xE5, 0xE2));
        Surface.BorderBrush = _surfaceBorderBrush;
    }

    private void OnSurfaceMouseEnter(object sender, MouseEventArgs e) => AnimateHover(hovering: true);

    private void OnSurfaceMouseLeave(object sender, MouseEventArgs e) => AnimateHover(hovering: false);

    /// <summary>Clickable-card hover feedback: a border-colour transition toward the accent plus a
    /// -1 DIP lift, in place of the old instant BorderBrush swap. Kept off Width/Height/Margin per the
    /// performance rule - only Color and a TranslateTransform move.</summary>
    private void AnimateHover(bool hovering)
    {
        var targetColor = hovering
            ? ResolveColor("Brush.Accent", Color.FromRgb(0x25, 0x63, 0xEB))
            : ResolveColor("Brush.PanelBorder", Color.FromRgb(0xE4, 0xE5, 0xE2));

        if (!AnimationsEnabled)
        {
            _surfaceBorderBrush.BeginAnimation(SolidColorBrush.ColorProperty, null);
            SurfaceOffset.BeginAnimation(TranslateTransform.YProperty, null);
            _surfaceBorderBrush.Color = targetColor;
            SurfaceOffset.Y = hovering ? -1 : 0;
            return;
        }

        var duration = (Duration)FindResource("Motion.Fast");
        var ease = (CubicEase)FindResource("Ease.Out");

        _surfaceBorderBrush.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation
        {
            To = targetColor, Duration = duration, EasingFunction = ease, FillBehavior = FillBehavior.HoldEnd,
        });
        SurfaceOffset.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation
        {
            To = hovering ? -1 : 0, Duration = duration, EasingFunction = ease, FillBehavior = FillBehavior.HoldEnd,
        });
    }

    private Color ResolveColor(string resourceKey, Color fallback) =>
        TryFindResource(resourceKey) is SolidColorBrush brush ? brush.Color : fallback;
}
