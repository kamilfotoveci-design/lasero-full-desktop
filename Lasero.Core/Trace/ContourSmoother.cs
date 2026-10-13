namespace Lasero.Core.Trace;

/// <summary>A point of a sub-pixel contour, in source-image pixel coordinates (0,0 = top-left corner of
/// the image, x right, y down; pixel centres sit at half-integers).</summary>
internal readonly record struct ContourPoint(double X, double Y);

/// <summary>Resolved smoothing parameters, all in source pixels.</summary>
internal sealed record SmootherSettings(
    double SigmaPx,
    double CornerSigmaPx,
    double CornerWindowPx,
    double CornerAngleRad,
    double StraightTolerancePx,
    double MaxShiftPx);

/// <summary>
/// Turns one raw marching-squares loop into a clean closed polyline for the Bezier fitter.
///
/// 1. The loop is resampled at a uniform 0.5 px arc spacing.
/// 2. Corners are detected on a smoothed copy, as peaks of the turning angle over a window of several
///    pixels that is also still turning over twice that window and turns sharply over half of it. Pixel
///    staircases and round curves fail one of those tests; real corners pass all three.
/// 3. A corner is snapped to the intersection of two lines fitted to the contour on either side only when
///    the lines differ by at least 20 degrees and the intersection is within 1.5 px of the detected vertex.
/// 4. Between corners the contour is smoothed along its arc length by 2G - G(G), a Gaussian whose
///    low-frequency shrinkage is cancelled, so circles keep their radius while staircases vanish. Corners
///    are fixed (odd reflection). Smoothing that would move a point further than the allowed shift is
///    weakened until it fits.
/// 5. A chain becomes a single straight segment only when every smoothed point lies within the straight
///    tolerance of its chord.
/// 6. Guard: the result must stay within a couple of pixels of the raw contour everywhere along every
///    segment and keep its area. If not (a spike, a chord across a letter), the corner-aware result is
///    discarded for plain ring smoothing, and failing that for the raw contour.
/// </summary>
internal static class ContourSmoother
{
    private const double SampleSpacingPx = 0.5;
    private const double OutputSpacingPx = 1.0;
    private const double MinSmoothedPerimeterPx = 8;
    private const double SnapNearPx = 1.25;
    private const double SnapFarPx = 8;
    private const double SnapMaxShiftPx = 1.5;
    private const double SnapAcuteMaxShiftPx = 3.0;
    private const double SnapAcuteMaxResidualPx = 0.3;
    private const double SnapAcuteTurnRad = 100 * Math.PI / 180;
    private const double SnapMaxResidualPx = 0.6;
    private const double SnapMinSine = 0.342; // sin 20 degrees
    private const double SnapDropPx = 0.9;
    private const double GuardMinDistancePx = 3.5; // an acute apex may sit 3 px past the blunt contour
    private const double GuardAreaFraction = 0.03;
    private const double GuardAreaBandPx = 0.4;

    public static List<ContourPoint> Smooth(double[] xs, double[] ys, SmootherSettings settings)
    {
        var count = xs.Length;
        var raw = new List<ContourPoint>(count);
        for (var i = 0; i < count; i++) raw.Add(new ContourPoint(xs[i], ys[i]));

        var length = Perimeter(raw);
        if (settings.SigmaPx <= 0 || count < 4 || length < MinSmoothedPerimeterPx) return raw;

        var ring = ResampleRing(raw, length, out var step);
        var guard = new PolylineIndex(ring, Math.Max(GuardMinDistancePx, settings.MaxShiftPx + 0.5));

        var withCorners = SmoothWithCorners(ring, step, settings);
        if (withCorners is not null && guard.Accepts(withCorners, ring, length)) return withCorners;

        var plain = SmoothRing(ring, step, length, settings);
        if (guard.Accepts(plain, ring, length)) return plain;

        return raw;
    }

    private static List<ContourPoint> SmoothRing(ContourPoint[] ring, double step, double length, SmootherSettings settings)
    {
        var sigma = Math.Min(settings.SigmaPx, length / 16.0) / step;
        var smooth = SmoothWithin(ring, sigma, closed: true, settings.MaxShiftPx);
        return Thin(smooth, step, closed: true);
    }

    /// <summary>The corner-aware result, or null when the loop has no corners.</summary>
    private static List<ContourPoint>? SmoothWithCorners(ContourPoint[] ring, double step, SmootherSettings settings)
    {
        var m = ring.Length;
        var corners = DetectCorners(ring, step, settings);
        if (corners.Count == 0) return null;

        var cornerPositions = new ContourPoint[corners.Count];
        var drops = new int[corners.Count];
        for (var k = 0; k < corners.Count; k++)
        {
            var back = Gap(corners, k - 1, k, m);
            var forward = Gap(corners, k, k + 1, m);
            if (TrySnap(ring, step, corners[k], back, forward, out var position, out var shift))
            {
                cornerPositions[k] = position;
                drops[k] = (int)Math.Ceiling((SnapDropPx + shift) / step);
            }
            else
            {
                cornerPositions[k] = ring[corners[k]];
            }
        }

        var result = new List<ContourPoint>(ring.Length / 2);
        for (var k = 0; k < corners.Count; k++)
        {
            var next = (k + 1) % corners.Count;
            var chain = BuildChain(ring, corners[k], corners[next], corners.Count == 1,
                cornerPositions[k], cornerPositions[next], drops[k], drops[next]);
            var processed = ProcessChain(chain, settings);
            for (var i = 0; i < processed.Count - 1; i++) result.Add(processed[i]);
        }

        return result.Count >= 3 ? result : null;
    }

    private static double Perimeter(IReadOnlyList<ContourPoint> ring)
    {
        var total = 0d;
        for (var i = 0; i < ring.Count; i++) total += Distance(ring[i], ring[(i + 1) % ring.Count]);
        return total;
    }

    // -------------------------------------------------------------------------------------------
    // Resampling
    // -------------------------------------------------------------------------------------------

    private static ContourPoint[] ResampleRing(IReadOnlyList<ContourPoint> raw, double length, out double step)
    {
        var n = raw.Count;
        var m = Math.Max(16, (int)Math.Ceiling(length / SampleSpacingPx));
        step = length / m;
        var result = new ContourPoint[m];

        var segment = 0;
        var segmentStart = 0d;
        var segmentLength = Distance(raw[0], raw[1 % n]);
        for (var k = 0; k < m; k++)
        {
            var arc = k * step;
            while (segment < n - 1 && segmentStart + segmentLength < arc)
            {
                segmentStart += segmentLength;
                segment++;
                segmentLength = Distance(raw[segment], raw[(segment + 1) % n]);
            }

            var a = raw[segment];
            var b = raw[(segment + 1) % n];
            var t = segmentLength > 1e-12 ? Math.Clamp((arc - segmentStart) / segmentLength, 0, 1) : 0;
            result[k] = new ContourPoint(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t);
        }
        return result;
    }

    /// <summary>Resamples an open polyline at (nearly) uniform spacing, keeping both end points.</summary>
    private static ContourPoint[] ResampleOpen(IReadOnlyList<ContourPoint> points, out double spacing)
    {
        var length = 0d;
        for (var i = 1; i < points.Count; i++) length += Distance(points[i - 1], points[i]);
        var segments = Math.Max(1, (int)Math.Round(length / SampleSpacingPx));
        spacing = length / segments;
        var result = new ContourPoint[segments + 1];
        result[0] = points[0];
        result[segments] = points[^1];
        if (segments == 1 || length < 1e-12) return result;

        var segment = 0;
        var segmentStart = 0d;
        var segmentLength = Distance(points[0], points[1]);
        for (var k = 1; k < segments; k++)
        {
            var arc = k * spacing;
            while (segment < points.Count - 2 && segmentStart + segmentLength < arc)
            {
                segmentStart += segmentLength;
                segment++;
                segmentLength = Distance(points[segment], points[segment + 1]);
            }

            var a = points[segment];
            var b = points[segment + 1];
            var t = segmentLength > 1e-12 ? Math.Clamp((arc - segmentStart) / segmentLength, 0, 1) : 0;
            result[k] = new ContourPoint(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t);
        }
        return result;
    }


    private readonly record struct Line(double MeanX, double MeanY, double DirX, double DirY, double Residual);

    /// <summary>Least-squares (total) line through ring samples at offsets near..far from the corner.</summary>
    private static bool FitLine(ContourPoint[] ring, int corner, int direction, int near, int far, out Line line)
    {
        var m = ring.Length;
        var count = far - near + 1;
        double mx = 0, my = 0;
        for (var q = near; q <= far; q++)
        {
            var p = ring[(((corner + direction * q) % m) + m) % m];
            mx += p.X;
            my += p.Y;
        }
        mx /= count;
        my /= count;

        double sxx = 0, sxy = 0, syy = 0;
        for (var q = near; q <= far; q++)
        {
            var p = ring[(((corner + direction * q) % m) + m) % m];
            var ex = p.X - mx;
            var ey = p.Y - my;
            sxx += ex * ex;
            sxy += ex * ey;
            syy += ey * ey;
        }
        sxx /= count;
        sxy /= count;
        syy /= count;

        var angle = 0.5 * Math.Atan2(2 * sxy, sxx - syy);
        var half = (sxx + syy) / 2;
        var radius = Math.Sqrt(Math.Max(0, ((sxx - syy) / 2) * ((sxx - syy) / 2) + sxy * sxy));
        var minimumVariance = Math.Max(0, half - radius);
        line = new Line(mx, my, Math.Cos(angle), Math.Sin(angle), Math.Sqrt(minimumVariance));
        return true;
    }


    // -------------------------------------------------------------------------------------------
    // Corners
    // -------------------------------------------------------------------------------------------

    private static List<int> DetectCorners(ContourPoint[] ring, double step, SmootherSettings settings)
    {
        var m = ring.Length;
        var window = Math.Max(2, (int)Math.Round(settings.CornerWindowPx / step));
        var corners = new List<int>();
        if (m < 4 * window + 4) return corners;

        var detection = settings.CornerSigmaPx / step >= 0.3
            ? GaussianRing(ring, settings.CornerSigmaPx / step)
            : ring;

        var turns = new double[m];
        for (var i = 0; i < m; i++) turns[i] = TurnAt(detection, i, window);

        // A real corner turns over the window, keeps turning over a window twice as long, and turns almost
        // as much over half of it. A pixel step fails the long window; a round curve (turn growing with the
        // window) fails the half window. The long window is skipped when the loop is too short for it.
        var longWindow = window * 2;
        var halfWindow = Math.Max(1, window / 2);
        var confirm = m >= 4 * longWindow + 4;
        for (var i = 0; i < m; i++)
        {
            var turn = turns[i];
            if (turn < settings.CornerAngleRad) continue;
            if (confirm && TurnAt(detection, i, longWindow) < settings.CornerAngleRad * 0.5) continue;
            if (TurnAt(detection, i, halfWindow) < 0.55 * turn) continue;

            // Only the sharpest vertex of a run counts, but two corners a couple of pixels apart (the two
            // ends of a thin tip) stay two corners.
            var isPeak = true;
            for (var k = 1; k <= halfWindow && isPeak; k++)
            {
                if (turns[(i - k + m) % m] >= turn || turns[(i + k) % m] > turn) isPeak = false;
            }
            if (isPeak) corners.Add(i);
        }

        return corners;
    }

    private static double TurnAt(ContourPoint[] ring, int index, int window)
    {
        var m = ring.Length;
        var before = ring[(index - window + m) % m];
        var after = ring[(index + window) % m];
        var centre = ring[index];
        var v1x = centre.X - before.X;
        var v1y = centre.Y - before.Y;
        var v2x = after.X - centre.X;
        var v2y = after.Y - centre.Y;
        if (v1x * v1x + v1y * v1y < 1e-12 || v2x * v2x + v2y * v2y < 1e-12) return 0;
        return Math.Abs(Math.Atan2(v1x * v2y - v1y * v2x, v1x * v2x + v1y * v2y));
    }

    /// <summary>Samples from corner <paramref name="from"/> forward to corner <paramref name="to"/>
    /// (indices into the corner list, wrapped).</summary>
    private static int Gap(List<int> corners, int from, int to, int m)
    {
        var a = corners[((from % corners.Count) + corners.Count) % corners.Count];
        var b = corners[((to % corners.Count) + corners.Count) % corners.Count];
        var gap = ((b - a) % m + m) % m;
        return gap == 0 ? m : gap;
    }

    /// <summary>Snaps a corner to the intersection of the contour's two edge lines. Rejected unless both
    /// lines fit the contour well, differ by at least 20 degrees and meet within 1.5 px of the vertex;
    /// anything else keeps the smoothed vertex, because a far intersection is a spike.</summary>
    private static bool TrySnap(ContourPoint[] ring, double step, int corner, int backSamples, int forwardSamples, out ContourPoint snapped, out double shift)
    {
        snapped = default;
        shift = 0;
        var near = (int)Math.Ceiling(SnapNearPx / step);
        var far = (int)Math.Floor(SnapFarPx / step);
        var backFar = Math.Min(far, (int)(backSamples * 0.85));
        var forwardFar = Math.Min(far, (int)(forwardSamples * 0.85));
        if (backFar - near < 2 || forwardFar - near < 2) return false;

        if (!FitLine(ring, corner, -1, near, backFar, out var line1)) return false;
        if (!FitLine(ring, corner, 1, near, forwardFar, out var line2)) return false;
        if (line1.Residual > SnapMaxResidualPx || line2.Residual > SnapMaxResidualPx) return false;

        var cross = line1.DirX * line2.DirY - line1.DirY * line2.DirX;
        if (Math.Abs(cross) < SnapMinSine) return false;

        var dx = line2.MeanX - line1.MeanX;
        var dy = line2.MeanY - line1.MeanY;
        var t = (dx * line2.DirY - dy * line2.DirX) / cross;
        var point = new ContourPoint(line1.MeanX + line1.DirX * t, line1.MeanY + line1.DirY * t);
        if (!double.IsFinite(point.X) || !double.IsFinite(point.Y)) return false;

        shift = Distance(point, ring[corner]);
        var allowedShift = SnapMaxShiftPx;
        if (backFar - near >= 6 && forwardFar - near >= 6 &&
            line1.Residual <= SnapAcuteMaxResidualPx && line2.Residual <= SnapAcuteMaxResidualPx)
        {
            // A very sharp tip (the contour turns by more than 100 degrees) is blunted by sampling, so its true
            // apex lies well beyond the contour's own corner sample. When both edges are well supported the
            // apex is trusted further, so a pointed stem stays pointed instead of ending in a flat cut.
            var m = ring.Length;
            var towards = Sub(ring[((corner - near) % m + m) % m], ring[((corner - backFar) % m + m) % m]);
            var away = Sub(ring[(corner + forwardFar) % m], ring[(corner + near) % m]);
            var lengths = Math.Sqrt((towards.X * towards.X + towards.Y * towards.Y) * (away.X * away.X + away.Y * away.Y));
            if (lengths > 1e-12)
            {
                var turn = Math.Acos(Math.Clamp((towards.X * away.X + towards.Y * away.Y) / lengths, -1, 1));
                if (turn > SnapAcuteTurnRad) allowedShift = SnapAcuteMaxShiftPx;
            }
        }
        if (shift > allowedShift) return false;
        snapped = point;
        return true;
    }

    // -------------------------------------------------------------------------------------------
    // Chains between corners
    // -------------------------------------------------------------------------------------------

    private static List<ContourPoint> BuildChain(
        ContourPoint[] ring, int startCorner, int endCorner, bool singleCorner,
        ContourPoint startPosition, ContourPoint endPosition, int startDrop, int endDrop)
    {
        var m = ring.Length;
        var length = singleCorner ? m : ((endCorner - startCorner) % m + m) % m;

        // Samples right at a snapped corner are the rounded-off apex and are replaced by the snapped point,
        // but never more than a third of the chain from either end, so a short chain keeps its own shape.
        startDrop = Math.Min(startDrop, length / 3);
        endDrop = Math.Min(endDrop, length / 3);
        var first = startDrop > 0 ? startDrop : 1;
        var last = length - 1 - (endDrop > 0 ? endDrop - 1 : 0);

        var chain = new List<ContourPoint>(Math.Max(2, length)) { startPosition };
        for (var q = first; q <= last; q++) chain.Add(ring[(startCorner + q) % m]);
        chain.Add(endPosition);
        return chain;
    }

    private static List<ContourPoint> ProcessChain(List<ContourPoint> chain, SmootherSettings settings)
    {
        if (chain.Count <= 2) return chain;

        var resampled = ResampleOpen(chain, out var spacing);
        var segments = resampled.Length - 1;
        var length = spacing * segments;
        var tolerance = settings.StraightTolerancePx;
        if (segments < 4) return IsWithinChord(resampled, tolerance) ? Ends(resampled) : resampled.ToList();

        var sigmaPx = Math.Min(settings.SigmaPx, length / 6.0);
        var smooth = SmoothWithin(resampled, sigmaPx / spacing, closed: false, settings.MaxShiftPx);
        if (IsWithinChord(smooth, tolerance)) return Ends(smooth);

        // A long edge with notches of a pixel or two needs a longer kernel to be seen as straight.
        var heavyPx = Math.Min(settings.SigmaPx * 2, length / 4.0);
        if (heavyPx > sigmaPx * 1.2)
        {
            var heavy = SmoothWithin(resampled, heavyPx / spacing, closed: false, settings.MaxShiftPx);
            if (IsWithinChord(heavy, tolerance)) return Ends(heavy);
        }

        return Thin(smooth, spacing, closed: false);
    }

    private static List<ContourPoint> Ends(ContourPoint[] points) => new() { points[0], points[^1] };

    /// <summary>True when every point lies within the tolerance of the chord segment between the first and
    /// last point, the only condition under which a chain may become one straight segment.</summary>
    private static bool IsWithinChord(ContourPoint[] points, double tolerance)
    {
        var a = points[0];
        var b = points[^1];
        var abx = b.X - a.X;
        var aby = b.Y - a.Y;
        var lengthSquared = abx * abx + aby * aby;
        if (lengthSquared < 1e-12) return false;
        foreach (var p in points)
        {
            var t = Math.Clamp(((p.X - a.X) * abx + (p.Y - a.Y) * aby) / lengthSquared, 0, 1);
            var dx = p.X - (a.X + abx * t);
            var dy = p.Y - (a.Y + aby * t);
            if (dx * dx + dy * dy > tolerance * tolerance) return false;
        }
        return true;
    }

    private static List<ContourPoint> Thin(ContourPoint[] points, double spacing, bool closed)
    {
        var stride = Math.Max(1, (int)Math.Round(OutputSpacingPx / spacing));
        var result = new List<ContourPoint>(points.Length / stride + 2);
        for (var i = 0; i < points.Length; i += stride) result.Add(points[i]);
        if (!closed && (points.Length - 1) % stride != 0) result.Add(points[^1]);
        return result;
    }

    // -------------------------------------------------------------------------------------------
    // Gaussian smoothing along the arc
    // -------------------------------------------------------------------------------------------

    /// <summary>Smooths with the shrink-free kernel, halving it (up to three times) while it would move any
    /// sample further than <paramref name="maxShift"/>. Pixel staircases move a contour by a pixel or two;
    /// a larger move means the feature is thinner or tighter than the kernel, which then must not eat it.</summary>
    private static ContourPoint[] SmoothWithin(ContourPoint[] points, double sigmaSamples, bool closed, double maxShift)
    {
        for (var attempt = 0; attempt < 3 && sigmaSamples >= 0.5; attempt++)
        {
            var smooth = Compensate(points, sigmaSamples, closed);
            var shift = 0d;
            for (var i = 0; i < points.Length; i++) shift = Math.Max(shift, Distance(points[i], smooth[i]));
            if (shift <= maxShift) return smooth;
            sigmaSamples *= 0.5;
        }
        return points;
    }

    /// <summary>2G(P) - G(G(P)): transfer function 1 - (1 - g)^2, so slowly varying shape (a circle's
    /// curvature) is returned unchanged while samples-scale noise is removed.</summary>
    private static ContourPoint[] Compensate(ContourPoint[] points, double sigmaSamples, bool closed)
    {
        var once = closed ? GaussianRing(points, sigmaSamples) : GaussianOpen(points, sigmaSamples);
        var twice = closed ? GaussianRing(once, sigmaSamples) : GaussianOpen(once, sigmaSamples);
        var result = new ContourPoint[points.Length];
        for (var i = 0; i < points.Length; i++)
            result[i] = new ContourPoint(2 * once[i].X - twice[i].X, 2 * once[i].Y - twice[i].Y);
        return result;
    }

    // -------------------------------------------------------------------------------------------
    // Guard
    // -------------------------------------------------------------------------------------------

    /// <summary>Nearest-segment lookup over the raw (resampled) contour, on a grid whose cell is the
    /// distance limit, so a point with no segment in its 3 x 3 neighbourhood is further than the limit.</summary>
    private sealed class PolylineIndex
    {
        private readonly ContourPoint[] _ring;
        private readonly double _limit;
        private readonly Dictionary<long, List<int>> _cells = new();

        public PolylineIndex(ContourPoint[] ring, double limit)
        {
            _ring = ring;
            _limit = limit;
            for (var i = 0; i < ring.Length; i++)
            {
                Add(i, ring[i]);
                var next = ring[(i + 1) % ring.Length];
                if (CellX(next.X) != CellX(ring[i].X) || CellY(next.Y) != CellY(ring[i].Y)) Add(i, next);
            }
        }

        private int CellX(double x) => (int)Math.Floor(x / _limit);
        private int CellY(double y) => (int)Math.Floor(y / _limit);
        private static long Key(int cx, int cy) => (cx + 1_000_000L) * 4_000_000L + (cy + 1_000_000L);

        private void Add(int segment, ContourPoint p)
        {
            var key = Key(CellX(p.X), CellY(p.Y));
            if (!_cells.TryGetValue(key, out var list)) _cells[key] = list = new List<int>();
            if (list.Count == 0 || list[^1] != segment) list.Add(segment);
        }

        /// <summary>Distance from a point to the raw contour, or infinity beyond the limit.</summary>
        private double DistanceTo(ContourPoint p)
        {
            var cx = CellX(p.X);
            var cy = CellY(p.Y);
            var best = double.PositiveInfinity;
            for (var dx = -1; dx <= 1; dx++)
            {
                for (var dy = -1; dy <= 1; dy++)
                {
                    if (!_cells.TryGetValue(Key(cx + dx, cy + dy), out var list)) continue;
                    foreach (var segment in list)
                    {
                        var a = _ring[segment];
                        var b = _ring[(segment + 1) % _ring.Length];
                        var abx = b.X - a.X;
                        var aby = b.Y - a.Y;
                        var lengthSquared = abx * abx + aby * aby;
                        var t = lengthSquared < 1e-18 ? 0 : Math.Clamp(((p.X - a.X) * abx + (p.Y - a.Y) * aby) / lengthSquared, 0, 1);
                        var ex = p.X - (a.X + abx * t);
                        var ey = p.Y - (a.Y + aby * t);
                        best = Math.Min(best, Math.Sqrt(ex * ex + ey * ey));
                    }
                }
            }
            return best;
        }

        /// <summary>True when the candidate polygon stays within the limit of the raw contour along every
        /// segment (sampled every pixel, so a chord across a letter is caught) and keeps its area.</summary>
        public bool Accepts(List<ContourPoint> candidate, ContourPoint[] raw, double rawPerimeter)
        {
            if (candidate.Count < 3) return false;
            for (var i = 0; i < candidate.Count; i++)
            {
                var a = candidate[i];
                var b = candidate[(i + 1) % candidate.Count];
                if (!double.IsFinite(a.X) || !double.IsFinite(a.Y)) return false;
                var distance = Distance(a, b);
                var steps = Math.Max(1, (int)Math.Ceiling(distance / 1.0));
                for (var s = 0; s <= steps; s++)
                {
                    var t = s / (double)steps;
                    var p = new ContourPoint(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t);
                    if (DistanceTo(p) > _limit) return false;
                }
            }

            var rawArea = Math.Abs(Area(raw));
            var change = Math.Abs(Math.Abs(Area(candidate)) - rawArea);
            return change <= GuardAreaFraction * rawArea + GuardAreaBandPx * rawPerimeter;
        }

        private static double Area(IReadOnlyList<ContourPoint> points)
        {
            var area = 0d;
            for (var i = 0; i < points.Count; i++)
            {
                var a = points[i];
                var b = points[(i + 1) % points.Count];
                area += a.X * b.Y - b.X * a.Y;
            }
            return area / 2;
        }
    }

    // -------------------------------------------------------------------------------------------
    // Gaussian kernels
    // -------------------------------------------------------------------------------------------

    private static double[] Kernel(double sigma, out int radius)
    {
        radius = Math.Max(1, (int)Math.Ceiling(sigma * 3));
        var kernel = new double[radius + 1];
        var sum = 0d;
        for (var k = 0; k <= radius; k++)
        {
            kernel[k] = Math.Exp(-(k * k) / (2 * sigma * sigma));
            sum += k == 0 ? kernel[k] : 2 * kernel[k];
        }
        for (var k = 0; k <= radius; k++) kernel[k] /= sum;
        return kernel;
    }

    private static ContourPoint[] GaussianRing(ContourPoint[] points, double sigma)
    {
        var m = points.Length;
        var kernel = Kernel(sigma, out var radius);
        radius = Math.Min(radius, m / 2);
        var result = new ContourPoint[m];
        for (var i = 0; i < m; i++)
        {
            var x = kernel[0] * points[i].X;
            var y = kernel[0] * points[i].Y;
            for (var k = 1; k <= radius; k++)
            {
                var a = points[(i - k + m) % m];
                var b = points[(i + k) % m];
                x += kernel[k] * (a.X + b.X);
                y += kernel[k] * (a.Y + b.Y);
            }
            result[i] = new ContourPoint(x, y);
        }
        return result;
    }

    /// <summary>Gaussian smoothing of an open chain whose end points stay exactly where they are:
    /// beyond an end the chain continues as the point reflection of its own interior.</summary>
    private static ContourPoint[] GaussianOpen(ContourPoint[] points, double sigma)
    {
        var last = points.Length - 1;
        var kernel = Kernel(sigma, out var radius);
        radius = Math.Min(radius, last);
        var result = new ContourPoint[points.Length];
        result[0] = points[0];
        result[last] = points[last];
        for (var i = 1; i < last; i++)
        {
            var x = kernel[0] * points[i].X;
            var y = kernel[0] * points[i].Y;
            for (var k = 1; k <= radius; k++)
            {
                var a = Reflected(points, i - k, last);
                var b = Reflected(points, i + k, last);
                x += kernel[k] * (a.X + b.X);
                y += kernel[k] * (a.Y + b.Y);
            }
            result[i] = new ContourPoint(x, y);
        }
        return result;
    }

    private static ContourPoint Reflected(ContourPoint[] points, int index, int last)
    {
        if (index < 0)
        {
            var mirror = points[Math.Min(-index, last)];
            return new ContourPoint(2 * points[0].X - mirror.X, 2 * points[0].Y - mirror.Y);
        }
        if (index > last)
        {
            var mirror = points[Math.Max(2 * last - index, 0)];
            return new ContourPoint(2 * points[last].X - mirror.X, 2 * points[last].Y - mirror.Y);
        }
        return points[index];
    }

    private static ContourPoint Sub(ContourPoint a, ContourPoint b) => new(a.X - b.X, a.Y - b.Y);

    private static double Distance(ContourPoint a, ContourPoint b)
    {
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }
}
