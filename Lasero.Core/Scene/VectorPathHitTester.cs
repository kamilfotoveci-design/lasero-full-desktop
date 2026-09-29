using Lasero.Core.Grbl;

namespace Lasero.Core.Scene;

/// <summary>Screen-space segment hit testing shared by canvas interactions and geometry tests. The
/// projection must include object transform, viewport transform, zoom, and DPI as appropriate.</summary>
public static class VectorPathHitTester
{
    private const double FirstSampleT = 1.0 / 40;
    private const double LastSampleT = 39.0 / 40;
    private const int CurveSamples = 39;

    public readonly record struct Hit(int SubpathIndex, int SegmentIndex, double T, double Distance);

    /// <summary>Finds the closest segment within a screen-space tolerance. Straight segments use an
    /// exact projection. Cubics keep the existing 39-sample parameter search, with a transformed
    /// control-hull bounds check to skip curves that cannot reach the pointer.</summary>
    public static Hit? FindNearest(
        VectorPath path,
        Position pointerScreen,
        double toleranceScreenPx,
        Func<Position, Position> projectToScreen)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(projectToScreen);
        if (!double.IsFinite(toleranceScreenPx) || toleranceScreenPx < 0)
            throw new ArgumentOutOfRangeException(nameof(toleranceScreenPx));

        var toleranceSquared = toleranceScreenPx * toleranceScreenPx;
        var bestDistanceSquared = double.PositiveInfinity;
        Hit? best = null;

        for (var subpathIndex = 0; subpathIndex < path.Subpaths.Count; subpathIndex++)
        {
            var subpath = path.Subpaths[subpathIndex];
            for (var segmentIndex = 0; segmentIndex < subpath.SegmentCount; segmentIndex++)
            {
                var (a, b) = subpath.Segment(segmentIndex);
                var p0 = projectToScreen(a.Anchor);
                var p3 = projectToScreen(b.Anchor);
                if (!IsFinite(p0) || !IsFinite(p3)) continue;

                if (VectorSubpath.IsStraightSegment(a, b))
                {
                    var dx = p3.X - p0.X;
                    var dy = p3.Y - p0.Y;
                    var lengthSquared = dx * dx + dy * dy;
                    var t = lengthSquared <= double.Epsilon
                        ? 0.5
                        : Math.Clamp(((pointerScreen.X - p0.X) * dx + (pointerScreen.Y - p0.Y) * dy) / lengthSquared, FirstSampleT, LastSampleT);
                    var distanceSquared = DistanceSquared(pointerScreen, Lerp(p0, p3, t));
                    Consider(subpathIndex, segmentIndex, t, distanceSquared);
                    continue;
                }

                var p1 = projectToScreen(a.HandleOut ?? a.Anchor);
                var p2 = projectToScreen(b.HandleIn ?? b.Anchor);
                if (!IsFinite(p1) || !IsFinite(p2) || OutsideExpandedBounds(pointerScreen, toleranceScreenPx, p0, p1, p2, p3))
                    continue;

                for (var sample = 1; sample <= CurveSamples; sample++)
                {
                    var t = sample / 40.0;
                    var point = CubicBezier.Evaluate(p0, p1, p2, p3, t);
                    Consider(subpathIndex, segmentIndex, t, DistanceSquared(pointerScreen, point));
                }
            }
        }

        return bestDistanceSquared <= toleranceSquared ? best : null;

        void Consider(int subpathIndex, int segmentIndex, double t, double distanceSquared)
        {
            if (distanceSquared < bestDistanceSquared)
            {
                bestDistanceSquared = distanceSquared;
                best = new Hit(subpathIndex, segmentIndex, t, Math.Sqrt(distanceSquared));
            }
        }
    }

    private static bool OutsideExpandedBounds(
        Position point, double tolerance, Position p0, Position p1, Position p2, Position p3)
    {
        var minX = Math.Min(Math.Min(p0.X, p1.X), Math.Min(p2.X, p3.X));
        var maxX = Math.Max(Math.Max(p0.X, p1.X), Math.Max(p2.X, p3.X));
        var minY = Math.Min(Math.Min(p0.Y, p1.Y), Math.Min(p2.Y, p3.Y));
        var maxY = Math.Max(Math.Max(p0.Y, p1.Y), Math.Max(p2.Y, p3.Y));
        var dx = Math.Max(Math.Max(minX - point.X, 0), point.X - maxX);
        var dy = Math.Max(Math.Max(minY - point.Y, 0), point.Y - maxY);
        return dx * dx + dy * dy > tolerance * tolerance;
    }

    private static Position Lerp(Position a, Position b, double t) => new(
        a.X + (b.X - a.X) * t,
        a.Y + (b.Y - a.Y) * t,
        a.Z + (b.Z - a.Z) * t);

    private static double DistanceSquared(Position a, Position b)
    {
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;
        return dx * dx + dy * dy;
    }

    private static bool IsFinite(Position point) =>
        double.IsFinite(point.X) && double.IsFinite(point.Y);
}
