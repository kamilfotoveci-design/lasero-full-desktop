using Lasero.App.ViewModels;
using Lasero.Core.Grbl;
using Lasero.Core.Layers;
using Lasero.Core.Scene;
using Xunit;

namespace Lasero.Tests;

/// <summary>Align and distribute use the drawn geometry (not a rotated frame), move a group as one
/// unit, and are one undo step each.</summary>
public class ArrangeAlignDistributeTests
{
    private const double K = 0.5522847498;

    private static SceneObject Circle(double cx, double cy, double r)
    {
        var k = r * K;
        return VectorPathSceneFactory.Create(new VectorPath
        {
            Subpaths =
            [
                new VectorSubpath
                {
                    IsClosed = true,
                    Nodes =
                    [
                        new(new Position(cx + r, cy, 0), new Position(cx + r, cy - k, 0), new Position(cx + r, cy + k, 0), VectorNodeType.Smooth),
                        new(new Position(cx, cy + r, 0), new Position(cx + k, cy + r, 0), new Position(cx - k, cy + r, 0), VectorNodeType.Smooth),
                        new(new Position(cx - r, cy, 0), new Position(cx - r, cy + k, 0), new Position(cx - r, cy - k, 0), VectorNodeType.Smooth),
                        new(new Position(cx, cy - r, 0), new Position(cx - k, cy - r, 0), new Position(cx + k, cy - r, 0), VectorNodeType.Smooth),
                    ],
                },
            ],
        }, RgbColor.Black, "kruh");
    }

    private static SceneObject Rect(double x0, double y0, double x1, double y1) =>
        VectorPathSceneFactory.Create(new VectorPath
        {
            Subpaths =
            [
                new VectorSubpath
                {
                    IsClosed = true,
                    Nodes =
                    [
                        VectorNode.CornerAt(new Position(x0, y0, 0)), VectorNode.CornerAt(new Position(x1, y0, 0)),
                        VectorNode.CornerAt(new Position(x1, y1, 0)), VectorNode.CornerAt(new Position(x0, y1, 0)),
                    ],
                },
            ],
        }, RgbColor.Black, "obdélník");

    private static SceneViewModel Scene(params SceneObject[] objects)
    {
        var scene = new SceneViewModel();
        foreach (var item in objects) scene.Objects.Add(item);
        scene.SelectedObjects.ReplaceWith(objects);
        return scene;
    }

    [Fact]
    public void AlignLeftUsesTheDrawnEdgeOfARotatedCircleNotItsFrame()
    {
        var circle = Circle(50, 50, 10);
        circle.Transform = circle.Transform with { RotationDeg = 45 };
        var rect = Rect(0, 0, 10, 10);
        var scene = Scene(rect, circle);

        scene.AlignLeftCommand.Execute(null);

        Assert.Equal(rect.GeometricWorldBounds().MinX, circle.GeometricWorldBounds().MinX, 2);
    }

    [Fact]
    public void AGroupAlignsAsOneUnitAndKeepsItsLayout()
    {
        var a = Rect(0, 0, 10, 10);
        var b = Rect(30, 0, 40, 10);
        var c = Rect(5, 50, 15, 60);
        var scene = Scene(a, b, c);
        scene.SelectedObjects.ReplaceWith([a, b]);
        scene.GroupSelectionCommand.Execute(null);

        scene.SelectedObjects.ReplaceWith([a, b]);
        Assert.False(scene.AlignLeftCommand.CanExecute(null)); // a lone group has nothing to align to

        scene.SelectedObjects.ReplaceWith([a, b, c]);
        scene.AlignRightCommand.Execute(null);

        // The group's right edge is 40; c moves there, and the group does not collapse.
        Assert.Equal(0, a.GeometricWorldBounds().MinX, 6);
        Assert.Equal(30, b.GeometricWorldBounds().MinX, 6);
        Assert.Equal(40, c.GeometricWorldBounds().MaxX, 6);

        scene.UndoCommand.Execute(null);
        Assert.Equal(15, c.GeometricWorldBounds().MaxX, 6);
    }

    [Fact]
    public void DistributeCentersSpacesCentresEvenlyBetweenTheOuterTwo()
    {
        var a = Rect(0, 0, 10, 10);    // centre 5
        var b = Rect(12, 0, 16, 10);   // centre 14
        var c = Rect(90, 0, 110, 10);  // centre 100
        var scene = Scene(a, b, c);

        Assert.True(scene.DistributeCentersHorizontallyCommand.CanExecute(null));
        scene.DistributeCentersHorizontallyCommand.Execute(null);

        var centre = b.GeometricWorldBounds();
        Assert.Equal(52.5, (centre.MinX + centre.MaxX) / 2, 6);
        Assert.Equal(0, a.GeometricWorldBounds().MinX, 6);
        Assert.Equal(90, c.GeometricWorldBounds().MinX, 6);

        scene.UndoCommand.Execute(null);
        Assert.Equal(12, b.GeometricWorldBounds().MinX, 6);
    }

    [Fact]
    public void DistributeSpacingMakesTheGapsEqual()
    {
        var a = Rect(0, 0, 10, 10);
        var b = Rect(11, 0, 31, 10);   // 20 wide
        var c = Rect(90, 0, 100, 10);
        var scene = Scene(a, b, c);

        scene.DistributeSpacingHorizontallyCommand.Execute(null);

        // Span 0..100, occupied 40, two gaps of 30.
        Assert.Equal(40, b.GeometricWorldBounds().MinX, 6);
    }

    [Fact]
    public void DistributeVerticallyMovesOnlyY()
    {
        var a = Rect(0, 0, 10, 10);
        var b = Rect(50, 12, 60, 22);
        var c = Rect(5, 100, 15, 110);
        var scene = Scene(a, b, c);

        scene.DistributeCentersVerticallyCommand.Execute(null);

        Assert.Equal(50, b.GeometricWorldBounds().MinX, 6);
        Assert.Equal(55, (b.GeometricWorldBounds().MinY + b.GeometricWorldBounds().MaxY) / 2, 6);
    }

    [Fact]
    public void DistributeNeedsThreeUnits()
    {
        var a = Rect(0, 0, 10, 10);
        var b = Rect(20, 0, 30, 10);
        var scene = Scene(a, b);

        Assert.False(scene.DistributeCentersHorizontallyCommand.CanExecute(null));
        Assert.False(scene.DistributeSpacingVerticallyCommand.CanExecute(null));
    }
}
