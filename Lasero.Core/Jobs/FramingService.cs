using System.Globalization;
using Lasero.Core.GCode;

namespace Lasero.Core.Jobs;

/// <summary>
/// Builds a small G-code program that traces a job's bounding box so the
/// operator can verify placement/size on the material before committing to
/// the real burn. Rapids to the start corner with the laser off first, so
/// there is never a "phantom" diagonal burn on the way into the frame.
/// </summary>
public static class FramingService
{
    public static IReadOnlyList<string> BuildFrameGCode(BoundingBox2D box, FramingOptions options)
    {
        if (box.IsEmpty)
            throw new InvalidOperationException("Úlohu nelze orámovat, protože neobsahuje žádnou geometrii.");

        var lines = new List<string>
        {
            "G90", // absolute positioning — framing always operates in absolute coordinates
            "M5",  // ensure laser is off before any rapid move
            Format("G0", box.MinX, box.MinY),
        };

        var f = options.FeedRatePerMinute.ToString("0.###", CultureInfo.InvariantCulture);
        var laserOn = options.LaserPower > 0;

        if (options.Mode == FramingMode.FullOutline)
        {
            if (laserOn) lines.Add($"M3 S{options.LaserPower.ToString("0.###", CultureInfo.InvariantCulture)}");
            lines.Add(Format("G1", box.MaxX, box.MinY, f));
            lines.Add(Format("G1", box.MaxX, box.MaxY));
            lines.Add(Format("G1", box.MinX, box.MaxY));
            lines.Add(Format("G1", box.MinX, box.MinY));
            lines.Add("M5");
        }
        else
        {
            var stub = Math.Min(options.CornerStubLength, Math.Min(box.Width, box.Height) / 2);
            var corners = new (double X, double Y, double InX, double InY)[]
            {
                (box.MinX, box.MinY, box.MinX + stub, box.MinY + stub),
                (box.MaxX, box.MinY, box.MaxX - stub, box.MinY + stub),
                (box.MaxX, box.MaxY, box.MaxX - stub, box.MaxY - stub),
                (box.MinX, box.MaxY, box.MinX + stub, box.MaxY - stub),
            };

            foreach (var (cx, cy, inX, inY) in corners)
            {
                lines.Add(Format("G0", cx, cy)); // laser off — travel to this corner
                if (laserOn) lines.Add($"M3 S{options.LaserPower.ToString("0.###", CultureInfo.InvariantCulture)}");
                lines.Add(Format("G1", inX, cy, f));   // horizontal stub
                lines.Add("M5");                       // never rapid while the framing laser is enabled
                lines.Add(Format("G0", cx, cy));       // laser off, back to corner
                lines.Add(laserOn ? $"M3 S{options.LaserPower.ToString("0.###", CultureInfo.InvariantCulture)}" : "M5");
                lines.Add(Format("G1", cx, inY, f));   // vertical stub
                lines.Add("M5");
            }

            lines.Add(Format("G0", box.MinX, box.MinY)); // return to start corner
        }

        return lines;
    }

    private static string Format(string gword, double x, double y, string? feed = null)
    {
        var line = $"{gword} X{x.ToString("0.###", CultureInfo.InvariantCulture)} Y{y.ToString("0.###", CultureInfo.InvariantCulture)}";
        return feed is null ? line : $"{line} F{feed}";
    }
}
