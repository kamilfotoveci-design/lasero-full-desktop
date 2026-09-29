using Lasero.Core.Grbl;

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
                var (a, b) = subpath.Segment(seg);
                var mid = VectorSubpath.IsStraightSegment(a, b)
                    ? CubicBezier.Lerp(a.Anchor, b.Anchor, 0.5)
                    : CubicBezier.Evaluate(a.Anchor, a.HandleOut ?? a.Anchor, b.HandleIn ?? b.Anchor, b.Anchor, 0.5);
                candidates.Add(new SnapCandidate(toWorld(mid), SnapKind.Midpoint));
            }
        }

        AddIntersectionCandidates(path, toWorld, candidates);
        return candidates;
    }

    /// <summary>Path-intersection snap targets, scoped to this VectorPath's OWN subpaths crossing each
    /// other (e.g. an outer contour crossing a hole, or two overlapping hand-drawn subpaths in one
    /// compound object) — true cross-OBJECT intersection detection against the rest of the scene is out
    /// of scope for this pass (would need a scene-wide spatial query this builder has no access to; see
    /// docs/reference/NODE_EDIT_PARITY_AUDIT_2026-09-16.md). Tested on already-flattened polylines
    /// (straight-segment intersection), not the raw Bezier math, since "does this curve cross that
    /// curve" reduces cleanly to that once both are flattened and the flatten tolerance is already far
    /// finer than any reasonable snap tolerance.</summary>
    private static void AddIntersectionCandidates(
        VectorPath path, Func<Position, Position> toWorld, List<SnapCandidate> candidates)
    {
        if (path.Subpaths.Count < 2) return;

        var flattenedPerSubpath = path.Subpaths.Select(sp => sp.Flatten()).ToList();
        if (flattenedPerSubpath.Sum(points => points.Count) > MaxFlattenedPointsForIntersectionSearch) return;

        for (var i = 0; i < flattenedPerSubpath.Count; i++)
        {
            var a = flattenedPerSubpath[i];
            for (var j = i + 1; j < flattenedPerSubpath.Count; j++)
            {
                var b = flattenedPerSubpath[j];
                for (var ai = 0; ai < a.Count - 1; ai++)
                for (var bi = 0; bi < b.Count - 1; bi++)
                {
                    if (TrySegmentIntersection(a[ai], a[ai + 1], b[bi], b[bi + 1], out var hit))
                        candidates.Add(new SnapCandidate(toWorld(hit), SnapKind.Intersection));
                }
            }
        }
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
