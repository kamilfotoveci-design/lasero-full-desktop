using Lasero.Core.Grbl;
using Lasero.Core.Import;
using Lasero.Core.Layers;

namespace Lasero.Core.Scene.Snapping;

/// <summary>
/// Builds SnapEngine candidates from one VectorPath's own geometry, in whatever coordinate space
/// <paramref name="toWorld"/> maps into. Kept as a delegate rather than a SceneObject/ObjectTransform
/// parameter so this stays usable from a context that has no live SceneObject yet (e.g. a future
/// "continue drawing from an existing path's endpoint" preview, which only needs a path and a
/// transform, not a full scene selection).
///
/// Candidates are meant to be built ONCE per drag-gesture (at mouse-down, from the pre-drag path
/// snapshot every node-edit drag already keeps for its own undo/cancel contract — see
/// _nodeDragOriginalPath in SceneCanvas.VectorPathTool.cs) and then queried many times via
/// SnapEngine.Resolve as the pointer moves. This is both correct (per LIGHTBURN_VECTOR_PARITY-style
/// "preview = original + delta, never cumulative" convention — the candidate set must not drift
/// mid-drag just because the dragged node itself is moving) and cheap (segment-intersection detection
/// below is the most expensive part; doing it once per gesture instead of once per pointer-move frame
/// keeps it off the hot path entirely).
/// </summary>
public static class SnapCandidateBuilder
{
    /// <summary>Caps the flattened-point budget for intersection detection (an O(n²) pairwise segment
    /// test). A hand-drawn path or typical SVG import stays far under this; a pathological
    /// many-thousand-node import skips intersection candidates rather than stalling the first frame of
    /// a drag — node/endpoint/midpoint snapping (all O(n)) still work.</summary>
    private const int MaxFlattenedPointsForIntersectionSearch = 2000;

    /// <summary>
    /// Builds snap points from eligible scene geometry. Hidden or locked objects never contribute;
    /// shapes whose owning layer is hidden/locked (or missing) are also excluded. Layer membership
    /// is checked per shape so mixed-layer objects cannot leak hidden geometry into snapping.
    /// Positions are returned in world coordinates. Call once at gesture start, like
    /// <see cref="FromVectorPath"/>.
    /// </summary>
    public static List<SnapCandidate> FromSceneObjects(
        IEnumerable<SceneObject> objects,
        IReadOnlyDictionary<Guid, LayerSettings> layersById,
        Guid? excludeObjectId = null)
    {
        ArgumentNullException.ThrowIfNull(objects);
        ArgumentNullException.ThrowIfNull(layersById);

        var candidates = new List<SnapCandidate>();
        var eligibleShapes = new List<(IReadOnlyList<Position> Points, bool IsClosed)>();

        foreach (var obj in objects)
        {
            if (obj is null || !obj.IsVisible || obj.IsLocked || obj.Id == excludeObjectId) continue;

            var worldShapes = obj.GetWorldShapes();
            var eligibleForObject = new List<ImportedShape>();
            foreach (var shape in worldShapes)
            {
                if (!IsLayerEligible(shape, layersById)) continue;
                if (shape.Points.Count == 0) continue;

                eligibleShapes.Add((shape.Points, shape.IsClosed));
                eligibleForObject.Add(shape);
            }

            // Editable paths expose authored anchors only when every cached contour belongs to an
            // eligible layer. For mixed-layer objects, use eligible flattened contours below; this
            // avoids leaking nodes from a hidden/locked contour with no subpath-to-layer mapping.
            if (obj.VectorPath is not null && worldShapes.Count > 0 && eligibleForObject.Count == worldShapes.Count)
            {
                var worldPath = obj.GetWorldVectorPath()!;
                foreach (var subpath in worldPath.Subpaths)
                {
                    for (var i = 0; i < subpath.Nodes.Count; i++)
                    {
                        var endpoint = !subpath.IsClosed && (i == 0 || i == subpath.Nodes.Count - 1);
                        candidates.Add(new SnapCandidate(subpath.Nodes[i].Anchor,
                            endpoint ? SnapKind.Endpoint : SnapKind.Node));
                    }

                    for (var i = 0; i < subpath.SegmentCount; i++)
                    {
                        var (a, b) = subpath.Segment(i);
                        var midpoint = VectorSubpath.IsStraightSegment(a, b)
                            ? CubicBezier.Lerp(a.Anchor, b.Anchor, 0.5)
                            : CubicBezier.Evaluate(a.Anchor, a.HandleOut ?? a.Anchor,
                                b.HandleIn ?? b.Anchor, b.Anchor, 0.5);
                        candidates.Add(new SnapCandidate(midpoint, SnapKind.Midpoint));
                    }
                }
            }
            else
            {
                foreach (var shape in eligibleForObject)
                    AddPolylineCandidates(shape.Points, shape.IsClosed, candidates);
            }
        }

        AddSceneIntersections(eligibleShapes, candidates);
        return candidates;
    }

    private static bool IsLayerEligible(ImportedShape shape,
        IReadOnlyDictionary<Guid, LayerSettings> layersById)
    {
        if (shape.LayerId != Guid.Empty && layersById.TryGetValue(shape.LayerId, out var layer))
            return layer is { IsVisible: true, IsLocked: false };

        // Legacy shapes may not carry stable IDs. Resolve by their visual layer color, but fail
        // closed when no unique, eligible scene layer exists so hidden/ambiguous geometry cannot snap.
        var matches = layersById.Values.Where(candidate => candidate.Color.IsApproximately(shape.LayerColor)).ToList();
        return matches.Count > 0 && matches.All(candidate => candidate.IsVisible && !candidate.IsLocked);
    }

    private static void AddPolylineCandidates(IReadOnlyList<Position> points, bool closed,
        List<SnapCandidate> candidates)
    {
        for (var i = 0; i < points.Count; i++)
        {
            var endpoint = !closed && (i == 0 || i == points.Count - 1);
            candidates.Add(new SnapCandidate(points[i], endpoint ? SnapKind.Endpoint : SnapKind.Node));
        }

        var segmentCount = SegmentCount(points, closed);
        for (var i = 0; i < segmentCount; i++)
            candidates.Add(new SnapCandidate(CubicBezier.Lerp(points[i], points[(i + 1) % points.Count], 0.5), SnapKind.Midpoint));
    }

    private static void AddSceneIntersections(
        IReadOnlyList<(IReadOnlyList<Position> Points, bool IsClosed)> shapes,
        List<SnapCandidate> candidates)
    {
        var totalPoints = shapes.Sum(shape => shape.Points.Count);
        if (totalPoints > MaxFlattenedPointsForIntersectionSearch) return;

        for (var si = 0; si < shapes.Count; si++)
        {
            var a = shapes[si];
            var aSegments = SegmentCount(a.Points, a.IsClosed);
            for (var sj = si; sj < shapes.Count; sj++)
            {
                var b = shapes[sj];
                var bSegments = SegmentCount(b.Points, b.IsClosed);
                for (var ai = 0; ai < aSegments; ai++)
                for (var bi = sj == si ? ai + 1 : 0; bi < bSegments; bi++)
                {
                    if (sj == si && AreAdjacentSegments(ai, bi, aSegments, a.IsClosed)) continue;
                    if (TrySegmentIntersection(a.Points[ai], a.Points[(ai + 1) % a.Points.Count],
                            b.Points[bi], b.Points[(bi + 1) % b.Points.Count], out var hit))
                        candidates.Add(new SnapCandidate(hit, SnapKind.Intersection));
                }
            }
        }
    }

    private static int SegmentCount(IReadOnlyList<Position> points, bool closed) =>
        points.Count < 2 ? 0 : closed && points[0] != points[^1] ? points.Count : points.Count - 1;

    private static bool AreAdjacentSegments(int a, int b, int count, bool closed) =>
        Math.Abs(a - b) == 1 || (closed && a == 0 && b == count - 1);

    public static List<SnapCandidate> FromVectorPath(
        VectorPath path,
        Func<Position, Position> toWorld,
        IReadOnlySet<(int Subpath, int Node)>? exclude = null)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(toWorld);

        var candidates = new List<SnapCandidate>();
        for (var s = 0; s < path.Subpaths.Count; s++)
        {
            var subpath = path.Subpaths[s];
            var nodes = subpath.Nodes;
            for (var n = 0; n < nodes.Count; n++)
            {
                if (exclude?.Contains((s, n)) == true) continue; // never snap a dragged node onto itself
                var isEndpoint = !subpath.IsClosed && (n == 0 || n == nodes.Count - 1);
                candidates.Add(new SnapCandidate(toWorld(nodes[n].Anchor), isEndpoint ? SnapKind.Endpoint : SnapKind.Node));
            }

            for (var seg = 0; seg < subpath.SegmentCount; seg++)
            {
                // A segment touching a dragged node moves with the drag: its pre-drag midpoint is a
                // stale target that the node would otherwise jump onto.
                if (TouchesExcluded(subpath, s, seg, exclude)) continue;
                var (a, b) = subpath.Segment(seg);
                var mid = VectorSubpath.IsStraightSegment(a, b)
                    ? CubicBezier.Lerp(a.Anchor, b.Anchor, 0.5)
                    : CubicBezier.Evaluate(a.Anchor, a.HandleOut ?? a.Anchor, b.HandleIn ?? b.Anchor, b.Anchor, 0.5);
                candidates.Add(new SnapCandidate(toWorld(mid), SnapKind.Midpoint));
            }
        }

        AddIntersectionCandidates(path, toWorld, exclude, candidates);
        return candidates;
    }

    /// <summary>Most Bézier segments of one path checked pairwise for crossings (an O(n²) search with
    /// a cheap bounds rejection per pair). A larger path skips intersection candidates rather than
    /// stalling the first frame of a drag; node/endpoint/midpoint snapping still work.</summary>
    private const int MaxSegmentsForIntersectionSearch = 600;

    /// <summary>Path-intersection snap targets from this VectorPath's own geometry: subpaths crossing
    /// each other (an outer contour crossing a hole) AND one subpath crossing itself (a figure-8, a
    /// loop). Computed on the Bézier segments themselves (<see cref="BezierIntersection"/>), so a
    /// crossing of two curves is exact rather than a polyline approximation. Segments that meet at a
    /// shared node are not tested against each other (their common node is already a candidate), and
    /// segments touching a dragged node are skipped (their crossings move with the drag). Scene-wide
    /// intersections are built by <see cref="FromSceneObjects"/>.</summary>
    private static void AddIntersectionCandidates(
        VectorPath path, Func<Position, Position> toWorld, IReadOnlySet<(int Subpath, int Node)>? exclude,
        List<SnapCandidate> candidates)
    {
        var segments = new List<(int Subpath, int Index, int Count, bool Closed, BezierSegment Curve)>();
        for (var s = 0; s < path.Subpaths.Count; s++)
        {
            var subpath = path.Subpaths[s];
            for (var i = 0; i < subpath.SegmentCount; i++)
            {
                if (TouchesExcluded(subpath, s, i, exclude)) continue;
                var (a, b) = subpath.Segment(i);
                segments.Add((s, i, subpath.SegmentCount, subpath.IsClosed, BezierSegment.FromNodes(a, b)));
            }
        }
        if (segments.Count > MaxSegmentsForIntersectionSearch) return;

        for (var i = 0; i < segments.Count; i++)
        {
            for (var j = i + 1; j < segments.Count; j++)
            {
                var first = segments[i];
                var second = segments[j];
                if (first.Subpath == second.Subpath && AreAdjacentSegments(first.Index, second.Index, first.Count, first.Closed))
                    continue;
                foreach (var hit in BezierIntersection.Intersect(first.Curve, second.Curve))
                    candidates.Add(new SnapCandidate(toWorld(hit.Point), SnapKind.Intersection));
            }
        }
    }

    private static bool TouchesExcluded(VectorSubpath subpath, int subpathIndex, int segmentIndex, IReadOnlySet<(int Subpath, int Node)>? exclude)
    {
        if (exclude is null || exclude.Count == 0) return false;
        var next = (segmentIndex + 1) % subpath.Nodes.Count;
        return exclude.Contains((subpathIndex, segmentIndex)) || exclude.Contains((subpathIndex, next));
    }

    /// <summary>Standard parametric 2D segment-segment intersection (both t and u must land inside
    /// [0,1] for a real crossing, not just an intersection of the infinite lines through them).
    /// Returns false for parallel/collinear segments rather than trying to pick a point along an
    /// overlapping run — an overlap has no single "the" intersection point to snap to.</summary>
    private static bool TrySegmentIntersection(Position p1, Position p2, Position p3, Position p4, out Position hit)
    {
        hit = default;
        var d1X = p2.X - p1.X;
        var d1Y = p2.Y - p1.Y;
        var d2X = p4.X - p3.X;
        var d2Y = p4.Y - p3.Y;
        var denominator = d1X * d2Y - d1Y * d2X;
        if (Math.Abs(denominator) < 1e-9) return false;

        var t = ((p3.X - p1.X) * d2Y - (p3.Y - p1.Y) * d2X) / denominator;
        var u = ((p3.X - p1.X) * d1Y - (p3.Y - p1.Y) * d1X) / denominator;
        if (t is < 0 or > 1 || u is < 0 or > 1) return false;

        hit = new Position(p1.X + t * d1X, p1.Y + t * d1Y, p1.Z);
        return true;
    }
}
