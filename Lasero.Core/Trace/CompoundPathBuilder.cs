using Lasero.Core.Grbl;
using Lasero.Core.Layers;
using Lasero.Core.Scene;

namespace Lasero.Core.Trace;

/// <summary>
/// Turns a ContourExtractor hierarchy into node-editable VectorPaths: one BezierFitter fit per
/// contour, grouped so that one top-level (depth 0) RawContour plus its entire descendant subtree —
/// holes, islands nested inside those holes, holes inside those islands, and so on, recursively —
/// becomes one VectorPath/one traced object. That matches how a bitmap with several disconnected
/// shapes already becomes several separate compound-path groups elsewhere in this codebase (see
/// SceneViewModel.NormalizeNestedCompoundPaths, which this does NOT call — real hierarchy from OpenCV
/// makes that signed-area guessing unnecessary here).
///
/// Winding is assigned explicitly per contour from its tree depth (even = fill, odd = hole) rather
/// than trusted from whatever order FindContours happens to emit — see EnforceWinding.
/// </summary>
public static class CompoundPathBuilder
{
    public static IReadOnlyList<TracedVectorObject> Build(
        ContourTree tree, BitmapTraceOptions options, RgbColor color, double scaleMmPerPixel,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tree);
        ArgumentNullException.ThrowIfNull(options);

        var fitOptions = new BezierFitOptions
        {
            ToleranceMm = options.FitToleranceBasePx * scaleMmPerPixel,
            CornerAngleDegrees = options.CornerAngleThresholdDegrees,
            DedupeDistanceMm = BitmapTraceOptions.DedupeDistancePx * scaleMmPerPixel,
        };

        var results = new List<TracedVectorObject>();
        foreach (var root in tree.Roots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var subpaths = new List<VectorSubpath>();
            Collect(root, tree.Height, scaleMmPerPixel, fitOptions, subpaths);
            if (subpaths.Count == 0) continue;
            results.Add(new TracedVectorObject(new VectorPath { Subpaths = subpaths }, color));
        }
        return results;
    }

    private static void Collect(
        RawContour contour, int imageHeightPx, double scaleMmPerPixel, BezierFitOptions fitOptions, List<VectorSubpath> output)
    {
        var mmPoints = ToMillimeters(contour.Points, imageHeightPx, scaleMmPerPixel);
        var fitted = BezierFitter.FitClosed(mmPoints, fitOptions);
        if (fitted.Nodes.Count >= 3)
        {
            var wantPositiveArea = contour.Depth % 2 == 0; // even depth = fill, odd = hole
            output.Add(EnforceWinding(fitted, wantPositiveArea));
        }

        foreach (var child in contour.Children)
            Collect(child, imageHeightPx, scaleMmPerPixel, fitOptions, output);
    }

    /// <summary>Pixel (x right, y down, origin top-left) to mm (x right, y up, origin bottom-left) —
    /// the same "source.Height - y" flip the legacy tracer used, so trace output keeps landing right
    /// side up on the canvas.</summary>
    private static List<Position> ToMillimeters(IReadOnlyList<PixelPoint> points, int imageHeightPx, double scaleMmPerPixel) =>
        points.Select(p => new Position(p.X * scaleMmPerPixel, (imageHeightPx - p.Y) * scaleMmPerPixel, 0)).ToList();

    /// <summary>Forces the subpath's winding to match the desired fill/hole parity so it reads
    /// correctly under FillRule.Nonzero (see SceneCanvas.xaml.cs's BuildCompoundGeometry doc comment:
    /// a contour wound against its parent punches a hole, a same-wound contour merges instead).
    /// Computed from the fitted node anchors — ignoring curve bulge does not change a simple, non-
    /// self-intersecting contour's winding sign.</summary>
    private static VectorSubpath EnforceWinding(VectorSubpath subpath, bool wantPositiveArea)
    {
        var isPositive = SignedArea(subpath.Nodes) > 0;
        return isPositive == wantPositiveArea ? subpath : Reverse(subpath);
    }

    private static VectorSubpath Reverse(VectorSubpath subpath)
    {
        var reversed = subpath.Nodes
            .Reverse()
            .Select(node => node with { HandleIn = node.HandleOut, HandleOut = node.HandleIn })
            .ToList();
        return subpath with { Nodes = reversed };
    }

    private static double SignedArea(IReadOnlyList<VectorNode> nodes)
    {
        var area = 0d;
        for (var i = 0; i < nodes.Count; i++)
        {
            var current = nodes[i].Anchor;
            var next = nodes[(i + 1) % nodes.Count].Anchor;
            area += current.X * next.Y - next.X * current.Y;
        }
        return area / 2;
    }
}
