namespace Lasero.Core.Import;

public sealed record RasterImportOptions
{
    public double TargetWidthMm { get; init; } = 100;
    public double Dpi { get; init; } = 254; // matches lasero-app's own fill DPI default
    public double FeedRatePerMinute { get; init; } = 3000;
    public double MaxPower { get; init; } = 100;
    public int Passes { get; init; } = 1;

    /// <summary>true = binary on/off per pixel (threshold); false = continuous grayscale power (relies on M4 dynamic power).</summary>
    public bool UseThreshold { get; init; }
    public byte ThresholdValue { get; init; } = 128;

    /// <summary>Placement offset in mm — lets a raster SceneObject be moved on the canvas without
    /// re-scanning the bitmap. Width and optional height provide uniform or non-uniform scaling;
    /// rotation and mirroring are not supported by the raster planner.</summary>
    public double OffsetX { get; init; }
    public double OffsetY { get; init; }

    /// <summary>Power floor for pixels already marked for burning (0 = old behavior, full 0..MaxPower range).</summary>
    public double MinPower { get; init; }

    /// <summary>Additive brightness / photographic-contrast adjustment applied before tone mapping. 0 = no change.</summary>
    public double Brightness { get; init; }
    public double Contrast { get; init; }

    public bool Invert { get; init; }

    /// <summary>Deterministic Floyd-Steinberg dithering instead of continuous grayscale power. Takes
    /// precedence over UseThreshold when both are set.</summary>
    public bool UseDithering { get; init; }

    /// <summary>Null = aspect ratio preserved (height derived from TargetWidthMm and the image's own
    /// pixel aspect). Set explicitly to allow non-uniform scaling.</summary>
    public double? TargetHeightMm { get; init; }

    public double LineIntervalMm => 25.4 / Dpi;
}
