using Lasero.Core.Grbl;

namespace Lasero.Core.Geometry;

public enum VectorJoinType { Round, Miter, Bevel }

public interface IVectorOffsetService
{
    /// One or more CLOSED rings that together form one compound region (an outer ring plus any
    /// properly-nested hole rings, same GeometrySetId convention as IVectorBooleanService) offset
    /// together as one group so growing the outer ring correctly shrinks a nested hole ring by the
    /// same delta (and vice versa for negative delta) rather than treating each ring independently.
    /// Positive distanceMm grows the region outward, negative shrinks it inward. Rings that vanish
    /// (collapse to near-zero area) or become invalid are simply omitted from the result — never
    /// throws for a valid-but-extreme distance.
    IReadOnlyList<IReadOnlyList<Position>> OffsetClosedGroup(
        IReadOnlyList<IReadOnlyList<Position>> rings,
        double distanceMm,
        VectorJoinType joinType,
        double miterLimit);

    /// One OPEN polyline offset into a closed buffer region (both sides of the path plus flat
    /// (butt) end caps) at distanceMm (absolute value used — direction is not meaningful for a
    /// symmetric buffer). Returns the closed ring(s) of the buffer, or empty if distanceMm rounds to
    /// zero or the path is degenerate (fewer than 2 points).
    IReadOnlyList<IReadOnlyList<Position>> OffsetOpenPath(
        IReadOnlyList<Position> path,
        double distanceMm,
        VectorJoinType joinType,
        double miterLimit);
}
