using Lasero.Core.GCode;
using Lasero.Core.Grbl;
using Lasero.Core.Layers;
using Lasero.Core.Scene;
using Lasero.Core.Scene.Commands;
using Xunit;

namespace Lasero.Tests;

/// <summary>
/// Group (multi-selection) resize and rotate maths. The group box is the world bounds of the selected
/// objects; every object keeps its own ObjectTransform representation and is committed through one
/// CompositeSceneCommand of TransformObjectCommand.
/// </summary>
public class GroupTransformTests
{
    // Two 10x10 squares side by side: group box is (0,0)-(30,10), centre (15,5).
    private static SceneObject Left() => Square(0, 0, 10, 10, "A");
    private static SceneObject Right() => Square(20, 0, 30, 10, "B");

    private static SceneObject Square(double x1, double y1, double x2, double y2, string name) =>
        ScenePrimitiveFactory.CreateRectangle(new Position(x1, y1, 0), new Position(x2, y2, 0), RgbColor.Black, name);

    private static Position PivotWorld(SceneObject obj) =>
        obj.Transform.Apply(obj.LocalPivot, obj.LocalPivot);

    private static void AssertBounds(BoundingBox2D b, double minX, double minY, double maxX, double maxY)
    {
        Assert.Equal(minX, b.MinX, precision: 6);
        Assert.Equal(minY, b.MinY, precision: 6);
        Assert.Equal(maxX, b.MaxX, precision: 6);
        Assert.Equal(maxY, b.MaxY, precision: 6);
    }

    private static void ApplyScale(IEnumerable<SceneObject> objects, GroupTransform.ScaleSolution s)
    {
        foreach (var obj in objects)
            obj.Transform = GroupTransform.ScaleAbout(obj.Transform, obj.LocalPivot, s.Anchor, s.Gx, s.Gy);
    }

    [Fact]
    public void CornerDragScalesEveryObjectAboutTheOppositeCorner()
    {
        var objects = new[] { Left(), Right() };
        var box = GroupTransform.BoundsOf(objects);
        AssertBounds(box, 0, 0, 30, 10);

        // TopRight is the (max X, min Y) corner; its opposite, (0, 10), must not move.
        var solution = GroupTransform.ComputeScale(
            box, ResizeHandle.TopRight, new Position(60, -20, 0), lockAspectRatio: false, allowFlip: true);

        Assert.Equal(2.0, solution.Gx, precision: 9);
        Assert.Equal(3.0, solution.Gy, precision: 9);
        Assert.Equal(0, solution.Anchor.X, precision: 9);
        Assert.Equal(10, solution.Anchor.Y, precision: 9);

        ApplyScale(objects, solution);

        // The dragged corner lands on the pointer, the anchor stays put.
        AssertBounds(GroupTransform.BoundsOf(objects), 0, -20, 60, 10);
        // Each square is stretched 2 x 3 about (0, 10): centres (5,5) -> (10,-5) and (25,5) -> (50,-5).
        Assert.Equal(10, PivotWorld(objects[0]).X, precision: 6);
        Assert.Equal(-5, PivotWorld(objects[0]).Y, precision: 6);
        Assert.Equal(50, PivotWorld(objects[1]).X, precision: 6);
        AssertBounds(objects[0].WorldBounds(), 0, -20, 20, 10);
        AssertBounds(objects[1].WorldBounds(), 40, -20, 60, 10);
    }

    [Fact]
    public void LockedAspectUsesOneFactorProjectedOntoTheCornerDiagonal()
    {
        var box = new BoundingBox2D(0, 0, 30, 10);

        var free = GroupTransform.ComputeScale(
            box, ResizeHandle.TopRight, new Position(60, -20, 0), lockAspectRatio: false, allowFlip: true);
        var locked = GroupTransform.ComputeScale(
            box, ResizeHandle.TopRight, new Position(60, -20, 0), lockAspectRatio: true, allowFlip: true);

        Assert.NotEqual(free.Gx, free.Gy);
        // Pointer vector (60,-30) onto the diagonal (30,-10): (1800 + 300) / 1000.
        Assert.Equal(2.1, locked.Gx, precision: 9);
        Assert.Equal(locked.Gx, locked.Gy, precision: 12);
    }

    [Fact]
    public void EdgeHandleScalesOneAxisOnly()
    {
        var objects = new[] { Left(), Right() };
        var box = GroupTransform.BoundsOf(objects);

        // Right edge dragged to x = 45 while the pointer wanders in Y: only X changes.
        var solution = GroupTransform.ComputeScale(
            box, ResizeHandle.Right, new Position(45, 99, 0), lockAspectRatio: true, allowFlip: true);
        Assert.Equal(1.5, solution.Gx, precision: 9);
        Assert.Equal(1.0, solution.Gy, precision: 9);

        ApplyScale(objects, solution);
        AssertBounds(GroupTransform.BoundsOf(objects), 0, 0, 45, 10);
    }

    [Fact]
    public void FlipIsClampedWhenNotAllowed()
    {
        var box = new BoundingBox2D(0, 0, 30, 10);

        var clamped = GroupTransform.ComputeScale(
            box, ResizeHandle.Right, new Position(-10, 5, 0), lockAspectRatio: false, allowFlip: false);
        var flipped = GroupTransform.ComputeScale(
            box, ResizeHandle.Right, new Position(-30, 5, 0), lockAspectRatio: false, allowFlip: true);

        Assert.Equal(GroupTransform.MinScaleMagnitude, clamped.Gx, precision: 12);
        Assert.Equal(-1.0, flipped.Gx, precision: 9);
    }

    [Fact]
    public void ScaleBoxMatchesTheScaledObjects()
    {
        var box = new BoundingBox2D(0, 0, 30, 10);
        var solution = GroupTransform.ComputeScale(
            box, ResizeHandle.TopLeft, new Position(-30, -10, 0), lockAspectRatio: false, allowFlip: true);

        var scaled = GroupTransform.ScaleBox(box, solution.Anchor, solution.Gx, solution.Gy);

        // TopLeft is (min X, min Y); anchor is (30, 10).
        AssertBounds(scaled, -30, -10, 30, 10);
    }

    [Fact]
    public void RotationTurnsEveryObjectAboutTheGroupCentre()
    {
        var objects = new[] { Left(), Right() };
        var centre = GroupTransform.CentreOf(GroupTransform.BoundsOf(objects));
        Assert.Equal(15, centre.X, precision: 9);
        Assert.Equal(5, centre.Y, precision: 9);

        foreach (var obj in objects)
            obj.Transform = GroupTransform.RotateAbout(obj.Transform, obj.LocalPivot, centre, 90);

        // (5,5) is 10 left of the centre -> 10 below it; (25,5) is 10 right -> 10 above.
        Assert.Equal(15, PivotWorld(objects[0]).X, precision: 6);
        Assert.Equal(-5, PivotWorld(objects[0]).Y, precision: 6);
        Assert.Equal(15, PivotWorld(objects[1]).X, precision: 6);
        Assert.Equal(15, PivotWorld(objects[1]).Y, precision: 6);
        Assert.Equal(90, objects[0].Transform.RotationDeg, precision: 9);
        Assert.Equal(90, objects[1].Transform.RotationDeg, precision: 9);
        // Squares stay squares: the group's bounds are now 10 wide and 30 tall about the same centre.
        AssertBounds(GroupTransform.BoundsOf(objects), 10, -10, 20, 20);
    }

    [Fact]
    public void RotationComposesWithAnExistingRotationAndScale()
    {
        var obj = Left();
        obj.Transform = obj.Transform with { RotationDeg = 30, ScaleX = 2, ScaleY = 0.5 };
        var centre = new Position(15, 5, 0);
        var before = obj.Transform;

        var after = GroupTransform.RotateAbout(before, obj.LocalPivot, centre, 45);

        Assert.Equal(75, after.RotationDeg, precision: 9);
        Assert.Equal(2, after.ScaleX, precision: 12);
        Assert.Equal(0.5, after.ScaleY, precision: 12);

        // Rotating the world about the centre must equal rotating every local point the same way.
        var local = new Position(3, 8, 0);
        var was = before.Apply(local, obj.LocalPivot);
        var (rx, ry) = GroupTransform.RotateVector(was.X - centre.X, was.Y - centre.Y, 45);
        var now = after.Apply(local, obj.LocalPivot);
        Assert.Equal(centre.X + rx, now.X, precision: 6);
        Assert.Equal(centre.Y + ry, now.Y, precision: 6);
    }

    [Fact]
    public void UniformScaleIsExactForObjectsAtAnyRotation()
    {
        var obj = Square(0, 0, 10, 4, "R");
        obj.Transform = obj.Transform with { RotationDeg = 30, X = 7, Y = -2 };
        var original = obj.WorldBounds();
        var anchor = new Position(-5, 12, 0);

        obj.Transform = GroupTransform.ScaleAbout(obj.Transform, obj.LocalPivot, anchor, 2, 2);

        var scaled = obj.WorldBounds();
        Assert.Equal(anchor.X + (original.MinX - anchor.X) * 2, scaled.MinX, precision: 6);
        Assert.Equal(anchor.X + (original.MaxX - anchor.X) * 2, scaled.MaxX, precision: 6);
        Assert.Equal(anchor.Y + (original.MinY - anchor.Y) * 2, scaled.MinY, precision: 6);
        Assert.Equal(anchor.Y + (original.MaxY - anchor.Y) * 2, scaled.MaxY, precision: 6);
        Assert.Equal(30, obj.Transform.RotationDeg, precision: 9);
    }

    [Fact]
    public void NonUniformScaleOfAQuarterTurnedObjectIsExact()
    {
        // 10 x 4 rectangle turned 90 degrees: 4 wide, 10 tall in the world.
        var obj = Square(0, 0, 10, 4, "R");
        obj.Transform = obj.Transform with { RotationDeg = 90 };
        AssertBounds(obj.WorldBounds(), 3, -3, 7, 7);

        // Double the world width about the left edge (x = 3): the local Y axis carries the world X.
        obj.Transform = GroupTransform.ScaleAbout(obj.Transform, obj.LocalPivot, new Position(3, 0, 0), 2, 1);

        Assert.Equal(8, obj.WorldBounds().Width, precision: 6);
        Assert.Equal(10, obj.WorldBounds().Height, precision: 6);
        Assert.Equal(3, obj.WorldBounds().MinX, precision: 6);
    }

    [Fact]
    public void NoChangeReturnsTheSameTransformSoAClickRecordsNothing()
    {
        var obj = Left();
        obj.Transform = obj.Transform with { RotationDeg = 17, X = 1.1, Y = 2.2, ScaleX = 1.3 };
        var t = obj.Transform;

        Assert.Equal(t, GroupTransform.ScaleAbout(t, obj.LocalPivot, new Position(9, 9, 0), 1, 1));
        Assert.Equal(t, GroupTransform.RotateAbout(t, obj.LocalPivot, new Position(9, 9, 0), 0));
    }

    [Fact]
    public void ScaleNeverCollapsesAnObjectToZero()
    {
        var obj = Left();

        var t = GroupTransform.ScaleAbout(obj.Transform, obj.LocalPivot, new Position(0, 0, 0), 0, 0);

        Assert.True(Math.Abs(t.ScaleX) >= GroupTransform.MinScaleMagnitude);
        Assert.True(Math.Abs(t.ScaleY) >= GroupTransform.MinScaleMagnitude);
    }

    [Fact]
    public void OneCompositeCommandUndoesAndRedoesTheWholeGroupGesture()
    {
        var objects = new[] { Left(), Right() };
        var starts = objects.Select(o => o.Transform).ToArray();
        var box = GroupTransform.BoundsOf(objects);
        var solution = GroupTransform.ComputeScale(
            box, ResizeHandle.BottomRight, new Position(60, 30, 0), lockAspectRatio: false, allowFlip: true);

        // The live preview mutates the objects from their immutable start transforms.
        ApplyScale(objects, solution);
        var afters = objects.Select(o => o.Transform).ToArray();
        Assert.NotEqual(starts[0], afters[0]);

        var command = new CompositeSceneCommand(objects
            .Select((o, i) => (ISceneCommand)new TransformObjectCommand(o, starts[i], afters[i]))
            .ToList());

        command.Undo();
        Assert.Equal(starts[0], objects[0].Transform);
        Assert.Equal(starts[1], objects[1].Transform);
        AssertBounds(GroupTransform.BoundsOf(objects), 0, 0, 30, 10);

        command.Do();
        Assert.Equal(afters[0], objects[0].Transform);
        Assert.Equal(afters[1], objects[1].Transform);
    }

    [Fact]
    public void CancelledPreviewRestoresTheStartTransformsExactly()
    {
        var objects = new[] { Left(), Right() };
        var starts = objects.Select(o => o.Transform).ToArray();
        var box = GroupTransform.BoundsOf(objects);

        var solution = GroupTransform.ComputeScale(
            box, ResizeHandle.TopRight, new Position(80, -40, 0), lockAspectRatio: false, allowFlip: true);
        ApplyScale(objects, solution);
        // Deriving from the stored start (not the previewed state) is what makes cancel exact.
        for (var i = 0; i < objects.Length; i++) objects[i].Transform = starts[i];

        Assert.Equal(starts[0], objects[0].Transform);
        Assert.Equal(starts[1], objects[1].Transform);
    }
}
