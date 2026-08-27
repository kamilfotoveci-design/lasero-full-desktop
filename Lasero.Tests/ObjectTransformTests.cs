using Lasero.Core.GCode;
using Lasero.Core.Grbl;
using Lasero.Core.Scene;
using Xunit;

namespace Lasero.Tests;

public class ObjectTransformTests
{
    private static readonly Position Pivot = new(10, 10, 0);

    [Fact]
    public void IdentityTransformLeavesPointUnchanged()
    {
        var local = new Position(15, 20, 0);
        var world = ObjectTransform.Identity.Apply(local, Pivot);

        Assert.Equal(local.X, world.X, precision: 6);
        Assert.Equal(local.Y, world.Y, precision: 6);
    }

    [Fact]
    public void TranslationOffsetsPoint()
    {
        var transform = ObjectTransform.Identity with { X = 5, Y = -3 };
        var local = new Position(15, 20, 0);

        var world = transform.Apply(local, Pivot);

        Assert.Equal(20, world.X, precision: 6);
        Assert.Equal(17, world.Y, precision: 6);
    }

    [Fact]
    public void Rotation90DegreesAroundPivotSwapsAxes()
    {
        var transform = ObjectTransform.Identity with { RotationDeg = 90 };
        var local = new Position(20, 10, 0); // 10 mm to the right of the pivot

        var world = transform.Apply(local, Pivot);

        // Rotating a point 10mm to the +X of the pivot by 90 degrees lands it 10mm to +Y of the pivot.
        Assert.Equal(10, world.X, precision: 6);
        Assert.Equal(20, world.Y, precision: 6);
    }

    [Fact]
    public void ScaleStretchesAroundPivot()
    {
        var transform = ObjectTransform.Identity with { ScaleX = 2, ScaleY = 3 };
        var local = new Position(15, 15, 0); // 5mm right, 5mm down from pivot

        var world = transform.Apply(local, Pivot);

        Assert.Equal(20, world.X, precision: 6); // 10 + 5*2
        Assert.Equal(25, world.Y, precision: 6); // 10 + 5*3
    }

    [Theory]
    [InlineData(0, 1, 1)]
    [InlineData(37, 1, 1)]
    [InlineData(90, 2, 0.5)]
    [InlineData(-45, 1.5, 0.7)]
    public void ApplyThenInverseRoundTrips(double rotationDeg, double scaleX, double scaleY)
    {
        var transform = new ObjectTransform(4, -6, rotationDeg, scaleX, scaleY);
        var local = new Position(23, 17, 0);

        var world = transform.Apply(local, Pivot);
        var roundTripped = transform.Inverse(world, Pivot);

        Assert.Equal(local.X, roundTripped.X, precision: 6);
        Assert.Equal(local.Y, roundTripped.Y, precision: 6);
    }

    [Fact]
    public void ComputeResizeOnUnrotatedBottomRightHandleKeepsTopLeftAnchorFixed()
    {
        var bounds = new BoundingBox2D(0, 0, 20, 10);
        var pivot = new Position(10, 5, 0);
        var current = ObjectTransform.Identity; // object's bbox is exactly world [0,0]-[20,10]

        // Drag the bottom-right handle from (20,10) out to (40,20) -> doubles both dimensions.
        var resized = current.ComputeResize(pivot, bounds, ResizeHandle.BottomRight, new Position(40, 20, 0));

        var topLeftWorld = resized.Apply(new Position(0, 0, 0), pivot);
        var bottomRightWorld = resized.Apply(new Position(20, 10, 0), pivot);

        Assert.Equal(0, topLeftWorld.X, precision: 6);
        Assert.Equal(0, topLeftWorld.Y, precision: 6);
        Assert.Equal(40, bottomRightWorld.X, precision: 6);
        Assert.Equal(20, bottomRightWorld.Y, precision: 6);
    }

    [Fact]
    public void ComputeResizeOnRightEdgeHandleOnlyChangesWidth()
    {
        var bounds = new BoundingBox2D(0, 0, 20, 10);
        var pivot = new Position(10, 5, 0);
        var current = ObjectTransform.Identity;

        // Drag the right-edge handle from (20,5) out to (30,5) -> width grows, height untouched.
        var resized = current.ComputeResize(pivot, bounds, ResizeHandle.Right, new Position(30, 5, 0));

        var leftWorld = resized.Apply(new Position(0, 5, 0), pivot);
        var rightWorld = resized.Apply(new Position(20, 5, 0), pivot);
        var topWorld = resized.Apply(new Position(10, 0, 0), pivot);

        Assert.Equal(0, leftWorld.X, precision: 6);   // left edge (anchor) unmoved
        Assert.Equal(30, rightWorld.X, precision: 6); // right edge reached the mouse
        Assert.Equal(0, topWorld.Y, precision: 6);    // height (Y) untouched
        Assert.Equal(1, resized.ScaleY, precision: 6);
    }

    [Fact]
    public void ComputeResizeUnderRotationKeepsAnchorCornerFixed()
    {
        var bounds = new BoundingBox2D(0, 0, 20, 10);
        var pivot = new Position(10, 5, 0);
        var current = ObjectTransform.Identity with { RotationDeg = 30 };

        var anchorWorldBefore = current.Apply(new Position(0, 0, 0), pivot);

        // Drag the bottom-right handle to some arbitrary point; anchor (top-left) must not move.
        var mouseWorld = current.Apply(new Position(20, 10, 0), pivot).Offset(new Position(6, -4, 0));
        var resized = current.ComputeResize(pivot, bounds, ResizeHandle.BottomRight, mouseWorld);

        var anchorWorldAfter = resized.Apply(new Position(0, 0, 0), pivot);
        var draggedWorldAfter = resized.Apply(new Position(20, 10, 0), pivot);

        Assert.Equal(anchorWorldBefore.X, anchorWorldAfter.X, precision: 6);
        Assert.Equal(anchorWorldBefore.Y, anchorWorldAfter.Y, precision: 6);
        Assert.Equal(mouseWorld.X, draggedWorldAfter.X, precision: 6);
        Assert.Equal(mouseWorld.Y, draggedWorldAfter.Y, precision: 6);
    }

    [Fact]
    public void ComputeResizeWithAspectLockKeepsOriginalRatioAndAnchor()
    {
        var bounds = new BoundingBox2D(0, 0, 20, 10);
        var pivot = new Position(10, 5, 0);
        var current = ObjectTransform.Identity;

        var resized = current.ComputeResize(
            pivot,
            bounds,
            ResizeHandle.BottomRight,
            new Position(40, 30, 0),
            lockAspectRatio: true,
            allowFlip: false);

        var topLeft = resized.Apply(new Position(0, 0, 0), pivot);
        var bottomRight = resized.Apply(new Position(20, 10, 0), pivot);

        Assert.Equal(0, topLeft.X, precision: 6);
        Assert.Equal(0, topLeft.Y, precision: 6);
        Assert.Equal(2, (bottomRight.X - topLeft.X) / (bottomRight.Y - topLeft.Y), precision: 6);
    }

    [Fact]
    public void ComputeResizeCanPreventHandleFromFlippingRaster()
    {
        var bounds = new BoundingBox2D(0, 0, 20, 10);
        var pivot = new Position(10, 5, 0);

        var resized = ObjectTransform.Identity.ComputeResize(
            pivot,
            bounds,
            ResizeHandle.BottomRight,
            new Position(-10, -10, 0),
            allowFlip: false);

        Assert.True(resized.ScaleX > 0);
        Assert.True(resized.ScaleY > 0);
    }
}
