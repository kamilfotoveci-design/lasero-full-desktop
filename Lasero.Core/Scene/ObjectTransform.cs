using Lasero.Core.GCode;
using Lasero.Core.Grbl;

namespace Lasero.Core.Scene;

/// <summary>Which resize handle is being dragged, relative to local bounds (Min/Max, not screen up/down).</summary>
public enum ResizeHandle
{
    TopLeft, Top, TopRight,
    Left, Right,
    BottomLeft, Bottom, BottomRight,
}

/// <summary>
/// Position + rotation + non-uniform scale for one SceneObject, applied around
/// that object's own pivot (its local bounding-box center — see SceneObject).
/// Scale/rotate happen first (around the pivot), translation (X, Y) last, so
/// (X, Y) is simply where the pivot itself ends up in world space.
/// </summary>
public readonly record struct ObjectTransform(double X, double Y, double RotationDeg, double ScaleX, double ScaleY)
{
    public static readonly ObjectTransform Identity = new(0, 0, 0, 1, 1);

    private const double MinScaleMagnitude = 0.001;

    public Position Apply(Position local, Position pivot)
    {
        var dx = (local.X - pivot.X) * ScaleX;
        var dy = (local.Y - pivot.Y) * ScaleY;
        var (rx, ry) = Rotate(dx, dy, RotationDeg);
        return new Position(pivot.X + rx + X, pivot.Y + ry + Y, local.Z);
    }

    /// <summary>Inverse of Apply — maps a world-space point back to this object's local space. Used to
    /// convert mouse coordinates into local space before computing resize deltas.</summary>
    public Position Inverse(Position world, Position pivot)
    {
        var wx = world.X - pivot.X - X;
        var wy = world.Y - pivot.Y - Y;
        var (rx, ry) = Rotate(wx, wy, -RotationDeg);
        var sx = SafeScale(ScaleX);
        var sy = SafeScale(ScaleY);
        return new Position(pivot.X + rx / sx, pivot.Y + ry / sy, world.Z);
    }

    /// <summary>
    /// Computes the transform that results from dragging one resize handle to <paramref name="mouseWorld"/>,
    /// keeping the opposite corner/edge (the "anchor") fixed in world space. Correct under rotation: the
    /// anchor is defined in the object's local frame and re-derived in world space via the CURRENT transform,
    /// then the new scale/position are solved so the anchor stays put and the dragged handle reaches the mouse.
    /// </summary>
    public ObjectTransform ComputeResize(
        Position pivot,
        BoundingBox2D localBounds,
        ResizeHandle handle,
        Position mouseWorld,
        bool lockAspectRatio = false,
        bool allowFlip = true)
    {
        var dragged = HandleLocalPoint(localBounds, pivot, handle);
        var anchorLocal = new Position(2 * pivot.X - dragged.X, 2 * pivot.Y - dragged.Y, pivot.Z);

        var dDragX = dragged.X - pivot.X;
        var dDragY = dragged.Y - pivot.Y;
        var dAnchorX = -dDragX;
        var dAnchorY = -dDragY;

        var anchorWorld = Apply(anchorLocal, pivot);
        var diffWorldX = mouseWorld.X - anchorWorld.X;
        var diffWorldY = mouseWorld.Y - anchorWorld.Y;
        var (diffLocalX, diffLocalY) = Rotate(diffWorldX, diffWorldY, -RotationDeg);

        double newScaleX;
        double newScaleY;

        var isCornerHandle = Math.Abs(dDragX) > 1e-9 && Math.Abs(dDragY) > 1e-9;
        if (lockAspectRatio && isCornerHandle)
        {
            // Project the mouse vector onto the original corner-to-anchor diagonal. This keeps the
            // object's current aspect ratio while still preserving the opposite corner as the anchor.
            var baseX = 2 * dDragX * ScaleX;
            var baseY = 2 * dDragY * ScaleY;
            var denominator = baseX * baseX + baseY * baseY;
            var factor = denominator > 1e-12
                ? (diffLocalX * baseX + diffLocalY * baseY) / denominator
                : 1;
            factor = ClampResizeScale(factor, 1, allowFlip);
            newScaleX = ScaleX * factor;
            newScaleY = ScaleY * factor;
        }
        else
        {
            newScaleX = Math.Abs(dDragX) > 1e-9 ? diffLocalX / (2 * dDragX) : ScaleX;
            newScaleY = Math.Abs(dDragY) > 1e-9 ? diffLocalY / (2 * dDragY) : ScaleY;
            newScaleX = ClampResizeScale(newScaleX, ScaleX, allowFlip);
            newScaleY = ClampResizeScale(newScaleY, ScaleY, allowFlip);
        }

        var vLocalX = newScaleX * dAnchorX;
        var vLocalY = newScaleY * dAnchorY;
        var (vWorldX, vWorldY) = Rotate(vLocalX, vLocalY, RotationDeg);

        var newPivotWorldX = anchorWorld.X - vWorldX;
        var newPivotWorldY = anchorWorld.Y - vWorldY;

        return this with
        {
            ScaleX = newScaleX,
            ScaleY = newScaleY,
            X = newPivotWorldX - pivot.X,
            Y = newPivotWorldY - pivot.Y,
        };
    }

    /// <summary>The local-space point a given handle drags, using Min/Max for its active axes and the
    /// pivot's own coordinate (i.e. an offset of zero) for whichever axis that handle doesn't affect.
    /// Public so the canvas can compute matching on-screen handle positions (via Apply) for rendering.</summary>
    public static Position HandleLocalPoint(BoundingBox2D bounds, Position pivot, ResizeHandle handle)
    {
        double x = handle switch
        {
            ResizeHandle.TopLeft or ResizeHandle.Left or ResizeHandle.BottomLeft => bounds.MinX,
            ResizeHandle.TopRight or ResizeHandle.Right or ResizeHandle.BottomRight => bounds.MaxX,
            _ => pivot.X,
        };
        double y = handle switch
        {
            ResizeHandle.TopLeft or ResizeHandle.Top or ResizeHandle.TopRight => bounds.MinY,
            ResizeHandle.BottomLeft or ResizeHandle.Bottom or ResizeHandle.BottomRight => bounds.MaxY,
            _ => pivot.Y,
        };
        return new Position(x, y, pivot.Z);
    }

    private static double SafeScale(double scale) =>
        Math.Abs(scale) < MinScaleMagnitude ? MinScaleMagnitude * Math.Sign(scale is 0 ? 1 : scale) : scale;

    private static double ClampMagnitude(double scale) =>
        Math.Abs(scale) < MinScaleMagnitude ? MinScaleMagnitude * (scale < 0 ? -1 : 1) : scale;

    private static double ClampResizeScale(double scale, double originalScale, bool allowFlip)
    {
        if (!allowFlip && Math.Sign(scale) != Math.Sign(originalScale))
            return MinScaleMagnitude * (originalScale < 0 ? -1 : 1);
        return ClampMagnitude(scale);
    }

    private static (double X, double Y) Rotate(double x, double y, double degrees)
    {
        var rad = degrees * Math.PI / 180.0;
        var cos = Math.Cos(rad);
        var sin = Math.Sin(rad);
        return (x * cos - y * sin, x * sin + y * cos);
    }
}
