using System.Diagnostics;
using Lasero.Core.Grbl;
using Lasero.Core.Scene;
using Lasero.Core.Scene.Snapping;

namespace Lasero.Tests;

/// <summary>
/// Real (not simulated) timing measurements for the two operations complex-path node editing most
/// depends on: VectorPath.FlattenAll (re-run on every live-drag frame — see SceneCanvas.VectorPathTool
/// .RenderVectorPathLive) and SnapCandidateBuilder.FromVectorPath (built once per node-drag start).
/// Bounds are deliberately generous (CI/dev-machine safe, not a tight perf budget) — these exist to
/// catch an accidental O(n²)-per-frame regression, not to enforce a specific millisecond target.
/// See docs/reference/NODE_EDIT_PARITY_AUDIT_2026-09-16.md's Phase 5 complex-path pass for the actual
/// numbers measured when this was written.
/// </summary>
public sealed class NodeEditPerformanceTests
{
    private static VectorPath BuildZigzagPath(int nodeCount, bool curved)
    {
        var nodes = new List<VectorNode>(nodeCount);
        for (var i = 0; i < nodeCount; i++)
        {
            var x = i * 2.0;
            var y = i % 2 == 0 ? 0.0 : 10.0;
            var anchor = new Position(x, y, 0);
            if (!curved) { nodes.Add(VectorNode.CornerAt(anchor)); continue; }
            nodes.Add(new VectorNode(anchor, new Position(x - 0.5, y - 1, 0), new Position(x + 0.5, y + 1, 0), VectorNodeType.Smooth));
        }
        return VectorPath.SingleOpen(nodes);
    }

    [Theory]
    [InlineData(100)]
    [InlineData(500)]
    [InlineData(1000)]
    public void FlattenAllOfACurvedPathCompletesWellWithinOneLiveDragFrameBudget(int nodeCount)
    {
        var path = BuildZigzagPath(nodeCount, curved: true);
        // Warm up JIT before timing, so the assertion reflects steady-state cost, not first-call overhead.
        path.FlattenAll();

        var stopwatch = Stopwatch.StartNew();
        var flattened = path.FlattenAll();
        stopwatch.Stop();

        Assert.NotEmpty(flattened);
        // Generous: even a single-digit-ms budget would comfortably beat a 60fps (16ms) live-drag
        // frame; this asserts two orders of magnitude of headroom instead of a tight target, so it
        // fails only on a genuine regression (e.g. an accidental O(n^2) reintroduced here), not on
        // ordinary machine/CI variance.
        Assert.True(stopwatch.ElapsedMilliseconds < 500,
            $"FlattenAll({nodeCount} curved nodes) took {stopwatch.ElapsedMilliseconds}ms, expected < 500ms");
    }

    [Theory]
    [InlineData(100)]
    [InlineData(500)]
    [InlineData(1000)]
    public void SnapCandidateBuilderOnADragStartCompletesWellWithinInteractiveBudget(int nodeCount)
    {
        var path = BuildZigzagPath(nodeCount, curved: true);
        SnapCandidateBuilder.FromVectorPath(path, p => p); // warm-up

        var stopwatch = Stopwatch.StartNew();
        var candidates = SnapCandidateBuilder.FromVectorPath(path, p => p);
        stopwatch.Stop();

        Assert.NotEmpty(candidates);
        // Built once per drag-start (not per frame — see SnapCandidateBuilder's own doc comment), so
        // the interactive budget is generous (a user will not perceive up to ~150ms as unresponsive
        // for a one-time action at the start of a gesture); a single subpath means the intersection
        // pass (the O(n^2) part) never runs at all here, so this is really timing the O(n) node/
        // midpoint extraction.
        Assert.True(stopwatch.ElapsedMilliseconds < 500,
            $"SnapCandidateBuilder.FromVectorPath({nodeCount} nodes) took {stopwatch.ElapsedMilliseconds}ms, expected < 500ms");
    }

    [Fact]
    public void SnapCandidateBuilderSkipsIntersectionSearchAboveItsOwnPointBudgetRatherThanStalling()
    {
        // Two subpaths each with 1500 nodes (3000 total flattened points, comfortably over the
        // builder's own 2000-point intersection-search cap) — this must stay fast because the
        // intersection pass should skip itself, not because O(n^2) segment-pair testing is somehow
        // fast at this size.
        var path = new VectorPath
        {
            Subpaths =
            [
                BuildZigzagPath(1500, curved: false).Subpaths[0],
                BuildZigzagPath(1500, curved: false).Subpaths[0] with
                {
                    Nodes = BuildZigzagPath(1500, curved: false).Subpaths[0].Nodes
                        .Select(n => n.Translated(0, 100)).ToList(),
                },
            ],
        };
        SnapCandidateBuilder.FromVectorPath(path, p => p); // warm-up

        var stopwatch = Stopwatch.StartNew();
        var candidates = SnapCandidateBuilder.FromVectorPath(path, p => p);
        stopwatch.Stop();

        Assert.DoesNotContain(candidates, c => c.Kind == SnapKind.Intersection);
        Assert.True(stopwatch.ElapsedMilliseconds < 500,
            $"Over-budget intersection search took {stopwatch.ElapsedMilliseconds}ms, expected < 500ms (should have skipped)");
    }

    [Fact]
    public void HitTestingTenThousandStraightSegmentsCompletesWithinInteractiveBudget()
    {
        var path = BuildZigzagPath(10_000, curved: false);
        var pointer = new Position(19_995, 5, 0);
        VectorPathHitTester.FindNearest(path, pointer, 10, point => point); // warm-up

        var stopwatch = Stopwatch.StartNew();
        var hit = VectorPathHitTester.FindNearest(path, pointer, 10, point => point);
        stopwatch.Stop();

        Assert.NotNull(hit);
        Assert.True(stopwatch.ElapsedMilliseconds < 500,
            $"VectorPathHitTester over 10,000 nodes took {stopwatch.ElapsedMilliseconds}ms, expected < 500ms");
    }
}
