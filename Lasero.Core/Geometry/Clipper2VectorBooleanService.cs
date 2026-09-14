using Clipper2Lib;
using Lasero.Core.Grbl;

namespace Lasero.Core.Geometry;

/// <summary>IVectorBooleanService via Clipper2 (Boost-1.0 licensed — clipper2-nuget.dev). Clipper2's
/// own types (PathD/PathsD/FillRule) never leave this file; ToClipper/ToRings are the entire adapter
/// boundary.</summary>
public sealed class Clipper2VectorBooleanService : IVectorBooleanService
{
    public static readonly Clipper2VectorBooleanService Default = new();

    /// <summary>Decimal places Clipper2 keeps when it scales coordinates into its internal Int64
    /// space. mm-scale geometry with micrometre-level detail comfortably fits in 6 — an order of
    /// magnitude finer than the 0.002mm tolerance the WPF-based implementation used.</summary>
    private const int Precision = 6;

    public IReadOnlyList<IReadOnlyList<Position>> Resolve(
        IReadOnlyList<IReadOnlyList<Position>> rings, VectorFillRule fillRule) =>
        ToRings(Clipper.Union(ToClipper(rings), ToClipperFillRule(fillRule)));

    public IReadOnlyList<IReadOnlyList<Position>> Union(
        IReadOnlyList<IReadOnlyList<Position>> subject,
        IReadOnlyList<IReadOnlyList<Position>> clip,
        VectorFillRule fillRule) =>
        ToRings(Clipper.Union(ToClipper(subject), ToClipper(clip), ToClipperFillRule(fillRule), Precision));

    public IReadOnlyList<IReadOnlyList<Position>> Subtract(
        IReadOnlyList<IReadOnlyList<Position>> subject,
        IReadOnlyList<IReadOnlyList<Position>> clip,
        VectorFillRule fillRule) =>
        ToRings(Clipper.Difference(ToClipper(subject), ToClipper(clip), ToClipperFillRule(fillRule), Precision));

    public IReadOnlyList<IReadOnlyList<Position>> Intersect(
        IReadOnlyList<IReadOnlyList<Position>> subject,
        IReadOnlyList<IReadOnlyList<Position>> clip,
        VectorFillRule fillRule) =>
        ToRings(Clipper.Intersect(ToClipper(subject), ToClipper(clip), ToClipperFillRule(fillRule), Precision));

    public IReadOnlyList<IReadOnlyList<Position>> Xor(
        IReadOnlyList<IReadOnlyList<Position>> subject,
        IReadOnlyList<IReadOnlyList<Position>> clip,
        VectorFillRule fillRule) =>
        ToRings(Clipper.Xor(ToClipper(subject), ToClipper(clip), ToClipperFillRule(fillRule), Precision));

    private static FillRule ToClipperFillRule(VectorFillRule fillRule) => fillRule switch
    {
        VectorFillRule.EvenOdd => FillRule.EvenOdd,
        VectorFillRule.NonZero => FillRule.NonZero,
        _ => throw new ArgumentOutOfRangeException(nameof(fillRule), fillRule, null),
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

    /// <summary>Clipper2 rings do not repeat their first point as their last — matches what
    /// SceneViewModel's boolean-op pipeline already produced before this migration (its own
    /// ToImportedShapes explicitly stripped a duplicated closing point coming out of WPF's flattened
    /// geometry), so no closing point is added here either. A caller with a different convention
    /// (e.g. one that always repeats it) is responsible for that itself.</summary>
    private static List<IReadOnlyList<Position>> ToRings(PathsD paths)
    {
        var rings = new List<IReadOnlyList<Position>>(paths.Count);
        foreach (var path in paths)
        {
            if (path.Count < 3) continue;
            var points = new List<Position>(path.Count);
            foreach (var point in path)
                points.Add(new Position(point.x, point.y, 0));
            rings.Add(points);
        }
        return rings;
    }
}
