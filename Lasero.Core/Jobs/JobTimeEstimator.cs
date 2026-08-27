using Lasero.Core.GCode;

namespace Lasero.Core.Jobs;

public sealed record MachineMotionProfile
{
    public double RapidSpeedMmPerMinute { get; init; } = 6000;
    public double DefaultWorkSpeedMmPerMinute { get; init; } = 1000;
    public TimeSpan CommandOverhead { get; init; } = TimeSpan.FromMilliseconds(3);
}

public sealed record JobTimeEstimate
{
    public required TimeSpan Duration { get; init; }
    public required IReadOnlyList<TimeSpan> SegmentStartTimes { get; init; }
    public required IReadOnlyList<TimeSpan> SegmentDurations { get; init; }

    /// <summary>
    /// Split of the same total into the two things that actually consume it: distance travelled with
    /// the laser working, and distance travelled repositioning between shapes. A bare total says a
    /// job takes twelve minutes; this says whether that is twelve minutes of burning or mostly the
    /// head flying around because the path order is poor.
    /// </summary>
    public required double CutDistanceMm { get; init; }
    public required TimeSpan CutDuration { get; init; }
    public required double RapidDistanceMm { get; init; }
    public required TimeSpan RapidDuration { get; init; }
}

/// <summary>Builds one timing model from the exact parsed toolpath used by preview and execution.</summary>
public static class JobTimeEstimator
{
    public static JobTimeEstimate Estimate(GCodeDocument document, MachineMotionProfile? profile = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        profile ??= new MachineMotionProfile();

        var starts = new List<TimeSpan>(document.Segments.Count);
        var durations = new List<TimeSpan>(document.Segments.Count);
        var elapsed = TimeSpan.Zero;
        var cutDistance = 0d;
        var rapidDistance = 0d;
        var cutTime = TimeSpan.Zero;
        var rapidTime = TimeSpan.Zero;

        foreach (var segment in document.Segments)
        {
            starts.Add(elapsed);
            var speed = segment.IsRapid
                ? profile.RapidSpeedMmPerMinute
                : segment.FeedRatePerMinute > 0
                    ? segment.FeedRatePerMinute
                    : profile.DefaultWorkSpeedMmPerMinute;
            speed = Math.Max(speed, 0.001);

            var travel = SegmentLength(segment);
            var duration = TimeSpan.FromMinutes(travel / speed) + profile.CommandOverhead;
            durations.Add(duration);
            elapsed += duration;

            if (segment.IsRapid)
            {
                rapidDistance += travel;
                rapidTime += duration;
            }
            else
            {
                cutDistance += travel;
                cutTime += duration;
            }
        }

        return new JobTimeEstimate
        {
            Duration = elapsed,
            SegmentStartTimes = starts,
            SegmentDurations = durations,
            CutDistanceMm = cutDistance,
            CutDuration = cutTime,
            RapidDistanceMm = rapidDistance,
            RapidDuration = rapidTime,
        };
    }

    internal static double SegmentLength(GCodeSegment segment)
    {
        if (!segment.IsArc || segment.ArcCenter is not { } center)
            return Distance(segment.Start.X, segment.Start.Y, segment.End.X, segment.End.Y);

        var radius = Distance(center.X, center.Y, segment.Start.X, segment.Start.Y);
        if (radius <= 1e-9) return 0;
        var start = Math.Atan2(segment.Start.Y - center.Y, segment.Start.X - center.X);
        var end = Math.Atan2(segment.End.Y - center.Y, segment.End.X - center.X);
        var sweep = end - start;
        if (segment.ArcClockwise)
        {
            if (sweep >= 0) sweep -= Math.PI * 2;
        }
        else if (sweep <= 0)
        {
            sweep += Math.PI * 2;
        }
        return Math.Abs(sweep) * radius;
    }

    private static double Distance(double x1, double y1, double x2, double y2)
    {
        var dx = x2 - x1;
        var dy = y2 - y1;
        return Math.Sqrt(dx * dx + dy * dy);
    }
}
