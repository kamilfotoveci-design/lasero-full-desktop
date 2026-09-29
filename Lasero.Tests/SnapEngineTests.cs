using Lasero.Core.Grbl;
using Lasero.Core.Scene;
using Lasero.Core.Scene.Snapping;

namespace Lasero.Tests;

/// <summary>
/// Pure-logic coverage for the node-edit snapping engine: candidate resolution (nearest-within-
/// tolerance, deterministic tie-break, grid fallback) and candidate building from a VectorPath (node/
/// endpoint/midpoint/intersection extraction, self-exclusion). None of this needs a live app or WPF —
/// see SnapEngine.cs / SnapCandidateBuilder.cs for the design.
/// </summary>
public sealed class SnapEngineTests
{
    // ---------------------------------------------------------------------------------------
    // SnapEngine.Resolve
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void ResolveReturnsOriginalPointWhenNoCandidateIsWithinTolerance()
    {
        var point = new Position(0, 0, 0);
        var candidates = new[] { new SnapCandidate(new Position(10, 0, 0), SnapKind.Node) };

        var result = SnapEngine.Resolve(point, candidates, toleranceWorld: 1);

        Assert.False(result.Snapped);
        Assert.Equal(point, result.Point);
        Assert.Null(result.Target);
    }

    [Fact]
    public void ResolveSnapsToTheNearestCandidateWithinTolerance()
    {
        var point = new Position(0, 0, 0);
        var candidates = new[]
        {
            new SnapCandidate(new Position(5, 0, 0), SnapKind.Node),
            new SnapCandidate(new Position(1, 0, 0), SnapKind.Midpoint),
            new SnapCandidate(new Position(3, 0, 0), SnapKind.Endpoint),
        };

        var result = SnapEngine.Resolve(point, candidates, toleranceWorld: 10);

        Assert.True(result.Snapped);
        Assert.Equal(new Position(1, 0, 0), result.Point);
        Assert.Equal(SnapKind.Midpoint, result.Target!.Value.Kind);
    }

    [Fact]
    public void ResolveBreaksAnExactTieByDeterministicPriorityNotListOrder()
    {
        var point = new Position(0, 0, 0);
        // Both candidates are exactly equidistant (5 units away) -- Endpoint must win over Midpoint
        // regardless of which one appears first in the list, so two runs with reordered input still
        // agree (no oscillation from float noise or enumeration order).
        var midpointFirst = new[]
        {
            new SnapCandidate(new Position(5, 0, 0), SnapKind.Midpoint),
            new SnapCandidate(new Position(0, 5, 0), SnapKind.Endpoint),
        };
        var endpointFirst = new[]
        {
            new SnapCandidate(new Position(0, 5, 0), SnapKind.Endpoint),
            new SnapCandidate(new Position(5, 0, 0), SnapKind.Midpoint),
        };

        var resultA = SnapEngine.Resolve(point, midpointFirst, toleranceWorld: 10);
        var resultB = SnapEngine.Resolve(point, endpointFirst, toleranceWorld: 10);

        Assert.Equal(SnapKind.Endpoint, resultA.Target!.Value.Kind);
        Assert.Equal(SnapKind.Endpoint, resultB.Target!.Value.Kind);
    }

    [Fact]
    public void ResolveFallsBackToGridOnlyWhenNoGeometryCandidateIsInTolerance()
    {
        var point = new Position(1.1, 1.9, 0);

        var farGeometry = SnapEngine.Resolve(
            point,
            [new SnapCandidate(new Position(50, 50, 0), SnapKind.Node)],
            toleranceWorld: 1,
            gridStepMm: 1);

        Assert.True(farGeometry.Snapped);
        Assert.Equal(SnapKind.Grid, farGeometry.Target!.Value.Kind);
        Assert.Equal(new Position(1, 2, 0), farGeometry.Point);
    }

    [Fact]
    public void ResolvePrefersGeometryCandidateOverGridWhenBothAreInTolerance()
    {
        var point = new Position(1.1, 0, 0);
        var candidates = new[] { new SnapCandidate(new Position(1.2, 0, 0), SnapKind.Node) };

        var result = SnapEngine.Resolve(point, candidates, toleranceWorld: 1, gridStepMm: 1);

        Assert.True(result.Snapped);
        Assert.Equal(SnapKind.Node, result.Target!.Value.Kind);
        Assert.Equal(new Position(1.2, 0, 0), result.Point);
    }

    [Fact]
    public void ResolveWithNoGridStepNeverSnapsToGrid()
    {
        var point = new Position(0.1, 0.1, 0);

        var result = SnapEngine.Resolve(point, [], toleranceWorld: 5, gridStepMm: null);

        Assert.False(result.Snapped);
    }

    [Fact]
    public void SnapToGridRoundsToTheNearestMultipleOfStep()
    {
        Assert.Equal(new Position(10, 5, 0), SnapEngine.SnapToGrid(new Position(11.4, 4.6, 0), 5));
    }

    // ---------------------------------------------------------------------------------------
    // SnapCandidateBuilder.FromVectorPath
    // ---------------------------------------------------------------------------------------

    private static readonly Func<Position, Position> Identity = p => p;

    private static VectorPath SingleClosed(IReadOnlyList<VectorNode> nodes) =>
        new() { Subpaths = [new VectorSubpath { Nodes = nodes, IsClosed = true }] };

    [Fact]
    public void FromVectorPathExtractsNodeCandidatesForAClosedSubpath()
    {
        var path = SingleClosed(
        [
            VectorNode.CornerAt(new Position(0, 0, 0)),
            VectorNode.CornerAt(new Position(10, 0, 0)),
            VectorNode.CornerAt(new Position(10, 10, 0)),
        ]);

        var candidates = SnapCandidateBuilder.FromVectorPath(path, Identity);

        var nodeCandidates = candidates.Where(c => c.Kind == SnapKind.Node).ToList();
        Assert.Equal(3, nodeCandidates.Count);
        Assert.Contains(nodeCandidates, c => c.Point == new Position(0, 0, 0));
        Assert.Contains(nodeCandidates, c => c.Point == new Position(10, 0, 0));
        Assert.Contains(nodeCandidates, c => c.Point == new Position(10, 10, 0));
        // A closed subpath has no open endpoints.
        Assert.DoesNotContain(candidates, c => c.Kind == SnapKind.Endpoint);
    }

    [Fact]
    public void FromVectorPathMarksTheFirstAndLastNodeOfAnOpenSubpathAsEndpoints()
    {
        var path = VectorPath.SingleOpen(
        [
            VectorNode.CornerAt(new Position(0, 0, 0)),
            VectorNode.CornerAt(new Position(5, 0, 0)),
            VectorNode.CornerAt(new Position(10, 0, 0)),
        ]);

        var candidates = SnapCandidateBuilder.FromVectorPath(path, Identity);

        var endpoints = candidates.Where(c => c.Kind == SnapKind.Endpoint).Select(c => c.Point).ToList();
        Assert.Equal(2, endpoints.Count);
        Assert.Contains(new Position(0, 0, 0), endpoints);
        Assert.Contains(new Position(10, 0, 0), endpoints);
        // The middle node is a plain Node, not an Endpoint.
        Assert.Contains(candidates, c => c.Kind == SnapKind.Node && c.Point == new Position(5, 0, 0));
    }

    [Fact]
    public void FromVectorPathExcludesRequestedNodesSoADraggedNodeNeverSnapsToItself()
    {
        var path = SingleClosed(
        [
            VectorNode.CornerAt(new Position(0, 0, 0)),
            VectorNode.CornerAt(new Position(10, 0, 0)),
            VectorNode.CornerAt(new Position(10, 10, 0)),
        ]);

        var candidates = SnapCandidateBuilder.FromVectorPath(
            path, Identity, exclude: new HashSet<(int, int)> { (0, 0) });

        Assert.DoesNotContain(candidates, c => c.Kind == SnapKind.Node && c.Point == new Position(0, 0, 0));
        Assert.Contains(candidates, c => c.Kind == SnapKind.Node && c.Point == new Position(10, 0, 0));
    }

    [Fact]
    public void FromVectorPathIncludesTheExactCurveMidpointNotAChordApproximation()
    {
        var path = VectorPath.SingleOpen(
        [
            new VectorNode(new Position(0, 0, 0), null, new Position(3, 4, 0), VectorNodeType.Corner),
            new VectorNode(new Position(10, 0, 0), new Position(7, 4, 0), null, VectorNodeType.Corner),
        ]);
        var subpath = path.Subpaths[0];
        var (a, b) = subpath.Segment(0);
        var expectedMid = CubicBezier.Evaluate(a.Anchor, a.HandleOut ?? a.Anchor, b.HandleIn ?? b.Anchor, b.Anchor, 0.5);

        var candidates = SnapCandidateBuilder.FromVectorPath(path, Identity);

        var midpoint = Assert.Single(candidates, c => c.Kind == SnapKind.Midpoint);
        Assert.Equal(expectedMid.X, midpoint.Point.X, precision: 9);
        Assert.Equal(expectedMid.Y, midpoint.Point.Y, precision: 9);
    }

    [Fact]
    public void FromVectorPathFindsAnIntersectionBetweenTwoCrossingSubpaths()
    {
        var path = new VectorPath
        {
            Subpaths =
            [
                new VectorSubpath
                {
                    IsClosed = false,
                    Nodes = [VectorNode.CornerAt(new Position(0, 5, 0)), VectorNode.CornerAt(new Position(10, 5, 0))],
                },
                new VectorSubpath
                {
                    IsClosed = false,
                    Nodes = [VectorNode.CornerAt(new Position(5, 0, 0)), VectorNode.CornerAt(new Position(5, 10, 0))],
                },
            ],
        };

        var candidates = SnapCandidateBuilder.FromVectorPath(path, Identity);

        var intersection = Assert.Single(candidates, c => c.Kind == SnapKind.Intersection);
        Assert.Equal(5, intersection.Point.X, precision: 6);
        Assert.Equal(5, intersection.Point.Y, precision: 6);
    }

    [Fact]
    public void FromVectorPathAppliesTheGivenWorldTransformToEveryCandidate()
    {
        var path = SingleClosed(
        [
            VectorNode.CornerAt(new Position(0, 0, 0)),
            VectorNode.CornerAt(new Position(1, 0, 0)),
            VectorNode.CornerAt(new Position(1, 1, 0)),
        ]);
        Position ShiftByTen(Position local) => local with { X = local.X + 10, Y = local.Y + 10 };

        var candidates = SnapCandidateBuilder.FromVectorPath(path, ShiftByTen);

        Assert.Contains(candidates, c => c.Kind == SnapKind.Node && c.Point == new Position(10, 10, 0));
        Assert.Contains(candidates, c => c.Kind == SnapKind.Node && c.Point == new Position(11, 10, 0));
    }
}
