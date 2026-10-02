using Lasero.Core.Grbl;

namespace Lasero.Core.GCode;

/// <summary>G2/G3 arc helpers, XY plane only (covers the overwhelming majority of 2D laser jobs).</summary>
internal static class ArcMath
{
    /// <summary>Resolves an arc's center from the R (radius) form rather than I/J offsets.</summary>
    public static Position CenterFromRadius(Position start, Position end, double radius, bool clockwise)
    {
        var dx = end.X - start.X;
        var dy = end.Y - start.Y;
        var chord = Math.Sqrt(dx * dx + dy * dy);
        if (chord < 1e-9) return start;

        var midX = (start.X + end.X) / 2;
        var midY = (start.Y + end.Y) / 2;

        var h = Math.Sqrt(Math.Max(0, radius * radius - (chord / 2) * (chord / 2)));
        // Perpendicular to the chord, unit length.
        var perpX = -dy / chord;
        var perpY = dx / chord;

        // Two candidate centers; sign convention matches GRBL's G2 (CW) negative-radius handling
        // closely enough for preview purposes (positive R = shorter arc).
        var sign = (radius >= 0) == clockwise ? 1 : -1;
        return new Position(midX + sign * perpX * h, midY + sign * perpY * h, start.Z);
    }

    public static IEnumerable<Position> Sample(Position start, Position end, Position center, bool clockwise, int steps)
    {
        var startAngle = Math.Atan2(start.Y - center.Y, start.X - center.X);
        var radius = Math.Sqrt(Math.Pow(start.X - center.X, 2) + Math.Pow(start.Y - center.Y, 2));
        var sweep = GetSweep(start, end, center, clockwise);

        for (int k = 0; k <= steps; k++)
        {
            var a = startAngle + sweep * k / steps;
            yield return new Position(center.X + radius * Math.Cos(a), center.Y + radius * Math.Sin(a), start.Z);
        }
    }

    /// <summary>Returns arc endpoints and every cardinal extremum for conservative exact XY bounds.</summary>
    public static IEnumerable<Position> BoundsPoints(Position start, Position end, Position center, bool clockwise)
    {
        yield return start;
        yield return end;

        var radius = Math.Sqrt(Math.Pow(start.X - center.X, 2) + Math.Pow(start.Y - center.Y, 2));
        if (!double.IsFinite(radius) || radius <= 0) yield break;

        var sweep = GetSweep(start, end, center, clockwise);
        var startAngle = Math.Atan2(start.Y - center.Y, start.X - center.X);
        var cardinalAngles = new[] { 0d, Math.PI / 2, Math.PI, 3 * Math.PI / 2 };
        foreach (var angle in cardinalAngles)
        {
            var directedDelta = clockwise
                ? NormalizePositive(startAngle - angle)
                : NormalizePositive(angle - startAngle);
            if (directedDelta <= Math.Abs(sweep) + 1e-10)
                yield return new Position(center.X + radius * Math.Cos(angle), center.Y + radius * Math.Sin(angle), start.Z);
        }
    }

    private static double GetSweep(Position start, Position end, Position center, bool clockwise)
    {
        var startAngle = Math.Atan2(start.Y - center.Y, start.X - center.X);
        var endAngle = Math.Atan2(end.Y - center.Y, end.X - center.X);
        var sweep = endAngle - startAngle;
        if (clockwise && sweep > 0) sweep -= 2 * Math.PI;
        if (!clockwise && sweep < 0) sweep += 2 * Math.PI;

        // GRBL treats an arc with coincident endpoints and a non-zero I/J center offset as a full circle.
        if (Math.Abs(sweep) < 1e-12 && Math.Abs(start.X - end.X) <= 1e-9 && Math.Abs(start.Y - end.Y) <= 1e-9)
            return clockwise ? -2 * Math.PI : 2 * Math.PI;
        return sweep;
    }

    private static double NormalizePositive(double angle)
    {
        angle %= 2 * Math.PI;
        return angle < 0 ? angle + 2 * Math.PI : angle;
    }
}
