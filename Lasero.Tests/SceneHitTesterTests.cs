using Lasero.App.Controls;
using Lasero.Core.GCode;
using Lasero.Core.Grbl;
using Lasero.Core.Import;
using Lasero.Core.Layers;
using Lasero.Core.Scene;

namespace Lasero.Tests;

public class SceneHitTesterTests
{
    [Fact]
    public void EmptyInteriorOfStrokeOnlyContourDoesNotHit()
    {
        var outline = MakeObject(MakeSquare(0, 0, 100, 100));

        var hits = SceneHitTester.HitTest([outline], new Position(50, 50, 0), 4, (_, _) => false);

        Assert.Empty(hits);
    }

    [Fact]
    public void SelectedStrokeOnlyObjectCanBeGrabbedInsideItsTransformBounds()
    {
        var outline = MakeObject(MakeSquare(0, 0, 100, 100));

        var canBeginMove = SceneHitTester.IsInsideSelectionBounds(
            [outline],
            new Position(50, 50, 0));

        Assert.True(canBeginMove);
    }

    [Fact]
    public void SingleSelectionBoundsFollowObjectRotationInsteadOfAxisAlignedWorldBounds()
    {
        var outline = MakeObject(MakeSquare(0, 0, 100, 20));
        outline.Transform = new ObjectTransform(0, 0, 45, 1, 1);

        Assert.True(SceneHitTester.IsInsideSelectionBounds(
            [outline],
            outline.Transform.Apply(new Position(50, 10, 0), outline.LocalPivot)));
        Assert.False(SceneHitTester.IsInsideSelectionBounds(
            [outline],
            new Position(10, 10, 0)));
    }

    [Fact]
    public void VisibleInnerStrokeWinsOverOuterBoundingBox()
    {
        var inner = MakeObject(MakeSquare(40, 40, 60, 60));
        var outer = MakeObject(MakeSquare(0, 0, 100, 100));

        var hit = SceneHitTester.HitTest([inner, outer], new Position(40, 50, 0), 4, (_, _) => false).First();

        Assert.Same(inner, hit.Object);
        Assert.Equal(SceneHitKind.Stroke, hit.Kind);
    }

    [Theory]
    [InlineData(0.25)]
    [InlineData(0.5)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(5)]
    public void StrokeToleranceRemainsSixScreenPixelsAtEveryZoom(double pixelsPerMillimeter)
    {
        var line = MakeObject(new ImportedShape
        {
            Points = [new Position(0, 0, 0), new Position(100, 0, 0)],
            IsClosed = false,
            LayerColor = RgbColor.Black,
            PreferredMode = LayerMode.Cut,
        });
        var insideToleranceMm = 5.5 / pixelsPerMillimeter;
        var outsideToleranceMm = 6.5 / pixelsPerMillimeter;

        Assert.Single(SceneHitTester.HitTest([line], new Position(50, insideToleranceMm, 0), pixelsPerMillimeter, (_, _) => false));
        Assert.Empty(SceneHitTester.HitTest([line], new Position(50, outsideToleranceMm, 0), pixelsPerMillimeter, (_, _) => false));
    }

    [Fact]
    public void FilledContourCanBeSelectedFromItsInterior()
    {
        var filled = MakeObject(MakeSquare(0, 0, 100, 100));

        var hit = Assert.Single(SceneHitTester.HitTest([filled], new Position(50, 50, 0), 4, (_, _) => true));

        Assert.Equal(SceneHitKind.Fill, hit.Kind);
    }

    private static SceneObject MakeObject(params ImportedShape[] shapes)
    {
        var bounds = BoundingBox2D.Empty;
        foreach (var point in shapes.SelectMany(shape => shape.Points))
            bounds = bounds.Include(point.X, point.Y);

        return new SceneObject
        {
            Name = "Test",
            LocalShapes = shapes,
            LocalBounds = bounds,
            LocalPivot = new Position((bounds.MinX + bounds.MaxX) / 2, (bounds.MinY + bounds.MaxY) / 2, 0),
        };
    }

    private static ImportedShape MakeSquare(double minX, double minY, double maxX, double maxY) => new()
    {
        Points =
        [
            new Position(minX, minY, 0),
            new Position(maxX, minY, 0),
            new Position(maxX, maxY, 0),
            new Position(minX, maxY, 0),
            new Position(minX, minY, 0),
        ],
        IsClosed = true,
        LayerColor = RgbColor.Black,
        PreferredMode = LayerMode.Cut,
    };
}
