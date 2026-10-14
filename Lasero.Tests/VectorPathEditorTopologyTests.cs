using Lasero.Core.Grbl;
using Lasero.Core.Scene;

namespace Lasero.Tests;

/// <summary>
/// Coverage for the LightBurn-parity operations added to VectorPathEditor/VectorPath: explicit
/// line↔curve conversion, exact-point segment dragging, break-at-node, and delete-segment. Pure model
/// tests — no WPF, no SceneObject — matching VectorPathTests.cs's existing convention.
/// </summary>
public sealed class VectorPathEditorTopologyTests
{
    // ---------------------------------------------------------------------------------------
    // ConvertSegmentToCurve / ConvertSegmentToLine — LB-VEC-010, LB-VEC-014
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void ConvertSegmentToCurveIsVisuallyEquivalentToTheOriginalLine()
    {
        var subpath = new VectorSubpath
        {
            Nodes = [VectorNode.CornerAt(new Position(0, 0, 0)), VectorNode.CornerAt(new Position(30, 0, 0))],
            IsClosed = false,
        };

        var curved = VectorPathEditor.ConvertSegmentToCurve(subpath, 0);

        Assert.False(VectorSubpath.IsStraightSegment(curved.Nodes[0], curved.Nodes[1]));
        // Sampling the new curve at many t must reproduce the original straight line exactly.
        for (var i = 0; i <= 10; i++)
        {
            var t = i / 10.0;
            var expected = CubicBezier.Lerp(new Position(0, 0, 0), new Position(30, 0, 0), t);
            var actual = CubicBezier.Evaluate(
                curved.Nodes[0].Anchor, curved.Nodes[0].HandleOut!.Value, curved.Nodes[1].HandleIn!.Value, curved.Nodes[1].Anchor, t);
            Assert.True(Distance(expected, actual) < 1e-9);
        }
    }

    [Fact]
    public void ConvertSegmentToCurveIsIdempotentOnAnAlreadyCurvedSegment()
    {
        var a = new VectorNode(new Position(0, 0, 0), null, new Position(0, 10, 0), VectorNodeType.Smooth);
        var b = new VectorNode(new Position(20, 0, 0), new Position(20, 10, 0), null, VectorNodeType.Smooth);
        var subpath = new VectorSubpath { Nodes = [a, b], IsClosed = false };

        var result = VectorPathEditor.ConvertSegmentToCurve(subpath, 0);

        Assert.Equal(subpath, result);
    }

    [Fact]
    public void ConvertSegmentToLinePreservesEndpointsExactlyAndRemovesHandles()
    {
        var a = new VectorNode(new Position(0, 0, 0), null, new Position(0, 10, 0), VectorNodeType.Smooth);
        var b = new VectorNode(new Position(20, 0, 0), new Position(20, 10, 0), null, VectorNodeType.Smooth);
        var subpath = new VectorSubpath { Nodes = [a, b], IsClosed = false };

        var lined = VectorPathEditor.ConvertSegmentToLine(subpath, 0);

        Assert.Equal(a.Anchor, lined.Nodes[0].Anchor);
        Assert.Equal(b.Anchor, lined.Nodes[1].Anchor);
        Assert.Null(lined.Nodes[0].HandleOut);
        Assert.Null(lined.Nodes[1].HandleIn);
        Assert.True(VectorSubpath.IsStraightSegment(lined.Nodes[0], lined.Nodes[1]));
    }

    // ---------------------------------------------------------------------------------------
    // DragSegmentPoint — LB-VEC-011
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void DragSegmentPointOnAStraightLineBowsTheCurveThroughThePointerExactly()
    {
        var subpath = new VectorSubpath
        {
            Nodes = [VectorNode.CornerAt(new Position(0, 0, 0)), VectorNode.CornerAt(new Position(20, 0, 0))],
            IsClosed = false,
        };
        const double t = 0.5;
        var target = new Position(10, 6, 0);

        var result = VectorPathEditor.DragSegmentPoint(subpath, 0, t, target);

        Assert.False(VectorSubpath.IsStraightSegment(result.Nodes[0], result.Nodes[1]));
        var actual = CubicBezier.Evaluate(
            result.Nodes[0].Anchor, result.Nodes[0].HandleOut!.Value, result.Nodes[1].HandleIn!.Value, result.Nodes[1].Anchor, t);
        Assert.True(Distance(target, actual) < 1e-9);
        // Endpoints never move.
        Assert.Equal(new Position(0, 0, 0), result.Nodes[0].Anchor);
        Assert.Equal(new Position(20, 0, 0), result.Nodes[1].Anchor);
    }

    [Fact]
    public void DragSegmentPointOnACurveReshapesToPassThroughTheNewPointAtTheSameT()
    {
        var a = new VectorNode(new Position(0, 0, 0), null, new Position(0, 10, 0), VectorNodeType.Smooth);
        var b = new VectorNode(new Position(20, 0, 0), new Position(20, 10, 0), null, VectorNodeType.Smooth);
        var subpath = new VectorSubpath { Nodes = [a, b], IsClosed = false };
        const double t = 0.3;
        var target = new Position(5, 20, 0);

        var result = VectorPathEditor.DragSegmentPoint(subpath, 0, t, target);

        var actual = CubicBezier.Evaluate(
            result.Nodes[0].Anchor, result.Nodes[0].HandleOut!.Value, result.Nodes[1].HandleIn!.Value, result.Nodes[1].Anchor, t);
        Assert.True(Distance(target, actual) < 1e-9);
    }

    [Fact]
    public void DragSegmentPointIsNotCumulativeAcrossRepeatedCallsFromTheSameOriginal()
    {
        // Simulates the "every preview frame = original + current total delta" contract: calling
        // DragSegmentPoint twice from the SAME pristine subpath with two different targets must give
        // two independent results, not a compounding one.
        var subpath = new VectorSubpath
        {
            Nodes = [VectorNode.CornerAt(new Position(0, 0, 0)), VectorNode.CornerAt(new Position(20, 0, 0))],
            IsClosed = false,
        };

        var frame1 = VectorPathEditor.DragSegmentPoint(subpath, 0, 0.5, new Position(10, 3, 0));
        var frame2 = VectorPathEditor.DragSegmentPoint(subpath, 0, 0.5, new Position(10, 9, 0));

        var point1 = CubicBezier.Evaluate(frame1.Nodes[0].Anchor, frame1.Nodes[0].HandleOut!.Value, frame1.Nodes[1].HandleIn!.Value, frame1.Nodes[1].Anchor, 0.5);
        var point2 = CubicBezier.Evaluate(frame2.Nodes[0].Anchor, frame2.Nodes[0].HandleOut!.Value, frame2.Nodes[1].HandleIn!.Value, frame2.Nodes[1].Anchor, 0.5);
        Assert.True(Distance(new Position(10, 3, 0), point1) < 1e-9);
        Assert.True(Distance(new Position(10, 9, 0), point2) < 1e-9);
    }

    // ---------------------------------------------------------------------------------------
    // BreakAtNode — LB-VEC-040
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void BreakClosedPathAtNodeProducesOneOpenSubpathWithDuplicatedEndpoints()
    {
        var subpath = new VectorSubpath
        {
            Nodes =
            [
                VectorNode.CornerAt(new Position(0, 0, 0)),
                VectorNode.CornerAt(new Position(10, 0, 0)),
                VectorNode.CornerAt(new Position(10, 10, 0)),
                VectorNode.CornerAt(new Position(0, 10, 0)),
            ],
            IsClosed = true,
        };
        var path = new VectorPath { Subpaths = [subpath] };

        var broken = VectorPathEditor.BreakAtNode(path, 0, nodeIndex: 1);

        var result = Assert.Single(broken.Subpaths);
        Assert.False(result.IsClosed);
        Assert.Equal(5, result.Nodes.Count); // original 4 + duplicated break node
        Assert.Equal(result.Nodes[0].Anchor, result.Nodes[^1].Anchor); // both endpoints at the break location
        Assert.Equal(new Position(10, 0, 0), result.Nodes[0].Anchor);
        Assert.Null(result.Nodes[0].HandleIn);
        Assert.Null(result.Nodes[^1].HandleOut);
    }

    [Fact]
    public void BreakOpenPathAtInternalNodeSplitsIntoTwoSubpaths()
    {
        var subpath = new VectorSubpath
        {
            Nodes =
            [
                VectorNode.CornerAt(new Position(0, 0, 0)),
                VectorNode.CornerAt(new Position(10, 0, 0)),
                VectorNode.CornerAt(new Position(20, 0, 0)),
            ],
            IsClosed = false,
        };
        var path = new VectorPath { Subpaths = [subpath] };

        var broken = VectorPathEditor.BreakAtNode(path, 0, nodeIndex: 1);

        Assert.Equal(2, broken.Subpaths.Count);
        Assert.Equal(2, broken.Subpaths[0].Nodes.Count);
        Assert.Equal(2, broken.Subpaths[1].Nodes.Count);
        Assert.Equal(new Position(10, 0, 0), broken.Subpaths[0].Nodes[^1].Anchor);
        Assert.Equal(new Position(10, 0, 0), broken.Subpaths[1].Nodes[0].Anchor);
        Assert.False(broken.Subpaths[0].IsClosed);
        Assert.False(broken.Subpaths[1].IsClosed);
    }

    [Fact]
    public void BreakOpenPathAtEndpointIsRejected()
    {
        var subpath = new VectorSubpath
        {
            Nodes = [VectorNode.CornerAt(Position.Zero), VectorNode.CornerAt(new Position(10, 0, 0))],
            IsClosed = false,
        };
        var path = new VectorPath { Subpaths = [subpath] };

        Assert.Throws<ArgumentException>(() => VectorPathEditor.BreakAtNode(path, 0, nodeIndex: 0));
        Assert.Throws<ArgumentException>(() => VectorPathEditor.BreakAtNode(path, 0, nodeIndex: 1));
    }

    // ---------------------------------------------------------------------------------------
    // DeleteSegment — LB-VEC-024
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void DeleteSegmentOnClosedPathOpensItWithoutLosingAnyNode()
    {
        var subpath = new VectorSubpath
        {
            Nodes =
            [
                VectorNode.CornerAt(new Position(0, 0, 0)),
                VectorNode.CornerAt(new Position(10, 0, 0)),
                VectorNode.CornerAt(new Position(10, 10, 0)),
                VectorNode.CornerAt(new Position(0, 10, 0)),
            ],
            IsClosed = true,
        };
        var path = new VectorPath { Subpaths = [subpath] };

        // Delete the segment between node 0 and node 1.
        var result = VectorPathEditor.DeleteSegment(path, 0, segmentIndex: 0);

        var opened = Assert.Single(result.Subpaths);
        Assert.False(opened.IsClosed);
        Assert.Equal(4, opened.Nodes.Count); // no node lost, only the connecting segment
        Assert.Equal(new Position(10, 0, 0), opened.Nodes[0].Anchor); // starts right after the cut
        Assert.Equal(new Position(0, 0, 0), opened.Nodes[^1].Anchor); // ends right before the cut
    }

    [Fact]
    public void DeleteInternalSegmentOnOpenPathSplitsIntoTwoSubpaths()
    {
        var subpath = new VectorSubpath
        {
            Nodes =
            [
                VectorNode.CornerAt(new Position(0, 0, 0)),
                VectorNode.CornerAt(new Position(10, 0, 0)),
                VectorNode.CornerAt(new Position(20, 0, 0)),
                VectorNode.CornerAt(new Position(30, 0, 0)),
            ],
            IsClosed = false,
        };
        var path = new VectorPath { Subpaths = [subpath] };

        var result = VectorPathEditor.DeleteSegment(path, 0, segmentIndex: 1); // between node 1 and node 2

        Assert.Equal(2, result.Subpaths.Count);
        Assert.Equal(2, result.Subpaths[0].Nodes.Count);
        Assert.Equal(2, result.Subpaths[1].Nodes.Count);
        Assert.Equal(new Position(10, 0, 0), result.Subpaths[0].Nodes[^1].Anchor);
        Assert.Equal(new Position(20, 0, 0), result.Subpaths[1].Nodes[0].Anchor);
    }

    [Fact]
    public void DeleteEndSegmentOnOpenPathDropsTheDegenerateSingleNodeSide()
    {
        var subpath = new VectorSubpath
        {
            Nodes =
            [
                VectorNode.CornerAt(new Position(0, 0, 0)),
                VectorNode.CornerAt(new Position(10, 0, 0)),
                VectorNode.CornerAt(new Position(20, 0, 0)),
            ],
            IsClosed = false,
        };
        var path = new VectorPath { Subpaths = [subpath] };

        // Deleting the FIRST segment leaves node 0 isolated (a degenerate 1-node "path") — dropped.
        var result = VectorPathEditor.DeleteSegment(path, 0, segmentIndex: 0);

        var survivor = Assert.Single(result.Subpaths);
        Assert.Equal(2, survivor.Nodes.Count);
        Assert.Equal(new Position(10, 0, 0), survivor.Nodes[0].Anchor);
        Assert.Equal(new Position(20, 0, 0), survivor.Nodes[1].Anchor);
    }

    [Fact]
    public void SmoothNodeCreatesTangentHandlesAndKeepsAdjacentStraightSegmentsStraight()
    {
        var subpath = new VectorSubpath
        {
            Nodes = [VectorNode.CornerAt(new Position(0, 0, 0)), VectorNode.CornerAt(new Position(10, 0, 0)), VectorNode.CornerAt(new Position(20, 0, 0))],
            IsClosed = false,
        };

        var smooth = VectorPathEditor.SetNodeType(subpath, 1, VectorNodeType.Smooth);

        Assert.NotNull(smooth.Nodes[1].HandleIn);
        Assert.NotNull(smooth.Nodes[1].HandleOut);
        Assert.Equal(VectorNodeType.Smooth, smooth.Nodes[1].Type);
        for (var i = 0; i <= 10; i++)
        {
            var t = i / 10.0;
            Assert.Equal(0, CubicBezier.Evaluate(
                smooth.Nodes[0].Anchor, smooth.Nodes[0].HandleOut ?? smooth.Nodes[0].Anchor,
                smooth.Nodes[1].HandleIn ?? smooth.Nodes[1].Anchor, smooth.Nodes[1].Anchor, t).Y, 8);
            Assert.Equal(0, CubicBezier.Evaluate(
                smooth.Nodes[1].Anchor, smooth.Nodes[1].HandleOut ?? smooth.Nodes[1].Anchor,
                smooth.Nodes[2].HandleIn ?? smooth.Nodes[2].Anchor, smooth.Nodes[2].Anchor, t).Y, 8);
        }
    }

    [Fact]
    public void AltHandleDragTurnsSmoothNodeIntoCuspBeforeTheNextHandleDrag()
    {
        var node = new VectorNode(new Position(0, 0, 0), new Position(-5, 0, 0), new Position(5, 0, 0), VectorNodeType.Smooth);

        var cusp = VectorPathEditor.MoveHandle(node, isOutHandle: true, new Position(0, 5, 0), breakSymmetry: true);
        var next = VectorPathEditor.MoveHandle(cusp, isOutHandle: false, new Position(-5, 0.01, 0), breakSymmetry: false);

        Assert.Equal(VectorNodeType.Corner, cusp.Type);
        Assert.Equal(cusp.HandleOut, next.HandleOut);
    }

    [Fact]
    public void ClosingWithDragKeepsFirstNodeHandlesCollinear()
    {
        var path = VectorSubpath.Empty;
        path = VectorPathEditor.AppendNode(path, new Position(0, 0, 0), new Position(5, 0, 0));
        path = VectorPathEditor.AppendNode(path, new Position(20, 0, 0), null);
        path = VectorPathEditor.AppendNode(path, new Position(10, 15, 0), null);

        var closed = VectorPathEditor.Close(path, new Position(0, 5, 0));

        Assert.True(closed.IsClosed);
        Assert.True(VectorPathEditor.IsTangentContinuous(closed, 0));
    }

    [Fact]
    public void JoiningSeparatedEndpointsKeepsBothAnchorsAndAddsAConnectingSegment()
    {
        var a = new VectorSubpath { Nodes = [VectorNode.CornerAt(new Position(0, 0, 0)), VectorNode.CornerAt(new Position(10, 0, 0))] };
        var b = new VectorSubpath { Nodes = [VectorNode.CornerAt(new Position(13, 0, 0)), VectorNode.CornerAt(new Position(20, 5, 0))] };

        var joined = VectorPathEditor.JoinAtEndpoints(a, aJoinAtStart: false, b, bJoinAtStart: true);

        Assert.Equal(4, joined.Nodes.Count);
        Assert.Equal(new Position(10, 0, 0), joined.Nodes[1].Anchor);
        Assert.Equal(new Position(13, 0, 0), joined.Nodes[2].Anchor);
    }

    [Fact]
    public void InsertNodeRejectsNonFiniteParametersWithoutCreatingInvalidGeometry()
    {
        var subpath = new VectorSubpath { Nodes = [VectorNode.CornerAt(Position.Zero), VectorNode.CornerAt(new Position(10, 0, 0))] };

        Assert.Throws<ArgumentOutOfRangeException>(() => VectorPathEditor.InsertNode(subpath, 0, double.NaN));
        Assert.Throws<ArgumentOutOfRangeException>(() => VectorPathEditor.InsertNode(subpath, 0, double.PositiveInfinity));
    }

    [Fact]
    public void ConvertingASegmentToLineNormalizesASmoothCornerThatWouldBecomeAKink()
    {
        var subpath = new VectorSubpath
        {
            Nodes =
            [
                VectorNode.CornerAt(new Position(0, 0, 0)),
                new VectorNode(new Position(10, 0, 0), new Position(7, -3, 0), new Position(13, 3, 0), VectorNodeType.Smooth),
                VectorNode.CornerAt(new Position(20, 0, 0)),
            ],
        };

        var result = VectorPathEditor.ConvertSegmentToLine(subpath, 0);

        Assert.Equal(VectorNodeType.Corner, result.Nodes[1].Type);
        Assert.Null(result.Nodes[1].HandleIn);
    }

    [Fact]
    public void TrimRemovesOnlyTheClickedPieceBetweenIntersections()
    {
        var target = new VectorSubpath { Nodes = [VectorNode.CornerAt(new Position(0, 0, 0)), VectorNode.CornerAt(new Position(10, 0, 0))] };
        var path = new VectorPath { Subpaths = [target] };
        var cuts = new[] { 0.4, 0.6 };

        var trimmed = VectorPathTrim.TrimSegment(path, 0, 0, 0.5, cuts);

        Assert.Equal(2, trimmed.Subpaths.Count);
        Assert.Equal(new Position(4, 0, 0), trimmed.Subpaths[0].Nodes[^1].Anchor);
        Assert.Equal(new Position(6, 0, 0), trimmed.Subpaths[1].Nodes[0].Anchor);
    }

    [Fact]
    public void ExtendMovesAStraightOpenEndpointToTheNearestIntersection()
    {
        var subpath = new VectorSubpath { Nodes = [VectorNode.CornerAt(new Position(0, 0, 0)), VectorNode.CornerAt(new Position(5, 0, 0))] };
        var path = new VectorPath { Subpaths = [subpath] };
        var cutter = BezierSegment.Line(new Position(10, -5, 0), new Position(10, 5, 0));

        var extended = VectorPathTrim.TryExtend(path, 0, atStart: false, p => p, p => p, [cutter], 20, out var result);

        Assert.True(extended);
        Assert.Equal(new Position(10, 0, 0), result.Subpaths[0].Nodes[^1].Anchor);
        Assert.False(VectorPathTrim.TryExtend(result, 0, atStart: false, p => p, p => p, [], 20, out _));
    }

    [Fact]
    public void DeleteNodeRefitKeepsAnSCurveWhenOneCubicCannotMeetTheErrorTolerance()
    {
        var subpath = new VectorSubpath
        {
            Nodes =
            [
                new VectorNode(new Position(0, 0, 0), null, new Position(0, 5, 0), VectorNodeType.Corner),
                new VectorNode(new Position(10, 0, 0), new Position(5, -5, 0), new Position(15, 5, 0), VectorNodeType.Smooth),
                new VectorNode(new Position(20, 0, 0), new Position(20, -5, 0), null, VectorNodeType.Corner),
            ],
        };

        var result = VectorPathEditor.RemoveNode(subpath, 1, refit: true, out var deviation);

        Assert.Equal(subpath.Nodes, result.Nodes);
        Assert.True(double.IsFinite(deviation));
        Assert.True(deviation > VectorPathEditor.RefitToleranceMm, $"the S-curve should use the no-refit fallback, measured {deviation:F3} mm");
    }

    [Fact]
    public void DeleteWithoutRefitKeepsSurvivingHandlePositions()
    {
        var subpath = new VectorSubpath
        {
            Nodes =
            [
                new VectorNode(new Position(0, 0, 0), null, new Position(0, 5, 0), VectorNodeType.Corner),
                new VectorNode(new Position(10, 0, 0), new Position(5, -5, 0), new Position(15, 5, 0), VectorNodeType.Smooth),
                new VectorNode(new Position(20, 0, 0), new Position(20, -5, 0), null, VectorNodeType.Corner),
            ],
        };

        var result = VectorPathEditor.RemoveNode(subpath, 1, refit: false, out _);

        Assert.Equal(subpath.Nodes[0].HandleOut, result.Nodes[0].HandleOut);
        Assert.Equal(subpath.Nodes[2].HandleIn, result.Nodes[1].HandleIn);
    }

    private static double Distance(Position a, Position b) =>
        Math.Sqrt((b.X - a.X) * (b.X - a.X) + (b.Y - a.Y) * (b.Y - a.Y));
}
