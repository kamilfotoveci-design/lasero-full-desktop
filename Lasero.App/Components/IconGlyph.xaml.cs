using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Lasero.App.Components;

/// <summary>Renders every Lasero icon on the same 24×24 grid and optical stroke.</summary>
public partial class IconGlyph : UserControl
{
    public static readonly DependencyProperty IconDataProperty = DependencyProperty.Register(
        nameof(IconData), typeof(Geometry), typeof(IconGlyph), new PropertyMetadata(null));

    public Geometry? IconData
    {
        get => (Geometry?)GetValue(IconDataProperty);
        set => SetValue(IconDataProperty, value);
    }

    public static readonly DependencyProperty IsFilledProperty = DependencyProperty.Register(
        nameof(IsFilled), typeof(bool), typeof(IconGlyph), new PropertyMetadata(false));

    /// <summary>True for an imported Phosphor-style filled silhouette; false (default) for this app's
    /// own hand-authored centerline-stroke icons.</summary>
    public bool IsFilled
    {
        get => (bool)GetValue(IsFilledProperty);
        set => SetValue(IsFilledProperty, value);
    }

    public static readonly DependencyProperty GridSizeProperty = DependencyProperty.Register(
        nameof(GridSize), typeof(double), typeof(IconGlyph), new PropertyMetadata(24.0));

    /// <summary>The square grid the geometry was authored on (24 for this app's own icons, 256 for
    /// Phosphor). The Viewbox scales it to whatever Width/Height the caller sets.</summary>
    public double GridSize
    {
        get => (double)GetValue(GridSizeProperty);
        set => SetValue(GridSizeProperty, value);
    }

    public IconGlyph() => InitializeComponent();
}
