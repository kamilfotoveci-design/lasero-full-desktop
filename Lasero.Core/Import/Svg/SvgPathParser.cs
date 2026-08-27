namespace Lasero.Core.Import.Svg;

/// <summary>
/// Flattens an SVG path `d` attribute into polylines (subpaths of straight
/// line segments). Curves are sampled at a fixed step count per segment
/// rather than adaptively by arc-length — simpler to get right, and at laser
/// resolution (sub-mm) the difference is invisible. Supports all standard
/// commands including the S/T shorthand-reflection forms.
/// </summary>
internal static class SvgPathParser
{
    private const int CurveSteps = 16;

    public static List<SvgSubpath> Flatten(string d)
    {
        var subpaths = new List<SvgSubpath>();
        var tokenizer = new SvgPathTokenizer(d);

        double curX = 0, curY = 0;
        double subpathStartX = 0, subpathStartY = 0;
        double? prevCubicCtrlX = null, prevCubicCtrlY = null;
        double? prevQuadCtrlX = null, prevQuadCtrlY = null;
        SvgSubpath? current = null;
        char lastCommand = '\0';

        void StartSubpath(double x, double y)
        {
            current = new SvgSubpath();
            current.Points.Add((x, y));
            subpaths.Add(current);
            subpathStartX = x;
            subpathStartY = y;
        }

        void LineTo(double x, double y)
        {
            current ??= StartAt(x, y);
            current.Points.Add((x, y));
        }

        SvgSubpath StartAt(double x, double y) { StartSubpath(x, y); return current!; }

        while (tokenizer.TryReadCommand(out var cmd))
        {
            var relative = char.IsLower(cmd);
            var upper = char.ToUpperInvariant(cmd);

            // M/m with multiple coordinate pairs: first pair is moveto, rest are implicit lineto.
            bool firstPairInMove = true;

            do
            {
                switch (upper)
                {
                    case 'M':
                    {
                        var x = tokenizer.ReadNumber();
                        var y = tokenizer.ReadNumber();
                        // Relative moveto is always relative to the current point, whether this
                        // is the initial pair or an implicit-lineto continuation pair.
                        if (relative) { x += curX; y += curY; }
                        curX = x; curY = y;
                        if (firstPairInMove) StartSubpath(x, y); else LineTo(x, y);
                        firstPairInMove = false;
                        prevCubicCtrlX = prevCubicCtrlY = prevQuadCtrlX = prevQuadCtrlY = null;
                        break;
                    }
                    case 'L':
                    {
                        var x = tokenizer.ReadNumber();
                        var y = tokenizer.ReadNumber();
                        if (relative) { x += curX; y += curY; }
                        curX = x; curY = y;
                        LineTo(x, y);
                        prevCubicCtrlX = prevCubicCtrlY = prevQuadCtrlX = prevQuadCtrlY = null;
                        break;
                    }
                    case 'H':
                    {
                        var x = tokenizer.ReadNumber();
                        if (relative) x += curX;
                        curX = x;
                        LineTo(x, curY);
                        prevCubicCtrlX = prevCubicCtrlY = prevQuadCtrlX = prevQuadCtrlY = null;
                        break;
                    }
                    case 'V':
                    {
                        var y = tokenizer.ReadNumber();
                        if (relative) y += curY;
                        curY = y;
                        LineTo(curX, y);
                        prevCubicCtrlX = prevCubicCtrlY = prevQuadCtrlX = prevQuadCtrlY = null;
                        break;
                    }
                    case 'C':
                    {
                        var x1 = tokenizer.ReadNumber(); var y1 = tokenizer.ReadNumber();
                        var x2 = tokenizer.ReadNumber(); var y2 = tokenizer.ReadNumber();
                        var x = tokenizer.ReadNumber(); var y = tokenizer.ReadNumber();
                        if (relative) { x1 += curX; y1 += curY; x2 += curX; y2 += curY; x += curX; y += curY; }
                        SampleCubic(current ??= StartAt(curX, curY), curX, curY, x1, y1, x2, y2, x, y);
                        prevCubicCtrlX = x2; prevCubicCtrlY = y2;
                        prevQuadCtrlX = prevQuadCtrlY = null;
                        curX = x; curY = y;
                        break;
                    }
                    case 'S':
                    {
                        var x2 = tokenizer.ReadNumber(); var y2 = tokenizer.ReadNumber();
                        var x = tokenizer.ReadNumber(); var y = tokenizer.ReadNumber();
                        if (relative) { x2 += curX; y2 += curY; x += curX; y += curY; }
                        var x1 = prevCubicCtrlX is { } pcx ? 2 * curX - pcx : curX;
                        var y1 = prevCubicCtrlY is { } pcy ? 2 * curY - pcy : curY;
                        SampleCubic(current ??= StartAt(curX, curY), curX, curY, x1, y1, x2, y2, x, y);
                        prevCubicCtrlX = x2; prevCubicCtrlY = y2;
                        prevQuadCtrlX = prevQuadCtrlY = null;
                        curX = x; curY = y;
                        break;
                    }
                    case 'Q':
                    {
                        var x1 = tokenizer.ReadNumber(); var y1 = tokenizer.ReadNumber();
                        var x = tokenizer.ReadNumber(); var y = tokenizer.ReadNumber();
                        if (relative) { x1 += curX; y1 += curY; x += curX; y += curY; }
                        SampleQuadratic(current ??= StartAt(curX, curY), curX, curY, x1, y1, x, y);
                        prevQuadCtrlX = x1; prevQuadCtrlY = y1;
                        prevCubicCtrlX = prevCubicCtrlY = null;
                        curX = x; curY = y;
                        break;
                    }
                    case 'T':
                    {
                        var x = tokenizer.ReadNumber(); var y = tokenizer.ReadNumber();
                        if (relative) { x += curX; y += curY; }
                        var x1 = prevQuadCtrlX is { } pqx ? 2 * curX - pqx : curX;
                        var y1 = prevQuadCtrlY is { } pqy ? 2 * curY - pqy : curY;
                        SampleQuadratic(current ??= StartAt(curX, curY), curX, curY, x1, y1, x, y);
                        prevQuadCtrlX = x1; prevQuadCtrlY = y1;
                        prevCubicCtrlX = prevCubicCtrlY = null;
                        curX = x; curY = y;
                        break;
                    }
                    case 'A':
                    {
                        var rx = tokenizer.ReadNumber(); var ry = tokenizer.ReadNumber();
                        var rot = tokenizer.ReadNumber();
                        var large = tokenizer.ReadFlag() != 0;
                        var sweep = tokenizer.ReadFlag() != 0;
                        var x = tokenizer.ReadNumber(); var y = tokenizer.ReadNumber();
                        if (relative) { x += curX; y += curY; }
                        var sub = current ??= StartAt(curX, curY);
                        foreach (var pt in SvgArcMath.FlattenArc(curX, curY, rx, ry, rot, large, sweep, x, y, CurveSteps))
                            sub.Points.Add(pt);
                        prevCubicCtrlX = prevCubicCtrlY = prevQuadCtrlX = prevQuadCtrlY = null;
                        curX = x; curY = y;
                        break;
                    }
                    case 'Z':
                    {
                        if (current is not null)
                        {
                            current.Points.Add((subpathStartX, subpathStartY));
                            current.Closed = true;
                        }
                        curX = subpathStartX; curY = subpathStartY;
                        prevCubicCtrlX = prevCubicCtrlY = prevQuadCtrlX = prevQuadCtrlY = null;
                        break;
                    }
                    default:
                        // Unsupported command letter — stop parsing this path rather than looping forever.
                        return subpaths;
                }
            } while (upper != 'Z' && tokenizer.HasMoreNumbers());

            lastCommand = cmd;
        }

        _ = lastCommand;
        return subpaths;
    }

    private static void SampleCubic(SvgSubpath sub, double x0, double y0, double x1, double y1, double x2, double y2, double x3, double y3)
    {
        for (int i = 1; i <= CurveSteps; i++)
        {
            var t = (double)i / CurveSteps;
            var mt = 1 - t;
            var x = mt * mt * mt * x0 + 3 * mt * mt * t * x1 + 3 * mt * t * t * x2 + t * t * t * x3;
            var y = mt * mt * mt * y0 + 3 * mt * mt * t * y1 + 3 * mt * t * t * y2 + t * t * t * y3;
            sub.Points.Add((x, y));
        }
    }

    private static void SampleQuadratic(SvgSubpath sub, double x0, double y0, double x1, double y1, double x2, double y2)
    {
        for (int i = 1; i <= CurveSteps; i++)
        {
            var t = (double)i / CurveSteps;
            var mt = 1 - t;
            var x = mt * mt * x0 + 2 * mt * t * x1 + t * t * x2;
            var y = mt * mt * y0 + 2 * mt * t * y1 + t * t * y2;
            sub.Points.Add((x, y));
        }
    }
}
