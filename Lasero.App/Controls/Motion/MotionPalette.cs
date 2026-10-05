using System.Windows;
using System.Windows.Media;

namespace Lasero.App.Controls.Motion;

/// <summary>
/// The only colours the brand motion uses: warm-white ground, graphite ink, one red accent, and two
/// warm neutrals for hairlines and the tagline. Defaults equal the shipped tokens (Brush.Background,
/// Brush.TextPrimary, Brush.Brand, Brush.TextSecondary, Brush.PanelBorderStrong); inside the app the
/// controls re-read those resources, so a future palette change follows automatically. The video and
/// installer renderers use the defaults.
/// </summary>
internal readonly record struct MotionPalette(Color Background, Color Ink, Color Brand, Color Muted, Color Hairline, Color Grid)
{
    public static readonly MotionPalette Default = new(
        Background: Color.FromRgb(0xF7, 0xF6, 0xF3),
        Ink: Color.FromRgb(0x18, 0x18, 0x18),
        Brand: Color.FromRgb(0xE4, 0x51, 0x3D),
        Muted: Color.FromRgb(0x5C, 0x5A, 0x54),
        Hairline: Color.FromRgb(0xD3, 0xCF, 0xC5),
        Grid: Color.FromRgb(0x8A, 0x85, 0x7C));

    /// <summary>Reads the theme tokens when they are available, falls back to <see cref="Default"/> per colour.</summary>
    public static MotionPalette FromResources(FrameworkElement element)
    {
        Color Pick(string key, Color fallback) =>
            element.TryFindResource(key) is SolidColorBrush b ? b.Color : fallback;
        var d = Default;
        return new MotionPalette(
            Pick("Brush.Background", d.Background),
            Pick("Brush.TextPrimary", d.Ink),
            Pick("Brush.Brand", d.Brand),
            Pick("Brush.TextSecondary", d.Muted),
            Pick("Brush.PanelBorderStrong", d.Hairline),
            d.Grid);
    }

    public Brush InkBrush(double alpha = 1) => Solid(Ink, alpha);
    public Brush BrandBrush(double alpha = 1) => Solid(Brand, alpha);

    public static SolidColorBrush Solid(Color c, double alpha = 1)
    {
        var brush = new SolidColorBrush(Color.FromArgb((byte)Math.Round(Math.Clamp(alpha, 0, 1) * c.A), c.R, c.G, c.B));
        brush.Freeze();
        return brush;
    }

    public Pen Stroke(Color c, double thickness, double alpha = 1, PenLineCap cap = PenLineCap.Flat)
    {
        var pen = new Pen(Solid(c, alpha), thickness) { StartLineCap = cap, EndLineCap = cap, LineJoin = PenLineJoin.Round };
        pen.Freeze();
        return pen;
    }
}

/// <summary>Easing curves. Everything in the brand motion is ease-out or a gentle sine in-out; there is
/// no bounce and no overshoot.</summary>
internal static class MotionEase
{
    public static double Clamp01(double v) => v < 0 ? 0 : v > 1 ? 1 : v;
    public static double Lerp(double a, double b, double t) => a + (b - a) * t;

    /// <summary>Progress of <paramref name="t"/> through [start, start+duration], clamped to 0..1.</summary>
    public static double Phase(double t, double start, double duration) =>
        duration <= 0 ? (t >= start ? 1 : 0) : Clamp01((t - start) / duration);

    public static double OutCubic(double t) { t = Clamp01(t); var u = 1 - t; return 1 - u * u * u; }
    public static double OutQuart(double t) { t = Clamp01(t); var u = 1 - t; return 1 - u * u * u * u; }
    public static double InOutSine(double t) => 0.5 - 0.5 * Math.Cos(Math.PI * Clamp01(t));
    public static double InOutCubic(double t)
    {
        t = Clamp01(t);
        return t < 0.5 ? 4 * t * t * t : 1 - Math.Pow(-2 * t + 2, 3) / 2;
    }
    public static double SmoothStep(double e0, double e1, double x)
    {
        var t = Clamp01((x - e0) / (e1 - e0));
        return t * t * (3 - 2 * t);
    }
}
