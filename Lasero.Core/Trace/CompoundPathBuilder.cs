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
            ToleranceMm = options.FitToleranceBasePx * tree.SubpixelScale * scaleMmPerPixel,
            CornerAngleDegrees = options.CornerAngleThresholdDegrees,
            DedupeDistanceMm = BitmapTraceOptions.DedupeDistancePx * tree.SubpixelScale * scaleMmPerPixel,
        };

        // One source pixel in millimetres, to express the end-of-pipeline guard in pixels.
        var referenceLimitMm = MaxDeviationFromContourPx * tree.SubpixelScale * scaleMmPerPixel;

        var results = new List<TracedVectorObject>();
        foreach (var root in tree.Roots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var subpaths = new List<VectorSubpath>();
            Collect(root, tree.Height, scaleMmPerPixel, fitOptions, referenceLimitMm, subpaths);
            if (subpaths.Count == 0) continue;
            results.Add(new TracedVectorObject(new VectorPath { Subpaths = subpaths }, color));
        }
        return results;
    }

    /// <summary>Furthest, in source pixels, any point of a fitted ring may be from the contour it was fitted
    /// to. Nothing legitimate comes near this; a fit that does is discarded (see Collect).</summary>
    internal const double MaxDeviationFromContourPx = 3.5;

    private static void Collect(
        RawContour contour, int imageHeightPx, double scaleMmPerPixel, BezierFitOptions fitOptions,
        double referenceLimitMm, List<VectorSubpath> output)
    {
        var mmPoints = ToMillimeters(contour.Points, imageHeightPx, scaleMmPerPixel);
        var fitted = BezierFitter.FitClosed(mmPoints, fitOptions);
        if (fitted.Nodes.Count >= 3)
        {
            // Last line of defence, whatever the cause: no point of the fitted ring may be further than
            // 3.5 px from the raw contour. If one is (a chord across a letter, a bad seam), the ring is
            // replaced by a plain simplified polyline of the raw contour, for this ring only.
            if (contour.Reference is { Count: >= 3 } reference)
            {
                var referenceMm = ToMillimeters(reference, imageHeightPx, scaleMmPerPixel);
                if (!StaysNear(fitted, referenceMm, referenceLimitMm))
                {
                    var plain = SimplifiedPolyline(referenceMm, fitOptions.ToleranceMm);
                    if (plain.Nodes.Count >= 3) fitted = plain;
                }
            }

            var wantPositiveArea = contour.Depth % 2 == 0; // even depth = fill, odd = hole
            output.Add(EnforceWinding(fitted, wantPositiveArea));
        }

        foreach (var child in contour.Children)
            Collect(child, imageHeightPx, scaleMmPerPixel, fitOptions, referenceLimitMm, output);
    }

    /// <summary>True when every flattened point of the subpath, and the midpoint of every flattened segment,
    /// is within the limit of the reference ring.</summary>
    internal static bool StaysNear(VectorSubpath subpath, IReadOnlyList<Position> reference, double limitMm)
    {
        var x = new double[reference.Count];
        var y = new double[reference.Count];
        for (var i = 0; i < reference.Count; i++)
        {
            x[i] = reference[i].X;
            y[i] = reference[i].Y;
        }
        var proximity = new PolylineProximity(x, y, limitMm);

        var points = subpath.Flatten(limitMm / 25);
        for (var i = 0; i < points.Count; i++)
        {
            var a = points[i];
            var b = points[(i + 1) % points.Count];
            if (!double.IsFinite(a.X) || !double.IsFinite(a.Y)) return false;
            var distance = Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));
            var steps = Math.Max(1, (int)Math.Ceiling(distance / (limitMm / 2.5)));
            for (var s = 0; s <= steps; s++)
            {
                var t = s / (double)steps;
                if (proximity.DistanceTo(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t) > limitMm) return false;
            }
        }
        return true;
    }

    /// <summary>The raw ring as a closed polygon with every vertex that is not needed within the tolerance
    /// removed (Douglas-Peucker from the two most distant points). Corner nodes, no curves.</summary>
    private static VectorSubpath SimplifiedPolyline(IReadOnlyList<Position> ring, double toleranceMm)
    {
        var count = ring.Count;
        var far = 0;
        var best = -1d;
        for (var i = 1; i < count; i++)
        {
            var d = (ring[i].X - ring[0].X) * (ring[i].X - ring[0].X) + (ring[i].Y - ring[0].Y) * (ring[i].Y - ring[0].Y);
            if (d > best) { best = d; far = i; }
        }

        var keep = new bool[count];
        keep[0] = true;
        keep[far] = true;
        Simplify(ring, 0, far, toleranceMm, keep);
        Simplify(ring, far, count, toleranceMm, keep);

        var nodes = new List<VectorNode>();
        for (var i = 0; i < count; i++)
            if (keep[i]) nodes.Add(VectorNode.CornerAt(ring[i]));
        return new VectorSubpath { Nodes = nodes, IsClosed = true };
    }

    /// <summary>Marks the vertices of ring[from..to] (indices wrap at the ring length) to keep.</summary>
    private static void Simplify(IReadOnlyList<Position> ring, int from, int to, double tolerance, bool[] keep)
    {
        var stack = new Stack<(int From, int To)>();
        stack.Push((from, to));
        while (stack.Count > 0)
        {
            var (a, b) = stack.Pop();
            if (b - a < 2) continue;
            var pa = ring[a % ring.Count];
            var pb = ring[b % ring.Count];
            var abx = pb.X - pa.X;
            var aby = pb.Y - pa.Y;
            var lengthSquared = abx * abx + aby * aby;
            var worst = -1;
            var worstDistance = tolerance;
            for (var i = a + 1; i < b; i++)
            {
                var p = ring[i % ring.Count];
                var t = lengthSquared < 1e-18 ? 0 : Math.Clamp(((p.X - pa.X) * abx + (p.Y - pa.Y) * aby) / lengthSquared, 0, 1);
                var dx = p.X - (pa.X + abx * t);
                var dy = p.Y - (pa.Y + aby * t);
                var distance = Math.Sqrt(dx * dx + dy * dy);
                if (distance <= worstDistance) continue;
                worstDistance = distance;
                worst = i;
            }
            if (worst < 0) continue;
            keep[worst % ring.Count] = true;
            stack.Push((a, worst));
            stack.Push((worst, b));
        }
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
