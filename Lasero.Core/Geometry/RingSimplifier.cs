using Lasero.Core.Grbl;

namespace Lasero.Core.Geometry;

/// <summary>Drops points that carry no shape: repeated points, and points that lie within a tolerance
/// of the line through their neighbours (Ramer-Douglas-Peucker). Used by "Optimize results" so an
/// offset of a traced logo does not keep thousands of near-collinear nodes. The tolerance is the
/// largest distance the simplified outline may move from the input.</summary>
public static class RingSimplifier
{
    /// <summary>Simplifies one closed ring (no repeated closing point). A ring that would collapse
    /// below three points is returned with only its duplicates removed.</summary>
    public static IReadOnlyList<Position> SimplifyClosed(IReadOnlyList<Position> ring, double toleranceMm)
    {
        ArgumentNullException.ThrowIfNull(ring);
        var points = RemoveDuplicates(ring);
        if (points.Count <= 3 || toleranceMm <= 0) return points;

        // Two anchors that are certainly part of the outline: the first point and the one farthest
        // from it. Each half is then an open polyline for the standard algorithm.
        var far = 0;
        var farDistance = -1.0;
        for (var index = 1; index < points.Count; index++)
        {
            var distance = DistanceSquared(points[0], points[index]);
            if (distance <= farDistance) continue;
            farDistance = distance;
            far = index;
        }

        var first = SimplifyOpen(points.GetRange(0, far + 1), toleranceMm);
        var secondInput = points.GetRange(far, points.Count - far);
        secondInput.Add(points[0]);
        var second = SimplifyOpen(secondInput, toleranceMm);

        var result = new List<Position>(first.Count + second.Count);
        result.AddRange(first);
        // second starts at the junction point (already in first) and ends on points[0] (also there).
        for (var index = 1; index < second.Count - 1; index++)
            result.Add(second[index]);

        return result.Count >= 3 ? result : points;
    }

    private static List<Position> RemoveDuplicates(IReadOnlyList<Position> ring)
    {
        var result = new List<Position>(ring.Count);
        foreach (var point in ring)
        {
            if (result.Count > 0 && DistanceSquared(result[^1], point) < 1e-12) continue;
            result.Add(point);
        }
        while (result.Count > 1 && DistanceSquared(result[0], result[^1]) < 1e-12)
            result.RemoveAt(result.Count - 1);
        return result;
    }

    /// <summary>Ramer-Douglas-Peucker simplification for an open polyline. Endpoints are always
    /// preserved. The requested tolerance is the maximum geometric deviation from the source.</summary>
    public static IReadOnlyList<Position> SimplifyOpen(IReadOnlyList<Position> polyline, double toleranceMm)
    {
        ArgumentNullException.ThrowIfNull(polyline);
        if (toleranceMm <= 0) return polyline.ToList();
        polyline = RemoveDuplicates(polyline);
        if (polyline.Count <= 2) return polyline;

        var keep = new bool[polyline.Count];
        keep[0] = true;
        keep[^1] = true;
        var pending = new Stack<(int Start, int End)>();
        pending.Push((0, polyline.Count - 1));

        while (pending.Count > 0)
        {
            var (start, end) = pending.Pop();
            if (end <= start + 1) continue;

            var worst = -1.0;
            var worstIndex = -1;
            for (var index = start + 1; index < end; index++)
            {
                var distance = DistanceToSegment(polyline[index], polyline[start], polyline[end]);
                if (distance <= worst) continue;
                worst = distance;
                worstIndex = index;
            }

            if (worst <= toleranceMm) continue;
            keep[worstIndex] = true;
            pending.Push((start, worstIndex));
            pending.Push((worstIndex, end));
        }

        var result = new List<Position>();
        for (var index = 0; index < polyline.Count; index++)
            if (keep[index]) result.Add(polyline[index]);
        return result;
    }

    private static double DistanceToSegment(Position point, Position a, Position b)
    {
        var dx = b.X - a.X;
        var dy = b.Y - a.Y;
        var lengthSquared = dx * dx + dy * dy;
        if (lengthSquared < 1e-18) return Math.Sqrt(DistanceSquared(point, a));

        var t = Math.Clamp(((point.X - a.X) * dx + (point.Y - a.Y) * dy) / lengthSquared, 0, 1);
        var projectedX = a.X + t * dx;
        var projectedY = a.Y + t * dy;
        var offsetX = point.X - projectedX;
        var offsetY = point.Y - projectedY;
        return Math.Sqrt(offsetX * offsetX + offsetY * offsetY);
    }

    private static double DistanceSquared(Position a, Position b)
    {
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;
        return dx * dx + dy * dy;
    }
}
