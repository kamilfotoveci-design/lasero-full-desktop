using Lasero.Core.Grbl;
using Lasero.Core.Layers;
using Lasero.Core.Scene;

namespace Lasero.Tests;

/// <summary>
/// Pure-logic coverage for the new curve-preserving VectorPath model: De Casteljau flattening and
/// subdivision, node add/delete/convert, path closing, and the VectorPath→ImportedShape.Points
/// flattening boundary. None of this needs a live app or WPF — see VectorPath.cs for the design.
/// </summary>
public sealed class VectorPathTests
{
    private static readonly RgbColor Color = new(18, 18, 18);

    // ---------------------------------------------------------------------------------------
    // CubicBezier: evaluate / split / flatten
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void EvaluateAtZeroAndOneReturnsEndpoints()
    {
        var p0 = new Position(0, 0, 0);
        var c1 = new Position(0, 10, 0);
        var c2 = new Position(10, 10, 0);
        var p1 = new Position(10, 0, 0);

        Assert.Equal(p0, CubicBezier.Evaluate(p0, c1, c2, p1, 0));
        Assert.Equal(p1, CubicBezier.Evaluate(p0, c1, c2, p1, 1));
    }

    [Fact]
    public void SplitReproducesSameCurveAtMidpoint()
    {
        var p0 = new Position(0, 0, 0);
        var c1 = new Position(0, 10, 0);
        var c2 = new Position(10, 10, 0);
        var p1 = new Position(10, 0, 0);

        var expectedMid = CubicBezier.Evaluate(p0, c1, c2, p1, 0.5);
        var split = CubicBezier.Split(p0, c1, c2, p1, 0.5);

        Assert.Equal(expectedMid, split.P1);
        Assert.Equal(split.P1, split.Q0);
        Assert.Equal(p0, split.P0);
        Assert.Equal(p1, split.Q1);
    }

    [Fact]
    public void SplitAtArbitraryTMatchesDirectEvaluationAlongBothHalves()
    {
        var p0 = new Position(0, 0, 0);
        var c1 = new Position(3, 12, 0);
        var c2 = new Position(9, -4, 0);
        var p1 = new Position(14, 6, 0);
        const double t = 0.3;

        var split = CubicBezier.Split(p0, c1, c2, p1, t);

        // A point 40% along the LEFT sub-curve corresponds to parameter t*0.4 on the original.
        var expected = CubicBezier.Evaluate(p0, c1, c2, p1, t * 0.4);
        var actual = CubicBezier.Evaluate(split.P0, split.C1, split.C2, split.P1, 0.4);
        Assert.True(Distance(expected, actual) < 1e-9);
    }

    [Fact]
    public void FlattenStraightLineProducesTwoPoints()
    {
        var p0 = new Position(0, 0, 0);
        var p1 = new Position(10, 0, 0);
        var output = new List<Position> { p0 };
        CubicBezier.Flatten(p0, p0, p1, p1, 0.05, output);

        // A "curve" whose control points sit exactly on the endpoints is flat immediately.
        Assert.Equal(2, output.Count);
        Assert.Equal(p1, output[^1]);
    }

    [Fact]
    public void FlattenCurvedSegmentStaysWithinToleranceOfTrueCurve()
    {
        var p0 = new Position(0, 0, 0);
        var c1 = new Position(0, 20, 0);
        var c2 = new Position(20, 20, 0);
        var p1 = new Position(20, 0, 0);
        const double tolerance = 0.05;

        var output = new List<Position> { p0 };
        CubicBezier.Flatten(p0, c1, c2, p1, tolerance, output);

        Assert.True(output.Count > 2, "A curved segment should subdivide into more than its two endpoints.");
        Assert.Equal(p1, output[^1]);

        // Every flattened vertex must itself lie on (or extremely close to) the true curve — sample
        // densely and confirm each polyline vertex is near some point on the analytic curve.
        foreach (var vertex in output)
        {
            var closest = ClosestDistanceOnCurve(p0, c1, c2, p1, vertex, samples: 500);
            Assert.True(closest < tolerance + 1e-6, $"Vertex {vertex} strayed {closest}mm from the true curve.");
        }
    }

    [Fact]
    public void TighterToleranceProducesMorePoints()
    {
        var p0 = new Position(0, 0, 0);
        var c1 = new Position(0, 20, 0);
        var c2 = new Position(20, 20, 0);
        var p1 = new Position(20, 0, 0);

        var loose = new List<Position> { p0 };
        CubicBezier.Flatten(p0, c1, c2, p1, 0.5, loose);
        var tight = new List<Position> { p0 };
        CubicBezier.Flatten(p0, c1, c2, p1, 0.01, tight);

        Assert.True(tight.Count >= loose.Count);
    }

    // ---------------------------------------------------------------------------------------
    // VectorSubpath.Flatten — straight vs curved segments, open vs closed
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void SubpathOfAllCornerNodesFlattensToExactPolyline()
    {
        var subpath = new VectorSubpath
        {
            Nodes =
            [
                VectorNode.CornerAt(new Position(0, 0, 0)),
                VectorNode.CornerAt(new Position(10, 0, 0)),
                VectorNode.CornerAt(new Position(10, 10, 0)),
            ],
            IsClosed = false,
        };

        var points = subpath.Flatten();
        Assert.Equal(3, points.Count);
        Assert.Equal(new Position(0, 0, 0), points[0]);
        Assert.Equal(new Position(10, 0, 0), points[1]);
        Assert.Equal(new Position(10, 10, 0), points[2]);
    }

    [Fact]
    public void SubpathWithSmoothNodeFlattensCurvedSegment()
    {
        var a = new VectorNode(new Position(0, 0, 0), null, new Position(0, 10, 0), VectorNodeType.Smooth);
        var b = new VectorNode(new Position(20, 0, 0), new Position(20, 10, 0), null, VectorNodeType.Smooth);
        var subpath = new VectorSubpath { Nodes = [a, b], IsClosed = false };

        var points = subpath.Flatten();
        Assert.True(points.Count > 2);
        Assert.Equal(a.Anchor, points[0]);
        Assert.Equal(b.Anchor, points[^1]);
    }

    [Fact]
    public void ClosedSubpathFlattenClosesTheLoopBackToTheFirstNode()
    {
        var subpath = new VectorSubpath
        {
            Nodes =
            [
                VectorNode.CornerAt(new Position(0, 0, 0)),
                VectorNode.CornerAt(new Position(10, 0, 0)),
                VectorNode.CornerAt(new Position(10, 10, 0)),
            ],
            IsClosed = true,
        };

        var points = subpath.Flatten();
        // 3 corner nodes closed = 3 straight segments, the last one wrapping back to node 0, so the
        // polyline is 4 points long with the first point repeated at the end (closed-shape convention).
        Assert.Equal(4, points.Count);
        Assert.Equal(points[0], points[^1]);
    }

    [Fact]
    public void VectorPathFlattenAllRepeatsFirstPointForClosedSubpaths()
    {
        var path = VectorPath.SingleOpen(
        [
            VectorNode.CornerAt(new Position(0, 0, 0)),
            VectorNode.CornerAt(new Position(10, 0, 0)),
            VectorNode.CornerAt(new Position(10, 10, 0)),
        ]);
        path = path.ReplaceSubpath(0, path.Subpaths[0] with { IsClosed = true });

        var flattened = path.FlattenAll();
        var points = flattened[0];
        Assert.Equal(points[0], points[^1]);
    }

    // ---------------------------------------------------------------------------------------
    // VectorPathEditor: append / close / delete / insert / convert / move handle
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void AppendNodePlainClickCreatesCornerNode()
    {
        var subpath = VectorSubpath.Empty;
        subpath = VectorPathEditor.AppendNode(subpath, new Position(5, 5, 0), dragOffset: null);

        Assert.Single(subpath.Nodes);
        Assert.Equal(VectorNodeType.Corner, subpath.Nodes[0].Type);
        Assert.Null(subpath.Nodes[0].HandleIn);
        Assert.Null(subpath.Nodes[0].HandleOut);
    }

    [Fact]
    public void AppendNodeWithDragCreatesSymmetricSmoothHandles()
    {
        var subpath = VectorSubpath.Empty;
        var anchor = new Position(5, 5, 0);
        var drag = new Position(2, 3, 0);
        subpath = VectorPathEditor.AppendNode(subpath, anchor, drag);

        var node = subpath.Nodes[0];
        Assert.Equal(VectorNodeType.Smooth, node.Type);
        Assert.Equal(new Position(7, 8, 0), node.HandleOut);
        Assert.Equal(new Position(3, 2, 0), node.HandleIn);
    }

    [Fact]
    public void CloseConnectsLastNodeBackToFirstWithoutDuplicatingIt()
    {
        var subpath = new VectorSubpath
        {
            Nodes =
            [
                VectorNode.CornerAt(new Position(0, 0, 0)),
                VectorNode.CornerAt(new Position(10, 0, 0)),
                VectorNode.CornerAt(new Position(10, 10, 0)),
            ],
            IsClosed = false,
        };

        var closed = VectorPathEditor.Close(subpath);
        Assert.True(closed.IsClosed);
        Assert.Equal(3, closed.Nodes.Count);
        Assert.Equal(3, closed.SegmentCount);
    }

    [Fact]
    public void OpenClearsIsClosedWithoutTouchingNodesOrHandles()
    {
        var subpath = new VectorSubpath
        {
            Nodes =
            [
                new VectorNode(new Position(0, 0, 0), null, new Position(2, 2, 0), VectorNodeType.Smooth),
                VectorNode.CornerAt(new Position(10, 0, 0)),
                VectorNode.CornerAt(new Position(10, 10, 0)),
            ],
            IsClosed = true,
        };

        var opened = VectorPathEditor.Open(subpath);

        Assert.False(opened.IsClosed);
        Assert.Equal(subpath.Nodes, opened.Nodes);
    }

    [Fact]
    public void RemoveNodeReconnectsNeighboursDirectlyForStraightSegments()
    {
        var subpath = new VectorSubpath
        {
            Nodes =
            [
                VectorNode.CornerAt(new Position(0, 0, 0)),
                VectorNode.CornerAt(new Position(5, 5, 0)),
                VectorNode.CornerAt(new Position(10, 0, 0)),
            ],
            IsClosed = false,
        };

        var result = VectorPathEditor.RemoveNode(subpath, 1);
        Assert.Equal(2, result.Nodes.Count);
        Assert.Equal(new Position(0, 0, 0), result.Nodes[0].Anchor);
        Assert.Equal(new Position(10, 0, 0), result.Nodes[1].Anchor);
        // A-C reconnects straight since neither surviving node has a handle facing the other.
        Assert.True(VectorSubpath.IsStraightSegment(result.Nodes[0], result.Nodes[1]));
    }

    [Fact]
    public void RemoveNodeRefusesToDropBelowMinimumNodeCount()
    {
        var openTwoNode = new VectorSubpath
        {
            Nodes = [VectorNode.CornerAt(new Position(0, 0, 0)), VectorNode.CornerAt(new Position(10, 0, 0))],
            IsClosed = false,
        };

        var result = VectorPathEditor.RemoveNode(openTwoNode, 0);
        Assert.Equal(2, result.Nodes.Count); // unchanged — refused
    }

    [Fact]
    public void InsertNodeOnStraightSegmentIsLinearInterpolation()
    {
        var subpath = new VectorSubpath
        {
            Nodes = [VectorNode.CornerAt(new Position(0, 0, 0)), VectorNode.CornerAt(new Position(10, 0, 0))],
            IsClosed = false,
        };

        var result = VectorPathEditor.InsertNode(subpath, 0, 0.5);
        Assert.Equal(3, result.Nodes.Count);
        Assert.Equal(new Position(5, 0, 0), result.Nodes[1].Anchor);
        Assert.Equal(VectorNodeType.Corner, result.Nodes[1].Type);
    }

    [Fact]
    public void InsertNodeOnCurvedSegmentPreservesExactCurveShape()
    {
        var a = new VectorNode(new Position(0, 0, 0), null, new Position(0, 10, 0), VectorNodeType.Smooth);
        var b = new VectorNode(new Position(20, 0, 0), new Position(20, 10, 0), null, VectorNodeType.Smooth);
        var subpath = new VectorSubpath { Nodes = [a, b], IsClosed = false };
        const double splitT = 0.4;

        var afterInsert = VectorPathEditor.InsertNode(subpath, 0, splitT);
        Assert.Equal(3, afterInsert.Nodes.Count);

        var newA = afterInsert.Nodes[0];
        var middle = afterInsert.Nodes[1];
        var newB = afterInsert.Nodes[2];

        // Directly re-evaluate the ORIGINAL analytic curve and the two NEW analytic segments at exact
        // parameters (not flattened polylines, which sample the two curves at different vertex
        // densities and so cannot be compared point-for-point) — this pins the mapping exactly rather
        // than approximately.
        for (var i = 0; i <= 20; i++)
        {
            var tLeft = (double)i / 20; // 0..1 across the left (0..splitT of the original) sub-curve
            var expected = CubicBezier.Evaluate(a.Anchor, a.HandleOut!.Value, b.HandleIn!.Value, b.Anchor, tLeft * splitT);
            var actual = CubicBezier.Evaluate(newA.Anchor, newA.HandleOut!.Value, middle.HandleIn!.Value, middle.Anchor, tLeft);
            Assert.True(Distance(expected, actual) < 1e-9, $"Left sub-curve diverged at t={tLeft}: expected {expected}, got {actual}.");
        }

        for (var i = 0; i <= 20; i++)
        {
            var tRight = (double)i / 20; // 0..1 across the right (splitT..1 of the original) sub-curve
            var originalT = splitT + tRight * (1 - splitT);
            var expected = CubicBezier.Evaluate(a.Anchor, a.HandleOut!.Value, b.HandleIn!.Value, b.Anchor, originalT);
            var actual = CubicBezier.Evaluate(middle.Anchor, middle.HandleOut!.Value, newB.HandleIn!.Value, newB.Anchor, tRight);
            Assert.True(Distance(expected, actual) < 1e-9, $"Right sub-curve diverged at t={tRight}: expected {expected}, got {actual}.");
        }
    }

    [Fact]
    public void InsertNodeRejectsOutOfRangeParameter()
    {
        var subpath = new VectorSubpath
        {
            Nodes = [VectorNode.CornerAt(new Position(0, 0, 0)), VectorNode.CornerAt(new Position(10, 0, 0))],
            IsClosed = false,
        };
        Assert.Throws<ArgumentOutOfRangeException>(() => VectorPathEditor.InsertNode(subpath, 0, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => VectorPathEditor.InsertNode(subpath, 0, 1));
    }

    [Fact]
    public void ConvertToSmoothMakesHandlesCollinearThroughAnchor()
    {
        var node = new VectorNode(
            new Position(0, 0, 0),
            new Position(-3, 1, 0),
            new Position(4, -2, 0),
            VectorNodeType.Corner);

        var smooth = VectorPathEditor.ConvertNodeType(node, VectorNodeType.Smooth);

        Assert.Equal(VectorNodeType.Smooth, smooth.Type);
        var handleIn = smooth.HandleIn!.Value;
        var handleOut = smooth.HandleOut!.Value;
        // Collinear through the anchor: cross product of (anchor->in) and (anchor->out) is ~0.
        var cross = (handleIn.X - node.Anchor.X) * (handleOut.Y - node.Anchor.Y) -
                    (handleIn.Y - node.Anchor.Y) * (handleOut.X - node.Anchor.X);
        Assert.True(Math.Abs(cross) < 1e-9);
    }

    [Fact]
    public void ConvertToCornerLeavesHandlePositionsUnchanged()
    {
        var node = new VectorNode(
            new Position(0, 0, 0),
            new Position(-3, 1, 0),
            new Position(4, -2, 0),
            VectorNodeType.Smooth);

        var corner = VectorPathEditor.ConvertNodeType(node, VectorNodeType.Corner);

        Assert.Equal(VectorNodeType.Corner, corner.Type);
        Assert.Equal(node.HandleIn, corner.HandleIn);
        Assert.Equal(node.HandleOut, corner.HandleOut);
    }

    [Fact]
    public void MoveHandleOnSmoothNodeKeepsOppositeHandleCollinear()
    {
        var node = new VectorNode(
            new Position(0, 0, 0),
            new Position(-5, 0, 0),
            new Position(5, 0, 0),
            VectorNodeType.Smooth);

        var moved = VectorPathEditor.MoveHandle(node, isOutHandle: true, new Position(0, 5, 0), breakSymmetry: false);

        Assert.Equal(new Position(0, 5, 0), moved.HandleOut);
        // Opposite handle keeps its original 5mm distance, now pointing the opposite direction.
        var handleIn = moved.HandleIn!.Value;
        Assert.True(Math.Abs(handleIn.X - 0) < 1e-9);
        Assert.True(Math.Abs(handleIn.Y - -5) < 1e-9);
    }

    [Fact]
    public void MoveHandleWithBreakSymmetryLeavesOppositeHandleAlone()
    {
        var node = new VectorNode(
            new Position(0, 0, 0),
            new Position(-5, 0, 0),
            new Position(5, 0, 0),
            VectorNodeType.Smooth);

        var moved = VectorPathEditor.MoveHandle(node, isOutHandle: true, new Position(0, 5, 0), breakSymmetry: true);

        Assert.Equal(new Position(0, 5, 0), moved.HandleOut);
        Assert.Equal(new Position(-5, 0, 0), moved.HandleIn);
    }

    [Fact]
    public void MoveHandleOnCornerNodeNeverAdjustsOppositeHandle()
    {
        var node = new VectorNode(
            new Position(0, 0, 0),
            new Position(-5, 0, 0),
            new Position(5, 0, 0),
            VectorNodeType.Corner);

        var moved = VectorPathEditor.MoveHandle(node, isOutHandle: true, new Position(0, 5, 0), breakSymmetry: false);

        Assert.Equal(new Position(-5, 0, 0), moved.HandleIn);
    }

    [Fact]
    public void TranslatedMovesAnchorAndBothHandlesTogether()
    {
        var node = new VectorNode(
            new Position(0, 0, 0),
            new Position(-5, 0, 0),
            new Position(5, 0, 0),
            VectorNodeType.Smooth);

        var moved = node.Translated(2, 3);

        Assert.Equal(new Position(2, 3, 0), moved.Anchor);
        Assert.Equal(new Position(-3, 3, 0), moved.HandleIn);
        Assert.Equal(new Position(7, 3, 0), moved.HandleOut);
    }

    // ---------------------------------------------------------------------------------------
    // VectorPathSceneFactory — the flatten-at-the-boundary contract SceneObject relies on
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void CreateFlattensStraightPathToExactImportedShapePoints()
    {
        var path = VectorPath.SingleOpen(
        [
            VectorNode.CornerAt(new Position(0, 0, 0)),
            VectorNode.CornerAt(new Position(10, 0, 0)),
            VectorNode.CornerAt(new Position(10, 10, 0)),
        ]);

        var obj = VectorPathSceneFactory.Create(path, Color, "Cesta");

        Assert.True(obj.IsVectorPath);
        Assert.Single(obj.LocalShapes);
        var shape = obj.LocalShapes[0];
        Assert.False(shape.IsClosed);
        Assert.Equal(3, shape.Points.Count);
        Assert.Equal(LayerMode.Cut, shape.PreferredMode);
    }

    [Fact]
    public void CreateFlattensClosedPathAndMarksPreferredModeFill()
    {
        var subpath = new VectorSubpath
        {
            Nodes =
            [
                VectorNode.CornerAt(new Position(0, 0, 0)),
                VectorNode.CornerAt(new Position(10, 0, 0)),
                VectorNode.CornerAt(new Position(10, 10, 0)),
            ],
            IsClosed = true,
        };
        var path = new VectorPath { Subpaths = [subpath] };

        var obj = VectorPathSceneFactory.Create(path, Color, "Cesta uzavřená");

        var shape = obj.LocalShapes[0];
        Assert.True(shape.IsClosed);
        Assert.Equal(LayerMode.Fill, shape.PreferredMode);
        Assert.Equal(shape.Points[0], shape.Points[^1]);
    }

    [Fact]
    public void RebuildKeepsIdentityTransformAndLayerButUpdatesGeometry()
    {
        var path = VectorPath.SingleOpen(
        [
            VectorNode.CornerAt(new Position(0, 0, 0)),
            VectorNode.CornerAt(new Position(10, 0, 0)),
        ]);
        var obj = VectorPathSceneFactory.Create(path, Color, "Cesta");
        obj.Transform = obj.Transform with { X = 5, Y = 7 };

        var edited = path.ReplaceSubpath(0, VectorPathEditor.AppendNode(path.Subpaths[0], new Position(10, 10, 0), null));
        var rebuilt = VectorPathSceneFactory.Rebuild(obj, edited);

        Assert.Equal(obj.Id, rebuilt.Id);
        Assert.Equal(obj.Transform, rebuilt.Transform);
        Assert.Equal(3, rebuilt.LocalShapes[0].Points.Count);
    }

    [Fact]
    public void RebuildThrowsForObjectsThatAreNotVectorPaths()
    {
        var plain = Lasero.Core.Scene.ScenePrimitiveFactory.CreateLine(
            new Position(0, 0, 0), new Position(10, 0, 0), Color, "Čára");

        Assert.Throws<InvalidOperationException>(() =>
            VectorPathSceneFactory.Rebuild(plain, VectorPath.SingleOpen([VectorNode.CornerAt(Position.Zero)])));
    }

    // ---------------------------------------------------------------------------------------
    // helpers
    // ---------------------------------------------------------------------------------------

    private static double Distance(Position a, Position b) =>
        Math.Sqrt((b.X - a.X) * (b.X - a.X) + (b.Y - a.Y) * (b.Y - a.Y));

    private static double ClosestDistanceOnCurve(Position p0, Position c1, Position c2, Position p1, Position point, int samples)
    {
        var min = double.MaxValue;
        for (var i = 0; i <= samples; i++)
        {
            var t = (double)i / samples;
            var curvePoint = CubicBezier.Evaluate(p0, c1, c2, p1, t);
            var d = Distance(curvePoint, point);
            if (d < min) min = d;
        }
        return min;
    }
}
