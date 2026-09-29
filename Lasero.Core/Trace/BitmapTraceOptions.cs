namespace Lasero.Core.Trace;

/// <summary>Which family of contours BitmapTracer looks for.</summary>
public enum TraceMode
{
    /// <summary>Traces filled regions (ink/shape vs. background), producing closed compound paths
    /// with correctly wound holes — the LightBurn/Illustrator "Image Trace" default.</summary>
    FilledShapes,

    /// <summary>Traces edges/contours in the source photo instead of solid regions — for line-art or
    /// engraving-guide style output. Simpler pipeline, no hole/compound-path handling.</summary>
    Outline,

    /// <summary>Quantizes a color graphic into distinct editable filled regions and layers.</summary>
    Color,
}

/// <summary>How FilledShapes mode separates ink from background before contour extraction.</summary>
public enum ThresholdMode
{
    /// <summary>A single global threshold picked automatically from the image histogram (Otsu).</summary>
    Auto,

    /// <summary>The single global threshold in <see cref="BitmapTraceOptions.Threshold"/>.</summary>
    Manual,

    /// <summary>A per-region threshold that follows local brightness — better for scans/photos with
    /// uneven lighting than one global cut.</summary>
    Adaptive,
}

/// <summary>Working resolution tier — higher fidelity costs more CPU time on large source images.</summary>
public enum TraceQuality
{
    Fast,
    Balanced,
    HighFidelity,
}

/// <summary>Parameters used when converting a bitmap into vector contours. The original fields
/// (Threshold/MinimumFeaturePixels/SimplificationPixels/TargetWidthMm/Invert) remain the Manual-mode
/// defaults so existing callers keep working unchanged; everything below them is the user-facing
/// knob set for the curve-fitting pipeline. None of these properties name a raw OpenCV/curve-fitting
/// parameter directly — see the internal mapping members further down for that translation, which is
/// deliberately kept out of the public surface the UI will eventually bind to.</summary>
public sealed record BitmapTraceOptions
{
    public byte Threshold { get; init; } = 128;
    /// <summary>Minimum contour area in working-resolution pixels squared.</summary>
    public int MinimumFeaturePixels { get; init; } = 2;
    public double SimplificationPixels { get; init; } = 0.4;
    public double TargetWidthMm { get; init; } = 100;
    public bool Invert { get; init; }

    public TraceMode Mode { get; init; } = TraceMode.FilledShapes;
    public ThresholdMode ThresholdMode { get; init; } = ThresholdMode.Manual;

    /// <summary>0 = only the broadest strokes (soft corners, few nodes), 1 = preserve every sharp
    /// corner and fine feature. Drives corner-detection sensitivity during curve fitting.</summary>
    public double Detail { get; init; } = 0.5;

    /// <summary>0 = hug the traced pixels tightly (more nodes), 1 = looser fit, fewer/smoother nodes.
    /// Drives the Bezier-fit error tolerance.</summary>
    public double Smoothness { get; init; } = 0.4;

    /// <summary>0 = keep every speck, 1 = aggressively drop small components. Drives the minimum
    /// contour-area filter; supersedes <see cref="MinimumFeaturePixels"/> as the user-facing knob,
    /// but both are honoured (whichever removes more wins) so existing callers/tests are unaffected.</summary>
    public double NoiseRemoval { get; init; } = 0;

    /// <summary>-1 (flatten) .. 0 (unchanged) .. 1 (punch up) contrast applied before thresholding.</summary>
    public double Contrast { get; init; }

    /// <summary>Working-resolution tier. Higher fidelity keeps more source detail at the cost of more
    /// CPU time; see <see cref="WorkingWidthPx"/> for the concrete pixel caps.</summary>
    public TraceQuality Quality { get; init; } = TraceQuality.Balanced;

    internal void Validate()
    {
        if (MinimumFeaturePixels < 1)
            throw new ArgumentOutOfRangeException(nameof(MinimumFeaturePixels), "Minimum feature size must be at least one pixel.");
        if (!double.IsFinite(SimplificationPixels) || SimplificationPixels < 0)
            throw new ArgumentOutOfRangeException(nameof(SimplificationPixels), "Simplification must be a finite non-negative value.");
        if (!double.IsFinite(TargetWidthMm) || TargetWidthMm <= 0)
            throw new ArgumentOutOfRangeException(nameof(TargetWidthMm), "Target width must be a finite positive value.");
        if (!double.IsFinite(Detail) || Detail < 0 || Detail > 1)
            throw new ArgumentOutOfRangeException(nameof(Detail), "Detail must be between 0 and 1.");
        if (!double.IsFinite(Smoothness) || Smoothness < 0 || Smoothness > 1)
            throw new ArgumentOutOfRangeException(nameof(Smoothness), "Smoothness must be between 0 and 1.");
        if (!double.IsFinite(NoiseRemoval) || NoiseRemoval < 0 || NoiseRemoval > 1)
            throw new ArgumentOutOfRangeException(nameof(NoiseRemoval), "Noise removal must be between 0 and 1.");
        if (!double.IsFinite(Contrast) || Contrast < -1 || Contrast > 1)
            throw new ArgumentOutOfRangeException(nameof(Contrast), "Contrast must be between -1 and 1.");
    }

    // -------------------------------------------------------------------------------------------
    // Internal knob -> algorithm-parameter mapping. Kept here, next to the knobs themselves, rather
    // than scattered across ContourExtractor/BezierFitter, so the "what does Detail actually do"
    // question always has one answer. Nothing below is part of the public contract.
    // -------------------------------------------------------------------------------------------

    /// <summary>Contrast stretch factor fed to a linear brightness remap before thresholding
    /// (alpha in `dst = alpha * src + beta`, beta stays 0).</summary>
    internal double ContrastAlpha => 1.0 + Contrast;

    /// <summary>Corner-detection turning-angle threshold, in degrees, passed to BezierFitter — lower
    /// means "more sensitive", i.e. more points get forced into sharp corners.</summary>
    internal double CornerAngleThresholdDegrees => Lerp(70, 20, Detail);

    /// <summary>Points closer together than this (in working-resolution pixels) are collapsed to one
    /// before fitting — FindContours' ApproxNone gives one point per pixel step, far denser than any
    /// curve fit needs.</summary>
    internal const double DedupeDistancePx = 0.5;

    /// <summary>Max perpendicular error, in working-resolution pixels, a fitted Bezier segment may
    /// have against the source polyline before BezierFitter splits and refits. Zero requests the
    /// most detailed fit, with a small floor to avoid recursive fitting toward zero error. Contours
    /// are extracted at subpixel resolution, so this remains a source-pixel tolerance.</summary>
    internal double FitToleranceBasePx => Math.Max(0.2, SimplificationPixels);

    /// <summary>Minimum contour area, in working-resolution pixels, below which a component is
    /// treated as noise and dropped. Honours whichever of NoiseRemoval/MinimumFeaturePixels is more
    /// aggressive so legacy callers that only ever set MinimumFeaturePixels keep working.</summary>
    internal double MinimumContourAreaPx => Math.Max(MinimumFeaturePixels, NoiseRemoval * 60);

    /// <summary>Longest-side pixel cap for the resolution tracing actually runs at, downscaled from
    /// the source image when it is larger (never upscaled).</summary>
    internal int WorkingWidthPx(int sourceWidthPx) => Math.Min(sourceWidthPx, Quality switch
    {
        TraceQuality.Fast => 1000,
        TraceQuality.HighFidelity => 4000,
        _ => 1700,
    });

    private static double Lerp(double from, double to, double t) => from + (to - from) * Math.Clamp(t, 0, 1);
}
