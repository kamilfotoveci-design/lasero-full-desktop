using Lasero.Core.Grbl;

namespace Lasero.Core.Scene.Snapping;

/// <summary>
/// Snaps the edge(s) a resize handle drags instead of the raw pointer. A side handle moves one edge, a
/// corner handle two; each is resolved on its own world axis (<see cref="SnapEngine.ResolveAxis"/>) and
/// the pointer is shifted by the difference, so the resized box lands exactly on the target whatever
/// offset the user grabbed the handle at. Pure world-space maths: the caller converts its screen-pixel
/// tolerance to <c>toleranceWorld</c>; none of it ever reaches toolpaths.
/// </summary>
public static class ResizeEdgeSnap
{
    public readonly record struct Result(Position Mouse, SnapCandidate? TargetX, SnapCandidate? TargetY)
    {
        public bool Snapped => TargetX is not null || TargetY is not null;
    }

    /// <param name="mouse">Raw pointer in world space.</param>
    /// <param name="anchor">The fixed opposite corner/edge, world space.</param>
    /// <param name="draggedHandleAt">Where the dragged handle ends up for a given pointer position (the
    /// caller's own resize solve), so the edge snapped is the one the user actually sees.</param>
    /// <param name="affectsX">The drag moves an edge perpendicular to the world X axis.</param>
    /// <param name="affectsY">The drag moves an edge perpendicular to the world Y axis.</param>
    /// <param name="lockAspect">Corner drags keep the aspect ratio: the handle slides along the diagonal,
    /// so only the closer axis snaps and the other follows.</param>
    public static Result Resolve(
        Position mouse,
        Position anchor,
        Func<Position, Position> draggedHandleAt,
        bool affectsX,
        bool affectsY,
        bool lockAspect,
        IReadOnlyList<SnapCandidate> candidates,
        double toleranceWorld,
        double? gridStepMm)
    {
        var handle = draggedHandleAt(mouse);
        var x = affectsX
            ? SnapEngine.ResolveAxis(handle.X, SnapAxis.X, candidates, toleranceWorld, gridStepMm, handle.Y)
            : new AxisSnapResult(handle.X, null);
        var y = affectsY
            ? SnapEngine.ResolveAxis(handle.Y, SnapAxis.Y, candidates, toleranceWorld, gridStepMm, handle.X)
            : new AxisSnapResult(handle.Y, null);
        if (!x.Snapped && !y.Snapped) return new Result(mouse, null, null);

        if (lockAspect && affectsX && affectsY)
        {
            var useX = x.Snapped && (!y.Snapped || Math.Abs(x.Value - handle.X) <= Math.Abs(y.Value - handle.Y));
            var axisValue = useX ? x.Value : y.Value;
            var anchorValue = useX ? anchor.X : anchor.Y;
            var handleValue = useX ? handle.X : handle.Y;
            var span = handleValue - anchorValue;
            if (Math.Abs(span) < 1e-9) return new Result(mouse, null, null);

            var factor = (axisValue - anchorValue) / span;
            var scaled = new Position(
                anchor.X + (handle.X - anchor.X) * factor,
                anchor.Y + (handle.Y - anchor.Y) * factor,
                mouse.Z);
            // A grid target is displayed where the snapped edge really ends up on the other axis.
            SnapCandidate? target = useX ? x.Target : y.Target;
            if (target is { Kind: SnapKind.Grid })
                target = new SnapCandidate(new Position(scaled.X, scaled.Y, 0), SnapKind.Grid);
            return useX ? new Result(scaled, target, null) : new Result(scaled, null, target);
        }

        var shifted = new Position(
            mouse.X + (x.Snapped ? x.Value - handle.X : 0),
            mouse.Y + (y.Snapped ? y.Value - handle.Y : 0),
            mouse.Z);
        return new Result(shifted, x.Target, y.Target);
    }
}
