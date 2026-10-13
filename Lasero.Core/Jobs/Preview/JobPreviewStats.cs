using System.Globalization;
using Lasero.Core.GCode;

namespace Lasero.Core.Jobs;

/// <summary>
/// The three numbers under the preview: how far the head travels cutting, how far it travels repositioning,
/// and the total time. The split follows <see cref="JobPreviewModel.IsTravel"/>, so what the legend calls a
/// move and what the statistics count as one are the same thing, and the two times add up to the estimate.
/// </summary>
public sealed record JobPreviewStats(double CutDistanceMm, TimeSpan CutTime, double RapidDistanceMm, TimeSpan RapidTime)
{
    private static readonly NumberFormatInfo Czech = new() { NumberDecimalSeparator = ",", NumberGroupSeparator = " " };

    public TimeSpan TotalTime => CutTime + RapidTime;

    public string CutLabel => $"{FormatDistance(CutDistanceMm)} mm (~{FormatMinutes(CutTime)})";
    public string RapidLabel => $"{FormatDistance(RapidDistanceMm)} mm (~{FormatHours(RapidTime)})";
    public string TotalLabel => FormatHours(TotalTime);

    public static JobPreviewStats Compute(GCodeDocument document, JobTimeEstimate estimate)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(estimate);
        if (document.Segments.Count != estimate.SegmentDurations.Count)
            throw new ArgumentException("Časový model neodpovídá zadané dráze.", nameof(estimate));

        var cutDistance = 0d;
        var rapidDistance = 0d;
        var cutTime = TimeSpan.Zero;
        var rapidTime = TimeSpan.Zero;
        for (var i = 0; i < document.Segments.Count; i++)
        {
            var segment = document.Segments[i];
            var length = JobTimeEstimator.SegmentLength(segment);
            if (JobPreviewModel.IsTravel(segment))
            {
                rapidDistance += length;
                rapidTime += estimate.SegmentDurations[i];
            }
            else
            {
                cutDistance += length;
                cutTime += estimate.SegmentDurations[i];
            }
        }

        return new JobPreviewStats(cutDistance, cutTime, rapidDistance, rapidTime);
    }

    /// <summary>Whole millimetres from 100 up, one decimal below that, decimal comma.</summary>
    public static string FormatDistance(double millimetres)
    {
        if (!double.IsFinite(millimetres) || millimetres < 0) millimetres = 0;
        return millimetres >= 100
            ? Math.Round(millimetres).ToString("#,0", Czech)
            : millimetres.ToString("0.#", Czech);
    }

    /// <summary>mm:ss, with the minutes growing past 59 rather than rolling into hours.</summary>
    public static string FormatMinutes(TimeSpan time)
    {
        var seconds = WholeSeconds(time);
        return $"{seconds / 60:00}:{seconds % 60:00}";
    }

    /// <summary>h:mm:ss.</summary>
    public static string FormatHours(TimeSpan time)
    {
        var seconds = WholeSeconds(time);
        return $"{seconds / 3600}:{seconds / 60 % 60:00}:{seconds % 60:00}";
    }

    private static long WholeSeconds(TimeSpan time) =>
        time <= TimeSpan.Zero ? 0 : (long)Math.Round(time.TotalSeconds, MidpointRounding.AwayFromZero);
}
