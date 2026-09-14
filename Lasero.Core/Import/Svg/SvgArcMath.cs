namespace Lasero.Core.Import.Svg;

/// <summary>
/// SVG elliptical arc (the `A`/`a` path command) endpoint-to-center
/// conversion, per the SVG 1.1 spec appendix F.6.5 — the standard algorithm,
/// not a shortcut. Needed because the path command only gives radii + flags,
/// not the actual ellipse center our sampler needs to walk the arc.
/// </summary>
internal static class SvgArcMath
{
    /// <summary>Full center parameterization of one elliptical arc segment (spec appendix F.6.5,
    /// steps 1-4: out-of-range radii already corrected). Null means the arc degenerates to a
    /// straight line — either endpoint coincide (spec F.6.2: "if the endpoints are identical, then
    /// this is equivalent to omitting the elliptical arc segment entirely") or a radius is
    /// (numerically) zero, which the same spec clause treats the same way.</summary>
    public readonly record struct ArcParameters(double Cx, double Cy, double Phi, double Rx, double Ry, double Theta1, double DeltaTheta);

    public static ArcParameters? ComputeParameters(
        double x1, double y1, double rx, double ry, double xAxisRotationDeg,
        bool largeArcFlag, bool sweepFlag, double x2, double y2)
    {
        rx = Math.Abs(rx);
        ry = Math.Abs(ry);

        if (rx < 1e-9 || ry < 1e-9 || (Math.Abs(x1 - x2) < 1e-9 && Math.Abs(y1 - y2) < 1e-9))
            return null;

        var phi = xAxisRotationDeg * Math.PI / 180.0;
        var cosPhi = Math.Cos(phi);
        var sinPhi = Math.Sin(phi);

        // Step 1: compute (x1', y1') — midpoint-relative, rotated into the ellipse's frame.
        var dx2 = (x1 - x2) / 2.0;
        var dy2 = (y1 - y2) / 2.0;
        var x1P = cosPhi * dx2 + sinPhi * dy2;
        var y1P = -sinPhi * dx2 + cosPhi * dy2;

        // Step 2: correct out-of-range radii.
        var lambda = (x1P * x1P) / (rx * rx) + (y1P * y1P) / (ry * ry);
        if (lambda > 1)
        {
            var scale = Math.Sqrt(lambda);
            rx *= scale;
            ry *= scale;
        }

        // Step 3: compute (cx', cy').
        var sign = largeArcFlag == sweepFlag ? -1.0 : 1.0;
        var rxSq = rx * rx;
        var rySq = ry * ry;
        var x1PSq = x1P * x1P;
        var y1PSq = y1P * y1P;
        var num = rxSq * rySq - rxSq * y1PSq - rySq * x1PSq;
        var den = rxSq * y1PSq + rySq * x1PSq;
        var coef = sign * Math.Sqrt(Math.Max(0, num / den));
        var cxP = coef * (rx * y1P / ry);
        var cyP = coef * (-ry * x1P / rx);

        // Step 4: transform back to get the actual center.
        var cx = cosPhi * cxP - sinPhi * cyP + (x1 + x2) / 2.0;
        var cy = sinPhi * cxP + cosPhi * cyP + (y1 + y2) / 2.0;

        // Step 5: start angle and sweep angle.
        double Angle(double ux, double uy, double vx, double vy)
        {
            var dot = ux * vx + uy * vy;
            var len = Math.Sqrt((ux * ux + uy * uy) * (vx * vx + vy * vy));
            var a = Math.Acos(Math.Clamp(dot / len, -1, 1));
            return (ux * vy - uy * vx) < 0 ? -a : a;
        }

        var theta1 = Angle(1, 0, (x1P - cxP) / rx, (y1P - cyP) / ry);
        var deltaTheta = Angle((x1P - cxP) / rx, (y1P - cyP) / ry, (-x1P - cxP) / rx, (-y1P - cyP) / ry);

        if (!sweepFlag && deltaTheta > 0) deltaTheta -= 2 * Math.PI;
        if (sweepFlag && deltaTheta < 0) deltaTheta += 2 * Math.PI;

        return new ArcParameters(cx, cy, phi, rx, ry, theta1, deltaTheta);
    }

    public static IEnumerable<(double X, double Y)> FlattenArc(
        double x1, double y1, double rx, double ry, double xAxisRotationDeg,
        bool largeArcFlag, bool sweepFlag, double x2, double y2, int steps)
    {
        var p = ComputeParameters(x1, y1, rx, ry, xAxisRotationDeg, largeArcFlag, sweepFlag, x2, y2);
        if (p is not { } arc)
        {
            yield return (x2, y2);
            yield break;
        }

        var cosPhi = Math.Cos(arc.Phi);
        var sinPhi = Math.Sin(arc.Phi);
        for (int i = 1; i <= steps; i++)
        {
            var t = arc.Theta1 + arc.DeltaTheta * i / steps;
            var ex = arc.Rx * Math.Cos(t);
            var ey = arc.Ry * Math.Sin(t);
            yield return (cosPhi * ex - sinPhi * ey + arc.Cx, sinPhi * ex + cosPhi * ey + arc.Cy);
        }
    }

    /// <summary>Exact-form cubic Bézier approximation of one elliptical arc, split into segments of
    /// at most 90° each (the standard practice for good fidelity — a single cubic cannot represent
    /// more than roughly a quarter-turn without visible error). Each returned tuple is
    /// (C1, C2, End) in the same coordinate space as x1/y1/x2/y2; the caller already knows the
    /// segment's start point (x1,y1) for the first segment, End for one segment is the start for
    /// the next. Returns empty when the arc degenerates to a straight line (see ComputeParameters) —
    /// the caller is expected to fall back to a plain line-to in that case, per the SVG spec.</summary>
    public static IEnumerable<((double X, double Y) C1, (double X, double Y) C2, (double X, double Y) End)> ArcToBezierSegments(
        double x1, double y1, double rx, double ry, double xAxisRotationDeg,
        bool largeArcFlag, bool sweepFlag, double x2, double y2)
    {
        var p = ComputeParameters(x1, y1, rx, ry, xAxisRotationDeg, largeArcFlag, sweepFlag, x2, y2);
        if (p is not { } arc) yield break;

        var cosPhi = Math.Cos(arc.Phi);
        var sinPhi = Math.Sin(arc.Phi);
        (double X, double Y) ToWorld(double ex, double ey) =>
            (cosPhi * ex - sinPhi * ey + arc.Cx, sinPhi * ex + cosPhi * ey + arc.Cy);

        var segmentCount = Math.Max(1, (int)Math.Ceiling(Math.Abs(arc.DeltaTheta) / (Math.PI / 2)));
        var segmentDelta = arc.DeltaTheta / segmentCount;

        for (var i = 0; i < segmentCount; i++)
        {
            var theta1 = arc.Theta1 + segmentDelta * i;
            var theta2 = theta1 + segmentDelta;
            var alpha = 4.0 / 3.0 * Math.Tan(segmentDelta / 4.0);

            var p0 = (X: Math.Cos(theta1), Y: Math.Sin(theta1));
            var p3 = (X: Math.Cos(theta2), Y: Math.Sin(theta2));
            var c1Unit = (X: p0.X - alpha * Math.Sin(theta1), Y: p0.Y + alpha * Math.Cos(theta1));
            var c2Unit = (X: p3.X + alpha * Math.Sin(theta2), Y: p3.Y - alpha * Math.Cos(theta2));

            var c1 = ToWorld(arc.Rx * c1Unit.X, arc.Ry * c1Unit.Y);
            var c2 = ToWorld(arc.Rx * c2Unit.X, arc.Ry * c2Unit.Y);
            var end = ToWorld(arc.Rx * p3.X, arc.Ry * p3.Y);

            yield return (c1, c2, end);
        }
    }
}
