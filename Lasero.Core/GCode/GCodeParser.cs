using System.Globalization;
using System.Text.RegularExpressions;
using Lasero.Core.Grbl;

namespace Lasero.Core.GCode;

/// <summary>
/// Minimal G-code interpreter purpose-built for 2D laser jobs (not a general
/// CNC/milling parser — no tool length compensation, no multi-axis kinematics).
/// Tracks just enough modal state to resolve every line to an absolute-position
/// motion segment for the workspace preview, bounding box (used by framing),
/// and streaming. Unsupported/unrecognized words are ignored rather than
/// rejected, since GRBL itself is the final authority on validity — this
/// parser only needs to be good enough to *draw* the job, not execute it.
/// </summary>
public static class GCodeParser
{
    private static readonly Regex WordPattern = new(@"([A-Za-z])\s*(-?\d+\.?\d*)", RegexOptions.Compiled);

    public static GCodeDocument Parse(IEnumerable<string> lines, string? sourceFileName = null)
    {
        var rawLines = lines.ToList();
        var segments = new List<GCodeSegment>();
        var bbox = BoundingBox2D.Empty;

        var motionMode = 0;           // G0 rapid by default until told otherwise
        var absoluteMode = true;      // G90
        var unitScale = 1.0;          // G21 = mm (1.0); G20 = inch (25.4)
        var spindleOn = false;
        var power = 0.0;
        var feedRatePerMinute = 0.0;
        var pos = Position.Zero;

        for (int lineIndex = 0; lineIndex < rawLines.Count; lineIndex++)
        {
            var line = StripComment(rawLines[lineIndex]);
            if (line.Length == 0) continue;

            double? x = null, y = null, z = null, i = null, j = null, r = null, s = null, f = null;
            bool sawX = false, sawY = false, sawZ = false;
            var explicitMotion = -1;

            foreach (Match m in WordPattern.Matches(line))
            {
                var letter = char.ToUpperInvariant(m.Groups[1].Value[0]);
                var value = double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);

                switch (letter)
                {
                    case 'G':
                        var g = (int)value;
                        switch (g)
                        {
                            case 0: case 1: case 2: case 3:
                                explicitMotion = g;
                                break;
                            case 20: unitScale = 25.4; break;
                            case 21: unitScale = 1.0; break;
                            case 90: absoluteMode = true; break;
                            case 91: absoluteMode = false; break;
                            // G4 (dwell), G17-19 (plane), G54-59 (WCS), G10 (offset), G28/30 (predefined
                            // positions) intentionally not resolved here — they don't affect the 2D
                            // preview geometry in a way this MVP needs to render.
                        }
                        break;
                    case 'M':
                        var mCode = (int)value;
                        if (mCode is 3 or 4) spindleOn = true;
                        else if (mCode == 5) spindleOn = false;
                        break;
                    case 'X': x = value * unitScale; sawX = true; break;
                    case 'Y': y = value * unitScale; sawY = true; break;
                    case 'Z': z = value * unitScale; sawZ = true; break;
                    case 'I': i = value * unitScale; break;
                    case 'J': j = value * unitScale; break;
                    case 'R': r = value * unitScale; break;
                    case 'S': s = value; break;
                    case 'F': f = value * unitScale; break;
                }
            }

            if (explicitMotion >= 0) motionMode = explicitMotion;
            if (s.HasValue) power = s.Value;
            if (f.HasValue) feedRatePerMinute = f.Value;

            var hasArcDefinition = motionMode is 2 or 3 && (i.HasValue || j.HasValue || r.HasValue);
            if (!sawX && !sawY && !sawZ && !hasArcDefinition)
                continue; // A pure modal/setting line (e.g. "G21", "M3 S255" alone) — no motion to record.

            var target = new Position(
                sawX ? (absoluteMode ? x!.Value : pos.X + x!.Value) : pos.X,
                sawY ? (absoluteMode ? y!.Value : pos.Y + y!.Value) : pos.Y,
                sawZ ? (absoluteMode ? z!.Value : pos.Z + z!.Value) : pos.Z);

            var laserOn = spindleOn && power > 0 && motionMode is 1 or 2 or 3;

            if (motionMode is 2 or 3 && (i.HasValue || j.HasValue || r.HasValue))
            {
                var clockwise = motionMode == 2;
                Position center;
                if (i.HasValue || j.HasValue)
                {
                    center = new Position(pos.X + (i ?? 0), pos.Y + (j ?? 0), pos.Z);
                }
                else
                {
                    center = ArcMath.CenterFromRadius(pos, target, r!.Value, clockwise);
                }

                segments.Add(new GCodeSegment
                {
                    Start = pos,
                    End = target,
                    IsRapid = false,
                    IsArc = true,
                    ArcCenter = center,
                    ArcClockwise = clockwise,
                    LaserOn = laserOn,
                    Power = power,
                    FeedRatePerMinute = feedRatePerMinute,
                    SourceLine = lineIndex,
                });

                foreach (var point in ArcMath.BoundsPoints(pos, target, center, clockwise))
                    bbox = bbox.Include(point.X, point.Y);
            }
            else
            {
                segments.Add(new GCodeSegment
                {
                    Start = pos,
                    End = target,
                    IsRapid = motionMode == 0,
                    LaserOn = laserOn,
                    Power = power,
                    FeedRatePerMinute = feedRatePerMinute,
                    SourceLine = lineIndex,
                });
                bbox = bbox.Include(target.X, target.Y);
            }

            // The implicit start (machine 0,0) only belongs to the extent when the first move is a
            // cutting one. A rapid travel from there is not part of the artwork; counting it made a job
            // placed at 200,100 report a 200x100 size and made framing trace a box from the bed corner.
            if (segments.Count == 1 && !segments[0].IsRapid)
                bbox = bbox.Include(pos.X, pos.Y);

            pos = target;
        }

        return new GCodeDocument
        {
            RawLines = rawLines,
            Segments = segments,
            BoundingBox = bbox,
            SourceFileName = sourceFileName,
        };
    }

    private static string StripComment(string line)
    {
        var semi = line.IndexOf(';');
        if (semi >= 0) line = line[..semi];

        // Strip parenthetical comments, which may appear anywhere in the line.
        int start;
        while ((start = line.IndexOf('(')) >= 0)
        {
            var end = line.IndexOf(')', start);
            line = end >= 0 ? line[..start] + line[(end + 1)..] : line[..start];
        }
        return line.Trim();
    }
}
