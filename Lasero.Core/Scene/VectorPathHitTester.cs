using Lasero.Core.Grbl;

namespace Lasero.Core.Scene;

/// <summary>Screen-space segment hit testing shared by canvas interactions and geometry tests. The
/// projection must include object transform, viewport transform, zoom, and DPI as appropriate.</summary>
public static class VectorPathHitTester
{
    private const double FirstSampleT = 1.0 / 40;
    private const double LastSampleT = 39.0 / 40;

    public readonly record struct Hit(int SubpathIndex, int SegmentIndex, double T, double Distance);

    /// <summary>Finds the closest segment within a screen-space tolerance. Straight segments use an
    /// exact projection. Cubics use a zoom-adaptive parameter search plus refinement, with a transformed
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

                // Sample density follows the on-screen length of the curve, so a curve zoomed in to
                // thousands of pixels has no dead zones between samples; the best sample is then
                // refined by golden-section search for an accurate t (used to insert a node).
                var hullLength = Math.Sqrt(DistanceSquared(p0, p1)) + Math.Sqrt(DistanceSquared(p1, p2))
                    + Math.Sqrt(DistanceSquared(p2, p3));
                var step = Math.Max(1.0, toleranceScreenPx * 0.5);
                var samples = (int)Math.Clamp(Math.Ceiling(hullLength / step), 40, 1024);
                var bestT = FirstSampleT;
                var bestSampleDistance = double.PositiveInfinity;
                for (var sample = 1; sample < samples; sample++)
                {
                    var t = Math.Clamp(sample / (double)samples, FirstSampleT, LastSampleT);
                    var d = DistanceSquared(pointerScreen, CubicBezier.Evaluate(p0, p1, p2, p3, t));
                    if (d < bestSampleDistance) { bestSampleDistance = d; bestT = t; }
                }

                var low = Math.Max(FirstSampleT, bestT - 1.0 / samples);
                var high = Math.Min(LastSampleT, bestT + 1.0 / samples);
                const double inversePhi = 0.6180339887498949;
                for (var iteration = 0; iteration < 16 && high - low > 1e-6; iteration++)
                {
                    var m1 = high - (high - low) * inversePhi;
                    var m2 = low + (high - low) * inversePhi;
                    var d1 = DistanceSquared(pointerScreen, CubicBezier.Evaluate(p0, p1, p2, p3, m1));
                    var d2 = DistanceSquared(pointerScreen, CubicBezier.Evaluate(p0, p1, p2, p3, m2));
                    if (d1 < d2) high = m2; else low = m1;
                }
                var refinedT = (low + high) / 2;
                var refinedDistance = DistanceSquared(pointerScreen, CubicBezier.Evaluate(p0, p1, p2, p3, refinedT));
                if (refinedDistance <= bestSampleDistance)
                    Consider(subpathIndex, segmentIndex, refinedT, refinedDistance);
                else
                    Consider(subpathIndex, segmentIndex, bestT, bestSampleDistance);
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
