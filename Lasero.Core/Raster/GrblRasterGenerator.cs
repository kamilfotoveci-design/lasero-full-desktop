using System.Globalization;

namespace Lasero.Core.Raster;

/// <summary>Turns a planned LaserJob into GRBL G-code — the only stage that produces serial-bound
/// text. Pure function of its input, so identical LaserJobs always produce identical G-code.</summary>
public static class GrblRasterGenerator
{
    public static IReadOnlyList<string> Generate(LaserJob job)
    {
        var lines = new List<string> { "G90", "G21", "M5" };
        var feed = Fmt(job.FeedRatePerMinute);
        var inRun = false;
        var isFirstG1InRun = true;

        foreach (var move in job.Moves)
        {
            if (move.Kind == RasterMoveKind.Travel)
            {
                if (inRun) lines.Add("M5");
                lines.Add($"G0 X{Fmt(move.X)} Y{Fmt(move.Y)}");
                inRun = false;
                isFirstG1InRun = true;
                continue;
            }

            if (!inRun)
            {
                lines.Add($"M4 S{Fmt(move.Power)}");
                inRun = true;
                continue;
            }

            lines.Add(isFirstG1InRun
                ? $"G1 X{Fmt(move.X)} Y{Fmt(move.Y)} S{Fmt(move.Power)} F{feed}"
                : $"G1 X{Fmt(move.X)} Y{Fmt(move.Y)} S{Fmt(move.Power)}");
            isFirstG1InRun = false;
        }

        if (inRun) lines.Add("M5");
        lines.Add("M5");
        return lines;
    }

    private static string Fmt(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);
}
