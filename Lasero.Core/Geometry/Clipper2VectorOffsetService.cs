using Clipper2Lib;
using Lasero.Core.Grbl;

namespace Lasero.Core.Geometry;

/// <summary>IVectorOffsetService via Clipper2's InflatePaths (Boost-1.0 licensed — clipper2-nuget.dev).
/// Clipper2's own types (PathD/PathsD/JoinType/EndType) never leave this file; ToClipper/ToRings are
/// the entire adapter boundary, matching Clipper2VectorBooleanService's convention exactly.</summary>
public sealed class Clipper2VectorOffsetService : IVectorOffsetService
{
    public static readonly Clipper2VectorOffsetService Default = new();

    /// <summary>Decimal places Clipper2 keeps when it scales coordinates into its internal Int64
    /// space — same value Clipper2VectorBooleanService uses, so a boolean op followed by an offset (or
    /// vice versa) never introduces a precision mismatch between the two services.</summary>
    private const int Precision = 6;

    /// <summary>Below this a ring is a sliver, not a shape — the same kind of "offset ran the shape
    /// off the map" collapse IVectorOffsetService's contract says to omit rather than throw for.</summary>
    private const double MinimumRingAreaSquareMm = 1e-6;

    public IReadOnlyList<IReadOnlyList<Position>> OffsetClosedGroup(
        IReadOnlyList<IReadOnlyList<Position>> rings,
        double distanceMm,
        VectorJoinType joinType,
        double miterLimit)
    {
        ArgumentNullException.ThrowIfNull(rings);
        if (rings.Count == 0) return [];

        var inflated = Clipper.InflatePaths(
            ToClipper(rings), distanceMm, MapJoin(joinType), EndType.Polygon, miterLimit, Precision);
        return ToRings(inflated);
    }

    public IReadOnlyList<IReadOnlyList<Position>> OffsetOpenPath(
        IReadOnlyList<Position> path,
        double distanceMm,
        VectorJoinType joinType,
        double miterLimit)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (path.Count < 2 || Math.Abs(distanceMm) < 1e-6) return [];

        var inflated = Clipper.InflatePaths(
            ToClipper([path]), Math.Abs(distanceMm), MapJoin(joinType), EndType.Butt, miterLimit, Precision);
        return ToRings(inflated);
    }

    private static JoinType MapJoin(VectorJoinType joinType) => joinType switch
    {
        VectorJoinType.Round => JoinType.Round,
        VectorJoinType.Miter => JoinType.Miter,
        VectorJoinType.Bevel => JoinType.Bevel,
        _ => throw new ArgumentOutOfRangeException(nameof(joinType), joinType, null),
    };

    private static PathsD ToClipper(IReadOnlyList<IReadOnlyList<Position>> rings)
    {
        var paths = new PathsD(rings.Count);
        foreach (var ring in rings)
        {
            var path = new PathD(ring.Count);
            foreach (var point in ring)
                path.Add(new PointD(point.X, point.Y));
            paths.Add(path);
        }
        return paths;
    }

    /// <summary>Same "do not repeat the closing point" convention as
    /// Clipper2VectorBooleanService.ToRings, plus dropping any ring that collapsed to a sliver
    /// (fewer than 3 points, or near-zero area) rather than letting a degenerate ring reach the
    /// caller.</summary>
    private static List<IReadOnlyList<Position>> ToRings(PathsD paths)
    {
        var rings = new List<IReadOnlyList<Position>>(paths.Count);
        foreach (var path in paths)
        {
            if (path.Count < 3) continue;
            var points = new List<Position>(path.Count);
            foreach (var point in path)
                points.Add(new Position(point.x, point.y, 0));
            if (Math.Abs(SignedArea(points)) < MinimumRingAreaSquareMm) continue;
            rings.Add(points);
        }
        return rings;
    }

    private static double SignedArea(IReadOnlyList<Position> points)
    {
        double twiceArea = 0;
        for (var index = 0; index < points.Count; index++)
        {
            var current = points[index];
            var next = points[(index + 1) % points.Count];
            twiceArea += current.X * next.Y - next.X * current.Y;
        }
        return twiceArea / 2;
    }
}
