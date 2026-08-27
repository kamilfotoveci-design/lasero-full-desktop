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

    public IconGlyph() => InitializeComponent();
}
