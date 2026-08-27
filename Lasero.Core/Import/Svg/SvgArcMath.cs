namespace Lasero.Core.Import.Svg;

/// <summary>
/// SVG elliptical arc (the `A`/`a` path command) endpoint-to-center
/// conversion, per the SVG 1.1 spec appendix F.6.5 — the standard algorithm,
/// not a shortcut. Needed because the path command only gives radii + flags,
/// not the actual ellipse center our sampler needs to walk the arc.
/// </summary>
internal static class SvgArcMath
{
    public static IEnumerable<(double X, double Y)> FlattenArc(
        double x1, double y1, double rx, double ry, double xAxisRotationDeg,
        bool largeArcFlag, bool sweepFlag, double x2, double y2, int steps)
    {
        rx = Math.Abs(rx);
        ry = Math.Abs(ry);

        if (rx < 1e-9 || ry < 1e-9 || (Math.Abs(x1 - x2) < 1e-9 && Math.Abs(y1 - y2) < 1e-9))
        {
            yield return (x2, y2);
            yield break;
        }

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

        for (int i = 1; i <= steps; i++)
        {
            var t = theta1 + deltaTheta * i / steps;
            var ex = rx * Math.Cos(t);
            var ey = ry * Math.Sin(t);
            yield return (cosPhi * ex - sinPhi * ey + cx, sinPhi * ex + cosPhi * ey + cy);
        }
    }
}
