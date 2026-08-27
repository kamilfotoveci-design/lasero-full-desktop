using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Lasero.App.Components;

/// <summary>Consistent icon-plus-label composition for commands and navigation.</summary>
public partial class IconLabel : UserControl
{
    public static readonly DependencyProperty IconDataProperty = DependencyProperty.Register(
        nameof(IconData), typeof(Geometry), typeof(IconLabel), new PropertyMetadata(null));
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(IconLabel), new PropertyMetadata(string.Empty));
    public static readonly DependencyProperty IconSizeProperty = DependencyProperty.Register(
        nameof(IconSize), typeof(double), typeof(IconLabel), new PropertyMetadata(16d));
    public static readonly DependencyProperty TextMarginProperty = DependencyProperty.Register(
        nameof(TextMargin), typeof(Thickness), typeof(IconLabel), new PropertyMetadata(new Thickness(8, 0, 0, 0)));

    /// <summary>
    /// Hides the label, leaving the icon. Set through a Style trigger rather than locally at each
    /// call site — Text is assigned locally in XAML, and a local value outranks a Style setter, so
    /// a width-driven trigger cannot blank it out that way.
    /// </summary>
    public static readonly DependencyProperty CompactModeProperty = DependencyProperty.Register(
        nameof(CompactMode), typeof(bool), typeof(IconLabel), new PropertyMetadata(false));

    public Geometry? IconData
    {
        get => (Geometry?)GetValue(IconDataProperty);
        set => SetValue(IconDataProperty, value);
    }

    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public double IconSize
    {
        get => (double)GetValue(IconSizeProperty);
        set => SetValue(IconSizeProperty, value);
    }

    public Thickness TextMargin
    {
        get => (Thickness)GetValue(TextMarginProperty);
        set => SetValue(TextMarginProperty, value);
    }

    public bool CompactMode
    {
        get => (bool)GetValue(CompactModeProperty);
        set => SetValue(CompactModeProperty, value);
    }

    public IconLabel() => InitializeComponent();
}
