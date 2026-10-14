using Lasero.Core.GCode;
using Lasero.Core.Grbl;

namespace Lasero.Core.Scene;

/// <summary>
/// Pure maths for transforming a multi-object selection as one group: scale about the opposite
/// corner/edge of the group's world bounding box, or rotate about the box centre. Every function maps
/// one object's existing <see cref="ObjectTransform"/> to a new one, so the result is committed through
/// the same <c>TransformObjectCommand</c> the single-object path uses; no new transform model.
///
/// Group scale acts on world axes. An <see cref="ObjectTransform"/> scales in the object's own rotated
/// frame, which cannot express the shear that a non-uniform world scale produces on an object rotated
/// by anything other than a multiple of 90 degrees. Those objects keep their rotation and take the
/// world scale projected onto their own axes (see <see cref="ScaleAbout"/>); uniform scale and
/// axis-aligned objects are exact.
/// </summary>
public static class GroupTransform
{
    /// <summary>Smallest scale magnitude a resize may reach, the same floor ObjectTransform uses.</summary>
    public const double MinScaleMagnitude = 0.001;

    private const double Epsilon = 1e-9;

    /// <summary>The group's scale factors and the world point that stays fixed while dragging a handle.</summary>
    public readonly record struct ScaleSolution(double Gx, double Gy, Position Anchor);

    /// <summary>
    /// Scale factors for dragging <paramref name="handle"/> of the group box to <paramref name="mouseWorld"/>,
    /// keeping the opposite corner (or edge) fixed. Edge handles scale one axis only. With
    /// <paramref name="lockAspectRatio"/> a corner drag projects the pointer onto the corner diagonal so
    /// both axes share one factor, exactly as the single-object resize does.
    /// </summary>
    public static ScaleSolution ComputeScale(
        BoundingBox2D box, ResizeHandle handle, Position mouseWorld, bool lockAspectRatio, bool allowFlip)
    {
        var centre = CentreOf(box);
        var dragged = ObjectTransform.HandleLocalPoint(box, centre, handle);
        var anchor = new Position(2 * centre.X - dragged.X, 2 * centre.Y - dragged.Y, centre.Z);

        var vx = dragged.X - anchor.X;
        var vy = dragged.Y - anchor.Y;
        var dx = mouseWorld.X - anchor.X;
        var dy = mouseWorld.Y - anchor.Y;

        var hasX = Math.Abs(vx) > Epsilon;
        var hasY = Math.Abs(vy) > Epsilon;

        double gx;
        double gy;
        if (lockAspectRatio && hasX && hasY)
        {
            var factor = (dx * vx + dy * vy) / (vx * vx + vy * vy);
            gx = gy = ClampFactor(factor, allowFlip);
        }
        else
        {
            gx = hasX ? ClampFactor(dx / vx, allowFlip) : 1;
            gy = hasY ? ClampFactor(dy / vy, allowFlip) : 1;
        }

        return new ScaleSolution(gx, gy, anchor);
    }

    /// <summary>The group box after scaling by (gx, gy) about <paramref name="anchor"/>, normalised so Min &lt;= Max.</summary>
    public static BoundingBox2D ScaleBox(BoundingBox2D box, Position anchor, double gx, double gy)
    {
        var x1 = anchor.X + (box.MinX - anchor.X) * gx;
        var x2 = anchor.X + (box.MaxX - anchor.X) * gx;
        var y1 = anchor.Y + (box.MinY - anchor.Y) * gy;
        var y2 = anchor.Y + (box.MaxY - anchor.Y) * gy;
        return new BoundingBox2D(Math.Min(x1, x2), Math.Min(y1, y2), Math.Max(x1, x2), Math.Max(y1, y2));
    }

    /// <summary>
    /// <paramref name="t"/> after the world transform p' = anchor + diag(gx, gy) * (p - anchor).
    /// The object's pivot moves exactly. Its own scale becomes the group scale projected onto its local
    /// axes, <c>Sx' = Sx (gx cos^2 + gy sin^2)</c> and <c>Sy' = Sy (gx sin^2 + gy cos^2)</c>, with the
    /// rotation unchanged. That is exact for uniform scale and for rotations of 0/90/180/270 degrees.
    /// For other angles under non-uniform scale a shear would be needed; this is the closest
    /// representable result and the single-object resize keeps the same rotation-preserving convention.
    /// </summary>
    public static ObjectTransform ScaleAbout(ObjectTransform t, Position pivot, Position anchor, double gx, double gy)
    {
        // Exactly no change must stay bit-for-bit equal so a click without a drag records no history.
        if (gx == 1 && gy == 1) return t;

        var pivotWorldX = pivot.X + t.X;
        var pivotWorldY = pivot.Y + t.Y;
        var newPivotX = anchor.X + (pivotWorldX - anchor.X) * gx;
        var newPivotY = anchor.Y + (pivotWorldY - anchor.Y) * gy;

        var rad = t.RotationDeg * Math.PI / 180.0;
        var c = Math.Cos(rad);
        var s = Math.Sin(rad);

        if ((gx < 0 || gy < 0) && Math.Abs(c * s) > 1e-9)
        {
            // A flip. The projection formula below goes through zero (cos 2a = 0 at 45 degrees) and
            // would collapse the object, so rebuild the linear part G * R(a) * diag(Sx, Sy) from its
            // columns. When the columns are orthogonal (|gx| == |gy|, or an axis-aligned child) the
            // result is exactly R(phi) * diag(sx, sy); otherwise the column lengths and the sign of
            // the determinant are the closest representable, non-degenerate answer.
            var col1X = gx * c * t.ScaleX;
            var col1Y = gy * s * t.ScaleX;
            var col2X = -gx * s * t.ScaleY;
            var col2Y = gy * c * t.ScaleY;
            var len1 = Math.Sqrt(col1X * col1X + col1Y * col1Y);
            var len2 = Math.Sqrt(col2X * col2X + col2Y * col2Y);
            var detSign = Math.Sign(gx * gy * t.ScaleX * t.ScaleY);
            if (detSign == 0) detSign = 1;
            var phi = Math.Atan2(col1Y, col1X) * 180.0 / Math.PI;
            return t with
            {
                X = newPivotX - pivot.X,
                Y = newPivotY - pivot.Y,
                RotationDeg = phi,
                ScaleX = KeepAboveMinimum(len1, 1),
                ScaleY = KeepAboveMinimum(detSign * len2, detSign),
            };
        }

        var scaleX = KeepAboveMinimum(t.ScaleX * (gx * c * c + gy * s * s), t.ScaleX);
        var scaleY = KeepAboveMinimum(t.ScaleY * (gx * s * s + gy * c * c), t.ScaleY);

        return t with
        {
            X = newPivotX - pivot.X,
            Y = newPivotY - pivot.Y,
            ScaleX = scaleX,
            ScaleY = scaleY,
        };
    }

    /// <summary>
    /// <paramref name="t"/> after rotating the world by <paramref name="deltaDeg"/> about
    /// <paramref name="centre"/>: the pivot orbits the centre and the object's own rotation advances by
    /// the same angle. Exact for any scale, flips included.
    /// </summary>
    public static ObjectTransform RotateAbout(ObjectTransform t, Position pivot, Position centre, double deltaDeg)
    {
        if (deltaDeg == 0) return t;
        var (rx, ry) = RotateVector(pivot.X + t.X - centre.X, pivot.Y + t.Y - centre.Y, deltaDeg);
        return t with
        {
            RotationDeg = t.RotationDeg + deltaDeg,
            X = centre.X + rx - pivot.X,
            Y = centre.Y + ry - pivot.Y,
        };
    }

    /// <summary>World bounding box of the objects, ignoring empty ones. <see cref="BoundingBox2D.Empty"/> when none.</summary>
    public static BoundingBox2D BoundsOf(IEnumerable<SceneObject> objects)
    {
        var box = BoundingBox2D.Empty;
        foreach (var obj in objects)
        {
            var b = obj.WorldBounds();
            if (b.IsEmpty) continue;
            box = box.Include(b.MinX, b.MinY).Include(b.MaxX, b.MaxY);
        }
        return box;
    }

    public static Position CentreOf(BoundingBox2D box) => new((box.MinX + box.MaxX) / 2, (box.MinY + box.MaxY) / 2, 0);

    public static (double X, double Y) RotateVector(double x, double y, double degrees)
    {
        var rad = degrees * Math.PI / 180.0;
        var cos = Math.Cos(rad);
        var sin = Math.Sin(rad);
        return (x * cos - y * sin, x * sin + y * cos);
    }

    private static double ClampFactor(double factor, bool allowFlip)
    {
        if (!allowFlip && factor < 0) return MinScaleMagnitude;
        return Math.Abs(factor) < MinScaleMagnitude ? MinScaleMagnitude * (factor < 0 ? -1 : 1) : factor;
    }

    private static double KeepAboveMinimum(double value, double original)
    {
        if (Math.Abs(value) >= MinScaleMagnitude) return value;
        var sign = value < 0 ? -1 : value > 0 ? 1 : original < 0 ? -1 : 1;
        return MinScaleMagnitude * sign;
    }
}
