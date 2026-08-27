namespace Lasero.Core.Trace;

/// <summary>Parameters used when converting a bitmap into closed vector contours.</summary>
public sealed record BitmapTraceOptions
{
    public byte Threshold { get; init; } = 128;
    public int MinimumFeaturePixels { get; init; } = 8;
    public double SimplificationPixels { get; init; } = 1.2;
    public double TargetWidthMm { get; init; } = 100;
    public bool Invert { get; init; }

    internal void Validate()
    {
        if (MinimumFeaturePixels < 1)
            throw new ArgumentOutOfRangeException(nameof(MinimumFeaturePixels), "Minimum feature size must be at least one pixel.");
        if (!double.IsFinite(SimplificationPixels) || SimplificationPixels < 0)
            throw new ArgumentOutOfRangeException(nameof(SimplificationPixels), "Simplification must be a finite non-negative value.");
        if (!double.IsFinite(TargetWidthMm) || TargetWidthMm <= 0)
            throw new ArgumentOutOfRangeException(nameof(TargetWidthMm), "Target width must be a finite positive value.");
    }
}
