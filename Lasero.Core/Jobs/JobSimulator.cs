using Lasero.Core.GCode;
using Lasero.Core.Grbl;

namespace Lasero.Core.Jobs;

public sealed record JobSimulationState
{
    public required Position LaserPosition { get; init; }
    public required int CompletedSegments { get; init; }
    public required double Progress { get; init; }
    public required TimeSpan Elapsed { get; init; }
    public required TimeSpan Duration { get; init; }
}

public static class JobSimulator
{
    public static JobSimulationState GetStateAtTime(GCodeDocument document, JobTimeEstimate estimate, TimeSpan time)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(estimate);
        if (document.Segments.Count != estimate.SegmentDurations.Count)
            throw new ArgumentException("Časový model neodpovídá zadané dráze.", nameof(estimate));

        var elapsed = time < TimeSpan.Zero ? TimeSpan.Zero : time > estimate.Duration ? estimate.Duration : time;
        if (document.Segments.Count == 0)
            return new JobSimulationState
            {
                LaserPosition = Position.Zero,
                CompletedSegments = 0,
                Progress = estimate.Duration == TimeSpan.Zero ? 1 : 0,
                Elapsed = elapsed,
                Duration = estimate.Duration,
            };

        if (elapsed < estimate.Duration)
        {
            var index = FindActiveSegment(estimate, elapsed);
            var start = estimate.SegmentStartTimes[index];
            var duration = estimate.SegmentDurations[index];
            var fraction = duration <= TimeSpan.Zero
                ? 1
                : Math.Clamp((elapsed - start).TotalSeconds / duration.TotalSeconds, 0, 1);
            var segment = document.Segments[index];
            return new JobSimulationState
            {
                LaserPosition = Interpolate(segment, fraction),
                CompletedSegments = index,
                Progress = estimate.Duration <= TimeSpan.Zero ? 1 : elapsed.TotalSeconds / estimate.Duration.TotalSeconds,
                Elapsed = elapsed,
                Duration = estimate.Duration,
            };
        }

        return new JobSimulationState
        {
            LaserPosition = document.Segments[^1].End,
            CompletedSegments = document.Segments.Count,
            Progress = 1,
            Elapsed = elapsed,
            Duration = estimate.Duration,
        };
    }

    private static int FindActiveSegment(JobTimeEstimate estimate, TimeSpan elapsed)
    {
        // Upper-bound binary search: simulation ticks no longer scan the complete path from
        // segment zero. This keeps seeking and playback responsive even for dense raster jobs.
        var low = 0;
        var high = estimate.SegmentStartTimes.Count;
        while (low < high)
        {
            var middle = low + (high - low) / 2;
            if (estimate.SegmentStartTimes[middle] <= elapsed) low = middle + 1;
            else high = middle;
        }

        var index = Math.Clamp(low - 1, 0, estimate.SegmentStartTimes.Count - 1);
        while (index < estimate.SegmentStartTimes.Count - 1 &&
               elapsed >= estimate.SegmentStartTimes[index] + estimate.SegmentDurations[index])
            index++;
        return index;
    }

    private static Position Interpolate(GCodeSegment segment, double fraction)
    {
        if (!segment.IsArc || segment.ArcCenter is not { } center)
            return Lerp(segment.Start, segment.End, fraction);

        var startAngle = Math.Atan2(segment.Start.Y - center.Y, segment.Start.X - center.X);
        var endAngle = Math.Atan2(segment.End.Y - center.Y, segment.End.X - center.X);
        var sweep = endAngle - startAngle;
        if (segment.ArcClockwise)
        {
            if (sweep >= 0) sweep -= Math.PI * 2;
        }
        else if (sweep <= 0)
        {
            sweep += Math.PI * 2;
        }

        var radius = Math.Sqrt(Math.Pow(segment.Start.X - center.X, 2) + Math.Pow(segment.Start.Y - center.Y, 2));
        var angle = startAngle + sweep * fraction;
        return new Position(
            center.X + Math.Cos(angle) * radius,
            center.Y + Math.Sin(angle) * radius,
            segment.Start.Z + (segment.End.Z - segment.Start.Z) * fraction);
    }

    private static Position Lerp(Position start, Position end, double fraction) => new(
        start.X + (end.X - start.X) * fraction,
        start.Y + (end.Y - start.Y) * fraction,
        start.Z + (end.Z - start.Z) * fraction);
}
