using Lasero.Core.Grbl;
using Lasero.Core.Import;
using Lasero.Core.Scene;

namespace Lasero.Core.Geometry;

public enum VectorOffsetResultKind { Outward, Inward, OpenPath }
public sealed record VectorOffsetPart(VectorOffsetResultKind Kind, VectorPath Path);

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

    /// <summary>The Offset dialog's computation: the WHOLE selection is one region. Closed shapes
    /// are first united (each GeometrySetId group under NonZero, the contract the canvas and toolpath
    /// fill follow, then the groups of all sources together), so overlapping or touching shapes become
    /// one outline and no ring is offset inside another. That union is then offset once.
    ///
    /// Returns the objects to create: one path for Outward or Inward, two for Both (outward first),
    /// plus one more holding the buffered outlines of any open paths in the selection. Open paths
    /// ignore Direction (a buffer has two sides). Empty when nothing survived the offset. Only outer
    /// shapes: the region's outer boundaries are kept and every hole and nested island dropped before
    /// offsetting, so Inward shrinks the outer silhouette instead of growing the counters.</summary>
    public static IReadOnlyList<VectorPath> ComputeMergedOffset(
        IReadOnlyList<SceneObject> sources,
        IVectorBooleanService booleans,
        IVectorOffsetService service,
        VectorOffsetOptions options,
        CancellationToken cancellationToken = default) =>
        ComputeMergedOffsetParts(sources, booleans, service, options, cancellationToken)
            .Select(part => part.Path).ToList();

    public static IReadOnlyList<VectorOffsetPart> ComputeMergedOffsetParts(
        IReadOnlyList<SceneObject> sources,
        IVectorBooleanService booleans,
        IVectorOffsetService service,
        VectorOffsetOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(booleans);
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(options);

        var distance = Math.Abs(options.DistanceMm);
        if (!double.IsFinite(distance) || distance < MinimumDistanceMm) return [];
        var miterLimit = Math.Max(1.0, options.MiterLimit);
        var results = new List<VectorOffsetPart>();

        var region = BuildRegion(sources, booleans, cancellationToken);
        var basis = options.OnlyOuterShapes ? OuterBoundaries(region) : region;
        if (basis.Count > 0)
        {
            if (options.Direction != VectorOffsetDirection.Inward)
            {
                cancellationToken.ThrowIfCancellationRequested();
                AddResult(results, VectorOffsetResultKind.Outward,
                    service.OffsetClosedGroup(basis, distance, options.JoinType, miterLimit), options);
            }
            if (options.Direction != VectorOffsetDirection.Outward)
            {
                cancellationToken.ThrowIfCancellationRequested();
                AddResult(results, VectorOffsetResultKind.Inward,
                    service.OffsetClosedGroup(basis, -distance, options.JoinType, miterLimit), options);
            }
        }

        var buffers = new List<IReadOnlyList<Position>>();
        foreach (var source in sources)
        {
            foreach (var shape in source.GetWorldShapes().Where(shape => !shape.IsClosed && shape.Points.Count >= 2))
            {
                cancellationToken.ThrowIfCancellationRequested();
                buffers.AddRange(service.OffsetOpenPath(shape.Points, distance, options.JoinType, miterLimit));
            }
        }
        if (buffers.Count > 0)
            AddResult(results, VectorOffsetResultKind.OpenPath,
                booleans.Union(buffers, [], VectorFillRule.NonZero), options);

        return results;
    }

    /// <summary>Distances below this are no offset at all (and below the geometry's own precision).</summary>
    private const double MinimumDistanceMm = 1e-6;

    /// <summary>Rings below this area are slivers left over from the boolean pass, not shapes.</summary>
    private const double MinimumRingAreaSquareMm = 1e-6;

    /// <summary>The union of every closed shape of every source as clean rings (outers one winding,
    /// holes the other). Per source and GeometrySetId the rings are resolved under NonZero, then all
    /// groups are united pairwise.</summary>
    private static IReadOnlyList<IReadOnlyList<Position>> BuildRegion(
        IReadOnlyList<SceneObject> sources, IVectorBooleanService booleans, CancellationToken cancellationToken)
    {
        var parts = new List<IReadOnlyList<IReadOnlyList<Position>>>();
        foreach (var source in sources)
        {
            var closedShapes = source.GetWorldShapes().Where(shape => shape.IsClosed && shape.Points.Count >= 3);
            foreach (var group in closedShapes.GroupBy(shape => shape.GeometrySetId))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var resolved = booleans.Union(group.Select(shape => shape.Points).ToList(), [], VectorFillRule.NonZero);
                if (resolved.Count > 0) parts.Add(resolved);
            }
        }

        while (parts.Count > 1)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var next = new List<IReadOnlyList<IReadOnlyList<Position>>>((parts.Count + 1) / 2);
            for (var index = 0; index < parts.Count; index += 2)
            {
                next.Add(index + 1 < parts.Count
                    ? booleans.Union(parts[index], parts[index + 1], VectorFillRule.NonZero)
                    : parts[index]);
            }
            parts = next;
        }

        return parts.Count == 0 ? [] : parts[0];
    }

    /// <summary>The outer boundaries of a resolved region: rings wound like the largest ring (always
    /// an outer one), minus any that sit inside a hole (islands in a counter, at any depth).</summary>
    private static List<IReadOnlyList<Position>> OuterBoundaries(IReadOnlyList<IReadOnlyList<Position>> region)
    {
        var rings = region
            .Select(ring => (Ring: ring, Area: SignedArea(ring)))
            .Where(item => Math.Abs(item.Area) > MinimumRingAreaSquareMm)
            .ToList();
        if (rings.Count == 0) return [];

        var outerSign = Math.Sign(rings.MaxBy(item => Math.Abs(item.Area)).Area);
        var holes = rings.Where(item => Math.Sign(item.Area) != outerSign).Select(item => item.Ring).ToList();
        var holeBounds = holes.Select(BoundsOf).ToList();

        var outers = new List<IReadOnlyList<Position>>();
        foreach (var (ring, area) in rings)
        {
            if (Math.Sign(area) != outerSign) continue;
            var bounds = BoundsOf(ring);
            var insideHole = false;
            for (var index = 0; index < holes.Count && !insideHole; index++)
                insideHole = Contains(holeBounds[index], bounds) && MostlyInside(ring, holes[index]);
            if (!insideHole) outers.Add(ring);
        }
        return outers;
    }

    private static void AddResult(
        List<VectorOffsetPart> results, VectorOffsetResultKind kind,
        IReadOnlyList<IReadOnlyList<Position>> rings, VectorOffsetOptions options)
    {
        var subpaths = new List<VectorSubpath>(rings.Count);
        foreach (var ring in rings)
        {
            var points = options.Optimize ? RingSimplifier.SimplifyClosed(ring, VectorOffsetOptions.OptimizeToleranceMm) : ring;
            if (points.Count < 3) continue;
            subpaths.Add(new VectorSubpath { Nodes = points.Select(VectorNode.CornerAt).ToList(), IsClosed = true });
        }
        if (subpaths.Count > 0)
            results.Add(new VectorOffsetPart(kind, new VectorPath { Subpaths = subpaths }));
    }

    private static (double MinX, double MinY, double MaxX, double MaxY) BoundsOf(IReadOnlyList<Position> ring)
    {
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        foreach (var point in ring)
        {
            if (point.X < minX) minX = point.X;
            if (point.Y < minY) minY = point.Y;
            if (point.X > maxX) maxX = point.X;
            if (point.Y > maxY) maxY = point.Y;
        }
        return (minX, minY, maxX, maxY);
    }

    private static bool Contains(
        (double MinX, double MinY, double MaxX, double MaxY) outer,
        (double MinX, double MinY, double MaxX, double MaxY) inner)
    {
        const double margin = 1e-4;
        return inner.MinX >= outer.MinX - margin && inner.MinY >= outer.MinY - margin &&
               inner.MaxX <= outer.MaxX + margin && inner.MaxY <= outer.MaxY + margin;
    }

    /// <summary>True when most of a sample of the ring's vertices lie inside the polygon. A vertex
    /// that merely touches the polygon's boundary cannot flip the answer.</summary>
    private static bool MostlyInside(IReadOnlyList<Position> ring, IReadOnlyList<Position> polygon)
    {
        var samples = Math.Min(ring.Count, 9);
        var inside = 0;
        for (var sample = 0; sample < samples; sample++)
        {
            if (IsPointInsidePolygon(ring[sample * ring.Count / samples], polygon)) inside++;
        }
        return inside * 2 > samples;
    }

    private static bool IsPointInsidePolygon(Position point, IReadOnlyList<Position> polygon)
    {
        var inside = false;
        for (int current = 0, previous = polygon.Count - 1; current < polygon.Count; previous = current++)
        {
            var a = polygon[current];
            var b = polygon[previous];
            if ((a.Y > point.Y) == (b.Y > point.Y)) continue;
            var crossingX = (b.X - a.X) * (point.Y - a.Y) / (b.Y - a.Y) + a.X;
            if (point.X < crossingX) inside = !inside;
        }
        return inside;
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
