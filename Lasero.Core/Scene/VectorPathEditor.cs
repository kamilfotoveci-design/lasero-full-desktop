using Lasero.Core.Grbl;

namespace Lasero.Core.Scene;

/// <summary>
/// Pure node-editing operations on a VectorSubpath — add/delete/convert/close/move, all returning a
/// new VectorSubpath (the model is immutable) rather than mutating in place, so callers can build one
/// ReplaceObjectsCommand per gesture the same way CommitTextEdit rebuilds a whole SceneObject per
/// wording change. No WPF/canvas types are referenced here — this is deliberately unit-testable
/// without a live app.
/// </summary>
public static class VectorPathEditor
{
    /// <summary>Adds a corner node (click) or a smooth node with symmetric handles (click-drag) to
    /// the end of an in-progress open subpath. dragOffset is the drag distance from the click point,
    /// in the same units as the anchor — a smooth node's outgoing handle is placed at anchor +
    /// dragOffset and, symmetrically, its incoming handle at anchor - dragOffset, exactly like every
    /// mainstream pen tool's "drag while placing a point" gesture.</summary>
    public static VectorSubpath AppendNode(VectorSubpath subpath, Position anchor, Position? dragOffset)
    {
        ArgumentNullException.ThrowIfNull(subpath);
        var node = BuildNode(anchor, dragOffset);
        return subpath with { Nodes = [.. subpath.Nodes, node] };
    }

    /// <summary>Closes an open subpath onto its own first node — the gesture behind "hover the first
    /// node, click to close". Any drag handle placed while making this closing click is applied to
    /// the FIRST node's incoming handle (and, symmetrically, its outgoing handle if it had none),
    /// exactly like appending a node, except the anchor is reused rather than duplicated.</summary>
    public static VectorSubpath Close(VectorSubpath subpath, Position? dragOffset = null)
    {
        ArgumentNullException.ThrowIfNull(subpath);
        if (subpath.Nodes.Count < 2) return subpath with { IsClosed = false };

        var nodes = subpath.Nodes.ToList();
        if (HasMagnitude(dragOffset, out var offset))
        {
            var first = nodes[0];
            var anchor = first.Anchor;
            var handleIn = new Position(anchor.X - offset.X, anchor.Y - offset.Y, anchor.Z);
            var handleOut = first.HandleOut ?? new Position(anchor.X + offset.X, anchor.Y + offset.Y, anchor.Z);
            nodes[0] = new VectorNode(anchor, handleIn, handleOut, VectorNodeType.Smooth);
        }

        return subpath with { Nodes = nodes, IsClosed = true };
    }

    /// <summary>The inverse of Close: drops the closing segment back to the first node, without
    /// touching any node or handle. Symmetric with Close's own "just flip IsClosed, no dragOffset"
    /// path — reopening never needs to guess at a handle the way closing does with a drag.</summary>
    public static VectorSubpath Open(VectorSubpath subpath)
    {
        ArgumentNullException.ThrowIfNull(subpath);
        return subpath with { IsClosed = false };
    }

    /// <summary>Removes one node and reconnects its neighbours directly. When the two segments that
    /// met at the deleted node were both straight, the result is a plain straight segment. Otherwise
    /// the new A→C segment keeps whichever adjacent handles already existed (A's HandleOut, C's
    /// HandleIn) so the curve does not collapse to a straight line just because a node in the middle
    /// was removed — this is a documented simplification, not a full curvature-preserving refit: a
    /// true refit would need to re-solve for new control points that best approximate the original
    /// two-segment curve, which is out of scope for this pass (see project notes). It is still far
    /// better than naive point removal, which is what would happen if this simply dropped the node
    /// from an already-flattened polyline.</summary>
    public static VectorSubpath RemoveNode(VectorSubpath subpath, int index)
    {
        ArgumentNullException.ThrowIfNull(subpath);
        if (index < 0 || index >= subpath.Nodes.Count) throw new ArgumentOutOfRangeException(nameof(index));
        if (subpath.Nodes.Count <= (subpath.IsClosed ? 3 : 2))
            // Below the minimum node count for a usable subpath (2 open / 3 closed) — caller should
            // instead delete the whole object; refuse rather than produce a degenerate 0-1 node path.
            return subpath;

        var nodes = subpath.Nodes.ToList();
        nodes.RemoveAt(index);
        return subpath with { Nodes = nodes };
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
        if (t is <= 0 or >= 1) throw new ArgumentOutOfRangeException(nameof(t));
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

    /// <summary>Corner ↔ Smooth. Smooth forces the two handles collinear through the anchor (context
    /// menu / shortcut action, not a drag) — see MakeCollinear for the exact rule. Corner just flips
    /// the flag; existing handle positions are left alone so nothing visibly jumps on conversion.</summary>
    public static VectorNode ConvertNodeType(VectorNode node, VectorNodeType type)
    {
        if (type == VectorNodeType.Corner) return node with { Type = VectorNodeType.Corner };
        return MakeCollinear(node);
    }

    /// <summary>Moves a handle to a new position. On a Smooth node (and not breaking symmetry) the
    /// opposite handle is re-pointed to stay collinear through the anchor, keeping its own distance
    /// from the anchor unchanged — only its direction follows the dragged handle. On a Corner node,
    /// or when breakSymmetry is true, the opposite handle is untouched.</summary>
    public static VectorNode MoveHandle(VectorNode node, bool isOutHandle, Position newPosition, bool breakSymmetry)
    {
        var moved = isOutHandle ? node with { HandleOut = newPosition } : node with { HandleIn = newPosition };
        if (moved.Type != VectorNodeType.Smooth || breakSymmetry) return moved;

        var opposite = isOutHandle ? moved.HandleIn : moved.HandleOut;
        if (opposite is null) return moved;

        var dir = Normalize(newPosition.X - node.Anchor.X, newPosition.Y - node.Anchor.Y);
        if (dir.X == 0 && dir.Y == 0) return moved;

        var oppositeLen = Distance(node.Anchor, opposite.Value);
        var newOpposite = new Position(node.Anchor.X - dir.X * oppositeLen, node.Anchor.Y - dir.Y * oppositeLen, node.Anchor.Z);
        return isOutHandle ? moved with { HandleIn = newOpposite } : moved with { HandleOut = newOpposite };
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
        return subpath with { Nodes = nodes };
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
        if (t is <= 0 or >= 1) throw new ArgumentOutOfRangeException(nameof(t));

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
        nodes[aIndex] = a with { HandleOut = newC1 };
        nodes[bIndex] = b with { HandleIn = newC2 };
        return baseline with { Nodes = nodes };
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
    /// exactly like dragging A's end onto B's start. The join point keeps side A's own anchor and
    /// incoming handle (the endpoint the user actually dragged there) and side B's own outgoing
    /// handle (the curve leaving into the rest of B) — matching TryCloseByEndpointJoin's own "snap
    /// exactly onto the far endpoint, keep the dragged side's own data" convention. The merged node's
    /// type is always Corner: A and B may disagree on Smooth/Corner, and forcing either choice would
    /// silently move a handle the user did not touch — Corner is the only type that changes
    /// nothing else about either side's geometry, and can be toggled to Smooth afterward like any
    /// other node.</summary>
    public static VectorSubpath JoinAtEndpoints(VectorSubpath a, bool aJoinAtStart, VectorSubpath b, bool bJoinAtStart)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        if (a.IsClosed || b.IsClosed)
            throw new ArgumentException("Only open subpaths can be joined at an endpoint.");
        if (a.Nodes.Count == 0 || b.Nodes.Count == 0)
            throw new ArgumentException("Cannot join an empty subpath.");

        var orientedA = aJoinAtStart ? ReverseSubpath(a) : a;
        var orientedB = bJoinAtStart ? b : ReverseSubpath(b);

        var joinNode = orientedA.Nodes[^1] with { HandleOut = orientedB.Nodes[0].HandleOut, Type = VectorNodeType.Corner };
        var nodes = new List<VectorNode>(orientedA.Nodes.Count + orientedB.Nodes.Count - 1);
        nodes.AddRange(orientedA.Nodes.Take(orientedA.Nodes.Count - 1));
        nodes.Add(joinNode);
        nodes.AddRange(orientedB.Nodes.Skip(1));

        return new VectorSubpath { Nodes = nodes, IsClosed = false };
    }

    private static VectorNode BuildNode(Position anchor, Position? dragOffset)
    {
        if (!HasMagnitude(dragOffset, out var offset)) return VectorNode.CornerAt(anchor);
        var handleOut = new Position(anchor.X + offset.X, anchor.Y + offset.Y, anchor.Z);
        var handleIn = new Position(anchor.X - offset.X, anchor.Y - offset.Y, anchor.Z);
        return new VectorNode(anchor, handleIn, handleOut, VectorNodeType.Smooth);
    }

    private static bool HasMagnitude(Position? candidate, out Position value)
    {
        value = candidate ?? default;
        return candidate is not null && (Math.Abs(value.X) > 1e-9 || Math.Abs(value.Y) > 1e-9);
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
