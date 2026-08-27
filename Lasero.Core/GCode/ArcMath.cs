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
        var endAngle = Math.Atan2(end.Y - center.Y, end.X - center.X);
        var radius = Math.Sqrt(Math.Pow(start.X - center.X, 2) + Math.Pow(start.Y - center.Y, 2));

        var sweep = endAngle - startAngle;
        if (clockwise && sweep > 0) sweep -= 2 * Math.PI;
        if (!clockwise && sweep < 0) sweep += 2 * Math.PI;

        for (int k = 0; k <= steps; k++)
        {
            var a = startAngle + sweep * k / steps;
            yield return new Position(center.X + radius * Math.Cos(a), center.Y + radius * Math.Sin(a), start.Z);
        }
    }
}
