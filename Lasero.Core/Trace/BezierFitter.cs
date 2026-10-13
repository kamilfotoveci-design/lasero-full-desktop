using Lasero.Core.Grbl;
using Lasero.Core.Scene;

namespace Lasero.Core.Trace;

/// <summary>Tolerances BezierFitter fits against, already resolved from BitmapTraceOptions' 0-1/enum
/// knobs into concrete distances/angles (see BitmapTraceOptions' internal mapping members) — every
/// value here is in the same coordinate units as the points passed to Fit*, so a caller fitting mm
/// geometry passes mm distances, a caller fitting pixel geometry passes pixel distances.</summary>
public sealed record BezierFitOptions
{
    /// <summary>Max perpendicular error a fitted cubic segment may have against the source points
    /// before BezierFitter splits it and fits two curves instead.</summary>
    public required double ToleranceMm { get; init; }

    /// <summary>Turning-angle threshold, in degrees, above which a polyline vertex is forced to stay
    /// a sharp VectorNodeType.Corner instead of being folded into a smooth curve.</summary>
    public required double CornerAngleDegrees { get; init; }

    /// <summary>Points closer together than this are collapsed to one before any fitting happens.</summary>
    public required double DedupeDistanceMm { get; init; }
}

/// <summary>
/// Clean-room polygon-to-Bezier fitter, following the well-known numerical technique described in
/// Schneider's "An Algorithm for Automatically Fitting Digitized Curves" (Graphics Gems, 1990):
/// estimate a tangent direction at each anchor, least-squares fit a single cubic Bezier between two
/// anchors against the tangents, measure the worst perpendicular error against the source points, and
/// if it exceeds tolerance, split at the worst point and recurse. This is a generic, widely published
/// curve-fitting method (the same idea underlies Potrace's own curve stage, Adobe Illustrator's
/// "Image Trace", and countless vectorizers) — nothing here is transcribed or paraphrased from any
/// GPL-licensed source; the least-squares derivation, corner handling, and recursion below are this
/// codebase's own implementation of the published technique.
///
/// De Casteljau evaluation is NOT reimplemented here — CubicBezier.Evaluate (VectorPath.cs) is reused
/// as instructed, so this file only adds the fitting logic on top of it.
/// </summary>
public static class BezierFitter
{
    private const int MaxRecursionDepth = 24;

    /// <summary>Fits a closed ring (e.g. one contour from ContourExtractor) into one closed
    /// VectorSubpath. Returns VectorSubpath.Empty when the ring collapses to fewer than 3 usable
    /// points after cleanup (degenerate/too-small contour — callers should drop it).</summary>
    public static VectorSubpath FitClosed(IReadOnlyList<Position> ring, BezierFitOptions options)
    {
        var points = RemoveCollinear(Dedupe(ring, options.DedupeDistanceMm, wrap: true), wrap: true);
        if (points.Count < 3) return VectorSubpath.Empty;
        if (points.Count == 3)
            return new VectorSubpath { Nodes = points.Select(VectorNode.CornerAt).ToList(), IsClosed = true };

        var cornerSet = new HashSet<int>(DetectCorners(points, options.CornerAngleDegrees, wrap: true, CornerWindowDistance(options)));
        var breakIndices = cornerSet.OrderBy(i => i).ToList();
        switch (breakIndices.Count)
        {
            // A ring with no (or only one) hard corner — a circle/ellipse-like shape — still needs at
            // least two break points: a single cubic cannot represent a full closed loop, so seed the
            // farthest point from whatever anchor we have as an artificial (non-corner) split. Error-
            // based recursion below still adds as many further nodes as the tolerance demands.
            case 0:
                breakIndices = [0, FarthestIndex(points, 0)];
                break;
            case 1:
                breakIndices.Add(FarthestIndex(points, breakIndices[0]));
                break;
        }

        var nodes = new List<VectorNode>();
        for (var i = 0; i < breakIndices.Count; i++)
        {
            var start = breakIndices[i];
            var end = breakIndices[(i + 1) % breakIndices.Count];
            var chain = SliceRing(points, start, end);
            // A break that is not a real corner (the seed split of a smooth ring) is shared by two chains,
            // so both use the ring's own tangent there and the node stays smooth.
            Position? startTangent = cornerSet.Contains(start) ? null : CentralTangent(points, start);
            Position? endTangent = cornerSet.Contains(end) ? null : Negate(CentralTangent(points, end));
            var chainNodes = FitChain(chain, options, cornerSet.Contains(start), cornerSet.Contains(end), depth: 0, startTangent, endTangent);
            AppendChain(nodes, chainNodes);
        }
        // The last chain's end anchor is breakIndices[0] again — drop the duplicate.
        if (nodes.Count > 1)
        {
            // The last chain ends on the first node: it carries the closing segment's incoming handle.
            nodes[0] = Junction(nodes[^1], nodes[0]);
            nodes.RemoveAt(nodes.Count - 1);
        }

        return new VectorSubpath { Nodes = nodes, IsClosed = true };
    }

    /// <summary>Fits an open polyline (e.g. one edge chain from OutlineTracer) into one open
    /// VectorSubpath. Returns VectorSubpath.Empty when fewer than 2 usable points remain.</summary>
    public static VectorSubpath FitOpen(IReadOnlyList<Position> polyline, BezierFitOptions options)
    {
        var points = RemoveCollinear(Dedupe(polyline, options.DedupeDistanceMm, wrap: false), wrap: false);
        if (points.Count < 2) return VectorSubpath.Empty;
        if (points.Count == 2)
            return new VectorSubpath
            {
                Nodes = [VectorNode.CornerAt(points[0]), VectorNode.CornerAt(points[1])],
                IsClosed = false,
            };

        var breakIndices = new List<int> { 0 };
        breakIndices.AddRange(DetectCorners(points, options.CornerAngleDegrees, wrap: false, CornerWindowDistance(options)));
        breakIndices.Add(points.Count - 1);
        breakIndices = breakIndices.Distinct().OrderBy(i => i).ToList();

        var nodes = new List<VectorNode>();
        for (var i = 0; i < breakIndices.Count - 1; i++)
        {
            var start = breakIndices[i];
            var end = breakIndices[i + 1];
            var chain = points.Skip(start).Take(end - start + 1).ToList();
            // Every break point on an open path is either a real terminal (path start/end) or a
            // detected sharp corner — unlike the closed-ring case there is no artificial smooth seed.
            var chainNodes = FitChain(chain, options, forceStartCorner: true, forceEndCorner: true, depth: 0);
            AppendChain(nodes, chainNodes);
        }

        return new VectorSubpath { Nodes = nodes, IsClosed = false };
    }

    // -------------------------------------------------------------------------------------------
    // Core recursive fit-and-split (Schneider's FitCubic, reimplemented independently).
    // -------------------------------------------------------------------------------------------

    private static List<VectorNode> FitChain(
        IReadOnlyList<Position> chain, BezierFitOptions options, bool forceStartCorner, bool forceEndCorner, int depth,
        Position? startTangent = null, Position? endTangent = null)
    {
        if (chain.Count == 2)
        {
            return
            [
                new VectorNode(chain[0], null, null, forceStartCorner ? VectorNodeType.Corner : VectorNodeType.Smooth),
                new VectorNode(chain[^1], null, null, forceEndCorner ? VectorNodeType.Corner : VectorNodeType.Smooth),
            ];
        }

        var startType = forceStartCorner ? VectorNodeType.Corner : VectorNodeType.Smooth;
        var endType = forceEndCorner ? VectorNodeType.Corner : VectorNodeType.Smooth;

        // A chain that already lies on its chord is a straight segment: no handles, no curve to overshoot.
        if (IsStraight(chain, options.ToleranceMm * 0.5))
        {
            return
            [
                new VectorNode(chain[0], null, null, startType),
                new VectorNode(chain[^1], null, null, endType),
            ];
        }

        // Tangents handed down by a split (or by the seed break of a ring) are shared with the neighbouring
        // chain, so both cubics leave the common anchor along the same line and no kink appears there.
        var t1 = startTangent ?? ForwardTangent(chain, 0);
        var t2 = endTangent ?? Negate(ForwardTangent(chain, chain.Count - 1));
        IReadOnlyList<double> u = ChordLengthParameterize(chain);
        var chord = Distance(chain[0], chain[^1]);

        var (c1, c2) = FitControlPoints(chain, u, t1, t2, chord);
        var (maxError, worstIndex) = MeasureDeviation(chain, u, c1, c2);

        // Newton reparameterisation: moving each point's parameter to the nearest point of the curve and
        // refitting lets one long cubic follow a long arc that the chord-length parameters could not.
        for (var iteration = 0; iteration < 4 && worstIndex >= 0 && maxError > options.ToleranceMm; iteration++)
        {
            var refined = Reparameterize(chain, u, c1, c2);
            var (n1, n2) = FitControlPoints(chain, refined, t1, t2, chord);
            var (error, index) = MeasureDeviation(chain, refined, n1, n2);
            if (error >= maxError) break;
            u = refined;
            c1 = n1;
            c2 = n2;
            maxError = error;
            worstIndex = index;
        }

        if (worstIndex < 0 || maxError <= options.ToleranceMm)
        {
            return
            [
                new VectorNode(chain[0], null, c1, startType),
                new VectorNode(chain[^1], c2, null, endType),
            ];
        }

        if (depth >= MaxRecursionDepth)
        {
            // Out of splits: a straight segment cannot bulge outside the shape, a bad cubic can.
            return
            [
                new VectorNode(chain[0], null, null, startType),
                new VectorNode(chain[^1], null, null, endType),
            ];
        }

        var left = chain.Take(worstIndex + 1).ToList();
        var right = chain.Skip(worstIndex).ToList();
        // The new split point is an adaptive continuation, not a real corner, on either side of it.
        // Both halves leave the split along the same unit tangent (central difference of the contour there),
        // so the node between them has collinear handles.
        var split = Normalize(Sub(chain[worstIndex + 1], chain[worstIndex - 1]));
        var splitTangent = Length(split) < 1e-9 ? ForwardTangent(chain, worstIndex) : split;
        var leftNodes = FitChain(left, options, forceStartCorner, false, depth + 1, startTangent, Negate(splitTangent));
        var rightNodes = FitChain(right, options, false, forceEndCorner, depth + 1, splitTangent, endTangent);
        AppendChain(leftNodes, rightNodes);
        return leftNodes;
    }

    /// <summary>Least-squares control points along the given tangents, falling back to chord/3 handles when the
    /// solve is degenerate, with both handles capped (see <see cref="MaxHandleChordRatio"/>).</summary>
    private static (Position C1, Position C2) FitControlPoints(
        IReadOnlyList<Position> chain, IReadOnlyList<double> u, Position t1, Position t2, double chord)
    {
        if (!TryFitControlPoints(chain, u, t1, t2, out var c1, out var c2))
        {
            var chordLength = chord / 3;
            c1 = Add(chain[0], Scale(t1, chordLength));
            c2 = Add(chain[^1], Scale(t2, chordLength));
        }

        // A handle longer than a fraction of the chord is what lets a cubic swing past a tip when only a
        // few points constrain it, so the length is capped; the deviation check does the rest.
        if (chord > 1e-9)
        {
            c1 = ClampHandle(chain[0], c1, chord * MaxHandleChordRatio);
            c2 = ClampHandle(chain[^1], c2, chord * MaxHandleChordRatio);
        }
        return (c1, c2);
    }

    /// <summary>One Newton-Raphson step per point towards the curve parameter whose point is nearest to it.
    /// The end parameters stay 0 and 1.</summary>
    private static double[] Reparameterize(IReadOnlyList<Position> chain, IReadOnlyList<double> u, Position c1, Position c2)
    {
        var p0 = chain[0];
        var p3 = chain[^1];
        var result = new double[u.Count];
        result[0] = 0;
        result[^1] = 1;
        for (var i = 1; i < u.Count - 1; i++)
        {
            var t = u[i];
            var q = CubicBezier.Evaluate(p0, c1, c2, p3, t);
            var omt = 1 - t;
            var d1 = Add(Add(Scale(Sub(c1, p0), 3 * omt * omt), Scale(Sub(c2, c1), 6 * omt * t)), Scale(Sub(p3, c2), 3 * t * t));
            var d2 = Add(Scale(Add(Sub(c2, Scale(c1, 2)), p0), 6 * omt), Scale(Add(Sub(p3, Scale(c2, 2)), c1), 6 * t));
            var diff = Sub(q, chain[i]);
            var numerator = Dot(diff, d1);
            var denominator = Dot(d1, d1) + Dot(diff, d2);
            var next = Math.Abs(denominator) < 1e-12 ? t : t - numerator / denominator;
            result[i] = double.IsFinite(next) ? Math.Clamp(next, 0, 1) : t;
        }
        // Parameters must not cross, or the least-squares fit sees the chain folded back on itself.
        for (var i = 1; i < result.Length; i++) result[i] = Math.Max(result[i], result[i - 1]);
        return result;
    }

    /// <summary>Unit tangent of the ring at a vertex: central difference of its neighbours.</summary>
    private static Position CentralTangent(IReadOnlyList<Position> ring, int index)
    {
        var count = ring.Count;
        var tangent = Normalize(Sub(ring[(index + 1) % count], ring[(index - 1 + count) % count]));
        return tangent;
    }

    /// <summary>Appends a fitted chain to the nodes so far. The chain's first node and the last node so far
    /// are the same anchor: one carries the incoming handle of the previous cubic, the other the outgoing
    /// handle of the next, and both must survive in the merged node. Dropping either leaves a segment with
    /// a missing handle, and at the closing seam of a ring a straight chord across the whole last chain.</summary>
    private static void AppendChain(List<VectorNode> nodes, IReadOnlyList<VectorNode> chain)
    {
        if (chain.Count == 0) return;
        if (nodes.Count == 0)
        {
            nodes.AddRange(chain);
            return;
        }

        nodes[^1] = Junction(nodes[^1], chain[0]);
        for (var i = 1; i < chain.Count; i++) nodes.Add(chain[i]);
    }

    private static VectorNode Junction(VectorNode previousEnd, VectorNode nextStart) =>
        new(previousEnd.Anchor, previousEnd.HandleIn, nextStart.HandleOut,
            previousEnd.Type == VectorNodeType.Corner || nextStart.Type == VectorNodeType.Corner
                ? VectorNodeType.Corner
                : previousEnd.Type);

    /// <summary>Longest handle, as a fraction of the segment chord. A circular arc needs 0.39 of the chord
    /// per quarter turn, so this keeps every legitimate curve and rejects the long handles of a cubic
    /// forced across a sharp tip.</summary>
    private const double MaxHandleChordRatio = 0.4;

    private static Position ClampHandle(Position anchor, Position control, double maxLength)
    {
        var offset = Sub(control, anchor);
        var length = Length(offset);
        return length <= maxLength || length < 1e-12 ? control : Add(anchor, Scale(offset, maxLength / length));
    }

    private static bool IsStraight(IReadOnlyList<Position> chain, double tolerance)
    {
        var a = chain[0];
        var b = chain[^1];
        for (var i = 1; i < chain.Count - 1; i++)
            if (DistanceToSegment(chain[i], a, b) > tolerance) return false;
        return true;
    }

    private static double DistanceToSegment(Position p, Position a, Position b)
    {
        var ab = Sub(b, a);
        var lengthSquared = Dot(ab, ab);
        if (lengthSquared < 1e-18) return Distance(p, a);
        var t = Math.Clamp(Dot(Sub(p, a), ab) / lengthSquared, 0, 1);
        return Distance(p, Add(a, Scale(ab, t)));
    }

    /// <summary>Worst deviation between the cubic and its source polyline, measured both ways: from every
    /// source point to the curve at its parameter, and from points sampled along the curve to the nearest
    /// polyline segment. The second direction is what catches a bulge between two source points. Returns
    /// the error and the source index to split at (-1 when there is nothing to split).</summary>
    private static (double Error, int Index) MeasureDeviation(
        IReadOnlyList<Position> chain, IReadOnlyList<double> u, Position c1, Position c2)
    {
        var maxError = 0d;
        var worstIndex = -1;
        var last = chain.Count - 1;
        for (var i = 1; i < last; i++)
        {
            var sample = CubicBezier.Evaluate(chain[0], c1, c2, chain[last], u[i]);
            var error = Distance(sample, chain[i]);
            if (error <= maxError) continue;
            maxError = error;
            worstIndex = i;
        }

        var samples = Math.Clamp(chain.Count * 3, 12, 400);
        var look = Math.Max(6, 2 * chain.Count / samples + 4);
        var segment = 0;
        for (var s = 1; s < samples; s++)
        {
            var point = CubicBezier.Evaluate(chain[0], c1, c2, chain[last], s / (double)samples);
            var best = DistanceToSegment(point, chain[segment], chain[segment + 1]);
            var from = Math.Max(0, segment - 2);
            var to = Math.Min(last - 1, segment + look);
            for (var k = from; k <= to; k++)
            {
                var d = DistanceToSegment(point, chain[k], chain[k + 1]);
                if (d >= best) continue;
                best = d;
                segment = k;
            }

            if (best <= maxError) continue;
            maxError = best;
            var nearer = DistanceSquared(point, chain[segment]) <= DistanceSquared(point, chain[segment + 1]) ? segment : segment + 1;
            worstIndex = Math.Clamp(nearer, 1, last - 1);
        }
        return (maxError, worstIndex);
    }

    /// <summary>Standard Schneider least-squares solve for the two control-point distances (alpha1,
    /// alpha2) along the given unit tangents, using chord-length parameter values `u`. Returns false
    /// when the 2x2 normal-equations matrix is (near-)singular or yields a degenerate/negative
    /// distance, so the caller can fall back to the classic "chord/3" heuristic instead.</summary>
    private static bool TryFitControlPoints(
        IReadOnlyList<Position> chain, IReadOnlyList<double> u, Position t1, Position t2,
        out Position c1, out Position c2)
    {
        var p0 = chain[0];
        var p3 = chain[^1];
        double c00 = 0, c01 = 0, c11 = 0, x0 = 0, x1 = 0;

        for (var i = 0; i < chain.Count; i++)
        {
            var t = u[i];
            var omt = 1 - t;
            var b0 = omt * omt * omt;
            var b1 = 3 * t * omt * omt;
            var b2 = 3 * t * t * omt;
            var b3 = t * t * t;

            var a1 = Scale(t1, b1);
            var a2 = Scale(t2, b2);
            var fixedContribution = Add(Scale(p0, b0 + b1), Scale(p3, b2 + b3));
            var residual = Sub(chain[i], fixedContribution);

            c00 += Dot(a1, a1);
            c01 += Dot(a1, a2);
            c11 += Dot(a2, a2);
            x0 += Dot(a1, residual);
            x1 += Dot(a2, residual);
        }

        var det = c00 * c11 - c01 * c01;
        if (Math.Abs(det) < 1e-9)
        {
            c1 = default;
            c2 = default;
            return false;
        }

        var alpha1 = (c11 * x0 - c01 * x1) / det;
        var alpha2 = (c00 * x1 - c01 * x0) / det;
        var minAlpha = Math.Max(Distance(p0, p3) * 1e-3, 1e-6);
        if (!double.IsFinite(alpha1) || !double.IsFinite(alpha2) || alpha1 < minAlpha || alpha2 < minAlpha)
        {
            c1 = default;
            c2 = default;
            return false;
        }

        c1 = Add(p0, Scale(t1, alpha1));
        c2 = Add(p3, Scale(t2, alpha2));
        return true;
    }

    // -------------------------------------------------------------------------------------------
    // Pre-fit cleanup: dedupe + collinear merge + corner detection.
    // -------------------------------------------------------------------------------------------

    private static List<Position> Dedupe(IReadOnlyList<Position> points, double minDistance, bool wrap)
    {
        if (points.Count == 0) return [];
        var result = new List<Position> { points[0] };
        for (var i = 1; i < points.Count; i++)
        {
            if (Distance(result[^1], points[i]) >= minDistance) result.Add(points[i]);
        }
        if (wrap && result.Count > 1 && Distance(result[0], result[^1]) < minDistance)
            result.RemoveAt(result.Count - 1);
        return result;
    }

    /// <summary>Drops polyline vertices that lie (numerically) exactly on the line through their two
    /// neighbours — the axis-aligned pixel-grid edges FindContours produces for a straight-edged shape
    /// are perfectly collinear, so this alone collapses a rectangle's dense pixel-walk contour down to
    /// its 4 true corners before any curve fitting happens. Curved contours barely lose any points
    /// here (a staircase boundary is never exactly collinear), leaving corner detection/curve fitting
    /// to do the real simplification work for them.</summary>
    private static List<Position> RemoveCollinear(IReadOnlyList<Position> points, bool wrap)
    {
        var list = points.ToList();
        var minCount = wrap ? 3 : 2;
        var changed = true;
        while (changed && list.Count > minCount)
        {
            changed = false;
            var count = list.Count;
            var first = wrap ? 0 : 1;
            var last = wrap ? count - 1 : count - 2;
            for (var i = last; i >= first; i--)
            {
                var prev = list[(i - 1 + count) % count];
                var curr = list[i];
                var next = list[(i + 1) % count];
                var v1 = Sub(curr, prev);
                var v2 = Sub(next, curr);
                var scale = Length(v1) * Length(v2);
                if (scale < 1e-12) continue;
                var cross = v1.X * v2.Y - v1.Y * v2.X;
                if (Math.Abs(cross) / scale > 1e-6) continue;
                list.RemoveAt(i);
                changed = true;
                count--;
                if (count <= minCount) break;
            }
        }
        return list;
    }

    /// <summary>How far along the polyline (in the same units as the points, mm for CompoundPathBuilder's
    /// callers) DetectCorners looks on each side before measuring a turn. The window grows with fit
    /// tolerance so one-pixel staircase turns on a smooth contour are not mistaken for real corners;
    /// it still scales with source resolution through the pixel-derived tolerances.</summary>
    private static double CornerWindowDistance(BezierFitOptions options) =>
        Math.Max(Math.Max(options.DedupeDistanceMm * 6, options.ToleranceMm * 2), 1e-6);

    /// <summary>Flags vertices where the polyline turns sharply enough to force a VectorNodeType.Corner
    /// split. Deliberately measures the turn over a small arc-length window (StepBack/StepForward)
    /// rather than against the immediate neighbour: a FindContours pixel-grid walk is a staircase even
    /// along a perfectly straight diagonal or a smooth curve (each single-pixel step can turn close to
    /// 90 degrees), so an adjacent-point turning angle would flag nearly every vertex as a corner. A
    /// window spanning a few pixels' worth of arc length instead measures the macroscopic direction
    /// change, which is near-zero along a staircase and only large at a genuine corner.</summary>
    private static List<int> DetectCorners(IReadOnlyList<Position> points, double thresholdDegrees, bool wrap, double windowDistance)
    {
        var count = points.Count;
        var thresholdRad = thresholdDegrees * Math.PI / 180.0;
        var corners = new List<int>();
        var turns = new Dictionary<int, double>();
        var first = wrap ? 0 : 1;
        var last = wrap ? count - 1 : count - 2;
        for (var i = first; i <= last; i++)
        {
            var prevIndex = StepAlong(points, i, -1, windowDistance, wrap);
            var nextIndex = StepAlong(points, i, 1, windowDistance, wrap);
            if (prevIndex == i || nextIndex == i) continue;

            var v1 = Sub(points[i], points[prevIndex]);
            var v2 = Sub(points[nextIndex], points[i]);
            var len1 = Length(v1);
            var len2 = Length(v2);
            if (len1 < 1e-9 || len2 < 1e-9) continue;
            var cos = Math.Clamp(Dot(v1, v2) / (len1 * len2), -1, 1);
            var turnAngle = Math.Acos(cos);
            if (turnAngle < thresholdRad) continue;
            corners.Add(i);
            turns[i] = turnAngle;
        }
        return MergeNearbyCorners(points, corners, turns, windowDistance, wrap);
    }

    /// <summary>A real apex is rarely a single vertex: every vertex within a window of it turns sharply
    /// when the window reaches across the apex, so the loop above flags a small cluster for what is
    /// visually one corner. Each cluster (corners closer than `windowDistance` to the cluster's first
    /// member) collapses to its sharpest vertex, so the node lands on the apex itself and not on the
    /// first vertex that happened to see it.</summary>
    private static List<int> MergeNearbyCorners(
        IReadOnlyList<Position> points, List<int> corners, IReadOnlyDictionary<int, double> turns,
        double windowDistance, bool wrap)
    {
        if (corners.Count <= 1) return corners;

        var clusters = new List<List<int>> { new() { corners[0] } };
        for (var i = 1; i < corners.Count; i++)
        {
            var cluster = clusters[^1];
            if (Distance(points[cluster[0]], points[corners[i]]) < windowDistance) cluster.Add(corners[i]);
            else clusters.Add(new List<int> { corners[i] });
        }

        if (wrap && clusters.Count > 1 && Distance(points[clusters[0][0]], points[clusters[^1][0]]) < windowDistance)
        {
            clusters[0].AddRange(clusters[^1]);
            clusters.RemoveAt(clusters.Count - 1);
        }

        return clusters
            .Select(cluster => cluster.OrderByDescending(index => turns[index]).ThenBy(index => index).First())
            .OrderBy(index => index)
            .ToList();
    }

    /// <summary>Walks from `index` in `direction` (-1 or +1), accumulating arc length until at least
    /// `minDistance` has been covered, and returns the index reached. Stops at the chain boundary for
    /// an open polyline (returning `index` itself if there is nowhere to go) or after at most one full
    /// lap for a closed ring.</summary>
    private static int StepAlong(IReadOnlyList<Position> points, int index, int direction, double minDistance, bool wrap)
    {
        var count = points.Count;
        var current = index;
        var accumulated = 0d;
        for (var steps = 0; steps < count; steps++)
        {
            var next = wrap ? (current + direction + count) % count : current + direction;
            if (next < 0 || next >= count || next == index) break;
            accumulated += Distance(points[current], points[next]);
            current = next;
            if (accumulated >= minDistance) break;
        }
        return current;
    }

    /// <summary>Local forward-direction-of-travel estimate at `index`, using a small window of the
    /// nearest available neighbours on both sides — "average direction of the few neighbouring
    /// points", falling back to a direct chord when the chain is too short for a window.</summary>
    private static Position ForwardTangent(IReadOnlyList<Position> chain, int index)
    {
        var windowStart = Math.Max(0, index - 2);
        var windowEnd = Math.Min(chain.Count - 1, index + 2);
        if (windowStart == windowEnd)
        {
            windowStart = Math.Max(0, index - 1);
            windowEnd = Math.Min(chain.Count - 1, index + 1);
        }

        var direction = Sub(chain[windowEnd], chain[windowStart]);
        if (Length(direction) < 1e-9)
        {
            var nextIndex = Math.Min(chain.Count - 1, index + 1);
            var prevIndex = Math.Max(0, index - 1);
            direction = Sub(chain[nextIndex], chain[prevIndex]);
        }

        return Normalize(direction);
    }

    private static List<double> ChordLengthParameterize(IReadOnlyList<Position> chain)
    {
        var u = new double[chain.Count];
        var cumulative = 0d;
        for (var i = 1; i < chain.Count; i++)
        {
            cumulative += Distance(chain[i - 1], chain[i]);
            u[i] = cumulative;
        }

        if (cumulative < 1e-9)
        {
            for (var i = 0; i < u.Length; i++) u[i] = chain.Count <= 1 ? 0 : (double)i / (chain.Count - 1);
            return u.ToList();
        }

        for (var i = 0; i < u.Length; i++) u[i] /= cumulative;
        return u.ToList();
    }

    private static List<Position> SliceRing(IReadOnlyList<Position> points, int start, int end)
    {
        var result = new List<Position>();
        var index = start;
        while (true)
        {
            result.Add(points[index]);
            if (index == end) break;
            index = (index + 1) % points.Count;
        }
        return result;
    }

    private static int FarthestIndex(IReadOnlyList<Position> points, int from)
    {
        var best = (from + 1) % points.Count;
        var bestDistance = -1d;
        for (var i = 0; i < points.Count; i++)
        {
            if (i == from) continue;
            var distance = DistanceSquared(points[from], points[i]);
            if (distance <= bestDistance) continue;
            bestDistance = distance;
            best = i;
        }
        return best;
    }

    // -------------------------------------------------------------------------------------------
    // 2D vector helpers (Position.Z is always 0 for traced geometry — no operators exist on Position
    // itself, see Grbl/Position.cs, so these stay local to this file).
    // -------------------------------------------------------------------------------------------

    private static Position Add(Position a, Position b) => new(a.X + b.X, a.Y + b.Y, 0);
    private static Position Sub(Position a, Position b) => new(a.X - b.X, a.Y - b.Y, 0);
    private static Position Scale(Position a, double s) => new(a.X * s, a.Y * s, 0);
    private static Position Negate(Position a) => new(-a.X, -a.Y, 0);
    private static double Dot(Position a, Position b) => a.X * b.X + a.Y * b.Y;
    private static double Length(Position a) => Math.Sqrt(Dot(a, a));
    private static double Distance(Position a, Position b) => Length(Sub(a, b));
    private static double DistanceSquared(Position a, Position b)
    {
        var d = Sub(a, b);
        return Dot(d, d);
    }

    private static Position Normalize(Position a)
    {
        var length = Length(a);
        return length < 1e-9 ? new Position(0, 0, 0) : new Position(a.X / length, a.Y / length, 0);
    }
}
