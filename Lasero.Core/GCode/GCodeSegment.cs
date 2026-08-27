using Lasero.Core.Grbl;

namespace Lasero.Core.GCode;

/// <summary>One resolved motion segment — a straight line or an arc, in machine work-coordinate units (mm).</summary>
public sealed record GCodeSegment
{
    public required Position Start { get; init; }
    public required Position End { get; init; }
    public required bool IsRapid { get; init; }
    public bool IsArc { get; init; }
    public Position? ArcCenter { get; init; }
    public bool ArcClockwise { get; init; }
    public required bool LaserOn { get; init; }
    public double Power { get; init; }
    public double FeedRatePerMinute { get; init; }
    public required int SourceLine { get; init; }
}
