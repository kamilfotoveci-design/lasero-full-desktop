using Lasero.Core.Grbl;

namespace Lasero.Core.Scene;

/// <summary>One segment of a vector path as a cubic: a straight segment is stored with its control
/// points at 1/3 and 2/3 of the chord, which draws exactly that line with a LINEAR parameter, so a
/// parameter found here is the same t <see cref="VectorPathEditor.InsertNode"/> splits at.</summary>
public readonly record struct BezierSegment(Position P0, Position P1, Position P2, Position P3, bool IsLine)
{
    public static BezierSegment Line(Position from, Position to) =>
        new(from, CubicBezier.Lerp(from, to, 1.0 / 3), CubicBezier.Lerp(from, to, 2.0 / 3), to, true);

    public static BezierSegment FromNodes(VectorNode a, VectorNode b) =>
        VectorSubpath.IsStraightSegment(a, b)
            ? Line(a.Anchor, b.Anchor)
            : new BezierSegment(a.Anchor, a.HandleOut ?? a.Anchor, b.HandleIn ?? b.Anchor, b.Anchor, false);

    public Position Evaluate(double t) => IsLine ? CubicBezier.Lerp(P0, P3, t) : CubicBezier.Evaluate(P0, P1, P2, P3, t);

    /// <summary>Maps the control points through an affine transform (object local to world). Bézier
    /// curves are affine-invariant, so parameters stay valid across the mapping.</summary>
    public BezierSegment Map(Func<Position, Position> affine) => new(affine(P0), affine(P1), affine(P2), affine(P3), IsLine);

    public (double MinX, double MinY, double MaxX, double MaxY) Bounds() => (
        Math.Min(Math.Min(P0.X, P1.X), Math.Min(P2.X, P3.X)),
        Math.Min(Math.Min(P0.Y, P1.Y), Math.Min(P2.Y, P3.Y)),
        Math.Max(Math.Max(P0.X, P1.X), Math.Max(P2.X, P3.X)),
        Math.Max(Math.Max(P0.Y, P1.Y), Math.Max(P2.Y, P3.Y)));

    public bool IsFinite =>
        double.IsFinite(P0.X) && double.IsFinite(P0.Y) && double.IsFinite(P1.X) && double.IsFinite(P1.Y) &&
        double.IsFinite(P2.X) && double.IsFinite(P2.Y) && double.IsFinite(P3.X) && double.IsFinite(P3.Y);
}

/// <summary>A crossing of two segments: <see cref="T"/> on the first, <see cref="U"/> on the second.</summary>
public readonly record struct SegmentIntersection(double T, double U, Position Point);

/// <summary>
/// Exact-enough intersections between line and cubic segments, WPF-free. Line-line is closed form,
/// line-cubic solves the cubic polynomial of the curve's signed distance to the line, cubic-cubic
/// subdivides both curves (De Casteljau) while their control hulls overlap and intersects the chords
/// once both pieces are flat to 1e-7 mm. Parameters are accurate to far below any laser tolerance,
/// which is what Trim needs to split a curve exactly where another one crosses it.
/// </summary>
public static class BezierIntersection
{
    private const double ParameterEpsilon = 1e-9;
    private const double FlatnessMm = 1e-7;
    private const int MaxDepth = 48;
    private const int MaxWork = 20000;

    public static List<SegmentIntersection> Intersect(BezierSegment a, BezierSegment b)
    {
        var results = new List<SegmentIntersection>();
        if (!a.IsFinite || !b.IsFinite || !BoundsOverlap(a.Bounds(), b.Bounds(), 1e-9)) return results;

        if (a.IsLine && b.IsLine)
        {
            if (TryLineLine(a.P0, a.P3, b.P0, b.P3, out var t, out var u))
                results.Add(new SegmentIntersection(t, u, CubicBezier.Lerp(a.P0, a.P3, t)));
        }
        else if (a.IsLine)
        {
            foreach (var (lineT, curveU, point) in LineCubic(a.P0, a.P3, b)) results.Add(new SegmentIntersection(lineT, curveU, point));
        }
        else if (b.IsLine)
        {
            foreach (var (lineT, curveU, point) in LineCubic(b.P0, b.P3, a)) results.Add(new SegmentIntersection(curveU, lineT, point));
        }
        else
        {
            var work = 0;
            var estimates = new List<SegmentIntersection>();
            CubicCubic(a, 0, 1, b, 0, 1, 0, estimates, ref work);
            // A flat piece's chord is not parameterised like the curve itself, so the estimate is
            // polished with Newton on the two ORIGINAL curves before it is used to split anything.
            foreach (var estimate in estimates) results.Add(Refine(a, b, estimate));
        }

        return Deduplicate(results);
    }

    private static SegmentIntersection Refine(BezierSegment a, BezierSegment b, SegmentIntersection estimate)
    {
        var t = estimate.T;
        var u = estimate.U;
        for (var iteration = 0; iteration < 20; iteration++)
        {
            var pa = a.Evaluate(t);
            var pb = b.Evaluate(u);
            var fx = pa.X - pb.X;
            var fy = pa.Y - pb.Y;
            if (fx * fx + fy * fy < 1e-24) break;
            var (dax, day) = Derivative(a, t);
            var (dbx, dby) = Derivative(b, u);
            // Newton step: solve [da, -db] [dt, du]^T = -f by Cramer's rule.
            var det = day * dbx - dax * dby;
            if (Math.Abs(det) < 1e-18) return estimate;
            var dt = (fx * dby - fy * dbx) / det;
            var du = (fx * day - fy * dax) / det;
            t += dt;
            u += du;
            if (!double.IsFinite(t) || !double.IsFinite(u)) return estimate;
        }

        if (t < -ParameterEpsilon || t > 1 + ParameterEpsilon || u < -ParameterEpsilon || u > 1 + ParameterEpsilon) return estimate;
        t = Math.Clamp(t, 0, 1);
        u = Math.Clamp(u, 0, 1);
        if (Distance(a.Evaluate(t), b.Evaluate(u)) > 1e-6 || Distance(a.Evaluate(t), estimate.Point) > 1e-2) return estimate;
        return new SegmentIntersection(t, u, a.Evaluate(t));
    }

    private static (double X, double Y) Derivative(BezierSegment s, double t)
    {
        if (s.IsLine) return (s.P3.X - s.P0.X, s.P3.Y - s.P0.Y);
        var m = 1 - t;
        return (
            3 * (m * m * (s.P1.X - s.P0.X) + 2 * m * t * (s.P2.X - s.P1.X) + t * t * (s.P3.X - s.P2.X)),
            3 * (m * m * (s.P1.Y - s.P0.Y) + 2 * m * t * (s.P2.Y - s.P1.Y) + t * t * (s.P3.Y - s.P2.Y)));
    }

    /// <summary>Intersections of a ray from <paramref name="origin"/> along <paramref name="direction"/>
    /// (any length; distances are returned in the same units) with a segment, as distances along the
    /// ray. Used by Extend. <paramref name="reach"/> bounds the search.</summary>
    public static IEnumerable<double> RayDistances(Position origin, (double X, double Y) direction, double reach, BezierSegment target)
    {
        var length = Math.Sqrt(direction.X * direction.X + direction.Y * direction.Y);
        if (length < 1e-12 || !double.IsFinite(reach) || reach <= 0) yield break;
        var ux = direction.X / length;
        var uy = direction.Y / length;
        var end = new Position(origin.X + ux * reach, origin.Y + uy * reach, origin.Z);
        foreach (var hit in Intersect(BezierSegment.Line(origin, end), target))
            yield return hit.T * reach;
    }

    // ------------------------------------------------------------------ line / line

    private static bool TryLineLine(Position p1, Position p2, Position p3, Position p4, out double t, out double u)
    {
        t = u = 0;
        var d1X = p2.X - p1.X;
        var d1Y = p2.Y - p1.Y;
        var d2X = p4.X - p3.X;
        var d2Y = p4.Y - p3.Y;
        var denominator = d1X * d2Y - d1Y * d2X;
        var scale = Math.Sqrt((d1X * d1X + d1Y * d1Y) * (d2X * d2X + d2Y * d2Y));
        if (scale < 1e-24 || Math.Abs(denominator) < 1e-12 * scale) return false; // parallel / collinear: no single point

        t = ((p3.X - p1.X) * d2Y - (p3.Y - p1.Y) * d2X) / denominator;
        u = ((p3.X - p1.X) * d1Y - (p3.Y - p1.Y) * d1X) / denominator;
        if (t < -ParameterEpsilon || t > 1 + ParameterEpsilon || u < -ParameterEpsilon || u > 1 + ParameterEpsilon) return false;
        t = Math.Clamp(t, 0, 1);
        u = Math.Clamp(u, 0, 1);
        return true;
    }

    // ------------------------------------------------------------------ line / cubic

    private static IEnumerable<(double LineT, double CurveU, Position Point)> LineCubic(Position l0, Position l1, BezierSegment curve)
    {
        var dx = l1.X - l0.X;
        var dy = l1.Y - l0.Y;
        var lengthSquared = dx * dx + dy * dy;
        if (lengthSquared < 1e-24) yield break;
        var length = Math.Sqrt(lengthSquared);
        var nx = dx / length;
        var ny = dy / length;

        // Signed distance of B(u) from the line, as a cubic polynomial in u.
        double D(Position p) => (p.X - l0.X) * ny - (p.Y - l0.Y) * nx;
        var d0 = D(curve.P0);
        var d1 = D(curve.P1);
        var d2 = D(curve.P2);
        var d3 = D(curve.P3);
        var a = -d0 + 3 * d1 - 3 * d2 + d3;
        var b = 3 * d0 - 6 * d1 + 3 * d2;
        var c = -3 * d0 + 3 * d1;
        var d = d0;

        foreach (var u in RootsInUnitInterval(a, b, c, d))
        {
            var point = curve.Evaluate(u);
            var lineT = ((point.X - l0.X) * dx + (point.Y - l0.Y) * dy) / lengthSquared;
            if (lineT < -ParameterEpsilon || lineT > 1 + ParameterEpsilon) continue;
            yield return (Math.Clamp(lineT, 0, 1), u, point);
        }
    }

    /// <summary>Real roots of a·u³ + b·u² + c·u + d in [0, 1]: the interval is cut at the critical
    /// points (roots of the derivative) into monotonic pieces, each bisected where it changes sign. A
    /// touching (double) root shows up as a critical point with a near-zero value and is kept.</summary>
    private static List<double> RootsInUnitInterval(double a, double b, double c, double d)
    {
        double F(double u) => ((a * u + b) * u + c) * u + d;
        var magnitude = Math.Abs(a) + Math.Abs(b) + Math.Abs(c) + Math.Abs(d);
        var roots = new List<double>();
        if (magnitude < 1e-15) return roots; // the curve lies on the line: overlap, no single crossing

        var cuts = new List<double> { 0, 1 };
        // Derivative 3a u² + 2b u + c.
        var qa = 3 * a;
        var qb = 2 * b;
        var qc = c;
        if (Math.Abs(qa) > 1e-15 * magnitude)
        {
            var disc = qb * qb - 4 * qa * qc;
            if (disc >= 0)
            {
                var sq = Math.Sqrt(disc);
                cuts.Add((-qb + sq) / (2 * qa));
                cuts.Add((-qb - sq) / (2 * qa));
            }
        }
        else if (Math.Abs(qb) > 1e-15 * magnitude)
        {
            cuts.Add(-qc / qb);
        }
        cuts = cuts.Where(u => u >= 0 && u <= 1 && double.IsFinite(u)).Distinct().OrderBy(u => u).ToList();

        var touchTolerance = 1e-9 * Math.Max(1, magnitude);
        for (var i = 0; i < cuts.Count; i++)
        {
            var value = F(cuts[i]);
            if (Math.Abs(value) <= touchTolerance) roots.Add(cuts[i]);
            if (i == cuts.Count - 1) break;

            var lo = cuts[i];
            var hi = cuts[i + 1];
            var flo = value;
            var fhi = F(hi);
            if (Math.Abs(flo) <= touchTolerance || Math.Abs(fhi) <= touchTolerance || Math.Sign(flo) == Math.Sign(fhi)) continue;
            for (var iteration = 0; iteration < 80 && hi - lo > 1e-15; iteration++)
            {
                var mid = (lo + hi) / 2;
                var fm = F(mid);
                if (Math.Sign(fm) == Math.Sign(flo)) { lo = mid; flo = fm; }
                else hi = mid;
            }
            roots.Add((lo + hi) / 2);
        }
        return roots;
    }

    // ------------------------------------------------------------------ cubic / cubic

    private static void CubicCubic(BezierSegment a, double a0, double a1, BezierSegment b, double b0, double b1,
        int depth, List<SegmentIntersection> results, ref int work)
    {
        if (++work > MaxWork) return;
        var boundsA = a.Bounds();
        var boundsB = b.Bounds();
        if (!BoundsOverlap(boundsA, boundsB, FlatnessMm)) return;

        var flatA = IsFlat(a);
        var flatB = IsFlat(b);
        if ((flatA && flatB) || depth >= MaxDepth)
        {
            if (TryLineLine(a.P0, a.P3, b.P0, b.P3, out var t, out var u))
            {
                var ta = a0 + (a1 - a0) * t;
                var ub = b0 + (b1 - b0) * u;
                results.Add(new SegmentIntersection(ta, ub, CubicBezier.Lerp(a.P0, a.P3, t)));
            }
            return;
        }

        var aMid = (a0 + a1) / 2;
        var bMid = (b0 + b1) / 2;
        if (flatA)
        {
            var (bl, br) = Split(b);
            CubicCubic(a, a0, a1, bl, b0, bMid, depth + 1, results, ref work);
            CubicCubic(a, a0, a1, br, bMid, b1, depth + 1, results, ref work);
            return;
        }
        if (flatB)
        {
            var (al, ar) = Split(a);
            CubicCubic(al, a0, aMid, b, b0, b1, depth + 1, results, ref work);
            CubicCubic(ar, aMid, a1, b, b0, b1, depth + 1, results, ref work);
            return;
        }

        var (aLeft, aRight) = Split(a);
        var (bLeft, bRight) = Split(b);
        CubicCubic(aLeft, a0, aMid, bLeft, b0, bMid, depth + 1, results, ref work);
        CubicCubic(aLeft, a0, aMid, bRight, bMid, b1, depth + 1, results, ref work);
        CubicCubic(aRight, aMid, a1, bLeft, b0, bMid, depth + 1, results, ref work);
        CubicCubic(aRight, aMid, a1, bRight, bMid, b1, depth + 1, results, ref work);
    }

    private static (BezierSegment Left, BezierSegment Right) Split(BezierSegment s)
    {
        var split = CubicBezier.Split(s.P0, s.P1, s.P2, s.P3, 0.5);
        return (new BezierSegment(split.P0, split.C1, split.C2, split.P1, false),
                new BezierSegment(split.Q0, split.D1, split.D2, split.Q1, false));
    }

    private static bool IsFlat(BezierSegment s)
    {
        var dx = s.P3.X - s.P0.X;
        var dy = s.P3.Y - s.P0.Y;
        var length = Math.Sqrt(dx * dx + dy * dy);
        if (length < 1e-12)
            return Distance(s.P1, s.P0) <= FlatnessMm && Distance(s.P2, s.P0) <= FlatnessMm;
        var d1 = Math.Abs((s.P1.X - s.P0.X) * dy - (s.P1.Y - s.P0.Y) * dx) / length;
        var d2 = Math.Abs((s.P2.X - s.P0.X) * dy - (s.P2.Y - s.P0.Y) * dx) / length;
        return d1 <= FlatnessMm && d2 <= FlatnessMm;
    }

    private static bool BoundsOverlap(
        (double MinX, double MinY, double MaxX, double MaxY) a,
        (double MinX, double MinY, double MaxX, double MaxY) b, double slack) =>
        a.MinX <= b.MaxX + slack && b.MinX <= a.MaxX + slack && a.MinY <= b.MaxY + slack && b.MinY <= a.MaxY + slack;

    private static List<SegmentIntersection> Deduplicate(List<SegmentIntersection> hits)
    {
        if (hits.Count < 2) return hits;
        var ordered = hits.OrderBy(hit => hit.T).ToList();
        var result = new List<SegmentIntersection> { ordered[0] };
        foreach (var hit in ordered.Skip(1))
        {
            var last = result[^1];
            if (Math.Abs(hit.T - last.T) < 1e-7 && Math.Abs(hit.U - last.U) < 1e-7) continue;
            if (Distance(hit.Point, last.Point) < 1e-6) continue;
            result.Add(hit);
        }
        return result;
    }

    private static double Distance(Position a, Position b) =>
        Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));
}
