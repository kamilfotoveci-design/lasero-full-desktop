using Clipper2Lib;
using Lasero.Core.Grbl;

namespace Lasero.Core.Geometry;

/// <summary>IVectorOffsetService via Clipper2's InflatePaths (Boost-1.0 licensed — clipper2-nuget.dev).
/// Clipper2's own types (Path64/Paths64/JoinType/EndType) never leave this file; ToClipper/ToRings are
/// the entire adapter boundary, matching Clipper2VectorBooleanService's convention.
///
/// The integer (Paths64) overload is used on purpose, with the mm to integer scale applied here. The
/// scaled PathsD overload leaves the round-join arc tolerance to Clipper's built-in default, which
/// is derived from the scaled delta and, at 6 decimals, asks for arcs finer than a nanometre: tens of
/// thousands of vertices per rounded corner. Passing the tolerance explicitly in the same integer
/// units keeps a rounded offset smooth to a few micrometres with a few dozen vertices per corner.</summary>
public sealed class Clipper2VectorOffsetService : IVectorOffsetService
{
    public static readonly Clipper2VectorOffsetService Default = new();

    /// <summary>Integer units per mm (6 decimals) — the same precision Clipper2VectorBooleanService
    /// uses, so a boolean op followed by an offset (or vice versa) never introduces a precision
    /// mismatch between the two services.</summary>
    private const double Scale = 1_000_000;

    /// <summary>Below this a ring is a sliver, not a shape — the same kind of "offset ran the shape
    /// off the map" collapse IVectorOffsetService's contract says to omit rather than throw for.</summary>
    private const double MinimumRingAreaSquareMm = 1e-6;

    /// <summary>Largest allowed deviation of a round-join arc from the true circle, in mm. Far below
    /// the laser's own kerf, so the polyline is indistinguishable from a true arc when burned.</summary>
    private const double MaxArcToleranceMm = 0.005;
    private const double MinArcToleranceMm = 0.001;

    public IReadOnlyList<IReadOnlyList<Position>> OffsetClosedGroup(
        IReadOnlyList<IReadOnlyList<Position>> rings,
        double distanceMm,
        VectorJoinType joinType,
        double miterLimit)
    {
        ArgumentNullException.ThrowIfNull(rings);
        if (rings.Count == 0) return [];

        var inflated = Clipper.InflatePaths(
            ToClipper(rings), distanceMm * Scale, MapJoin(joinType), EndType.Polygon,
            miterLimit, ArcToleranceUnits(distanceMm));
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

        var distance = Math.Abs(distanceMm);
        var inflated = Clipper.InflatePaths(
            ToClipper([path]), distance * Scale, MapJoin(joinType), EndType.Butt,
            miterLimit, ArcToleranceUnits(distance));
        return ToRings(inflated);
    }

    private static double ArcToleranceUnits(double distanceMm) =>
        Math.Clamp(Math.Abs(distanceMm) * 0.02, MinArcToleranceMm, MaxArcToleranceMm) * Scale;

    private static JoinType MapJoin(VectorJoinType joinType) => joinType switch
    {
        VectorJoinType.Round => JoinType.Round,
        VectorJoinType.Miter => JoinType.Miter,
        VectorJoinType.Bevel => JoinType.Bevel,
        _ => throw new ArgumentOutOfRangeException(nameof(joinType), joinType, null),
    };

    private static Paths64 ToClipper(IReadOnlyList<IReadOnlyList<Position>> rings)
    {
        var paths = new Paths64(rings.Count);
        foreach (var ring in rings)
        {
            var path = new Path64(ring.Count);
            foreach (var point in ring)
                path.Add(new Point64((long)Math.Round(point.X * Scale), (long)Math.Round(point.Y * Scale)));
            paths.Add(path);
        }
        return paths;
    }

    /// <summary>Same "do not repeat the closing point" convention as
    /// Clipper2VectorBooleanService.ToRings, plus dropping any ring that collapsed to a sliver
    /// (fewer than 3 points, or near-zero area) rather than letting a degenerate ring reach the
    /// caller.</summary>
    private static List<IReadOnlyList<Position>> ToRings(Paths64 paths)
    {
        var rings = new List<IReadOnlyList<Position>>(paths.Count);
        foreach (var path in paths)
        {
            if (path.Count < 3) continue;
            var points = new List<Position>(path.Count);
            foreach (var point in path)
                points.Add(new Position(point.X / Scale, point.Y / Scale, 0));
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
