using Lasero.Core.Grbl;

namespace Lasero.Core.Scene;

/// <summary>
/// LightBurn-style Trim and Extend on editable paths (LIGHTBURN_VECTOR_PARITY.md §31/§32), WPF-free.
///
/// Trim removes only the piece of the clicked segment that lies between the nearest crossings with
/// other geometry on either side of the click; with no crossing on a side, the piece runs to the
/// segment's own node there. The cut is exact: the segment is split with De Casteljau
/// (<see cref="VectorPathEditor.InsertNode"/>) at the crossing parameters, so the parts that stay
/// keep their Bézier shape, and only then is the clicked piece deleted.
///
/// Extend lengthens an open end along its tangent to the nearest crossing.
///
/// Crossings are found in WORLD space (the target segment is mapped through its object's transform,
/// Bézier curves being affine-invariant) and applied in the path's own local space.
/// </summary>
public static class VectorPathTrim
{
    private const double EndEpsilon = 1e-6;

    /// <summary>Crossing parameters on <paramref name="targetWorld"/> against every cutter, strictly
    /// inside (0, 1), sorted and de-duplicated. A crossing exactly at a node is the segment's own
    /// boundary already and is left out.</summary>
    public static List<double> CutParameters(BezierSegment targetWorld, IEnumerable<BezierSegment> cuttersWorld)
    {
        ArgumentNullException.ThrowIfNull(cuttersWorld);
        var cuts = new List<double>();
        if (!targetWorld.IsFinite) return cuts;
        foreach (var cutter in cuttersWorld)
            foreach (var hit in BezierIntersection.Intersect(targetWorld, cutter))
                if (hit.T > EndEpsilon && hit.T < 1 - EndEpsilon) cuts.Add(hit.T);
        cuts.Sort();
        var distinct = new List<double>(cuts.Count);
        foreach (var cut in cuts)
            if (distinct.Count == 0 || cut - distinct[^1] > 1e-7) distinct.Add(cut);
        return distinct;
    }

    /// <summary>The parameter range removed by a click at <paramref name="t"/>: from the nearest cut
    /// before it (or 0) to the nearest cut after it (or 1).</summary>
    public static (double From, double To) PieceAround(double t, IReadOnlyList<double> sortedCuts)
    {
        ArgumentNullException.ThrowIfNull(sortedCuts);
        var from = 0.0;
        var to = 1.0;
        foreach (var cut in sortedCuts)
        {
            if (cut <= t) from = Math.Max(from, cut);
            else { to = cut; break; }
        }
        return (from, to);
    }

    /// <summary>Every segment of <paramref name="path"/> mapped to world space, optionally leaving
    /// out one segment (the one being trimmed: it cannot cut itself).</summary>
    public static IEnumerable<BezierSegment> WorldSegments(VectorPath path, Func<Position, Position> toWorld, (int Subpath, int Segment)? exclude = null)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(toWorld);
        for (var s = 0; s < path.Subpaths.Count; s++)
        {
            var subpath = path.Subpaths[s];
            for (var i = 0; i < subpath.SegmentCount; i++)
            {
                if (exclude is { } skip && skip.Subpath == s && skip.Segment == i) continue;
                var (a, b) = subpath.Segment(i);
                yield return BezierSegment.FromNodes(a, b).Map(toWorld);
            }
        }
    }

    /// <summary>Line segments of a flattened polyline (a plain imported/primitive outline as a cutter).</summary>
    public static IEnumerable<BezierSegment> PolylineSegments(IReadOnlyList<Position> points, bool closed)
    {
        ArgumentNullException.ThrowIfNull(points);
        for (var i = 0; i + 1 < points.Count; i++) yield return BezierSegment.Line(points[i], points[i + 1]);
        if (closed && points.Count > 2 && points[0] != points[^1]) yield return BezierSegment.Line(points[^1], points[0]);
    }

    /// <summary>Removes the piece of segment <paramref name="segmentIndex"/> around parameter
    /// <paramref name="t"/> bounded by <paramref name="sortedCuts"/> (see <see cref="PieceAround"/>).
    /// The result may contain fewer subpaths (a short open contour can vanish) or none at all, in
    /// which case the caller deletes the object.</summary>
    public static VectorPath TrimSegment(VectorPath path, int subpathIndex, int segmentIndex, double t, IReadOnlyList<double> sortedCuts)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (subpathIndex < 0 || subpathIndex >= path.Subpaths.Count) throw new ArgumentOutOfRangeException(nameof(subpathIndex));
        var subpath = path.Subpaths[subpathIndex];
        if (segmentIndex < 0 || segmentIndex >= subpath.SegmentCount) throw new ArgumentOutOfRangeException(nameof(segmentIndex));
        if (!double.IsFinite(t)) throw new ArgumentOutOfRangeException(nameof(t));

        var (from, to) = PieceAround(Math.Clamp(t, 0, 1), sortedCuts);
        var cutsStart = from > EndEpsilon;
        var cutsEnd = to < 1 - EndEpsilon;
        if (!cutsStart && !cutsEnd) return VectorPathEditor.DeleteSegment(path, subpathIndex, segmentIndex);

        if (cutsEnd) subpath = VectorPathEditor.InsertNode(subpath, segmentIndex, to);
        if (cutsStart)
        {
            var local = cutsEnd ? from / to : from;
            subpath = VectorPathEditor.InsertNode(subpath, segmentIndex, local);
        }

        var removed = cutsStart ? segmentIndex + 1 : segmentIndex;
        return VectorPathEditor.DeleteSegment(path.ReplaceSubpath(subpathIndex, subpath), subpathIndex, removed);
    }

    /// <summary>Extends an open end of subpath <paramref name="subpathIndex"/> along its tangent to
    /// the nearest crossing with <paramref name="worldCutters"/> within <paramref name="reachMm"/>.
    /// A straight last segment is lengthened (its end node moves); a curved one gets a straight
    /// continuation ending in a new corner node, so the curve itself is untouched. False when the end
    /// is not open, has no usable tangent, or nothing lies ahead of it.</summary>
    public static bool TryExtend(
        VectorPath path, int subpathIndex, bool atStart,
        Func<Position, Position> toWorld, Func<Position, Position> toLocal,
        IEnumerable<BezierSegment> worldCutters, double reachMm, out VectorPath extended)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(toWorld);
        ArgumentNullException.ThrowIfNull(toLocal);
        ArgumentNullException.ThrowIfNull(worldCutters);
        extended = path;
        if (subpathIndex < 0 || subpathIndex >= path.Subpaths.Count) return false;
        var subpath = path.Subpaths[subpathIndex];
        if (subpath.IsClosed || subpath.Nodes.Count < 2) return false;

        var endIndex = atStart ? 0 : subpath.Nodes.Count - 1;
        var neighbourIndex = atStart ? 1 : subpath.Nodes.Count - 2;
        var end = subpath.Nodes[endIndex];
        var neighbour = subpath.Nodes[neighbourIndex];

        // Point the curve comes FROM as it reaches the end; the extension continues away from it.
        var facingHandle = atStart ? end.HandleOut : end.HandleIn;
        var neighbourHandle = atStart ? neighbour.HandleIn : neighbour.HandleOut;
        var source = facingHandle is { } h && Distance(h, end.Anchor) > 1e-9 ? h
            : neighbourHandle is { } nh && Distance(nh, end.Anchor) > 1e-9 ? nh
            : neighbour.Anchor;

        var endWorld = toWorld(end.Anchor);
        var sourceWorld = toWorld(source);
        var direction = (X: endWorld.X - sourceWorld.X, Y: endWorld.Y - sourceWorld.Y);
        var length = Math.Sqrt(direction.X * direction.X + direction.Y * direction.Y);
        if (length < 1e-9 || !double.IsFinite(length)) return false;

        var nearest = double.PositiveInfinity;
        foreach (var cutter in worldCutters)
            foreach (var distance in BezierIntersection.RayDistances(endWorld, direction, reachMm, cutter))
                if (distance > 1e-6 && distance < nearest) nearest = distance;
        if (!double.IsFinite(nearest)) return false;

        var hitWorld = new Position(endWorld.X + direction.X / length * nearest, endWorld.Y + direction.Y / length * nearest, endWorld.Z);
        var hitLocal = toLocal(hitWorld);
        if (!double.IsFinite(hitLocal.X) || !double.IsFinite(hitLocal.Y)) return false;

        var nodes = subpath.Nodes.ToList();
        var endSegmentStraight = atStart
            ? VectorSubpath.IsStraightSegment(end, neighbour)
            : VectorSubpath.IsStraightSegment(neighbour, end);
        if (endSegmentStraight)
        {
            nodes[endIndex] = end.Translated(hitLocal.X - end.Anchor.X, hitLocal.Y - end.Anchor.Y);
        }
        else if (atStart)
        {
            nodes[0] = end with { HandleIn = null };
            nodes.Insert(0, VectorNode.CornerAt(hitLocal));
        }
        else
        {
            nodes[^1] = end with { HandleOut = null };
            nodes.Add(VectorNode.CornerAt(hitLocal));
        }

        extended = path.ReplaceSubpath(subpathIndex, subpath with { Nodes = nodes });
        return true;
    }

    private static double Distance(Position a, Position b) =>
        Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));
}
