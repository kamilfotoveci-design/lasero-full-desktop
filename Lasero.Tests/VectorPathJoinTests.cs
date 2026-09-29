using Lasero.Core.Grbl;
using Lasero.Core.Scene;

namespace Lasero.Tests;

/// <summary>
/// Pure-logic coverage for VectorPathEditor.ReverseSubpath / JoinAtEndpoints — the cross-object
/// endpoint-join building block (Phase 3 of the node-editing parity work). No WPF, no SceneObject;
/// SceneViewModelTests covers the world-space/metadata wiring on top of this. Matches
/// VectorPathEditorTopologyTests.cs's existing convention.
/// </summary>
public sealed class VectorPathJoinTests
{
    private static VectorSubpath OpenLine(Position start, Position end) => new()
    {
        Nodes = [VectorNode.CornerAt(start), VectorNode.CornerAt(end)],
        IsClosed = false,
    };

    // ---------------------------------------------------------------------------------------
    // ReverseSubpath
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void ReverseSubpathFlipsNodeOrderAndSwapsHandles()
    {
        var subpath = new VectorSubpath
        {
            Nodes =
            [
                new VectorNode(new Position(0, 0, 0), null, new Position(3, 4, 0), VectorNodeType.Corner),
                new VectorNode(new Position(10, 0, 0), new Position(7, 4, 0), null, VectorNodeType.Corner),
            ],
            IsClosed = false,
        };

        var reversed = VectorPathEditor.ReverseSubpath(subpath);

        Assert.Equal(new Position(10, 0, 0), reversed.Nodes[0].Anchor);
        Assert.Equal(new Position(7, 4, 0), reversed.Nodes[0].HandleOut);
        Assert.Null(reversed.Nodes[0].HandleIn);
        Assert.Equal(new Position(0, 0, 0), reversed.Nodes[1].Anchor);
        Assert.Equal(new Position(3, 4, 0), reversed.Nodes[1].HandleIn);
        Assert.Null(reversed.Nodes[1].HandleOut);
    }

    [Fact]
    public void ReversingTwiceReturnsTheOriginalNodeSequence()
    {
        var subpath = OpenLine(new Position(0, 0, 0), new Position(10, 5, 0));

        var roundTripped = VectorPathEditor.ReverseSubpath(VectorPathEditor.ReverseSubpath(subpath));

        Assert.Equal(subpath.Nodes, roundTripped.Nodes);
    }

    // ---------------------------------------------------------------------------------------
    // JoinAtEndpoints — all four start/end combinations
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void JoinAtEndpointsConnectsAEndToBStartWithoutReversingEither()
    {
        var a = OpenLine(new Position(0, 0, 0), new Position(10, 0, 0));
        var b = OpenLine(new Position(10, 0, 0), new Position(20, 5, 0));

        var joined = VectorPathEditor.JoinAtEndpoints(a, aJoinAtStart: false, b, bJoinAtStart: true);

        Assert.False(joined.IsClosed);
        Assert.Equal(3, joined.Nodes.Count);
        Assert.Equal(new Position(0, 0, 0), joined.Nodes[0].Anchor);
        Assert.Equal(new Position(10, 0, 0), joined.Nodes[1].Anchor);
        Assert.Equal(new Position(20, 5, 0), joined.Nodes[2].Anchor);
    }

    [Fact]
    public void JoinAtEndpointsConnectsAEndToBEndByReversingB()
    {
        var a = OpenLine(new Position(0, 0, 0), new Position(10, 0, 0));
        // B's own "end" is at (10,0,0) -- same join point as above, but expressed as B's END this time.
        var b = OpenLine(new Position(20, 5, 0), new Position(10, 0, 0));

        var joined = VectorPathEditor.JoinAtEndpoints(a, aJoinAtStart: false, b, bJoinAtStart: false);

        Assert.Equal(3, joined.Nodes.Count);
        Assert.Equal(new Position(0, 0, 0), joined.Nodes[0].Anchor);
        Assert.Equal(new Position(10, 0, 0), joined.Nodes[1].Anchor);
        Assert.Equal(new Position(20, 5, 0), joined.Nodes[2].Anchor);
    }

    [Fact]
    public void JoinAtEndpointsConnectsAStartToBStartByReversingA()
    {
        // A's "start" is at (10,0,0) -- reversing A puts that endpoint last, matching B's own start.
        var a = OpenLine(new Position(10, 0, 0), new Position(0, 0, 0));
        var b = OpenLine(new Position(10, 0, 0), new Position(20, 5, 0));

        var joined = VectorPathEditor.JoinAtEndpoints(a, aJoinAtStart: true, b, bJoinAtStart: true);

        Assert.Equal(3, joined.Nodes.Count);
        Assert.Equal(new Position(0, 0, 0), joined.Nodes[0].Anchor);
        Assert.Equal(new Position(10, 0, 0), joined.Nodes[1].Anchor);
        Assert.Equal(new Position(20, 5, 0), joined.Nodes[2].Anchor);
    }

    [Fact]
    public void JoinAtEndpointsConnectsAStartToBEndByReversingBoth()
    {
        var a = OpenLine(new Position(10, 0, 0), new Position(0, 0, 0));
        var b = OpenLine(new Position(20, 5, 0), new Position(10, 0, 0));

        var joined = VectorPathEditor.JoinAtEndpoints(a, aJoinAtStart: true, b, bJoinAtStart: false);

        Assert.Equal(3, joined.Nodes.Count);
        Assert.Equal(new Position(0, 0, 0), joined.Nodes[0].Anchor);
        Assert.Equal(new Position(10, 0, 0), joined.Nodes[1].Anchor);
        Assert.Equal(new Position(20, 5, 0), joined.Nodes[2].Anchor);
    }

    [Fact]
    public void JoinAtEndpointsKeepsSideAsIncomingHandleAndSideBsOutgoingHandleAtTheJoinNode()
    {
        var a = new VectorSubpath
        {
            Nodes =
            [
                VectorNode.CornerAt(new Position(0, 0, 0)),
                new VectorNode(new Position(10, 0, 0), new Position(8, 2, 0), null, VectorNodeType.Corner),
            ],
            IsClosed = false,
        };
        var b = new VectorSubpath
        {
            Nodes =
            [
                new VectorNode(new Position(10, 0, 0), null, new Position(12, -2, 0), VectorNodeType.Corner),
                VectorNode.CornerAt(new Position(20, 0, 0)),
            ],
            IsClosed = false,
        };

        var joined = VectorPathEditor.JoinAtEndpoints(a, aJoinAtStart: false, b, bJoinAtStart: true);

        var joinNode = joined.Nodes[1];
        Assert.Equal(new Position(8, 2, 0), joinNode.HandleIn);
        Assert.Equal(new Position(12, -2, 0), joinNode.HandleOut);
        Assert.Equal(VectorNodeType.Corner, joinNode.Type);
    }

    [Fact]
    public void JoinAtEndpointsThrowsForAClosedSubpath()
    {
        var closed = new VectorSubpath
        {
            Nodes = [VectorNode.CornerAt(Position.Zero), VectorNode.CornerAt(new Position(1, 0, 0)), VectorNode.CornerAt(new Position(1, 1, 0))],
            IsClosed = true,
        };
        var open = OpenLine(new Position(0, 0, 0), new Position(1, 0, 0));

        Assert.Throws<ArgumentException>(() => VectorPathEditor.JoinAtEndpoints(closed, false, open, true));
    }
}
