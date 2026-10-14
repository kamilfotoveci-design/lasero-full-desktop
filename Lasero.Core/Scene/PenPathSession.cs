using Lasero.Core.Grbl;

namespace Lasero.Core.Scene;

/// <summary>Where a pen session continues an existing open contour: which object and subpath, and
/// whether drawing grows its START (the session then works on the reversed contour and turns it back
/// on commit, so the contour keeps its own direction).</summary>
public readonly record struct PenContinuation(Guid ObjectId, int SubpathIndex, bool FromStart, int ExistingNodeCount);

/// <summary>
/// The in-progress state of the pen ("Pero") tool, in world millimetres: the nodes placed so far and,
/// when the drawing started on an open end of an existing path, that continuation. WPF-free; the canvas
/// only routes pointer and key events into it and renders what it holds.
///
/// Semantics (Illustrator / LightBurn): Esc or a tool switch FINISHES the path (commit when it has two
/// usable nodes), Backspace removes the last placed node, a click on the first node closes the contour
/// when <see cref="VectorPathEditor.CanClose"/> allows it.
/// </summary>
public sealed class PenPathSession
{
    private readonly List<VectorNode> _nodes = [];

    public IReadOnlyList<VectorNode> Nodes => _nodes;

    public bool IsDrawing => _nodes.Count > 0;

    public PenContinuation? Continuation { get; private set; }

    /// <summary>Nodes placed in THIS session (an existing contour's own nodes do not count).</summary>
    public int AddedCount => _nodes.Count - (Continuation?.ExistingNodeCount ?? 0);

    /// <summary>The contour as drawn so far, open, in drawing order (newest node last).</summary>
    public VectorSubpath Current => new() { Nodes = _nodes.ToList(), IsClosed = false };

    public void Reset()
    {
        _nodes.Clear();
        Continuation = null;
    }

    /// <summary>Starts drawing from an open end of an existing contour (given in world space). The
    /// end that was clicked becomes the node new points attach to.</summary>
    public void Continue(VectorSubpath existingWorld, bool fromStart, Guid objectId, int subpathIndex)
    {
        ArgumentNullException.ThrowIfNull(existingWorld);
        if (existingWorld.IsClosed || existingWorld.Nodes.Count < 2)
            throw new ArgumentException("Only an open contour with two or more nodes can be continued.", nameof(existingWorld));
        Reset();
        var oriented = fromStart ? VectorPathEditor.ReverseSubpath(existingWorld) : existingWorld;
        // The end the pen continues from had no reason to carry an outgoing handle (nothing left it);
        // a leftover one would bend the first new segment the user never shaped.
        var nodes = oriented.Nodes.ToList();
        nodes[^1] = nodes[^1] with { HandleOut = null };
        _nodes.AddRange(nodes);
        Continuation = new PenContinuation(objectId, subpathIndex, fromStart, existingWorld.Nodes.Count);
    }

    /// <summary>Places a node: a click (no drag) is a corner, a drag makes a smooth node with
    /// symmetric handles, Alt while dragging (<paramref name="breakHandle"/>) a cusp with only the
    /// outgoing handle. A press on the newest node itself never stacks a duplicate (a zero-length
    /// segment): a plain click there is ignored, a drag from it pulls that node's outgoing handle.</summary>
    public bool Append(Position anchor, Position? dragOffset, bool breakHandle)
    {
        if (!double.IsFinite(anchor.X) || !double.IsFinite(anchor.Y)) return false;
        if (_nodes.Count > 0 && Distance(_nodes[^1].Anchor, anchor) <= VectorPathEditor.JoinMergeToleranceMm)
        {
            if (dragOffset is not { } offset || !double.IsFinite(offset.X) || !double.IsFinite(offset.Y)
                || (Math.Abs(offset.X) < 1e-9 && Math.Abs(offset.Y) < 1e-9))
                return false;
            var last = _nodes[^1];
            var handleOut = new Position(last.Anchor.X + offset.X, last.Anchor.Y + offset.Y, last.Anchor.Z);
            // The incoming handle is not moved, so the node is smooth only if it already lines up.
            var smooth = !breakHandle && last.HandleIn is { } hi && IsOpposite(hi, last.Anchor, handleOut);
            _nodes[^1] = last with { HandleOut = handleOut, Type = smooth ? VectorNodeType.Smooth : VectorNodeType.Corner };
            return true;
        }
        var appended = VectorPathEditor.AppendNode(Current, anchor, dragOffset, breakHandle);
        _nodes.Clear();
        _nodes.AddRange(appended.Nodes);
        return true;
    }

    /// <summary>Backspace: drops the newest node placed in this session. False when there is none
    /// (a continued contour's own nodes are never removed by the pen).</summary>
    public bool RemoveLast()
    {
        if (AddedCount <= 0) return false;
        _nodes.RemoveAt(_nodes.Count - 1);
        if (_nodes.Count == 0) Continuation = null;
        return true;
    }

    /// <summary>True when the first node can be clicked to close the contour.</summary>
    public bool CanClose => _nodes.Count >= 2 && VectorPathEditor.CanClose(Current);

    /// <summary>The finished OPEN contour in its final orientation, or null when there is nothing to
    /// commit (fewer than two nodes, or a continuation that added nothing).</summary>
    public VectorSubpath? BuildOpen()
    {
        if (_nodes.Count < 2 || AddedCount <= 0) return null;
        return Orient(Current);
    }

    /// <summary>The closed contour (click on the first node, optionally dragging its handles), or
    /// null when closing is not allowed.</summary>
    public VectorSubpath? BuildClosed(Position? dragOffset, bool breakHandle)
    {
        if (!CanClose) return null;
        var closed = VectorPathEditor.Close(Current, dragOffset, breakHandle);
        return closed.IsClosed ? Orient(closed) : null;
    }

    private VectorSubpath Orient(VectorSubpath subpath) =>
        Continuation is { FromStart: true } ? VectorPathEditor.ReverseSubpath(subpath) : subpath;

    private static bool IsOpposite(Position handleIn, Position anchor, Position handleOut)
    {
        var ix = handleIn.X - anchor.X;
        var iy = handleIn.Y - anchor.Y;
        var ox = handleOut.X - anchor.X;
        var oy = handleOut.Y - anchor.Y;
        var li = Math.Sqrt(ix * ix + iy * iy);
        var lo = Math.Sqrt(ox * ox + oy * oy);
        if (li < 1e-9 || lo < 1e-9) return false;
        return (ix * ox + iy * oy) / (li * lo) < -0.99996; // within ~0.5°
    }

    private static double Distance(Position a, Position b) =>
        Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));
}
