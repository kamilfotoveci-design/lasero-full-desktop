using Lasero.Core.GCode;
using Lasero.Core.Grbl;
using Lasero.Core.Scene;
using Lasero.Core.Scene.Snapping;
using Xunit;

namespace Lasero.Tests;

public sealed class ResizeEdgeSnapTests
{
    private static readonly IReadOnlyList<SnapCandidate> NoCandidates = [];

    [Fact]
    public void ResolveAxisMatchesOnlyTheRequestedCoordinate()
    {
        var candidates = new[] { new SnapCandidate(new Position(50, 999, 0), SnapKind.Node) };

        var hit = SnapEngine.ResolveAxis(50.4, SnapAxis.X, candidates, 1);
        var miss = SnapEngine.ResolveAxis(50.4, SnapAxis.Y, candidates, 1);

        Assert.True(hit.Snapped);
        Assert.Equal(50, hit.Value);
        Assert.False(miss.Snapped);
        Assert.Equal(50.4, miss.Value);
    }

    [Fact]
    public void ResolveAxisPrefersGeometryOverGridAndFallsBackToGrid()
    {
        var candidates = new[] { new SnapCandidate(new Position(10.3, 0, 0), SnapKind.Node) };

        var geometry = SnapEngine.ResolveAxis(10.1, SnapAxis.X, candidates, 0.5, gridStepMm: 5);
        var grid = SnapEngine.ResolveAxis(20.2, SnapAxis.X, candidates, 0.5, gridStepMm: 5, crossValue: 7);
        var none = SnapEngine.ResolveAxis(22.5, SnapAxis.X, candidates, 0.5, gridStepMm: 5);

        Assert.Equal(SnapKind.Node, geometry.Target!.Value.Kind);
        Assert.Equal(10.3, geometry.Value, 9);
        Assert.Equal(SnapKind.Grid, grid.Target!.Value.Kind);
        Assert.Equal(20, grid.Value, 9);
        Assert.Equal(7, grid.Target.Value.Point.Y);
        Assert.False(none.Snapped);
    }

    // A 20 x 10 box whose anchor is the bottom-left corner (0,0); dragging the right edge or the
    // top-right corner, the handle follows the pointer one to one.
    private static Position Follow(Position mouse) => mouse;

    [Fact]
    public void SideHandleSnapsOnlyItsOwnAxis()
    {
        var candidates = new[] { new SnapCandidate(new Position(30, 55, 0), SnapKind.Node) };

        var result = ResizeEdgeSnap.Resolve(new Position(29.7, 4.2, 0), new Position(0, 0, 0), Follow,
            affectsX: true, affectsY: false, lockAspect: false, candidates, 0.5, gridStepMm: null);

        Assert.Equal(30, result.Mouse.X, 9);
        Assert.Equal(4.2, result.Mouse.Y, 9); // Y untouched even though 55 is a candidate coordinate
        Assert.NotNull(result.TargetX);
        Assert.Null(result.TargetY);
    }

    [Fact]
    public void SnapMovesTheEdgeNotThePointerGrabOffset()
    {
        // The handle sits 3 mm to the right of the pointer (grabbed off-centre). The edge, not the pointer,
        // must land on 30.
        Position Offset(Position mouse) => new(mouse.X + 3, mouse.Y, 0);
        var candidates = new[] { new SnapCandidate(new Position(30, 0, 0), SnapKind.Node) };

        var result = ResizeEdgeSnap.Resolve(new Position(26.8, 0, 0), new Position(0, 0, 0), Offset,
            true, false, false, candidates, 0.5, null);

        Assert.Equal(30, Offset(result.Mouse).X, 9);
    }

    [Fact]
    public void CornerHandleSnapsBothAxesIndependently()
    {
        var candidates = new[] { new SnapCandidate(new Position(30, 0, 0), SnapKind.Node) };

        var result = ResizeEdgeSnap.Resolve(new Position(29.8, 10.2, 0), new Position(0, 0, 0), Follow,
            true, true, false, candidates, 0.5, gridStepMm: 5);

        Assert.Equal(30, result.Mouse.X, 9); // geometry
        Assert.Equal(10, result.Mouse.Y, 9); // grid
        Assert.Equal(SnapKind.Node, result.TargetX!.Value.Kind);
        Assert.Equal(SnapKind.Grid, result.TargetY!.Value.Kind);
    }

    [Fact]
    public void AspectLockKeepsTheCornerOnItsDiagonal()
    {
        // 2:1 diagonal from the anchor; the raw handle (30.2, 15.1) is on it. X snaps to 30, so Y must
        // follow to 15 rather than snapping on its own.
        var candidates = new[] { new SnapCandidate(new Position(30, 0, 0), SnapKind.Node) };

        var result = ResizeEdgeSnap.Resolve(new Position(30.2, 15.1, 0), new Position(0, 0, 0), Follow,
            true, true, lockAspect: true, candidates, 0.5, gridStepMm: null);

        Assert.Equal(30, result.Mouse.X, 9);
        Assert.Equal(15, result.Mouse.Y, 9);
        Assert.Equal(result.Mouse.Y / result.Mouse.X, 15.1 / 30.2, 9);
    }

    [Fact]
    public void NothingInToleranceLeavesThePointerAlone()
    {
        var candidates = new[] { new SnapCandidate(new Position(30, 0, 0), SnapKind.Node) };

        var result = ResizeEdgeSnap.Resolve(new Position(25, 7, 0), new Position(0, 0, 0), Follow,
            true, true, false, candidates, 0.5, gridStepMm: null);

        Assert.False(result.Snapped);
        Assert.Equal(new Position(25, 7, 0), result.Mouse);
    }

    [Fact]
    public void ResizeHandlePointerMapsThroughQuarterTurnedObject()
    {
        // 90 degree object: dragging its local-X handle moves a world-Y edge.
        Assert.True(Lasero.App.Controls.SceneCanvas.IsQuarterTurn(90, out var swapped));
        Assert.True(swapped);
        Assert.True(Lasero.App.Controls.SceneCanvas.IsQuarterTurn(180, out swapped));
        Assert.False(swapped);
        Assert.True(Lasero.App.Controls.SceneCanvas.IsQuarterTurn(-90, out swapped));
        Assert.True(swapped);
        Assert.False(Lasero.App.Controls.SceneCanvas.IsQuarterTurn(30, out _));
    }

    [Fact]
    public void ObjectResizedWithSnappedPointerLandsItsEdgeOnTheTarget()
    {
        // End to end through the real resize solve: a 20 x 10 rectangle at the origin, drag the right
        // handle with a pointer 0.3 mm short of x = 30.
        var bounds = new BoundingBox2D(0, 0, 20, 10);
        var pivot = new Position(10, 5, 0);
        var start = ObjectTransform.Identity;
        var dragged = ObjectTransform.HandleLocalPoint(bounds, pivot, ResizeHandle.Right);
        var anchor = start.Apply(new Position(2 * pivot.X - dragged.X, 2 * pivot.Y - dragged.Y, 0), pivot);
        var candidates = new[] { new SnapCandidate(new Position(30, 80, 0), SnapKind.Node) };

        var snapped = ResizeEdgeSnap.Resolve(new Position(29.7, 5, 0), anchor,
            mouse => start.ComputeResize(pivot, bounds, ResizeHandle.Right, mouse).Apply(dragged, pivot),
            true, false, false, candidates, 0.5, null);
        var resized = start.ComputeResize(pivot, bounds, ResizeHandle.Right, snapped.Mouse);

        Assert.Equal(30, resized.Apply(dragged, pivot).X, 9);
        Assert.Equal(0, resized.Apply(new Position(0, 5, 0), pivot).X, 9); // the opposite edge stays put
    }
}
