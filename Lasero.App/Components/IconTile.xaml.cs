using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Lasero.App.Components;

/// <summary>The tile palette. Each name maps to a <c>Brush.Tile.*</c> token in LaseroTheme.xaml.</summary>
public enum TileTone
{
    Graphite,
    Red,
    Orange,
    Amber,
    Green,
    Teal,
    Blue,
    Indigo,
    /// <summary>Material categories only: leather, saddle brown.</summary>
    Brown,
    /// <summary>Material categories only: metal marking, cool steel.</summary>
    Steel,
    /// <summary>Material categories only: paper, neutral gray.</summary>
    Gray,
}

/// <summary>A 28px rounded-square tile with a white line icon on a muted, saturated fill.</summary>
public partial class IconTile : UserControl
{
    public static readonly DependencyProperty IconDataProperty = DependencyProperty.Register(
        nameof(IconData), typeof(Geometry), typeof(IconTile), new PropertyMetadata(null));

    public static readonly DependencyProperty ToneProperty = DependencyProperty.Register(
        nameof(Tone), typeof(TileTone), typeof(IconTile),
        new PropertyMetadata(TileTone.Graphite, (d, _) => ((IconTile)d).ApplyTone()));

    public static readonly DependencyProperty SizeProperty = DependencyProperty.Register(
        nameof(Size), typeof(double), typeof(IconTile),
        new PropertyMetadata(28.0, (d, e) => ((IconTile)d).GlyphSize = Math.Round((double)e.NewValue * 4 / 7)));

    public static readonly DependencyProperty GlyphSizeProperty = DependencyProperty.Register(
        nameof(GlyphSize), typeof(double), typeof(IconTile), new PropertyMetadata(16.0));

    public IconTile()
    {
        InitializeComponent();
        ApplyTone();
    }

    public Geometry? IconData
    {
        get => (Geometry?)GetValue(IconDataProperty);
        set => SetValue(IconDataProperty, value);
    }

    public TileTone Tone
    {
        get => (TileTone)GetValue(ToneProperty);
        set => SetValue(ToneProperty, value);
    }

    /// <summary>Edge length in DIPs. 28 is the standard; the glyph is 4/7 of it (16).</summary>
    public double Size
    {
        get => (double)GetValue(SizeProperty);
        set => SetValue(SizeProperty, value);
    }

    public double GlyphSize
    {
        get => (double)GetValue(GlyphSizeProperty);
        private set => SetValue(GlyphSizeProperty, value);
    }

    /// <summary>The resource key of the tile fill for a tone.</summary>
    public static string BrushKey(TileTone tone) => "Brush.Tile." + tone;

    private void ApplyTone() =>
        Tile.SetResourceReference(Border.BackgroundProperty, BrushKey(Tone));
}
