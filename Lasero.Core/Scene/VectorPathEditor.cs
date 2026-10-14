using Lasero.Core.GCode;
using Lasero.Core.Grbl;

namespace Lasero.Core.Scene;

/// <summary>What joining two open ends does with the gap between them.</summary>
public enum EndpointJoinMode
{
    /// <summary>Merge ends that already touch (within VectorPathEditor.JoinMergeToleranceMm), connect
    /// ends further apart with a straight segment.</summary>
    Auto,
    /// <summary>Always make the two ends one node, at their midpoint.</summary>
    Merge,
    /// <summary>Always keep both ends and connect them with a straight segment.</summary>
    Segment,
}

/// <summary>
/// Pure node-editing operations on a VectorSubpath — add/delete/convert/close/move, all returning a
/// new VectorSubpath (the model is immutable) rather than mutating in place, so callers can build one
/// ReplaceObjectsCommand per gesture the same way CommitTextEdit rebuilds a whole SceneObject per
/// wording change. No WPF/canvas types are referenced here — this is deliberately unit-testable
/// without a live app.
/// </summary>
public static class VectorPathEditor
{
    /// <summary>Two endpoints closer than this are the same point: joining them merges them into one
    /// node instead of drawing a hair-thin connecting segment, and closing a path whose last node sits
    /// on its first drops the duplicate instead of tracing a zero-length closing segment.</summary>
    public const double JoinMergeToleranceMm = 0.01;

    /// <summary>Maximum shape deviation allowed for a delete-node refit. If a single cubic cannot
    /// approximate the two original segments this closely, the model keeps the node; Ctrl+Delete is
    /// the explicit no-refit operation.</summary>
    public const double RefitToleranceMm = 0.1;

    /// <summary>A closed contour needs at least this many distinct anchors. Two would trace the same
    /// line out and back, which a laser burns twice.</summary>
    public const int MinimumClosedDistinctNodes = 3;

    /// <summary>Adds a corner node (click) or a smooth node with symmetric handles (click-drag) to
    /// the end of an in-progress open subpath. dragOffset is the drag distance from the click point,
    /// in the same units as the anchor — a smooth node's outgoing handle is placed at anchor +
    /// dragOffset and, symmetrically, its incoming handle at anchor - dragOffset, exactly like every
    /// mainstream pen tool's "drag while placing a point" gesture.</summary>
    public static VectorSubpath AppendNode(VectorSubpath subpath, Position anchor, Position? dragOffset) =>
        AppendNode(subpath, anchor, dragOffset, breakHandle: false);

    /// <summary>As <see cref="AppendNode(VectorSubpath,Position,Position?)"/>; with
    /// <paramref name="breakHandle"/> (Alt held while dragging) the drag only creates the OUTGOING
    /// handle and the node is a cusp, so the segment arriving at it stays as it was.</summary>
    public static VectorSubpath AppendNode(VectorSubpath subpath, Position anchor, Position? dragOffset, bool breakHandle)
    {
        ArgumentNullException.ThrowIfNull(subpath);
        if (!IsFinite(anchor)) throw new ArgumentOutOfRangeException(nameof(anchor));
        var node = BuildNode(anchor, dragOffset, breakHandle);
        return subpath with { Nodes = [.. subpath.Nodes, node] };
    }

    /// <summary>Number of different anchor positions in the contour once consecutive duplicates (and,
    /// for a contour that is or would be closed, a last node sitting on the first) are counted once.</summary>
    public static int CountDistinctAnchors(VectorSubpath subpath, bool asClosed)
    {
        ArgumentNullException.ThrowIfNull(subpath);
        var nodes = subpath.Nodes;
        if (nodes.Count == 0) return 0;
        var count = 1;
        for (var i = 1; i < nodes.Count; i++)
            if (Distance(nodes[i].Anchor, nodes[i - 1].Anchor) > JoinMergeToleranceMm) count++;
        if (asClosed && count > 1 && Distance(nodes[^1].Anchor, nodes[0].Anchor) <= JoinMergeToleranceMm) count--;
        return count;
    }

    /// <summary>True when closing this open contour gives a real outline: at least
    /// <see cref="MinimumClosedDistinctNodes"/> distinct anchors. Every close path (pen click on the
    /// first node, the toolbar, the menu, dragging one end onto the other) asks this one question.</summary>
    public static bool CanClose(VectorSubpath subpath)
    {
        ArgumentNullException.ThrowIfNull(subpath);
        return !subpath.IsClosed && CountDistinctAnchors(subpath, asClosed: true) >= MinimumClosedDistinctNodes;
    }

    /// <summary>Closes an open subpath onto its own first node — the gesture behind "hover the first
    /// node, click to close". Any drag handle placed while making this closing click becomes the FIRST
    /// node's incoming handle; its outgoing handle is kept collinear (its own length, the opposite
    /// direction), or mirrored from the drag when it had none, so the first node really is smooth.
    /// With <paramref name="breakHandle"/> only the incoming handle is set and the node is a cusp.
    ///
    /// A last node lying on the first one (within <see cref="JoinMergeToleranceMm"/>) is merged into
    /// it rather than kept as a zero-length closing segment. A contour that would have fewer than
    /// <see cref="MinimumClosedDistinctNodes"/> distinct anchors is returned unchanged (still open):
    /// see <see cref="CanClose"/>.</summary>
    public static VectorSubpath Close(VectorSubpath subpath, Position? dragOffset = null, bool breakHandle = false)
    {
        ArgumentNullException.ThrowIfNull(subpath);
        // A two-node contour would add the same segment in reverse as its closing edge,
        // causing duplicate cutting/burning. Count distinct anchors so duplicate endpoints do not
        // create a zero-length closing segment either.
        if (subpath.IsClosed || !CanClose(subpath)) return subpath;

        var nodes = subpath.Nodes.ToList();
        while (nodes.Count > 1 && Distance(nodes[^1].Anchor, nodes[0].Anchor) <= JoinMergeToleranceMm)
        {
            var last = nodes[^1];
            var first = nodes[0];
            var shift = (X: first.Anchor.X - last.Anchor.X, Y: first.Anchor.Y - last.Anchor.Y);
            Position? handleIn = last.HandleIn is { } hi ? new Position(hi.X + shift.X, hi.Y + shift.Y, hi.Z) : null;
            nodes[0] = first with { HandleIn = handleIn };
            nodes.RemoveAt(nodes.Count - 1);
        }

        if (HasMagnitude(dragOffset, out var offset))
        {
            var first = nodes[0];
            var anchor = first.Anchor;
            var handleIn = new Position(anchor.X - offset.X, anchor.Y - offset.Y, anchor.Z);
            if (breakHandle)
            {
                nodes[0] = new VectorNode(anchor, handleIn, first.HandleOut, VectorNodeType.Corner);
            }
            else
            {
                var direction = Normalize(offset.X, offset.Y);
                var outLength = first.HandleOut is { } existing ? Distance(anchor, existing) : Math.Sqrt(offset.X * offset.X + offset.Y * offset.Y);
                var handleOut = new Position(anchor.X + direction.X * outLength, anchor.Y + direction.Y * outLength, anchor.Z);
                nodes[0] = new VectorNode(anchor, handleIn, handleOut, VectorNodeType.Smooth);
            }
        }

        var closed = subpath with { Nodes = nodes, IsClosed = true };
        return NormalizeSmoothFlag(closed, 0);
    }

    /// <summary>Same-contour join of its two open ends (Join nodes on the first and last node).
    /// <see cref="EndpointJoinMode.Merge"/> moves both ends to their midpoint and merges them;
    /// <see cref="EndpointJoinMode.Segment"/> closes with a straight segment;
    /// <see cref="EndpointJoinMode.Auto"/> merges ends closer than <see cref="JoinMergeToleranceMm"/>
    /// and otherwise draws the segment. Unchanged (still open) when <see cref="CanClose"/> says no.</summary>
    public static VectorSubpath JoinOwnEnds(VectorSubpath subpath, EndpointJoinMode mode = EndpointJoinMode.Auto)
    {
        ArgumentNullException.ThrowIfNull(subpath);
        if (subpath.IsClosed || subpath.Nodes.Count < 2) return subpath;
        var nodes = subpath.Nodes.ToList();
        var first = nodes[0];
        var last = nodes[^1];
        var merge = mode == EndpointJoinMode.Merge
            || (mode == EndpointJoinMode.Auto && Distance(first.Anchor, last.Anchor) <= JoinMergeToleranceMm);
        if (merge)
        {
            var mid = Midpoint(first.Anchor, last.Anchor);
            nodes[0] = first.Translated(mid.X - first.Anchor.X, mid.Y - first.Anchor.Y);
            nodes[^1] = last.Translated(mid.X - last.Anchor.X, mid.Y - last.Anchor.Y);
        }
        else
        {
            nodes[0] = first with { HandleIn = null };
            nodes[^1] = last with { HandleOut = null };
        }

        var candidate = subpath with { Nodes = nodes };
        return CanClose(candidate) ? Close(candidate) : subpath;
    }

    /// <summary>The inverse of Close: drops the closing segment back to the first node, without
    /// touching any node or handle. Symmetric with Close's own "just flip IsClosed, no dragOffset"
    /// path — reopening never needs to guess at a handle the way closing does with a drag.</summary>
    public static VectorSubpath Open(VectorSubpath subpath)
    {
        ArgumentNullException.ThrowIfNull(subpath);
        return subpath with { IsClosed = false };
    }

    /// <summary>Removes one node and reconnects its neighbours, refitting the new segment to the
    /// curve the two old segments drew (see <see cref="RemoveNode(VectorSubpath,int,bool,out double)"/>).</summary>
    public static VectorSubpath RemoveNode(VectorSubpath subpath, int index) =>
        RemoveNode(subpath, index, refit: true, out _);

    /// <summary>Removes one node and reconnects its neighbours directly. When the two segments that
    /// met at the deleted node were both straight, the result is a plain straight segment. Otherwise,
    /// with <paramref name="refit"/>, the new A→C cubic is fitted to the curve the two old segments
    /// drew (least squares with Newton reparameterisation, end tangents kept so A's and C's own
    /// smoothness survives); <paramref name="deviationMm"/> reports how far the result strays from the
    /// old curve, so a caller can tell the operator when it exceeds <see cref="RefitToleranceMm"/>.
    /// Without <paramref name="refit"/> (Ctrl+Delete) the neighbours simply keep their own handles.
    /// Endpoints of an open contour are dropped with their segment; deviation is 0 for a corner of
    /// straight segments (removing it is the point) and for an endpoint.</summary>
    public static VectorSubpath RemoveNode(VectorSubpath subpath, int index, bool refit, out double deviationMm)
    {
        ArgumentNullException.ThrowIfNull(subpath);
        deviationMm = 0;
        if (index < 0 || index >= subpath.Nodes.Count) throw new ArgumentOutOfRangeException(nameof(index));
        if (subpath.Nodes.Count <= (subpath.IsClosed ? 3 : 2))
            // Below the minimum node count for a usable subpath (2 open / 3 closed) — caller should
            // instead delete the whole object; refuse rather than produce a degenerate 0-1 node path.
            return subpath;

        var nodes = subpath.Nodes.ToList();
        var n = nodes.Count;
        var hasBothNeighbours = subpath.IsClosed || (index > 0 && index < n - 1);
        if (hasBothNeighbours)
        {
            var previousIndex = (index - 1 + n) % n;
            var nextIndex = (index + 1) % n;
            var a = nodes[previousIndex];
            var removed = nodes[index];
            var c = nodes[nextIndex];
            var curved = !VectorSubpath.IsStraightSegment(a, removed) || !VectorSubpath.IsStraightSegment(removed, c);
            if (curved)
            {
                if (refit && TryRefitTwoSegments(a, removed, c, out var handleOut, out var handleIn, out var error))
                {
                    deviationMm = error;
                    if (error > RefitToleranceMm) return subpath;
                    nodes[previousIndex] = a with { HandleOut = handleOut };
                    nodes[nextIndex] = c with { HandleIn = handleIn };
                }
                else
                {
                    deviationMm = MeasureDeviation(a, removed, c, a.HandleOut, c.HandleIn);
                }
            }
        }

        nodes.RemoveAt(index);
        var result = subpath with { Nodes = nodes };
        if (hasBothNeighbours)
        {
            var newCount = nodes.Count;
            var left = (index - 1 + n) % n;
            if (left > index) left--;
            var right = index % newCount;
            result = NormalizeSmoothFlag(NormalizeSmoothFlag(result, left), right);
        }
        return result;
    }

    private const int RefitSamplesPerSegment = 24;

    /// <summary>Least-squares fit (Schneider) of ONE cubic from <paramref name="a"/> to
    /// <paramref name="c"/> against the two original segments a-&gt;b and b-&gt;c, keeping the original
    /// end tangent directions and solving the two handle lengths, then re-solving after each Newton
    /// reparameterisation of the samples. The fit with the smallest maximum deviation wins and that
    /// deviation is reported. Returns false (callers keep the neighbours' existing handles) when the
    /// geometry is degenerate.</summary>
    private static bool TryRefitTwoSegments(VectorNode a, VectorNode b, VectorNode c,
        out Position handleOut, out Position handleIn, out double maxError)
    {
        handleOut = default;
        handleIn = default;
        maxError = double.PositiveInfinity;

        var samples = SampleTwoSegments(a, b, c);
        var chord = Distance(a.Anchor, c.Anchor);
        if (chord < 1e-9) return false;

        var startTarget = StartTangentTarget(a, b);
        var t1 = Normalize(startTarget.X - a.Anchor.X, startTarget.Y - a.Anchor.Y);
        var endTarget = EndTangentSource(b, c);
        var t2 = Normalize(endTarget.X - c.Anchor.X, endTarget.Y - c.Anchor.Y);
        if ((t1.X == 0 && t1.Y == 0) || (t2.X == 0 && t2.Y == 0)) return false;

        var u = new double[samples.Count];
        for (var i = 1; i < samples.Count; i++) u[i] = u[i - 1] + Distance(samples[i - 1], samples[i]);
        var total = u[^1];
        if (total < 1e-9) return false;
        for (var i = 0; i < u.Length; i++) u[i] /= total;

        var p0 = a.Anchor;
        var p3 = c.Anchor;
        for (var iteration = 0; iteration < 12; iteration++)
        {
            var (alpha1, alpha2) = SolveHandleLengths(samples, u, p0, p3, t1, t2, chord, total);
            var c1 = new Position(p0.X + t1.X * alpha1, p0.Y + t1.Y * alpha1, p0.Z);
            var c2 = new Position(p3.X + t2.X * alpha2, p3.Y + t2.Y * alpha2, p3.Z);

            for (var i = 1; i < u.Length - 1; i++) u[i] = NewtonRefine(p0, c1, c2, p3, samples[i], u[i]);
            var error = 0.0;
            for (var i = 0; i < u.Length; i++)
                error = Math.Max(error, Distance(CubicBezier.Evaluate(p0, c1, c2, p3, u[i]), samples[i]));

            if (error < maxError)
            {
                maxError = error;
                handleOut = c1;
                handleIn = c2;
            }
            if (maxError < 1e-4) break;
        }

        if (!double.IsFinite(maxError) || !IsFinite(handleOut) || !IsFinite(handleIn)) return false;
        // Report the honest closest-point distance rather than the fit's own parametric residual.
        maxError = MeasureDeviation(a, b, c, handleOut, handleIn);
        return true;
    }

    private static List<Position> SampleTwoSegments(VectorNode a, VectorNode b, VectorNode c)
    {
        var samples = new List<Position>(RefitSamplesPerSegment * 2 + 1) { a.Anchor };
        for (var i = 1; i <= RefitSamplesPerSegment; i++)
            samples.Add(SegmentPoint(a, b, i / (double)RefitSamplesPerSegment));
        for (var i = 1; i <= RefitSamplesPerSegment; i++)
            samples.Add(SegmentPoint(b, c, i / (double)RefitSamplesPerSegment));
        return samples;
    }

    private static (double Alpha1, double Alpha2) SolveHandleLengths(
        List<Position> samples, double[] u, Position p0, Position p3,
        (double X, double Y) t1, (double X, double Y) t2, double chord, double total)
    {
        double c11 = 0, c12 = 0, c22 = 0, x1 = 0, x2 = 0;
        for (var i = 0; i < samples.Count; i++)
        {
            var s = u[i];
            var m = 1 - s;
            var b0 = m * m * m;
            var b1 = 3 * s * m * m;
            var b2 = 3 * s * s * m;
            var b3 = s * s * s;
            var a1x = t1.X * b1; var a1y = t1.Y * b1;
            var a2x = t2.X * b2; var a2y = t2.Y * b2;
            c11 += a1x * a1x + a1y * a1y;
            c12 += a1x * a2x + a1y * a2y;
            c22 += a2x * a2x + a2y * a2y;
            var tmpX = samples[i].X - (p0.X * (b0 + b1) + p3.X * (b2 + b3));
            var tmpY = samples[i].Y - (p0.Y * (b0 + b1) + p3.Y * (b2 + b3));
            x1 += a1x * tmpX + a1y * tmpY;
            x2 += a2x * tmpX + a2y * tmpY;
        }

        var det = c11 * c22 - c12 * c12;
        if (Math.Abs(det) > 1e-12)
        {
            var solved1 = (x1 * c22 - x2 * c12) / det;
            var solved2 = (c11 * x2 - c12 * x1) / det;
            // Wu/Barsky: a non-positive or runaway length means the least-squares system has no
            // sensible answer for these tangents; chord/3 is the standard, always-valid fallback.
            if (solved1 > chord * 1e-3 && solved2 > chord * 1e-3 && solved1 < total * 2 && solved2 < total * 2)
                return (solved1, solved2);
        }
        return (chord / 3, chord / 3);
    }

    /// <summary>One Newton-Raphson step towards the parameter of the curve point closest to
    /// <paramref name="point"/> (Schneider's reparameterisation), clamped to [0, 1].</summary>
    private static double NewtonRefine(Position p0, Position c1, Position c2, Position p3, Position point, double u)
    {
        var q = CubicBezier.Evaluate(p0, c1, c2, p3, u);
        var m = 1 - u;
        // First derivative.
        var d1X = 3 * (m * m * (c1.X - p0.X) + 2 * m * u * (c2.X - c1.X) + u * u * (p3.X - c2.X));
        var d1Y = 3 * (m * m * (c1.Y - p0.Y) + 2 * m * u * (c2.Y - c1.Y) + u * u * (p3.Y - c2.Y));
        // Second derivative.
        var d2X = 6 * (m * (c2.X - 2 * c1.X + p0.X) + u * (p3.X - 2 * c2.X + c1.X));
        var d2Y = 6 * (m * (c2.Y - 2 * c1.Y + p0.Y) + u * (p3.Y - 2 * c2.Y + c1.Y));
        var dx = q.X - point.X;
        var dy = q.Y - point.Y;
        var numerator = dx * d1X + dy * d1Y;
        var denominator = d1X * d1X + d1Y * d1Y + dx * d2X + dy * d2Y;
        if (Math.Abs(denominator) < 1e-12) return u;
        var next = u - numerator / denominator;
        return double.IsFinite(next) ? Math.Clamp(next, 0, 1) : u;
    }

    /// <summary>Largest distance from the old two-segment curve a-&gt;b-&gt;c to the single cubic a-&gt;c
    /// with the given handles, each old sample measured to its closest point on the new curve.</summary>
    private static double MeasureDeviation(VectorNode a, VectorNode b, VectorNode c, Position? handleOut, Position? handleIn)
    {
        var samples = SampleTwoSegments(a, b, c);
        var c1 = handleOut ?? a.Anchor;
        var c2 = handleIn ?? c.Anchor;
        const int dense = 200;
        var curve = new Position[dense + 1];
        for (var i = 0; i <= dense; i++) curve[i] = CubicBezier.Evaluate(a.Anchor, c1, c2, c.Anchor, i / (double)dense);
        var worst = 0.0;
        foreach (var sample in samples)
        {
            var best = double.PositiveInfinity;
            var bestIndex = 0;
            for (var i = 0; i <= dense; i++)
            {
                var d = Distance(curve[i], sample);
                if (d < best) { best = d; bestIndex = i; }
            }
            var u = bestIndex / (double)dense;
            for (var k = 0; k < 4; k++) u = NewtonRefine(a.Anchor, c1, c2, c.Anchor, sample, u);
            best = Math.Min(best, Distance(CubicBezier.Evaluate(a.Anchor, c1, c2, c.Anchor, u), sample));
            worst = Math.Max(worst, best);
        }
        return worst;
    }

    private static Position SegmentPoint(VectorNode from, VectorNode to, double t) =>
        VectorSubpath.IsStraightSegment(from, to)
            ? CubicBezier.Lerp(from.Anchor, to.Anchor, t)
            : CubicBezier.Evaluate(from.Anchor, from.HandleOut ?? from.Anchor, to.HandleIn ?? to.Anchor, to.Anchor, t);

    /// <summary>Point that defines the direction leaving <paramref name="a"/> along a-&gt;b.</summary>
    private static Position StartTangentTarget(VectorNode a, VectorNode b)
    {
        if (a.HandleOut is { } ho && Distance(a.Anchor, ho) > 1e-9) return ho;
        if (b.HandleIn is { } hi && Distance(a.Anchor, hi) > 1e-9) return hi;
        return b.Anchor;
    }

    /// <summary>Point that defines the direction arriving at <paramref name="c"/> along b-&gt;c, seen from c.</summary>
    private static Position EndTangentSource(VectorNode b, VectorNode c)
    {
        if (c.HandleIn is { } hi && Distance(c.Anchor, hi) > 1e-9) return hi;
        if (b.HandleOut is { } ho && Distance(c.Anchor, ho) > 1e-9) return ho;
        return b.Anchor;
    }

    /// <summary>Inserts a new node on segment <paramref name="segmentIndex"/> at curve parameter t
    /// (0..1). For a straight segment this is a plain linear interpolation. For a curved segment this
    /// uses exact De Casteljau subdivision (CubicBezier.Split) so the visible curve shape is bit-for-
    /// bit unchanged — the two new segments together retrace the original curve exactly, they are not
    /// a visual approximation.
    ///
    /// CubicBezier.Split(p0, c1, c2, p1, t) returns the LEFT curve as (P0, C1, C2, P1) and the RIGHT
    /// curve as (Q0, D1, D2, Q1), with P1 == Q0 the point at t. Mapped onto A → new-middle-node → B:
    /// A's new outgoing handle is the left curve's OWN first control point (split.C1, adjacent to A),
    /// the middle node's incoming/outgoing handles are the two control points adjacent to the split
    /// point (split.C2 from the left curve, split.D1 from the right curve), and B's new incoming
    /// handle is the right curve's own second control point (split.D2, adjacent to B). Swapping any
    /// of these (e.g. using split.C2 for A's handle) still compiles but silently distorts the curve —
    /// see VectorPathTests.InsertNodeOnCurvedSegmentPreservesExactCurveShape, which pins this exact
    /// mapping by resampling the curve before/after and failing on drift.</summary>
    public static VectorSubpath InsertNode(VectorSubpath subpath, int segmentIndex, double t)
    {
        ArgumentNullException.ThrowIfNull(subpath);
        // Written as a positive range test: "t is <= 0 or >= 1" lets NaN through (every comparison
        // with NaN is false) and a NaN anchor then poisons the flattened geometry and the G-code.
        if (!(t > 0 && t < 1)) throw new ArgumentOutOfRangeException(nameof(t));
        var (a, b) = subpath.Segment(segmentIndex);
        var nodes = subpath.Nodes.ToList();
        var aIndex = segmentIndex;
        var bIndex = (segmentIndex + 1) % nodes.Count;
        var insertAt = subpath.IsClosed && bIndex == 0 ? nodes.Count : bIndex;

        if (VectorSubpath.IsStraightSegment(a, b))
        {
            var mid = CubicBezier.Lerp(a.Anchor, b.Anchor, t);
            nodes.Insert(insertAt, VectorNode.CornerAt(mid));
            return subpath with { Nodes = nodes };
        }

        var c1 = a.HandleOut ?? a.Anchor;
        var c2 = b.HandleIn ?? b.Anchor;
        var split = CubicBezier.Split(a.Anchor, c1, c2, b.Anchor, t);

        nodes[aIndex] = a with { HandleOut = split.C1 };
        nodes[bIndex] = b with { HandleIn = split.D2 };
        nodes.Insert(insertAt, new VectorNode(split.P1, split.C2, split.D1, VectorNodeType.Smooth));
        return subpath with { Nodes = nodes };
    }

    /// <summary>Node-only Corner ↔ Smooth, for callers without the contour at hand. Smooth forces two
    /// existing handles collinear (see MakeCollinear); it cannot know the neighbours, so a node without
    /// handles only changes its flag. Editing code uses <see cref="SetNodeType"/>, which generates
    /// tangent handles from the neighbours instead. Corner just flips the flag; existing handle
    /// positions are left alone so nothing visibly jumps on conversion.</summary>
    public static VectorNode ConvertNodeType(VectorNode node, VectorNodeType type)
    {
        if (type == VectorNodeType.Corner) return node with { Type = VectorNodeType.Corner };
        return MakeCollinear(node);
    }

    /// <summary>Corner/Smooth on one node of a contour (toolbar, menu, Shift+S). Corner only flips the
    /// flag. Smooth makes the node genuinely tangent-continuous, whatever handles it has:
    /// <list type="bullet">
    /// <item>two handles: both re-pointed along their shared average direction, lengths kept;</item>
    /// <item>no handles: tangent handles generated Inkscape-style (see <see cref="AutoSmoothNode"/>);</item>
    /// <item>one handle beside a straight segment: the line stays straight and the handle is turned to
    /// continue it (Inkscape), length kept;</item>
    /// <item>one handle beside a curved segment: the missing handle is created opposite the existing
    /// one, a third of that segment's chord long.</item>
    /// </list>
    /// An open contour's end node has only one side, so it is tangent-continuous by definition: it is
    /// flagged Smooth and its handle left alone.</summary>
    public static VectorSubpath SetNodeType(VectorSubpath subpath, int index, VectorNodeType type)
    {
        ArgumentNullException.ThrowIfNull(subpath);
        CheckIndex(subpath, index);
        if (type == VectorNodeType.Corner) return ReplaceNode(subpath, index, subpath.Nodes[index] with { Type = VectorNodeType.Corner });
        return SmoothNode(subpath, index);
    }

    /// <summary>See <see cref="SetNodeType"/> (Smooth).</summary>
    public static VectorSubpath SmoothNode(VectorSubpath subpath, int index)
    {
        ArgumentNullException.ThrowIfNull(subpath);
        CheckIndex(subpath, index);
        var node = subpath.Nodes[index];
        if (!TryNeighbours(subpath, index, out var previous, out var next))
            return ReplaceNode(subpath, index, node with { Type = VectorNodeType.Smooth });

        var anchor = node.Anchor;
        if (node.HandleIn is null && node.HandleOut is null) return AutoSmoothNode(subpath, index);
        if (node.HandleIn is not null && node.HandleOut is not null) return ReplaceNode(subpath, index, MakeCollinear(node));

        if (node.HandleOut is { } onlyOut)
        {
            var length = Distance(anchor, onlyOut);
            if (previous.HandleOut is null)
            {
                // Straight segment arriving: continue the line, do not bend it.
                var along = Normalize(anchor.X - previous.Anchor.X, anchor.Y - previous.Anchor.Y);
                if (IsZero(along)) return ReplaceNode(subpath, index, node with { Type = VectorNodeType.Smooth });
                return ReplaceNode(subpath, index, node with { HandleOut = Offset(anchor, along, length), Type = VectorNodeType.Smooth });
            }

            var direction = Normalize(onlyOut.X - anchor.X, onlyOut.Y - anchor.Y);
            if (IsZero(direction)) return AutoSmoothNode(subpath, index);
            var inLength = Distance(anchor, previous.Anchor) / 3;
            return ReplaceNode(subpath, index, node with { HandleIn = Offset(anchor, direction, -inLength), Type = VectorNodeType.Smooth });
        }

        var onlyIn = node.HandleIn!.Value;
        var inHandleLength = Distance(anchor, onlyIn);
        if (next.HandleIn is null)
        {
            // Straight segment leaving: the incoming handle lines up with it.
            var back = Normalize(anchor.X - next.Anchor.X, anchor.Y - next.Anchor.Y);
            if (IsZero(back)) return ReplaceNode(subpath, index, node with { Type = VectorNodeType.Smooth });
            return ReplaceNode(subpath, index, node with { HandleIn = Offset(anchor, back, inHandleLength), Type = VectorNodeType.Smooth });
        }

        var inward = Normalize(onlyIn.X - anchor.X, onlyIn.Y - anchor.Y);
        if (IsZero(inward)) return AutoSmoothNode(subpath, index);
        var outLength = Distance(anchor, next.Anchor) / 3;
        return ReplaceNode(subpath, index, node with { HandleOut = Offset(anchor, inward, -outLength), Type = VectorNodeType.Smooth });
    }

    /// <summary>Auto-smooth (Inkscape): both handles are regenerated along the direction from the
    /// previous to the next anchor, each a third of its own segment's chord long. Smooth result; an
    /// open contour's end node only gets the Smooth flag (it has no second neighbour to aim by).</summary>
    public static VectorSubpath AutoSmoothNode(VectorSubpath subpath, int index)
    {
        ArgumentNullException.ThrowIfNull(subpath);
        CheckIndex(subpath, index);
        var node = subpath.Nodes[index];
        if (!TryNeighbours(subpath, index, out var previous, out var next))
            return ReplaceNode(subpath, index, node with { Type = VectorNodeType.Smooth });

        var anchor = node.Anchor;
        var direction = Normalize(next.Anchor.X - previous.Anchor.X, next.Anchor.Y - previous.Anchor.Y);
        if (IsZero(direction)) direction = Normalize(next.Anchor.X - anchor.X, next.Anchor.Y - anchor.Y);
        if (IsZero(direction)) return ReplaceNode(subpath, index, node with { Type = VectorNodeType.Smooth });

        var inLength = Distance(anchor, previous.Anchor) / 3;
        var outLength = Distance(anchor, next.Anchor) / 3;
        return ReplaceNode(subpath, index, new VectorNode(
            anchor, Offset(anchor, direction, -inLength), Offset(anchor, direction, outLength), VectorNodeType.Smooth));
    }

    /// <summary>Symmetric: both handles collinear AND equally long (the mean of the current lengths;
    /// a node without handles is auto-smoothed first; a lone handle is mirrored). Stored as a Smooth
    /// node — symmetry is the result of this command, not a separate persistent constraint.</summary>
    public static VectorSubpath MakeNodeSymmetric(VectorSubpath subpath, int index)
    {
        ArgumentNullException.ThrowIfNull(subpath);
        CheckIndex(subpath, index);
        var node = subpath.Nodes[index];
        var hasNeighbours = TryNeighbours(subpath, index, out _, out _);
        if (!node.HasAnyHandle)
        {
            if (!hasNeighbours) return ReplaceNode(subpath, index, node with { Type = VectorNodeType.Smooth });
            subpath = AutoSmoothNode(subpath, index);
            node = subpath.Nodes[index];
            if (!node.HasAnyHandle) return subpath;
        }

        var anchor = node.Anchor;
        if (node.HandleIn is { } hi && node.HandleOut is { } ho)
        {
            var collinear = MakeCollinear(node);
            var direction = Normalize(collinear.HandleOut!.Value.X - anchor.X, collinear.HandleOut.Value.Y - anchor.Y);
            if (IsZero(direction)) return ReplaceNode(subpath, index, collinear);
            var length = (Distance(anchor, hi) + Distance(anchor, ho)) / 2;
            return ReplaceNode(subpath, index, new VectorNode(anchor, Offset(anchor, direction, -length), Offset(anchor, direction, length), VectorNodeType.Smooth));
        }

        if (!hasNeighbours) return ReplaceNode(subpath, index, node with { Type = VectorNodeType.Smooth });
        return ReplaceNode(subpath, index, MakeCollinear(node));
    }

    /// <summary>Moves a handle to a new position. On a Smooth node (and not breaking symmetry) the
    /// opposite handle is re-pointed to stay collinear through the anchor, keeping its own distance
    /// from the anchor unchanged — only its direction follows the dragged handle. On a Corner node the
    /// opposite handle is untouched. Breaking symmetry (Alt-drag) on a Smooth node leaves the opposite
    /// handle where it is and turns the node into a Corner (cusp): the flag must never claim a tangency
    /// the handles no longer have, or the next ordinary drag would snap the other handle across.</summary>
    public static VectorNode MoveHandle(VectorNode node, bool isOutHandle, Position newPosition, bool breakSymmetry)
    {
        if (!IsFinite(newPosition)) return node;
        var moved = isOutHandle ? node with { HandleOut = newPosition } : node with { HandleIn = newPosition };
        if (moved.Type != VectorNodeType.Smooth) return moved;
        if (breakSymmetry) return moved with { Type = VectorNodeType.Corner };

        var opposite = isOutHandle ? moved.HandleIn : moved.HandleOut;
        if (opposite is null) return moved;

        var dir = Normalize(newPosition.X - node.Anchor.X, newPosition.Y - node.Anchor.Y);
        if (dir.X == 0 && dir.Y == 0) return moved;

        var oppositeLen = Distance(node.Anchor, opposite.Value);
        var newOpposite = new Position(node.Anchor.X - dir.X * oppositeLen, node.Anchor.Y - dir.Y * oppositeLen, node.Anchor.Z);
        return isOutHandle ? moved with { HandleIn = newOpposite } : moved with { HandleOut = newOpposite };
    }

    /// <summary>Handle drag on one node of a contour. As <see cref="MoveHandle(VectorNode,bool,Position,bool)"/>,
    /// plus the case the node alone cannot see: a Smooth node whose OTHER side is a straight segment.
    /// The line is its tangent, so the dragged handle slides along the line's continuation (Inkscape)
    /// instead of kinking the node; Alt breaks that (the node becomes a cusp).</summary>
    public static VectorSubpath MoveHandle(VectorSubpath subpath, int index, bool isOutHandle, Position newPosition, bool breakSymmetry)
    {
        ArgumentNullException.ThrowIfNull(subpath);
        CheckIndex(subpath, index);
        var node = subpath.Nodes[index];
        if (!IsFinite(newPosition)) return subpath;

        var opposite = isOutHandle ? node.HandleIn : node.HandleOut;
        if (node.Type == VectorNodeType.Smooth && !breakSymmetry && opposite is null
            && TryNeighbours(subpath, index, out var previous, out var next))
        {
            var lineFrom = isOutHandle ? previous : next;
            var lineIsStraight = isOutHandle ? lineFrom.HandleOut is null : lineFrom.HandleIn is null;
            if (lineIsStraight)
            {
                var along = Normalize(node.Anchor.X - lineFrom.Anchor.X, node.Anchor.Y - lineFrom.Anchor.Y);
                if (!IsZero(along))
                {
                    var projected = Math.Max(0, (newPosition.X - node.Anchor.X) * along.X + (newPosition.Y - node.Anchor.Y) * along.Y);
                    Position? constrained = projected < 1e-9 ? null : Offset(node.Anchor, along, projected);
                    return ReplaceNode(subpath, index, isOutHandle ? node with { HandleOut = constrained } : node with { HandleIn = constrained });
                }
            }
        }

        return ReplaceNode(subpath, index, MoveHandle(node, isOutHandle, newPosition, breakSymmetry));
    }

    /// <summary>True when the curve arrives at and leaves the node in the same direction (within
    /// <paramref name="toleranceDegrees"/>). An open contour's end node has one side only and always
    /// passes. Tangents come from the handles, or the neighbour's facing handle, or the neighbour.</summary>
    public static bool IsTangentContinuous(VectorSubpath subpath, int index, double toleranceDegrees = 0.5)
    {
        ArgumentNullException.ThrowIfNull(subpath);
        CheckIndex(subpath, index);
        if (!TryNeighbours(subpath, index, out var previous, out var next)) return true;
        var node = subpath.Nodes[index];
        var source = EndTangentSource(previous, node);
        var target = StartTangentTarget(node, next);
        var incoming = Normalize(node.Anchor.X - source.X, node.Anchor.Y - source.Y);
        var outgoing = Normalize(target.X - node.Anchor.X, target.Y - node.Anchor.Y);
        if (IsZero(incoming) || IsZero(outgoing)) return true;
        var cross = incoming.X * outgoing.Y - incoming.Y * outgoing.X;
        var dot = incoming.X * outgoing.X + incoming.Y * outgoing.Y;
        return dot > 0 && Math.Abs(cross) <= Math.Sin(toleranceDegrees * Math.PI / 180);
    }

    /// <summary>A Smooth flag on a node that is no longer tangent-continuous becomes Corner. Run after
    /// any edit that can bend one side of a node without touching the other (segment to line, segment
    /// drag next to a lone handle, node removal without refit), so the flag never lies.</summary>
    public static VectorSubpath NormalizeSmoothFlag(VectorSubpath subpath, int index)
    {
        ArgumentNullException.ThrowIfNull(subpath);
        if (index < 0 || index >= subpath.Nodes.Count) return subpath;
        var node = subpath.Nodes[index];
        if (node.Type != VectorNodeType.Smooth || IsTangentContinuous(subpath, index)) return subpath;
        return ReplaceNode(subpath, index, node with { Type = VectorNodeType.Corner });
    }

    /// <summary>After nodes moved, a Smooth neighbour with a single handle beside a straight segment
    /// re-aims that handle along the line again (Inkscape keeps the line its tangent); any other Smooth
    /// neighbour that lost its tangency is flagged Corner. <paramref name="moved"/> are the nodes that
    /// were translated; only their unmoved neighbours are touched.</summary>
    public static VectorSubpath ReconcileSmoothNeighbours(VectorSubpath subpath, IReadOnlyCollection<int> moved)
    {
        ArgumentNullException.ThrowIfNull(subpath);
        ArgumentNullException.ThrowIfNull(moved);
        var n = subpath.Nodes.Count;
        if (n < 2 || moved.Count == 0) return subpath;
        var neighbours = new SortedSet<int>();
        foreach (var index in moved)
        {
            if (index < 0 || index >= n) continue;
            if (subpath.IsClosed || index > 0) neighbours.Add((index - 1 + n) % n);
            if (subpath.IsClosed || index < n - 1) neighbours.Add((index + 1) % n);
        }

        foreach (var index in neighbours)
        {
            if (moved.Contains(index)) continue;
            var node = subpath.Nodes[index];
            if (node.Type != VectorNodeType.Smooth || !TryNeighbours(subpath, index, out var previous, out var next)) continue;
            if (node.HandleOut is { } ho && node.HandleIn is null && previous.HandleOut is null)
            {
                var along = Normalize(node.Anchor.X - previous.Anchor.X, node.Anchor.Y - previous.Anchor.Y);
                if (!IsZero(along)) subpath = ReplaceNode(subpath, index, node with { HandleOut = Offset(node.Anchor, along, Distance(node.Anchor, ho)) });
            }
            else if (node.HandleIn is { } hi && node.HandleOut is null && next.HandleIn is null)
            {
                var back = Normalize(node.Anchor.X - next.Anchor.X, node.Anchor.Y - next.Anchor.Y);
                if (!IsZero(back)) subpath = ReplaceNode(subpath, index, node with { HandleIn = Offset(node.Anchor, back, Distance(node.Anchor, hi)) });
            }
            subpath = NormalizeSmoothFlag(subpath, index);
        }
        return subpath;
    }

    /// <summary>Turns a straight segment into an equivalent cubic Bézier by placing handles at the 1/3
    /// and 2/3 points of the chord — a cubic with control points on the line between its endpoints
    /// draws exactly that line, so the visible geometry is unchanged until a handle is subsequently
    /// moved (LIGHTBURN_VECTOR_PARITY.md §16). Idempotent: a segment that already has a handle on
    /// either side is returned unchanged rather than flattening an existing curve back to this
    /// baseline.</summary>
    public static VectorSubpath ConvertSegmentToCurve(VectorSubpath subpath, int segmentIndex)
    {
        ArgumentNullException.ThrowIfNull(subpath);
        var (a, b) = subpath.Segment(segmentIndex);
        if (!VectorSubpath.IsStraightSegment(a, b)) return subpath;

        var aIndex = segmentIndex;
        var bIndex = (segmentIndex + 1) % subpath.Nodes.Count;
        var c1 = CubicBezier.Lerp(a.Anchor, b.Anchor, 1.0 / 3);
        var c2 = CubicBezier.Lerp(a.Anchor, b.Anchor, 2.0 / 3);

        var nodes = subpath.Nodes.ToList();
        nodes[aIndex] = a with { HandleOut = c1 };
        nodes[bIndex] = b with { HandleIn = c2 };
        return subpath with { Nodes = nodes };
    }

    /// <summary>Removes the handles on either side of one segment, turning a curve back into a straight
    /// line. Anchors are untouched, so both endpoints stay exactly where they were
    /// (LIGHTBURN_VECTOR_PARITY.md §17) — only the segment between them changes.</summary>
    public static VectorSubpath ConvertSegmentToLine(VectorSubpath subpath, int segmentIndex)
    {
        ArgumentNullException.ThrowIfNull(subpath);
        var (a, b) = subpath.Segment(segmentIndex);
        var aIndex = segmentIndex;
        var bIndex = (segmentIndex + 1) % subpath.Nodes.Count;

        var nodes = subpath.Nodes.ToList();
        nodes[aIndex] = a with { HandleOut = null };
        nodes[bIndex] = b with { HandleIn = null };
        // Straightening one side of a Smooth node breaks its tangency unless the remaining handle
        // happens to line up with the new line; such a node is a cusp now and is flagged as one.
        var straightened = subpath with { Nodes = nodes };
        return NormalizeSmoothFlag(NormalizeSmoothFlag(straightened, aIndex), bIndex);
    }

    /// <summary>Reshapes one segment so the curve point at parameter <paramref name="t"/> moves to
    /// <paramref name="pointerLocal"/> — the "grab the middle of a line/curve and drag it" gesture
    /// (LIGHTBURN_VECTOR_PARITY.md §15). Always computed from the segment's ORIGINAL (pre-drag) node
    /// pair, never from a previously-dragged result, matching the "preview = original + delta, never
    /// cumulative" contract (§7/§74) — the caller re-derives every preview frame from one immutable
    /// snapshot and a fixed <paramref name="t"/> captured once at drag-start.
    ///
    /// A straight segment is first baselined to the equivalent curve (ConvertSegmentToCurve) so the
    /// starting shape is visually identical to the line, then the same handle math applies uniformly.
    /// The two control points move by a minimal-movement least-squares split of the desired delta,
    /// weighted by each control's actual influence on the point at t (dB/dP1 = 3(1-t)²t, dB/dP2 =
    /// 3(1-t)t²) — the standard exact technique for "drag a point on a cubic Bézier", not an
    /// approximation: solving k1, k2 in w1·k1 + w2·k2 = 1 (minimizing k1²+k2² for a unique answer) and
    /// moving each control by k·delta reproduces the requested point exactly at t.</summary>
    public static VectorSubpath DragSegmentPoint(VectorSubpath subpath, int segmentIndex, double t, Position pointerLocal)
    {
        ArgumentNullException.ThrowIfNull(subpath);
        if (!(t > 0 && t < 1)) throw new ArgumentOutOfRangeException(nameof(t));
        if (!IsFinite(pointerLocal)) return subpath;

        var (originalA, originalB) = subpath.Segment(segmentIndex);
        var baseline = VectorSubpath.IsStraightSegment(originalA, originalB)
            ? ConvertSegmentToCurve(subpath, segmentIndex)
            : subpath;
        var (a, b) = baseline.Segment(segmentIndex);
        var aIndex = segmentIndex;
        var bIndex = (segmentIndex + 1) % baseline.Nodes.Count;
        var c1 = a.HandleOut ?? a.Anchor;
        var c2 = b.HandleIn ?? b.Anchor;

        var originalPoint = CubicBezier.Evaluate(a.Anchor, c1, c2, b.Anchor, t);
        var deltaX = pointerLocal.X - originalPoint.X;
        var deltaY = pointerLocal.Y - originalPoint.Y;

        var w1 = 3 * (1 - t) * (1 - t) * t;
        var w2 = 3 * (1 - t) * t * t;
        var denom = w1 * w1 + w2 * w2;
        if (denom < 1e-9) return baseline; // t too close to an endpoint to solve stably

        var k1 = w1 / denom;
        var k2 = w2 / denom;
        var newC1 = new Position(c1.X + deltaX * k1, c1.Y + deltaY * k1, c1.Z);
        var newC2 = new Position(c2.X + deltaX * k2, c2.Y + deltaY * k2, c2.Z);

        var nodes = baseline.Nodes.ToList();
        // MoveHandle re-aligns the opposite handle of a Smooth node (keeping its length), so dragging
        // the segment never leaves a smooth node with a kinked tangent.
        nodes[aIndex] = VectorSubpathNodeWithHandle(a, isOutHandle: true, newC1);
        nodes[bIndex] = VectorSubpathNodeWithHandle(b, isOutHandle: false, newC2);
        // A Smooth node with no handle on its far side (a straight segment there) cannot follow the
        // reshape without a kink; the drag point stays exact and the node is flagged a cusp.
        var reshaped = baseline with { Nodes = nodes };
        return NormalizeSmoothFlag(NormalizeSmoothFlag(reshaped, aIndex), bIndex);
    }

    /// <summary>Breaks a path at one node, per LIGHTBURN_VECTOR_PARITY.md §22. A closed subpath opens,
    /// with the break node duplicated as the path's two new endpoints ("two endpoints at the break
    /// location") — the first copy keeps the node's original HandleOut (continuing forward) with
    /// HandleIn cleared, the second copy keeps the original HandleIn with HandleOut cleared. An
    /// internal node of an OPEN subpath is defined identically but splits the subpath into two
    /// separate subpaths within the same compound VectorPath, rather than producing one subpath with
    /// duplicated nodes at both ends — there is nothing to "loop back" to. Breaking at an endpoint of
    /// an already-open subpath is a no-op target (nothing to break) and rejected.</summary>
    public static VectorPath BreakAtNode(VectorPath path, int subpathIndex, int nodeIndex)
    {
        ArgumentNullException.ThrowIfNull(path);
        var subpath = path.Subpaths[subpathIndex];
        var nodes = subpath.Nodes;
        var n = nodes.Count;
        if (nodeIndex < 0 || nodeIndex >= n) throw new ArgumentOutOfRangeException(nameof(nodeIndex));

        if (subpath.IsClosed)
        {
            var start = nodes[nodeIndex] with { HandleIn = null };
            var end = nodes[nodeIndex] with { HandleOut = null };
            var reordered = new List<VectorNode> { start };
            for (var i = 1; i < n; i++) reordered.Add(nodes[(nodeIndex + i) % n]);
            reordered.Add(end);
            return path.ReplaceSubpath(subpathIndex, subpath with { Nodes = reordered, IsClosed = false });
        }

        if (nodeIndex == 0 || nodeIndex == n - 1)
            throw new ArgumentException("Cannot break an open path at an endpoint — it is already open there.", nameof(nodeIndex));

        var first = nodes.Take(nodeIndex + 1).ToList();
        first[^1] = first[^1] with { HandleOut = null };
        var second = nodes.Skip(nodeIndex).ToList();
        second[0] = second[0] with { HandleIn = null };

        return path.ReplaceSubpathWithMany(subpathIndex,
        [
            subpath with { Nodes = first, IsClosed = false },
            subpath with { Nodes = second, IsClosed = false },
        ]);
    }

    /// <summary>Deletes one segment, per LIGHTBURN_VECTOR_PARITY.md §21 ("deleting a line/curve opens
    /// or splits the path"). A closed subpath opens, rotated so the two nodes that bounded the removed
    /// segment become the new end/start (their handles facing the removed segment are cleared). An
    /// open subpath splits into (up to) two subpaths at the cut; a resulting side with fewer than 2
    /// nodes is degenerate (a lone point is not a usable path) and is dropped rather than kept as
    /// corrupt geometry — deleting the first or last segment of a short open path can therefore leave
    /// just one subpath, or in the extreme (a 2-node open path) none at all, which callers must be
    /// prepared to see as a shrunken or empty VectorPath.Subpaths.</summary>
    public static VectorPath DeleteSegment(VectorPath path, int subpathIndex, int segmentIndex)
    {
        ArgumentNullException.ThrowIfNull(path);
        var subpath = path.Subpaths[subpathIndex];
        var nodes = subpath.Nodes;
        var n = nodes.Count;
        var aIndex = segmentIndex;
        var bIndex = (segmentIndex + 1) % n;

        if (subpath.IsClosed)
        {
            var reordered = new List<VectorNode>(n);
            for (var i = 0; i < n; i++) reordered.Add(nodes[(bIndex + i) % n]);
            reordered[0] = reordered[0] with { HandleIn = null };
            reordered[^1] = reordered[^1] with { HandleOut = null };
            return path.ReplaceSubpath(subpathIndex, subpath with { Nodes = reordered, IsClosed = false });
        }

        var first = nodes.Take(aIndex + 1).ToList();
        first[^1] = first[^1] with { HandleOut = null };
        var second = nodes.Skip(bIndex).ToList();
        second[0] = second[0] with { HandleIn = null };

        var survivors = new List<VectorSubpath>();
        if (first.Count >= 2) survivors.Add(subpath with { Nodes = first, IsClosed = false });
        if (second.Count >= 2) survivors.Add(subpath with { Nodes = second, IsClosed = false });
        return path.ReplaceSubpathWithMany(subpathIndex, survivors);
    }

    /// <summary>Reverses node order and swaps each node's HandleIn/HandleOut — the path still draws
    /// identically (same anchors, same curve shapes), only its direction of travel (which endpoint is
    /// "start" vs "end") is flipped. Used standalone as the "Reverse Path" command, and internally by
    /// JoinAtEndpoints to re-orient whichever side of a cross-object join needs its start and end
    /// swapped so the two subpaths can be concatenated head-to-tail.</summary>
    public static VectorSubpath ReverseSubpath(VectorSubpath subpath)
    {
        ArgumentNullException.ThrowIfNull(subpath);
        var nodes = subpath.Nodes
            .Select(n => new VectorNode(n.Anchor, n.HandleOut, n.HandleIn, n.Type))
            .Reverse()
            .ToList();
        return subpath with { Nodes = nodes };
    }

    /// <summary>Joins two OPEN subpaths end-to-end at the requested endpoints (LIGHTBURN_VECTOR_
    /// PARITY.md §23's cross-object case — the same-object case is TryCloseByEndpointJoin in
    /// SceneCanvas.VectorPathTool.cs). Both subpaths must already be expressed in the SAME coordinate
    /// space — the caller (SceneViewModel.JoinObjectEndpoints) is responsible for bringing two
    /// different objects' local geometry into one shared world space first, the same way Group's own
    /// BuildCombinedWorldVectorPath does.
    ///
    /// Reverses whichever side needs it (via ReverseSubpath) so the user never has to reverse a path
    /// manually before joining — dragging A's start onto B's start, or A's end onto B's end, works
    /// exactly like dragging A's end onto B's start.
    ///
    /// <paramref name="mode"/> decides what happens between the two ends (Inkscape "Join selected
    /// endpoints" / "Join with segment", Illustrator Join):
    /// <list type="bullet">
    /// <item><see cref="EndpointJoinMode.Merge"/>: the two ends become ONE node at their midpoint,
    /// each side's facing handle carried along with its anchor; nothing else of A or B moves.</item>
    /// <item><see cref="EndpointJoinMode.Segment"/>: both ends stay where they are and a straight
    /// segment connects them.</item>
    /// <item><see cref="EndpointJoinMode.Auto"/> (default): merge ends within
    /// <see cref="JoinMergeToleranceMm"/>, connect anything further apart with a segment. A join never
    /// drops an anchor or moves geometry by more than that tolerance.</item>
    /// </list>
    /// A merged node is Corner: A and B may disagree on Smooth/Corner, and forcing either would move a
    /// handle the user did not touch; it can be smoothed afterwards like any other node.</summary>
    public static VectorSubpath JoinAtEndpoints(VectorSubpath a, bool aJoinAtStart, VectorSubpath b, bool bJoinAtStart,
        EndpointJoinMode mode = EndpointJoinMode.Auto)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        if (a.IsClosed || b.IsClosed)
            throw new ArgumentException("Only open subpaths can be joined at an endpoint.");
        if (a.Nodes.Count == 0 || b.Nodes.Count == 0)
            throw new ArgumentException("Cannot join an empty subpath.");

        var orientedA = aJoinAtStart ? ReverseSubpath(a) : a;
        var orientedB = bJoinAtStart ? b : ReverseSubpath(b);
        var endA = orientedA.Nodes[^1];
        var startB = orientedB.Nodes[0];
        var merge = mode == EndpointJoinMode.Merge
            || (mode == EndpointJoinMode.Auto && Distance(endA.Anchor, startB.Anchor) <= JoinMergeToleranceMm);

        var nodes = new List<VectorNode>(orientedA.Nodes.Count + orientedB.Nodes.Count);
        nodes.AddRange(orientedA.Nodes.Take(orientedA.Nodes.Count - 1));
        if (merge)
        {
            var mid = Midpoint(endA.Anchor, startB.Anchor);
            var movedA = endA.Translated(mid.X - endA.Anchor.X, mid.Y - endA.Anchor.Y);
            var movedB = startB.Translated(mid.X - startB.Anchor.X, mid.Y - startB.Anchor.Y);
            nodes.Add(new VectorNode(mid, movedA.HandleIn, movedB.HandleOut, VectorNodeType.Corner));
        }
        else
        {
            // Both ends keep their position; a straight segment (no handles facing it) joins them.
            nodes.Add(endA with { HandleOut = null });
            nodes.Add(startB with { HandleIn = null });
        }
        nodes.AddRange(orientedB.Nodes.Skip(1));

        var joined = new VectorSubpath { Nodes = nodes, IsClosed = false };
        if (merge) return joined;
        var seam = orientedA.Nodes.Count - 1;
        return NormalizeSmoothFlag(NormalizeSmoothFlag(joined, seam), seam + 1);
    }

    /// <summary>Joins two different OPEN subpaths of one VectorPath at the given endpoints (the
    /// same-object counterpart of the cross-object <see cref="JoinAtEndpoints(VectorSubpath,bool,VectorSubpath,bool,EndpointJoinMode)"/>).
    /// The merged subpath takes the position of the lower-indexed one; the other is removed.</summary>
    public static VectorPath JoinSubpathsAtEndpoints(
        VectorPath path, int firstIndex, bool firstAtStart, int secondIndex, bool secondAtStart,
        EndpointJoinMode mode = EndpointJoinMode.Auto)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (firstIndex == secondIndex)
            throw new ArgumentException("Two different subpaths are required.", nameof(secondIndex));
        if (firstIndex < 0 || firstIndex >= path.Subpaths.Count) throw new ArgumentOutOfRangeException(nameof(firstIndex));
        if (secondIndex < 0 || secondIndex >= path.Subpaths.Count) throw new ArgumentOutOfRangeException(nameof(secondIndex));

        var joined = JoinAtEndpoints(path.Subpaths[firstIndex], firstAtStart, path.Subpaths[secondIndex], secondAtStart, mode);
        var keep = Math.Min(firstIndex, secondIndex);
        var drop = Math.Max(firstIndex, secondIndex);
        var subpaths = path.Subpaths.ToList();
        subpaths[keep] = joined;
        subpaths.RemoveAt(drop);
        return path with { Subpaths = subpaths };
    }

    /// <summary>Recognises a closed ring sampled from an axis-aligned ellipse (what the ellipse tool
    /// produces: vertices on the ellipse including all four axis extremes) and returns it as four
    /// smooth Bezier nodes (the standard 4-arc kappa construction) instead of dozens of corner nodes.
    /// Nodes start at the east extreme and follow the ring's winding. The trailing duplicate of the
    /// first point must not be included in <paramref name="count"/>.</summary>
    public static bool TryFitEllipseSubpath(IReadOnlyList<Position> points, int count, out VectorSubpath subpath)
    {
        subpath = VectorSubpath.Empty;
        if (count < 16 || count > points.Count) return false;

        var bounds = BoundingBox2D.Empty;
        for (var i = 0; i < count; i++) bounds = bounds.Include(points[i].X, points[i].Y);
        var cx = (bounds.MinX + bounds.MaxX) / 2;
        var cy = (bounds.MinY + bounds.MaxY) / 2;
        var rx = (bounds.MaxX - bounds.MinX) / 2;
        var ry = (bounds.MaxY - bounds.MinY) / 2;
        if (rx < 1e-6 || ry < 1e-6) return false;

        double area = 0;
        var hasEast = false;
        var hasNorth = false;
        var hasWest = false;
        var hasSouth = false;
        for (var i = 0; i < count; i++)
        {
            var p = points[i];
            var nx = (p.X - cx) / rx;
            var ny = (p.Y - cy) / ry;
            if (Math.Abs(nx * nx + ny * ny - 1) > 1e-6) return false;
            if (Math.Abs(ny) < 1e-6) { if (nx > 0) hasEast = true; else hasWest = true; }
            if (Math.Abs(nx) < 1e-6) { if (ny > 0) hasNorth = true; else hasSouth = true; }
            var q = points[(i + 1) % count];
            area += p.X * q.Y - q.X * p.Y;
        }
        if (!(hasEast && hasNorth && hasWest && hasSouth) || Math.Abs(area) < 1e-9) return false;

        const double kappa = 0.5522847498307936;
        var direction = area > 0 ? 1 : -1;
        var nodes = new List<VectorNode>(4);
        for (var k = 0; k < 4; k++)
        {
            var angle = direction * k * Math.PI / 2;
            var sin = Math.Sin(angle);
            var cos = Math.Cos(angle);
            var anchor = new Position(cx + rx * cos, cy + ry * sin, 0);
            var tx = -direction * rx * sin * kappa;
            var ty = direction * ry * cos * kappa;
            nodes.Add(new VectorNode(anchor,
                new Position(anchor.X - tx, anchor.Y - ty, 0),
                new Position(anchor.X + tx, anchor.Y + ty, 0),
                VectorNodeType.Smooth));
        }

        subpath = new VectorSubpath { Nodes = nodes, IsClosed = true };
        return true;
    }

    private static VectorNode VectorSubpathNodeWithHandle(VectorNode node, bool isOutHandle, Position handle) =>
        MoveHandle(node, isOutHandle, handle, breakSymmetry: false);

    private static VectorNode BuildNode(Position anchor, Position? dragOffset, bool breakHandle)
    {
        if (!HasMagnitude(dragOffset, out var offset)) return VectorNode.CornerAt(anchor);
        var handleOut = new Position(anchor.X + offset.X, anchor.Y + offset.Y, anchor.Z);
        if (breakHandle) return new VectorNode(anchor, null, handleOut, VectorNodeType.Corner);
        var handleIn = new Position(anchor.X - offset.X, anchor.Y - offset.Y, anchor.Z);
        return new VectorNode(anchor, handleIn, handleOut, VectorNodeType.Smooth);
    }

    private static bool HasMagnitude(Position? candidate, out Position value)
    {
        value = candidate ?? default;
        return candidate is not null && IsFinite(value) && (Math.Abs(value.X) > 1e-9 || Math.Abs(value.Y) > 1e-9);
    }

    private static bool IsFinite(Position p) => double.IsFinite(p.X) && double.IsFinite(p.Y);

    private static bool IsZero((double X, double Y) v) => v.X == 0 && v.Y == 0;

    private static Position Offset(Position anchor, (double X, double Y) direction, double length) =>
        new(anchor.X + direction.X * length, anchor.Y + direction.Y * length, anchor.Z);

    private static Position Midpoint(Position a, Position b) => new((a.X + b.X) / 2, (a.Y + b.Y) / 2, (a.Z + b.Z) / 2);

    private static void CheckIndex(VectorSubpath subpath, int index)
    {
        if (index < 0 || index >= subpath.Nodes.Count) throw new ArgumentOutOfRangeException(nameof(index));
    }

    private static VectorSubpath ReplaceNode(VectorSubpath subpath, int index, VectorNode node)
    {
        var nodes = subpath.Nodes.ToList();
        nodes[index] = node;
        return subpath with { Nodes = nodes };
    }

    /// <summary>The nodes before and after <paramref name="index"/>, wrapping on a closed contour.
    /// False for an open contour's end nodes (and contours too short to have two neighbours).</summary>
    private static bool TryNeighbours(VectorSubpath subpath, int index, out VectorNode previous, out VectorNode next)
    {
        previous = default;
        next = default;
        var n = subpath.Nodes.Count;
        if (n < 2) return false;
        if (!subpath.IsClosed && (index == 0 || index == n - 1)) return false;
        if (subpath.IsClosed && n < 3) return false;
        previous = subpath.Nodes[(index - 1 + n) % n];
        next = subpath.Nodes[(index + 1) % n];
        return true;
    }

    private static VectorNode MakeCollinear(VectorNode node)
    {
        if (node.HandleIn is null && node.HandleOut is null) return node with { Type = VectorNodeType.Smooth };

        if (node.HandleIn is { } hi && node.HandleOut is { } ho)
        {
            // Average the in/out directions (each measured from the anchor) into one shared tangent,
            // keep each handle's own distance from the anchor unchanged, just re-point it.
            var dOut = Normalize(ho.X - node.Anchor.X, ho.Y - node.Anchor.Y);
            var dIn = Normalize(hi.X - node.Anchor.X, hi.Y - node.Anchor.Y);
            // dIn should point roughly opposite dOut on an already-smooth node; for a corner node
            // being converted, subtracting (out) and (in) gives a stable shared direction even when
            // the original handles were not collinear at all.
            var shared = Normalize(dOut.X - dIn.X, dOut.Y - dIn.Y);
            if (shared.X == 0 && shared.Y == 0) shared = dOut;

            var outLen = Distance(node.Anchor, ho);
            var inLen = Distance(node.Anchor, hi);
            var newOut = new Position(node.Anchor.X + shared.X * outLen, node.Anchor.Y + shared.Y * outLen, node.Anchor.Z);
            var newIn = new Position(node.Anchor.X - shared.X * inLen, node.Anchor.Y - shared.Y * inLen, node.Anchor.Z);
            return new VectorNode(node.Anchor, newIn, newOut, VectorNodeType.Smooth);
        }

        // Only one handle exists: mirror it to create the other side with equal length, so the node
        // has two collinear handles rather than becoming "smooth" in name only.
        if (node.HandleOut is { } onlyOut)
        {
            var mirrored = new Position(2 * node.Anchor.X - onlyOut.X, 2 * node.Anchor.Y - onlyOut.Y, node.Anchor.Z);
            return new VectorNode(node.Anchor, mirrored, onlyOut, VectorNodeType.Smooth);
        }

        var onlyIn = node.HandleIn!.Value;
        var mirroredOut = new Position(2 * node.Anchor.X - onlyIn.X, 2 * node.Anchor.Y - onlyIn.Y, node.Anchor.Z);
        return new VectorNode(node.Anchor, onlyIn, mirroredOut, VectorNodeType.Smooth);
    }

    private static (double X, double Y) Normalize(double x, double y)
    {
        var len = Math.Sqrt(x * x + y * y);
        return len < 1e-9 ? (0, 0) : (x / len, y / len);
    }

    private static double Distance(Position a, Position b) =>
        Math.Sqrt((b.X - a.X) * (b.X - a.X) + (b.Y - a.Y) * (b.Y - a.Y));
}
