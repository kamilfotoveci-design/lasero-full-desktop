namespace Lasero.Core.Jobs;

public sealed record FramingOptions
{
    public double FeedRatePerMinute { get; init; } = 3000;
    public FramingMode Mode { get; init; } = FramingMode.FullOutline;

    /// <summary>Power (percent of the controller maximum S) for the visible framing dot.</summary>
    public const double VisiblePowerPercent = 1;

    /// <summary>Upper bound for the operator-chosen framing power. The framing dot only has to be visible;
    /// anything above this can mark the material while the head is just checking placement.</summary>
    public const double MaxVisiblePowerPercent = 10;

    /// <summary>Clamps a stored framing power to 0 (laser stays off) .. <see cref="MaxVisiblePowerPercent"/>;
    /// a non-finite value falls back to the default dot.</summary>
    public static double ClampVisiblePower(double percent) =>
        double.IsFinite(percent) ? Math.Clamp(percent, 0, MaxVisiblePowerPercent) : VisiblePowerPercent;

    /// <summary>0 = laser stays off (pure position check). &gt;0 = visible low-power dot while tracing.</summary>
    public double LaserPower { get; init; } = 0;

    /// <summary>Length (mm) of each corner stub in CornersOnly mode.</summary>
    public double CornerStubLength { get; init; } = 10;
}
