using Lasero.App.ViewModels;
using Lasero.Core.Grbl;
using Lasero.Core.Layers;
using Lasero.Core.Scene;
using Xunit;

namespace Lasero.Tests;

/// <summary>Kombinovat (Inkscape Combine) makes one path out of several objects without merging any
/// geometry; Rozdělit cestu (Inkscape Split Path) separates shapes but keeps every hole with its outline.</summary>
public class CombineSplitPathTests
{
    private const double K = 0.5522847498;

    private static VectorSubpath Rect(double x0, double y0, double x1, double y1, bool ccw = true)
    {
        var nodes = new List<VectorNode>
        {
            VectorNode.CornerAt(new Position(x0, y0, 0)), VectorNode.CornerAt(new Position(x1, y0, 0)),
            VectorNode.CornerAt(new Position(x1, y1, 0)), VectorNode.CornerAt(new Position(x0, y1, 0)),
        };
        if (!ccw) nodes.Reverse();
        return new VectorSubpath { Nodes = nodes, IsClosed = true };
    }

    private static VectorSubpath Circle(double cx, double cy, double r)
    {
        var k = r * K;
        return new VectorSubpath
        {
            IsClosed = true,
            Nodes =
            [
                new(new Position(cx + r, cy, 0), new Position(cx + r, cy - k, 0), new Position(cx + r, cy + k, 0), VectorNodeType.Smooth),
                new(new Position(cx, cy + r, 0), new Position(cx + k, cy + r, 0), new Position(cx - k, cy + r, 0), VectorNodeType.Smooth),
                new(new Position(cx - r, cy, 0), new Position(cx - r, cy + k, 0), new Position(cx - r, cy - k, 0), VectorNodeType.Smooth),
                new(new Position(cx, cy - r, 0), new Position(cx - k, cy - r, 0), new Position(cx + k, cy - r, 0), VectorNodeType.Smooth),
            ],
        };
    }

    private static SceneObject Obj(params VectorSubpath[] subpaths) =>
        VectorPathSceneFactory.Create(new VectorPath { Subpaths = subpaths }, RgbColor.Black, "p");

    private static SceneViewModel Scene(params SceneObject[] objects)
    {
        var scene = new SceneViewModel();
        foreach (var item in objects) scene.Objects.Add(item);
        scene.SelectedObjects.ReplaceWith(objects);
        return scene;
    }

    [Fact]
    public void CombineMakesOneObjectThatKeepsCurvesLayersAndTheStackingPosition()
    {
        var engrave = LayerSettings.CreateDefault(new RgbColor(10, 120, 200), LayerMode.Fill, "Gravír");
        var circle = Obj(Circle(0, 0, 10));
        var square = Obj(Rect(30, 0, 40, 10));
        var middle = Obj(Rect(100, 0, 110, 10));
        var front = Obj(Rect(200, 0, 210, 10));
        var scene = Scene(circle, middle, square, front);
        scene.Layers.Add(engrave);
        square.AssignToLayer(engrave);
        scene.SelectedObjects.ReplaceWith([circle, square]);

        Assert.True(scene.CombinePathsCommand.CanExecute(null));
        scene.CombinePathsCommand.Execute(null);

        var combined = Assert.Single(scene.SelectedObjects);
        Assert.Equal(3, scene.Objects.Count);
        Assert.Equal(2, combined.VectorPath!.Subpaths.Count);
        Assert.Equal(2, combined.LocalShapes.Count);
        Assert.True(combined.VectorPath.Subpaths[0].Nodes.All(node => node.HasAnyHandle), "the circle keeps its Bézier nodes");
        Assert.Equal(engrave.Id, combined.LocalShapes[1].LayerId);
        Assert.NotEqual(engrave.Id, combined.LocalShapes[0].LayerId);
        Assert.Single(combined.LocalShapes.Select(shape => shape.GeometrySetId).Distinct());
        // Where the topmost source (the square) was: above the middle object, below the front one.
        Assert.Equal([middle, combined, front], scene.Objects);

        scene.UndoCommand.Execute(null);
        Assert.Equal([circle, middle, square, front], scene.Objects);
        scene.RedoCommand.Execute(null);
        Assert.Equal([middle, combined, front], scene.Objects);
    }

    [Fact]
    public void CombiningAnOuterShapeWithAnOppositelyWoundInnerOneMakesAHole()
    {
        var outer = Obj(Rect(0, 0, 40, 40));
        var inner = Obj(Rect(10, 10, 30, 30, ccw: false));
        var scene = Scene(outer, inner);

        scene.CombinePathsCommand.Execute(null);

        var combined = Assert.Single(scene.Objects);
        var areas = combined.GetWorldShapes().Select(shape => SignedArea(shape.Points)).ToList();
        Assert.Equal(1600 - 400, Math.Abs(areas.Sum()), 3);
    }

    [Fact]
    public void ADrawnEllipseIsCombinedAsCurves()
    {
        var scene = new SceneViewModel();
        scene.DrawPrimitive(DesignerTool.Ellipse, new Position(0, 0, 0), new Position(20, 10, 0));
        scene.DrawPrimitive(DesignerTool.Rectangle, new Position(30, 0, 0), new Position(40, 10, 0));
        scene.SelectedObjects.ReplaceWith(scene.Objects.ToList());

        scene.CombinePathsCommand.Execute(null);

        var combined = Assert.Single(scene.Objects);
        Assert.Equal(4, combined.VectorPath!.Subpaths[0].Nodes.Count);
        Assert.True(combined.VectorPath.Subpaths[0].Nodes.All(node => node.HasAnyHandle));
    }

    [Fact]
    public void SplitPathKeepsTheHoleWithItsOutlineAndSeparatesTheRest()
    {
        var item = Obj(Rect(0, 0, 40, 40), Rect(10, 10, 30, 30, ccw: false), Circle(100, 20, 10));
        var scene = Scene(item);

        scene.SplitPathCommand.Execute(null);

        Assert.Equal(2, scene.Objects.Count);
        var ring = Assert.Single(scene.Objects, part => part.LocalShapes.Count == 2);
        var disc = Assert.Single(scene.Objects, part => part.LocalShapes.Count == 1);
        Assert.Single(ring.LocalShapes.Select(shape => shape.GeometrySetId).Distinct());
        Assert.True(disc.VectorPath!.Subpaths[0].Nodes.All(node => node.HasAnyHandle));

        scene.UndoCommand.Execute(null);
        Assert.Same(item, Assert.Single(scene.Objects));
    }

    [Fact]
    public void SplitPathOfASingleRingChangesNothingAndSaysWhy()
    {
        var item = Obj(Rect(0, 0, 40, 40), Rect(10, 10, 30, 30, ccw: false));
        var scene = Scene(item);
        string? message = null;
        scene.VectorOperationRejected += text => message = text;

        scene.SplitPathCommand.Execute(null);

        Assert.Same(item, Assert.Single(scene.Objects));
        Assert.False(scene.CanUndo);
        Assert.NotNull(message);
    }

    [Fact]
    public void BreakApartStillSeparatesEveryContour()
    {
        var item = Obj(Rect(0, 0, 40, 40), Rect(10, 10, 30, 30, ccw: false));
        var scene = Scene(item);

        scene.BreakApartSelectionCommand.Execute(null);

        Assert.Equal(2, scene.Objects.Count);
    }

    [Fact]
    public void ContourPiecesNestAnIslandInsideAHoleAsAPieceOfItsOwn()
    {
        static Lasero.Core.Import.ImportedShape Ring(double x0, double x1) => new()
        {
            Points = [new Position(x0, x0, 0), new Position(x1, x0, 0), new Position(x1, x1, 0), new Position(x0, x1, 0)],
            IsClosed = true,
            LayerColor = RgbColor.Black,
            PreferredMode = LayerMode.Cut,
        };

        var pieces = SceneViewModel.ContourPieces([Ring(0, 100), Ring(10, 90), Ring(20, 80), Ring(30, 70)]);

        Assert.Equal(2, pieces.Count);
        Assert.Equal(new[] { 0, 1 }, pieces[0]);
        Assert.Equal(new[] { 2, 3 }, pieces[1]);
    }

    private static double SignedArea(IReadOnlyList<Position> ring)
    {
        double sum = 0;
        for (var i = 0; i < ring.Count; i++)
        {
            var a = ring[i];
            var b = ring[(i + 1) % ring.Count];
            sum += a.X * b.Y - b.X * a.Y;
        }
        return sum / 2;
    }
}
