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

    /// <summary>Puts the label under the icon instead of beside it — the toolbar arrangement, where a
    /// row of named tools has to stay narrow. Composes with CompactMode: a stacked button that runs
    /// out of width still drops to its icon alone.</summary>
    public static readonly DependencyProperty StackedProperty = DependencyProperty.Register(
        nameof(Stacked), typeof(bool), typeof(IconLabel), new PropertyMetadata(false));

    /// <summary>Forwarded to the inner IconGlyph — see IconGlyph.IsFilled. Default false leaves every
    /// existing IconLabel call site unchanged.</summary>
    public static readonly DependencyProperty IsFilledProperty = DependencyProperty.Register(
        nameof(IsFilled), typeof(bool), typeof(IconLabel), new PropertyMetadata(false));

    /// <summary>Forwarded to the inner IconGlyph — see IconGlyph.GridSize.</summary>
    public static readonly DependencyProperty GridSizeProperty = DependencyProperty.Register(
        nameof(GridSize), typeof(double), typeof(IconLabel), new PropertyMetadata(24.0));

    public bool IsFilled
    {
        get => (bool)GetValue(IsFilledProperty);
        set => SetValue(IsFilledProperty, value);
    }

    public double GridSize
    {
        get => (double)GetValue(GridSizeProperty);
        set => SetValue(GridSizeProperty, value);
    }

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

    public bool Stacked
    {
        get => (bool)GetValue(StackedProperty);
        set => SetValue(StackedProperty, value);
    }

    public IconLabel() => InitializeComponent();
}
