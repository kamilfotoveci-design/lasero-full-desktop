namespace Lasero.Core.Jobs;

public sealed record FramingOptions
{
    public double FeedRatePerMinute { get; init; } = 3000;
    public FramingMode Mode { get; init; } = FramingMode.FullOutline;

    /// <summary>Power (percent of the controller maximum S) for the visible framing dot.</summary>
    public const double VisiblePowerPercent = 1;

    /// <summary>0 = laser stays off (pure position check). &gt;0 = visible low-power dot while tracing.</summary>
    public double LaserPower { get; init; } = 0;

    /// <summary>Length (mm) of each corner stub in CornersOnly mode.</summary>
    public double CornerStubLength { get; init; } = 10;
}
