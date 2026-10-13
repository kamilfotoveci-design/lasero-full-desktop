using Lasero.Core.GCode;
using Lasero.Core.Grbl;

namespace Lasero.Core.Jobs;

/// <summary>Where a point on the preview timeline falls in the toolpath.</summary>
/// <param name="CompletedSegments">Segments finished before this moment; also the index of the segment in progress.</param>
/// <param name="PartialFraction">How far (0..1) the segment in progress has got. Zero when the job is complete.</param>
/// <param name="Elapsed">Estimated machine time at this moment.</param>
/// <param name="Head">Where the laser head is, in work coordinates.</param>
public readonly record struct PreviewTimelinePoint(int CompletedSegments, double PartialFraction, TimeSpan Elapsed, Position Head);

/// <summary>
/// Maps the timeline slider and the playback clock onto the toolpath. Progress is a fraction of the estimated
/// duration (0..1), not of the segment count, so a long slow cut and a quick rapid take the share of the slider
/// they would take on the machine.
/// </summary>
public static class JobPreviewTimeline
{
    /// <summary>Playback of the whole job at 1,0x takes this long at least, so a short job is still watchable.</summary>
    public const double MinPlaybackSeconds = 4;

    /// <summary>...and at most this long, so a job of several hours does not take as long to preview.</summary>
    public const double MaxPlaybackSeconds = 30;

    public static TimeSpan TimeAt(JobTimeEstimate estimate, double progress)
    {
        ArgumentNullException.ThrowIfNull(estimate);
        if (double.IsNaN(progress)) progress = 0;
        return TimeSpan.FromTicks((long)(estimate.Duration.Ticks * Math.Clamp(progress, 0, 1)));
    }

    public static double ProgressAt(JobTimeEstimate estimate, TimeSpan time)
    {
        ArgumentNullException.ThrowIfNull(estimate);
        return estimate.Duration <= TimeSpan.Zero ? 1 : Math.Clamp(time.TotalSeconds / estimate.Duration.TotalSeconds, 0, 1);
    }

    /// <summary>Index of the segment being worked at <paramref name="time"/>: the last one that has started.</summary>
    public static int SegmentIndexAt(IReadOnlyList<TimeSpan> segmentStarts, TimeSpan time)
    {
        ArgumentNullException.ThrowIfNull(segmentStarts);
        if (segmentStarts.Count == 0) return 0;

        var low = 0;
        var high = segmentStarts.Count;
        while (low < high)
        {
            var middle = low + (high - low) / 2;
            if (segmentStarts[middle] <= time) low = middle + 1;
            else high = middle;
        }

        return Math.Clamp(low - 1, 0, segmentStarts.Count - 1);
    }

    public static PreviewTimelinePoint Locate(GCodeDocument document, JobTimeEstimate estimate, double progress)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(estimate);

        var count = document.Segments.Count;
        if (count == 0) return new PreviewTimelinePoint(0, 0, TimeSpan.Zero, Position.Zero);

        var elapsed = TimeAt(estimate, progress);
        if (double.IsNaN(progress) || progress >= 1 || elapsed >= estimate.Duration)
            return new PreviewTimelinePoint(count, 0, estimate.Duration, document.Segments[^1].End);

        var index = SegmentIndexAt(estimate.SegmentStartTimes, elapsed);
        var duration = estimate.SegmentDurations[index];
        var fraction = duration <= TimeSpan.Zero
            ? 1
            : Math.Clamp((elapsed - estimate.SegmentStartTimes[index]).TotalSeconds / duration.TotalSeconds, 0, 1);
        var head = JobSimulator.GetStateAtTime(document, estimate, elapsed).LaserPosition;
        return new PreviewTimelinePoint(index, fraction, elapsed, head);
    }

    /// <summary>How long playing the whole job takes at 1,0x.</summary>
    public static double PlaybackSeconds(TimeSpan jobDuration) =>
        Math.Clamp(jobDuration.TotalSeconds, MinPlaybackSeconds, MaxPlaybackSeconds);

    /// <summary>The progress after <paramref name="elapsedSeconds"/> of playback at <paramref name="speed"/> times normal.</summary>
    public static double Advance(double progress, double elapsedSeconds, TimeSpan jobDuration, double speed)
    {
        if (double.IsNaN(progress) || !double.IsFinite(elapsedSeconds) || elapsedSeconds < 0) return Math.Clamp(double.IsNaN(progress) ? 0 : progress, 0, 1);
        var factor = double.IsFinite(speed) && speed > 0.01 ? speed : 0.01;
        return Math.Clamp(progress + elapsedSeconds * factor / PlaybackSeconds(jobDuration), 0, 1);
    }
}
