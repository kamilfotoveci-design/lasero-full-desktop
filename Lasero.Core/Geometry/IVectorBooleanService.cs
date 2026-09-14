using Lasero.Core.Grbl;

namespace Lasero.Core.Geometry;

/// <summary>Which pixels/points a set of closed rings covers, when two or more rings overlap.
/// EvenOdd: covered by an odd number of rings. NonZero: covered by at least one ring, with winding
/// direction determining whether a nested ring adds to or subtracts from its parent's coverage.</summary>
public enum VectorFillRule
{
    EvenOdd,
    NonZero,
}

/// <summary>Polygon boolean operations for closed vector shapes, independent of any UI framework.
/// A "compound path" here is the same concept ImportedShape.GeometrySetId already groups: one or
/// more closed rings (an outer boundary plus any holes) that together describe one region, with the
/// held-together relationship expressed purely through fill-rule evaluation — nothing here needs to
/// know, or be told, which ring is a hole.
///
/// Every method takes and returns plain closed-ring point lists (Position, already used everywhere
/// else in Lasero.Core) rather than any Clipper-specific type, so a caller never needs a reference to
/// the underlying clipping library.</summary>
public interface IVectorBooleanService
{
    /// <summary>Normalizes one compound path into its own clean ring set under the given fill rule —
    /// e.g. an outer contour together with a properly nested hole collapses to the outer ring plus an
    /// oppositely-wound hole ring. Not a general "merge any overlapping shapes into one" operation:
    /// under EvenOdd specifically, two rings that partially overlap without one nesting inside the
    /// other have their shared area excluded, not merged (call Union for that instead) — see
    /// Clipper2VectorBooleanServiceTests for the exact distinction.</summary>
    IReadOnlyList<IReadOnlyList<Position>> Resolve(
        IReadOnlyList<IReadOnlyList<Position>> rings, VectorFillRule fillRule);

    /// <summary>The set of points covered by subject or clip (or both).</summary>
    IReadOnlyList<IReadOnlyList<Position>> Union(
        IReadOnlyList<IReadOnlyList<Position>> subject,
        IReadOnlyList<IReadOnlyList<Position>> clip,
        VectorFillRule fillRule);

    /// <summary>The set of points covered by subject but not by clip.</summary>
    IReadOnlyList<IReadOnlyList<Position>> Subtract(
        IReadOnlyList<IReadOnlyList<Position>> subject,
        IReadOnlyList<IReadOnlyList<Position>> clip,
        VectorFillRule fillRule);

    /// <summary>The set of points covered by both subject and clip.</summary>
    IReadOnlyList<IReadOnlyList<Position>> Intersect(
        IReadOnlyList<IReadOnlyList<Position>> subject,
        IReadOnlyList<IReadOnlyList<Position>> clip,
        VectorFillRule fillRule);

    /// <summary>The set of points covered by exactly one of subject or clip.</summary>
    IReadOnlyList<IReadOnlyList<Position>> Xor(
        IReadOnlyList<IReadOnlyList<Position>> subject,
        IReadOnlyList<IReadOnlyList<Position>> clip,
        VectorFillRule fillRule);
}
