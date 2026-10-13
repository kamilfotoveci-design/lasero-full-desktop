using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Lasero.App.Components;

/// <summary>Renders LASERO outline icons and size-specific filled artwork through one control.</summary>
public partial class IconGlyph : UserControl
{
    public static readonly DependencyProperty IconDataProperty = DependencyProperty.Register(
        nameof(IconData), typeof(Geometry), typeof(IconGlyph),
        new PropertyMetadata(null, (d, _) => ((IconGlyph)d).ResolveArtwork()));

    public Geometry? IconData
    {
        get => (Geometry?)GetValue(IconDataProperty);
        set => SetValue(IconDataProperty, value);
    }

    public static readonly DependencyProperty IsFilledProperty = DependencyProperty.Register(
        nameof(IsFilled), typeof(bool), typeof(IconGlyph),
        new PropertyMetadata(false, (d, _) => ((IconGlyph)d).ResolveArtwork()));

    /// <summary>Explicit fill mode for imported art; Fluent resources opt in via attached metadata.</summary>
    public bool IsFilled
    {
        get => (bool)GetValue(IsFilledProperty);
        set => SetValue(IsFilledProperty, value);
    }

    public static readonly DependencyProperty GridSizeProperty = DependencyProperty.Register(
        nameof(GridSize), typeof(double), typeof(IconGlyph),
        new PropertyMetadata(24.0, (d, _) => ((IconGlyph)d).ResolveArtwork()));

    public static readonly DependencyProperty ResolvedIconDataProperty = DependencyProperty.Register(
        nameof(ResolvedIconData), typeof(Geometry), typeof(IconGlyph), new PropertyMetadata(null));

    public static readonly DependencyProperty ResolvedIsFilledProperty = DependencyProperty.Register(
        nameof(ResolvedIsFilled), typeof(bool), typeof(IconGlyph), new PropertyMetadata(false));

    public static readonly DependencyProperty ResolvedGridSizeProperty = DependencyProperty.Register(
        nameof(ResolvedGridSize), typeof(double), typeof(IconGlyph), new PropertyMetadata(24.0));

    public Geometry? ResolvedIconData
    {
        get => (Geometry?)GetValue(ResolvedIconDataProperty);
        private set => SetValue(ResolvedIconDataProperty, value);
    }

    public bool ResolvedIsFilled
    {
        get => (bool)GetValue(ResolvedIsFilledProperty);
        private set => SetValue(ResolvedIsFilledProperty, value);
    }

    public double ResolvedGridSize
    {
        get => (double)GetValue(ResolvedGridSizeProperty);
        private set => SetValue(ResolvedGridSizeProperty, value);
    }

    /// <summary>The square grid the geometry was authored on (24 for this app's own icons, 256 for
    /// Phosphor). The Viewbox scales it to whatever Width/Height the caller sets.</summary>
    public double GridSize
    {
        get => (double)GetValue(GridSizeProperty);
        set => SetValue(GridSizeProperty, value);
    }

    public IconGlyph()
    {
        InitializeComponent();
        SizeChanged += (_, _) => ResolveArtwork();
        ResolveArtwork();
    }

    private void ResolveArtwork()
    {
        var source = IconData;
        if (source is null)
        {
            ResolvedIconData = null;
            ResolvedIsFilled = IsFilled;
            ResolvedGridSize = GridSize;
            return;
        }

        var isFluent = IconArtwork.GetIsFilled(source);
        var artwork = source;
        if (isFluent)
        {
            var requestedSize = ActualWidth > 0 ? ActualWidth : Width;
            if (double.IsNaN(requestedSize) || requestedSize <= 0)
                requestedSize = GridSize;

            if (requestedSize <= 18)
                artwork = IconArtwork.GetVariant16(source) ?? IconArtwork.GetVariant20(source) ?? source;
            else if (requestedSize <= 22)
                artwork = IconArtwork.GetVariant20(source) ?? source;
        }

        ResolvedIconData = artwork;
        ResolvedIsFilled = IsFilled || isFluent;
        ResolvedGridSize = isFluent ? IconArtwork.GetNativeSize(artwork) : GridSize;
    }
}
