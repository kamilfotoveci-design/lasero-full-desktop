using Lasero.Core.Layers;

namespace Lasero.Core.Jobs;

/// <summary>
/// "Stínovat podle výkonu": a cutting move is drawn lighter the weaker the beam is on it. Power is bucketed into
/// a few levels so the preview can reuse one colour per operation and level instead of one per segment.
/// </summary>
public static class PowerShading
{
    public const int Levels = 8;

    /// <summary>How much of the operation colour the weakest level keeps; weaker would vanish on the canvas.</summary>
    public const double WeakestWeight = 0.25;

    /// <summary>Beam strength as a 0..1 share of the controller maximum. An unknown maximum counts as full power.</summary>
    public static double Fraction(double power, double maxPower)
    {
        if (!double.IsFinite(power) || !double.IsFinite(maxPower) || maxPower <= 0) return 1;
        return Math.Clamp(power / maxPower, 0, 1);
    }

    /// <summary>The bucket (1..<see cref="Levels"/>) for a 0..1 share; any beam at all is at least level 1.</summary>
    public static int Level(double fraction)
    {
        if (double.IsNaN(fraction)) return Levels;
        return Math.Clamp((int)Math.Ceiling(Math.Clamp(fraction, 0, 1) * Levels), 1, Levels);
    }

    /// <summary>The share of the operation colour kept at a level: <see cref="WeakestWeight"/> at 1, the full colour at the top.</summary>
    public static double Weight(int level)
    {
        var clamped = Math.Clamp(level, 1, Levels);
        return WeakestWeight + (1 - WeakestWeight) * (clamped - 1) / (Levels - 1);
    }

    /// <summary>The colour of a move at <paramref name="level"/>: the operation colour faded toward the canvas.</summary>
    public static RgbColor Shade(RgbColor color, RgbColor background, int level) =>
        PreviewPalette.Blend(background, color, Weight(level));
}

/// <summary>Colour helpers for the preview canvas, free of any UI types.</summary>
public static class PreviewPalette
{
    public static readonly RgbColor Graphite = new(29, 29, 31);
    public static readonly RgbColor White = new(255, 255, 255);

    /// <summary>Rapid moves ("Přesunout"): a neutral grey that reads on both the light and the inverted canvas.</summary>
    public static readonly RgbColor Travel = new(142, 142, 147);

    /// <summary>Perceived brightness 0..1 (Rec. 709 weights on the sRGB values).</summary>
    public static double Luminance(RgbColor color) => (0.2126 * color.R + 0.7152 * color.G + 0.0722 * color.B) / 255.0;

    /// <summary>
    /// The operation colour, moved toward the opposite pole when it would be lost on the canvas: a pale yellow
    /// layer on a white canvas, or a black layer on the inverted dark one.
    /// </summary>
    public static RgbColor Visible(RgbColor color, bool darkBackground)
    {
        var luminance = Luminance(color);
        if (!darkBackground && luminance > 0.78) return Blend(color, Graphite, 0.6);
        if (darkBackground && luminance < 0.22) return Blend(color, White, 0.6);
        return color;
    }

    /// <summary><paramref name="amount"/> 0 gives <paramref name="from"/>, 1 gives <paramref name="to"/>.</summary>
    public static RgbColor Blend(RgbColor from, RgbColor to, double amount)
    {
        var t = Math.Clamp(double.IsNaN(amount) ? 0 : amount, 0, 1);
        return new RgbColor(
            (byte)Math.Round(from.R + (to.R - from.R) * t),
            (byte)Math.Round(from.G + (to.G - from.G) * t),
            (byte)Math.Round(from.B + (to.B - from.B) * t));
    }
}
