using Lasero.Core.GCode;
using Lasero.App.ViewModels;
using Lasero.Core.Grbl;
using Lasero.Core.Scene;
using Xunit;

namespace Lasero.Tests;

public sealed class RectangleCurveFitTests
{
    /// <summary>Closed ring (no trailing duplicate) of a rectangle with per-corner radii (tl, tr, br, bl),
    /// y growing downward, the way the canvas stores it.</summary>
    private static List<Position> Ring(double w, double h, double tl, double tr, double br, double bl, int arcSegments = 6)
    {
        var pts = new List<Position>();
        void Arc(double cx, double cy, double r, double fromDeg)
        {
            if (r <= 0) { pts.Add(new Position(cx, cy, 0)); return; }
            for (var i = 0; i <= arcSegments; i++)
            {
                var a = (fromDeg + 90.0 * i / arcSegments) * Math.PI / 180;
                pts.Add(new Position(cx + r * Math.Cos(a), cy + r * Math.Sin(a), 0));
            }
        }
        // Clockwise on screen starting at the top-left corner: arcs sweep 180->270, 270->360, 0->90, 90->180.
        Arc(tl, tl, tl, 180);
        Arc(w - tr, tr, tr, 270);
        Arc(w - br, h - br, br, 0);
        Arc(bl, h - bl, bl, 90);
        // A sharp corner contributes its own point (cx,cy == corner because r == 0 => centre at (0,0)): fix up.
        return pts;
    }

    private static List<Position> Plain(double w, double h) =>
    [
        new(0, 0, 0), new(w, 0, 0), new(w, h, 0), new(0, h, 0),
    ];

    [Fact]
    public void PlainRectangleBecomesFourCornerNodes()
    {
        var points = Plain(40, 20);

        Assert.True(RectangleCurveFit.TryFit(points, points.Count, out var subpath));

        Assert.True(subpath.IsClosed);
        Assert.Equal(4, subpath.Nodes.Count);
        Assert.All(subpath.Nodes, node => Assert.False(node.HasAnyHandle));
    }

    [Fact]
    public void PlainRectangleWithExtraEdgePointsStillFourNodes()
    {
        var points = new List<Position>
        {
            new(0, 0, 0), new(20, 0, 0), new(40, 0, 0), new(40, 20, 0), new(0, 20, 0), new(0, 10, 0),
        };

        Assert.True(RectangleCurveFit.TryFit(points, points.Count, out var subpath));
        Assert.Equal(4, subpath.Nodes.Count);
    }

    [Fact]
    public void RoundedRectangleBecomesEightNodesWithKappaHandles()
    {
        const double r = 5;
        var points = Ring(40, 20, r, r, r, r);

        Assert.True(RectangleCurveFit.TryFit(points, points.Count, out var subpath));

        Assert.Equal(8, subpath.Nodes.Count);
        Assert.All(subpath.Nodes, node => Assert.True(node.HasAnyHandle));
        var flat = new VectorPath { Subpaths = [subpath] }.FlattenAll()[0];
        var bounds = BoundingBox2D.Empty;
        foreach (var p in flat) bounds = bounds.Include(p.X, p.Y);
        Assert.Equal(0, bounds.MinX, 6);
        Assert.Equal(40, bounds.MaxX, 6);
        Assert.Equal(0, bounds.MinY, 6);
        Assert.Equal(20, bounds.MaxY, 6);
        // Every flattened point of the Bezier corner sits within 0.05 mm of the ideal rounded outline.
        foreach (var p in flat)
            Assert.True(DistanceToRoundedRect(p, 40, 20, r) < 0.05, $"{p.X},{p.Y}");
    }

    [Fact]
    public void PerCornerRadiiAreKept()
    {
        var points = Ring(60, 30, 4, 8, 12, 2);

        Assert.True(RectangleCurveFit.TryFit(points, points.Count, out var subpath));
        Assert.Equal(8, subpath.Nodes.Count);
    }

    [Fact]
    public void SingleSharpCornerKeepsOneCornerNode()
    {
        // top-left sharp, the other three rounded: 1 + 3*2 nodes.
        var points = new List<Position> { new(0, 0, 0) };
        var rest = Ring(40, 20, 5, 5, 5, 5).Skip(7).ToList(); // drop the top-left arc
        // Rest starts at the first top-right arc point (x = 35, y = 0); the ring's last point is (0, 15).
        points.AddRange(rest);

        Assert.True(RectangleCurveFit.TryFit(points, points.Count, out var subpath));
        Assert.Equal(7, subpath.Nodes.Count);
    }

    [Fact]
    public void ChamferedRectangleIsRejected()
    {
        var points = new List<Position>
        {
            new(5, 0, 0), new(35, 0, 0), new(40, 5, 0), new(40, 15, 0), new(35, 20, 0), new(5, 20, 0), new(0, 15, 0), new(0, 5, 0),
        };

        Assert.False(RectangleCurveFit.TryFit(points, points.Count, out _));
    }

    [Fact]
    public void RotatedSquareIsRejected()
    {
        var points = new List<Position> { new(10, 0, 0), new(20, 10, 0), new(10, 20, 0), new(0, 10, 0) };

        Assert.False(RectangleCurveFit.TryFit(points, points.Count, out _));
    }

    [Fact]
    public void ArcPointsOffTheCircleAreRejected()
    {
        var points = Ring(40, 20, 5, 5, 5, 5);
        // Pull one arc point 0.5 mm off the circle.
        points[2] = points[2] with { X = points[2].X + 0.5, Y = points[2].Y + 0.5 };

        Assert.False(RectangleCurveFit.TryFit(points, points.Count, out _));
    }

    [Fact]
    public void ConvertingRoundedRectangleToCurvesGivesBezierNodes()
    {
        var viewModel = new SceneViewModel();
        var points = Ring(40, 20, 5, 5, 5, 5);
        points.Add(points[0]);
        var shape = new Lasero.Core.Import.ImportedShape
        {
            Points = points,
            IsClosed = true,
            LayerColor = new Lasero.Core.Layers.RgbColor(0, 0, 0),
            PreferredMode = Lasero.Core.Layers.LayerMode.Cut,
        };
        var obj = new SceneObject
        {
            Name = "Zaoblený",
            LocalShapes = [shape],
            LocalBounds = new BoundingBox2D(0, 0, 40, 20),
            LocalPivot = new Position(20, 10, 0),
        };
        viewModel.Objects.Add(obj);
        viewModel.SelectedObjects.Add(obj);

        var converted = Assert.IsType<SceneObject>(viewModel.ConvertSelectedObjectToCurves());

        Assert.Equal(8, Assert.Single(converted.VectorPath!.Subpaths).Nodes.Count);
        Assert.Equal(obj.Id, converted.Id);
        viewModel.UndoCommand.Execute(null);
        Assert.Same(obj, Assert.Single(viewModel.Objects));
    }

    private static double DistanceToRoundedRect(Position p, double w, double h, double r)
    {
        // Distance to the outline of a rounded rectangle (0,0)-(w,h) with uniform radius r.
        var qx = Math.Abs(p.X - w / 2) - (w / 2 - r);
        var qy = Math.Abs(p.Y - h / 2) - (h / 2 - r);
        var outside = Math.Sqrt(Math.Max(qx, 0) * Math.Max(qx, 0) + Math.Max(qy, 0) * Math.Max(qy, 0));
        var inside = Math.Min(Math.Max(qx, qy), 0);
        return Math.Abs(outside + inside - r);
    }
}
