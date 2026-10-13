namespace Lasero.Core.Trace;

/// <summary>Which family of contours the legacy tracers look for. The app-facing pipeline
/// (<see cref="BitmapTracer"/>) no longer branches on this: there is one grayscale threshold trace.
/// The enum stays because the legacy Outline/Color engines still reference it.</summary>
public enum TraceMode
{
    FilledShapes,
    Outline,
    Color,
}

/// <summary>Legacy threshold selection, ignored by <see cref="BitmapTracer"/> (always a fixed threshold).</summary>
public enum ThresholdMode
{
    Auto,
    Manual,
    Adaptive,
}

/// <summary>Legacy working-resolution tier, ignored by <see cref="BitmapTracer"/>.</summary>
public enum TraceQuality
{
    Fast,
    Balanced,
    HighFidelity,
}

/// <summary>
/// Parameters of the bitmap trace. The user-facing set mirrors the LightBurn "Trace Image" dialog and
/// nothing else: <see cref="Cutoff"/>, <see cref="Threshold"/>, <see cref="IgnoreLessThanPixels"/>,
/// <see cref="Smoothness"/> and <see cref="OptimizeTolerance"/>. Everything marked legacy is kept only so
/// existing callers and tests keep compiling; the grayscale threshold tracer does not read it.
/// </summary>
public sealed record BitmapTraceOptions
{
    /// <summary>Brightness (0-255) at which ink and background separate. Pixels darker than this are
    /// ink (lighter than this when <see cref="Invert"/> is set). Default 128.</summary>
    public byte Threshold { get; init; } = 128;

    /// <summary>Pixels brighter than this are ignored (treated as background). 0 switches the cutoff off.
    /// Not applied together with <see cref="Invert"/>.</summary>
    public byte Cutoff { get; init; }

    /// <summary>Shapes whose area is below this many square pixels are dropped. Default 2.</summary>
    public double IgnoreLessThanPixels { get; init; } = 2;

    /// <summary>Legacy name of <see cref="IgnoreLessThanPixels"/>.</summary>
    public int MinimumFeaturePixels
    {
        get => (int)Math.Round(IgnoreLessThanPixels);
        init => IgnoreLessThanPixels = value;
    }

    /// <summary>Strength of the contour smoothing, 0 (none) to 4. Default 1.0. It sets the pre-blur of the
    /// brightness field, the arc-length smoothing of every contour and the corner detection window; sharp
    /// corners are detected first and stay sharp.</summary>
    public double Smoothness { get; init; } = 1.0;

    /// <summary>Largest distance, in source pixels, the fitted Bezier curves may deviate from the smoothed
    /// contour. Higher values merge curve segments into fewer nodes. Default 0.2.</summary>
    public double OptimizeTolerance { get; init; } = 0.2;

    /// <summary>Legacy name of <see cref="OptimizeTolerance"/>.</summary>
    public double SimplificationPixels
    {
        get => OptimizeTolerance;
        init => OptimizeTolerance = value;
    }

    /// <summary>Drops the contour that runs along the outer border of the image (a frame, or a background
    /// that reaches every edge) together with its inner edge; whatever sits inside the frame stays.</summary>
    public bool DeleteBoundary { get; init; }

    public double TargetWidthMm { get; init; } = 100;

    /// <summary>Traces light shapes on a dark background instead of the default dark ink on light paper.</summary>
    public bool Invert { get; init; }

    /// <summary>0 = only turns above 45 degrees are corners (default 0.5: 30 degrees), 1 = every turn above 15 degrees is a
    /// corner. Drives corner detection; the app keeps the default.</summary>
    public double Detail { get; init; } = 0.5;

    /// <summary>Legacy: honoured as an additional minimum shape area (0 = none, 1 = 60 square pixels).</summary>
    public double NoiseRemoval { get; init; } = 0;

    /// <summary>Legacy, ignored by <see cref="BitmapTracer"/>.</summary>
    public TraceMode Mode { get; init; } = TraceMode.FilledShapes;

    /// <summary>Legacy, ignored by <see cref="BitmapTracer"/>.</summary>
    public ThresholdMode ThresholdMode { get; init; } = ThresholdMode.Manual;

    /// <summary>Legacy, ignored by <see cref="BitmapTracer"/>.</summary>
    public double Contrast { get; init; }

    /// <summary>Legacy, ignored by <see cref="BitmapTracer"/>.</summary>
    public TraceQuality Quality { get; init; } = TraceQuality.Balanced;

    internal void Validate()
    {
        if (!double.IsFinite(IgnoreLessThanPixels) || IgnoreLessThanPixels < 0)
            throw new ArgumentOutOfRangeException(nameof(IgnoreLessThanPixels), "Ignore-less-than must be a finite non-negative value.");
        if (!double.IsFinite(OptimizeTolerance) || OptimizeTolerance < 0)
            throw new ArgumentOutOfRangeException(nameof(OptimizeTolerance), "Optimize must be a finite non-negative value.");
        if (!double.IsFinite(TargetWidthMm) || TargetWidthMm <= 0)
            throw new ArgumentOutOfRangeException(nameof(TargetWidthMm), "Target width must be a finite positive value.");
        if (!double.IsFinite(Detail) || Detail < 0 || Detail > 1)
            throw new ArgumentOutOfRangeException(nameof(Detail), "Detail must be between 0 and 1.");
        if (!double.IsFinite(Smoothness) || Smoothness < 0 || Smoothness > MaxSmoothness)
            throw new ArgumentOutOfRangeException(nameof(Smoothness), "Smoothness must be between 0 and 4.");
        if (!double.IsFinite(NoiseRemoval) || NoiseRemoval < 0 || NoiseRemoval > 1)
            throw new ArgumentOutOfRangeException(nameof(NoiseRemoval), "Noise removal must be between 0 and 1.");
        if (!double.IsFinite(Contrast) || Contrast < -1 || Contrast > 1)
            throw new ArgumentOutOfRangeException(nameof(Contrast), "Contrast must be between -1 and 1.");
    }

    public const double MaxSmoothness = 4;

    // -------------------------------------------------------------------------------------------
    // Internal knob -> algorithm-parameter mapping, kept next to the knobs so "what does Smoothness
    // actually do" has one answer. Nothing below is part of the public contract.
    // -------------------------------------------------------------------------------------------

    /// <summary>Contrast stretch factor used by the legacy contour extractor.</summary>
    internal double ContrastAlpha => 1.0 + Contrast;

    /// <summary>Corner-detection turning-angle threshold, in degrees. Lower means more sensitive.</summary>
    internal double CornerAngleThresholdDegrees => Lerp(45, 15, Detail);

    /// <summary>Points closer together than this (source pixels) are collapsed before curve fitting.</summary>
    internal const double DedupeDistancePx = 0.5;

    /// <summary>Max perpendicular error, in source pixels, a fitted Bezier segment may have against the
    /// smoothed contour before it is split. A small floor avoids recursive fitting toward zero error.</summary>
    internal double FitToleranceBasePx => Math.Max(MinimumFitTolerancePx, OptimizeTolerance);

    /// <summary>Smallest effective fit tolerance in source pixels: below this the fit would
    /// chase the residue of a pixel staircase and keep a node on every step.</summary>
    internal const double MinimumFitTolerancePx = 0.1;

    /// <summary>Minimum contour area in square source pixels below which a shape is treated as noise.</summary>
    internal double MinimumContourAreaPx => Math.Max(IgnoreLessThanPixels, NoiseRemoval * 60);

    /// <summary>Longest side, in pixels, the tracer works at. Larger sources are downscaled, smaller ones are
    /// never upscaled: the sub-pixel contour already carries the precision an upscale would only imitate.</summary>
    internal const int MaxWorkingLongSidePx = 3200;

    /// <summary>Width the source is decoded at so that its longer side stays within
    /// <see cref="MaxWorkingLongSidePx"/>.</summary>
    internal static int WorkingWidthPx(int sourceWidthPx, int sourceHeightPx)
    {
        var longSide = Math.Max(sourceWidthPx, sourceHeightPx);
        if (longSide <= MaxWorkingLongSidePx) return sourceWidthPx;
        return Math.Max(1, (int)Math.Round(sourceWidthPx * (MaxWorkingLongSidePx / (double)longSide)));
    }

    private static double Lerp(double from, double to, double t) => from + (to - from) * Math.Clamp(t, 0, 1);
}
