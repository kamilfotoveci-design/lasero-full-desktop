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
