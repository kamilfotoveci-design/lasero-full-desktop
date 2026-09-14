using Lasero.Core.Grbl;
using Lasero.Core.Import;
using Lasero.Core.Scene;

namespace Lasero.Core.Geometry;

/// <summary>Turns one SceneObject's world-space geometry into an offset VectorPath. Shared by
/// SceneViewModel.ApplyOffset (the committed result) and OffsetPathViewModel (the live preview) so
/// the two can never diverge — both call ComputeOffset with the same IVectorOffsetService.</summary>
public static class VectorOffsetPlanner
{
    /// <summary>The offset of one source object's complete world geometry, or null if nothing survived
    /// the offset at this distance (every contributing ring collapsed/vanished — see
    /// IVectorOffsetService). Each of the source's GeometrySetId groups is offset on its own (a closed
    /// group together, so an outer ring growing correctly shrinks a nested hole ring by the same
    /// delta; each open subpath individually, since a butt-capped buffer has no "together" to share
    /// with another open path) and every resulting ring becomes one closed VectorSubpath of corner
    /// nodes in the returned path.</summary>
    public static VectorPath? ComputeOffset(
        SceneObject source,
        IVectorOffsetService service,
        double distanceMm,
        VectorJoinType joinType,
        double miterLimit)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(service);

        var rings = ComputeOffsetRings(source.GetWorldShapes(), service, distanceMm, joinType, miterLimit);
        if (rings.Count == 0) return null;

        var subpaths = rings.Select(ring => new VectorSubpath
        {
            Nodes = ring.Select(VectorNode.CornerAt).ToList(),
            IsClosed = true,
        }).ToList();

        return new VectorPath { Subpaths = subpaths };
    }

    private static List<IReadOnlyList<Position>> ComputeOffsetRings(
        IReadOnlyList<ImportedShape> worldShapes,
        IVectorOffsetService service,
        double distanceMm,
        VectorJoinType joinType,
        double miterLimit)
    {
        var rings = new List<IReadOnlyList<Position>>();
        foreach (var group in worldShapes.GroupBy(shape => shape.GeometrySetId))
        {
            var closedRings = group
                .Where(shape => shape.IsClosed && shape.Points.Count >= 3)
                .Select(shape => shape.Points)
                .ToList();
            if (closedRings.Count > 0)
                rings.AddRange(service.OffsetClosedGroup(closedRings, distanceMm, joinType, miterLimit));

            foreach (var openShape in group.Where(shape => !shape.IsClosed && shape.Points.Count >= 2))
                rings.AddRange(service.OffsetOpenPath(openShape.Points, distanceMm, joinType, miterLimit));
        }
        return rings;
    }
}
