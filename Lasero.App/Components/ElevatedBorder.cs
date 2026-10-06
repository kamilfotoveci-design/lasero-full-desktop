using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Lasero.App.Components;

public enum ElevationLevel
{
    /// <summary>Flat: hairline only.</summary>
    None,
    /// <summary>A card on the canvas: contact shadow plus soft ambient shadow.</summary>
    Card,
    /// <summary>A clickable card under the pointer: the same shadow, deeper.</summary>
    Raised,
}

/// <summary>
/// A <see cref="Border"/> that lifts off the canvas with a soft neutral shadow, without an Effect.
///
/// Why not DropShadowEffect: an Effect renders the element and everything inside it to an offscreen
/// surface, which turns ClearType text inside the card to grayscale anti-aliasing and costs a
/// full-card bitmap per repaint. This control instead draws the shadow itself in <see cref="OnRender"/>
/// as a nine-slice of gradient brushes (four edge strips, four corner quarter-discs) underneath its
/// own background. It lives in the retained render tree, so after the first pass it costs nothing,
/// the text stays crisp, and nothing is ever applied to the design canvas.
///
/// The numbers come from the <c>Elevation.Card.*</c> tokens in LaseroTheme.xaml (0 1px 2px at 6% plus
/// 0 8px 24px at 6%), read through <see cref="ElevationTokens"/> so the theme stays the one place to
/// change them. The shadow is only drawn outside the card's bounds, so the parent must leave room
/// for it (a ClipToBounds ancestor will crop it).
/// </summary>
public class ElevatedBorder : Border
{
    public static readonly DependencyProperty ElevationProperty = DependencyProperty.Register(
        nameof(Elevation), typeof(ElevationLevel), typeof(ElevatedBorder),
        new FrameworkPropertyMetadata(ElevationLevel.Card, FrameworkPropertyMetadataOptions.AffectsRender));

    public ElevationLevel Elevation
    {
        get => (ElevationLevel)GetValue(ElevationProperty);
        set => SetValue(ElevationProperty, value);
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (Elevation != ElevationLevel.None && ActualWidth > 0 && ActualHeight > 0)
        {
            var radius = CornerRadius.TopLeft;
            var raised = Elevation == ElevationLevel.Raised;
            foreach (var layer in ElevationTokens.CardLayers())
                ShadowPainter.Draw(dc, new Size(ActualWidth, ActualHeight), radius,
                    raised ? layer with { Opacity = layer.Opacity * 1.8, OffsetY = layer.OffsetY + 1 } : layer);
        }

        base.OnRender(dc);
    }
}

/// <summary>One shadow layer, CSS-style: offset Y, blur radius and opacity of the ink.</summary>
public readonly record struct ShadowLayer(double OffsetY, double Blur, double Opacity);

/// <summary>Reads the card elevation tokens from the theme, with the specified values as fallback.</summary>
public static class ElevationTokens
{
    public static readonly ShadowLayer CardContactDefault = new(1, 2, 0.06);
    public static readonly ShadowLayer CardAmbientDefault = new(8, 24, 0.06);

    public static IEnumerable<ShadowLayer> CardLayers()
    {
        yield return new ShadowLayer(
            Read("Elevation.Card.Contact.OffsetY", CardContactDefault.OffsetY),
            Read("Elevation.Card.Contact.Blur", CardContactDefault.Blur),
            Read("Elevation.Card.Contact.Opacity", CardContactDefault.Opacity));
        yield return new ShadowLayer(
            Read("Elevation.Card.Ambient.OffsetY", CardAmbientDefault.OffsetY),
            Read("Elevation.Card.Ambient.Blur", CardAmbientDefault.Blur),
            Read("Elevation.Card.Ambient.Opacity", CardAmbientDefault.Opacity));
    }

    private static double Read(string key, double fallback) =>
        Application.Current?.TryFindResource(key) is double d ? d : fallback;
}

/// <summary>Draws a Gaussian-looking drop shadow for a rounded rectangle using only gradient brushes.</summary>
internal static class ShadowPainter
{
    // Edges: top, bottom, left, right. Corners: top-left, top-right, bottom-left, bottom-right.
    private static readonly Dictionary<(double Radius, double Blur, double Opacity, double OffsetY), (Brush[] Edges, Brush[] Corners)> Cache = new();

    // Ink of the shadow: the theme's graphite, neutral. Never a coloured glow.
    private static readonly Color Ink = Color.FromRgb(0x1D, 0x1D, 0x1F);

    /// <summary>Shadow intensity (0..1 of the layer opacity) at signed distance d outside the edge.</summary>
    internal static double Falloff(double d, double blur)
    {
        var sigma = Math.Max(blur / 2.0, 0.01);
        // 1 - Phi(d / sigma), with Phi from the logistic approximation of the normal CDF.
        var x = d / sigma * 1.702;
        return 1.0 / (1.0 + Math.Exp(x));
    }

    public static void Draw(DrawingContext dc, Size size, double radius, ShadowLayer layer)
    {
        if (layer.Opacity <= 0 || layer.Blur <= 0) return;
        var w = size.Width;
        var h = size.Height;
        var r = Math.Min(radius, Math.Min(w, h) / 2);
        var ext = layer.Blur;
        var key = (Math.Round(r, 2), layer.Blur, layer.Opacity, layer.OffsetY);

        if (!Cache.TryGetValue(key, out var brushes))
        {
            brushes = (BuildEdges(ext, layer), BuildCorners(r, ext, layer));
            Cache[key] = brushes;
        }

        // The shadow body is the card rect moved down by OffsetY.
        var top = layer.OffsetY;
        var bottom = h + layer.OffsetY;
        var spanX = Math.Max(w - 2 * r, 0);
        var spanY = Math.Max(bottom - top - 2 * r, 0);

        dc.DrawRectangle(brushes.Edges[0], null, new Rect(r, top - ext, spanX, ext));
        // The bottom strip also covers the part of the shadow body that peeks out under the card
        // (the card ends at h, the shadow body at h + OffsetY), so it starts at the card edge.
        dc.DrawRectangle(brushes.Edges[1], null, new Rect(r, h, spanX, layer.OffsetY + ext));
        dc.DrawRectangle(brushes.Edges[2], null, new Rect(-ext, top + r, ext, spanY));
        dc.DrawRectangle(brushes.Edges[3], null, new Rect(w, top + r, ext, spanY));

        var s = r + ext;
        dc.DrawRectangle(brushes.Corners[0], null, new Rect(-ext, top - ext, s, s));
        dc.DrawRectangle(brushes.Corners[1], null, new Rect(w - r, top - ext, s, s));
        dc.DrawRectangle(brushes.Corners[2], null, new Rect(-ext, bottom - r, s, s));
        dc.DrawRectangle(brushes.Corners[3], null, new Rect(w - r, bottom - r, s, s));
    }

    private static GradientStopCollection EdgeStops(double ext, ShadowLayer layer)
    {
        // t = 0 is against the card edge, t = 1 is ext away.
        var stops = new GradientStopCollection();
        const int n = 8;
        for (var i = 0; i <= n; i++)
        {
            var t = (double)i / n;
            stops.Add(new GradientStop(StopColor(Falloff(t * ext, layer.Blur), layer.Opacity), t));
        }

        return stops;
    }

    private static Brush[] BuildEdges(double ext, ShadowLayer layer)
    {
        Brush Make(Point from, Point to)
        {
            var b = new LinearGradientBrush(EdgeStops(ext, layer), from, to);
            b.Freeze();
            return b;
        }

        Brush MakeBottom()
        {
            // t = 0 is the card's bottom edge, which is OffsetY inside the shadow body: d runs from
            // -OffsetY to +ext.
            var inner = layer.OffsetY;
            var stops = new GradientStopCollection();
            const int n = 10;
            for (var i = 0; i <= n; i++)
            {
                var t = (double)i / n;
                stops.Add(new GradientStop(StopColor(Falloff(t * (inner + ext) - inner, layer.Blur), layer.Opacity), t));
            }

            var b = new LinearGradientBrush(stops, new Point(0, 0), new Point(0, 1));
            b.Freeze();
            return b;
        }

        return new[]
        {
            Make(new Point(0, 1), new Point(0, 0)), // top strip, the card is below it
            MakeBottom(), // bottom, starts inside the shadow body
            Make(new Point(1, 0), new Point(0, 0)), // left
            Make(new Point(0, 0), new Point(1, 0)), // right
        };
    }

    private static Brush[] BuildCorners(double r, double ext, ShadowLayer layer)
    {
        // Distance rho from the circle centre maps to d = rho - r outside the rounded edge.
        var s = r + ext;
        var stops = new GradientStopCollection();
        const int n = 10;
        for (var i = 0; i <= n; i++)
        {
            var t = (double)i / n;
            stops.Add(new GradientStop(StopColor(Falloff(t * s - r, layer.Blur), layer.Opacity), t));
        }

        Brush Make(double cx, double cy)
        {
            var b = new RadialGradientBrush(stops.Clone())
            {
                Center = new Point(cx, cy),
                GradientOrigin = new Point(cx, cy),
                RadiusX = 1,
                RadiusY = 1,
            };
            b.Freeze();
            return b;
        }

        return new[] { Make(1, 1), Make(0, 1), Make(1, 0), Make(0, 0) };
    }

    private static Color StopColor(double intensity, double opacity) =>
        Color.FromArgb((byte)Math.Round(Math.Clamp(intensity * opacity, 0, 1) * 255), Ink.R, Ink.G, Ink.B);
}
