using Lasero.App.ViewModels;
using Lasero.Core.Grbl;
using Lasero.Core.Layers;
using Lasero.Core.Scene;
using Xunit;

namespace Lasero.Tests;

/// <summary>Flip mirrors about the screen axis through the selection's centre and the 90° turns go the
/// way their names say on a Y-up canvas; a multi-selection or a group turns and mirrors as one piece.</summary>
public class SelectionFlipRotateTests
{
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
        }, RgbColor.Black, "r");

    private static SceneViewModel Scene(params SceneObject[] objects)
    {
        var scene = new SceneViewModel();
        foreach (var item in objects) scene.Objects.Add(item);
        scene.SelectedObjects.ReplaceWith(objects);
        return scene;
    }

    private static List<Position> Corners(SceneObject item) => item.GetWorldShapes()[0].Points.ToList();

    private static double WorstDistance(IEnumerable<Position> expected, IReadOnlyList<Position> actual) =>
        expected.Max(e => actual.Min(a => Math.Sqrt((a.X - e.X) * (a.X - e.X) + (a.Y - e.Y) * (a.Y - e.Y))));

    [Fact]
    public void FlipHorizontalOfARotatedObjectIsAWorldMirrorThatStaysInPlace()
    {
        var item = Rect(0, 0, 40, 10);
        item.Transform = item.Transform with { RotationDeg = 30 };
        var scene = Scene(item);
        var before = Corners(item);
        var cx = (before.Min(p => p.X) + before.Max(p => p.X)) / 2;

        scene.FlipHorizontalCommand.Execute(null);

        var mirrored = before.Select(p => new Position(2 * cx - p.X, p.Y, 0));
        Assert.True(WorstDistance(mirrored, Corners(item)) < 1e-6);
        Assert.Equal(-30, item.Transform.RotationDeg, 6);

        scene.UndoCommand.Execute(null);
        Assert.True(WorstDistance(before, Corners(item)) < 1e-9);
    }

    [Fact]
    public void FlipVerticalOfARotatedObjectMirrorsAboutTheHorizontalAxis()
    {
        var item = Rect(0, 0, 40, 10);
        item.Transform = item.Transform with { RotationDeg = 30 };
        var scene = Scene(item);
        var before = Corners(item);
        var cy = (before.Min(p => p.Y) + before.Max(p => p.Y)) / 2;

        scene.FlipVerticalCommand.Execute(null);

        Assert.True(WorstDistance(before.Select(p => new Position(p.X, 2 * cy - p.Y, 0)), Corners(item)) < 1e-6);
    }

    [Fact]
    public void FlipKeepsAnObjectWhoseDrawingIsOffItsPivotInPlace()
    {
        var item = Rect(0, 0, 20, 10);
        item.LocalPivot = new Position(0, 0, 0);
        var scene = Scene(item);
        var before = item.GeometricWorldBounds();

        scene.FlipHorizontalCommand.Execute(null);

        var after = item.GeometricWorldBounds();
        Assert.Equal(before.MinX, after.MinX, 6);
        Assert.Equal(before.MaxX, after.MaxX, 6);
    }

    [Fact]
    public void RotateLeftTurnsCounterClockwiseOnTheYUpCanvasAndRightClockwise()
    {
        var item = Rect(0, -1, 40, 1); // a bar along +X, pivot at (20, 0)
        var scene = Scene(item);

        scene.RotateLeftCommand.Execute(null);
        var tip = item.Transform.Apply(new Position(40, 0, 0), item.LocalPivot);
        Assert.True(tip.Y > 19, "the right-hand tip turns up: counter-clockwise");

        scene.RotateRightCommand.Execute(null);
        scene.RotateRightCommand.Execute(null);
        tip = item.Transform.Apply(new Position(40, 0, 0), item.LocalPivot);
        Assert.True(tip.Y < -19, "two right turns from up point it down");
    }

    [Fact]
    public void AMultiSelectionMirrorsAboutItsCommonCentreAsOneUndoStep()
    {
        var left = Rect(0, 0, 10, 10);
        var right = Rect(50, 0, 60, 20);
        var scene = Scene(left, right);

        Assert.True(scene.FlipHorizontalCommand.CanExecute(null));
        scene.FlipHorizontalCommand.Execute(null);

        // Common box x:[0,60]; mirrored about x = 30 the two swap sides.
        Assert.Equal(50, left.GeometricWorldBounds().MinX, 6);
        Assert.Equal(0, right.GeometricWorldBounds().MinX, 6);

        scene.UndoCommand.Execute(null);
        Assert.Equal(0, left.GeometricWorldBounds().MinX, 6);
        Assert.Equal(50, right.GeometricWorldBounds().MinX, 6);
    }

    [Fact]
    public void AGroupRotatesAsOnePieceAboutItsCentre()
    {
        var a = Rect(0, 0, 10, 10);
        var b = Rect(30, 0, 40, 10);
        var scene = Scene(a, b);
        scene.GroupSelectionCommand.Execute(null);
        scene.SelectedObjects.ReplaceWith([a, b]);

        Assert.True(scene.RotateLeftCommand.CanExecute(null));
        scene.RotateLeftCommand.Execute(null);

        // Group box x:[0,40] y:[0,10], centre (20,5); a quarter turn left stacks them vertically.
        Assert.Equal(15, a.GeometricWorldBounds().MinX, 6);
        Assert.Equal(-15, a.GeometricWorldBounds().MinY, 6);
        Assert.Equal(15, b.GeometricWorldBounds().MinX, 6);
        Assert.Equal(15, b.GeometricWorldBounds().MinY, 6);
        Assert.Equal(a.GroupId, b.GroupId);

        scene.UndoCommand.Execute(null);
        Assert.Equal(0, a.GeometricWorldBounds().MinX, 6);
        Assert.Equal(30, b.GeometricWorldBounds().MinX, 6);
    }

    [Fact]
    public void ARasterInTheSelectionDisablesFlipAndRotate()
    {
        var vector = Rect(0, 0, 10, 10);
        var raster = new SceneObject
        {
            LocalShapes = vector.LocalShapes,
            LocalPivot = vector.LocalPivot,
            LocalBounds = vector.LocalBounds,
            RasterFilePath = "photo.png",
        };
        var scene = Scene(vector, raster);

        Assert.False(scene.FlipHorizontalCommand.CanExecute(null));
        Assert.False(scene.RotateLeftCommand.CanExecute(null));
    }
}
