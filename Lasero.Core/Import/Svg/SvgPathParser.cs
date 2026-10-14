using Lasero.Core.Grbl;
using Lasero.Core.Scene;

namespace Lasero.Core.Import.Svg;

/// <summary>
/// Flattens an SVG path `d` attribute into polylines (subpaths of straight
/// line segments). Curves and arcs are subdivided adaptively until they are
/// flat within the caller's tolerance (in path user units). Supports all
/// standard commands including the S/T shorthand-reflection forms.
/// </summary>
internal static class SvgPathParser
{
    /// <summary>Default flatness tolerance in user units when the caller does not know the output scale.</summary>
    public const double DefaultTolerance = 0.05;

    private static bool SamePoint(double ax, double ay, double bx, double by) =>
        Math.Abs(ax - bx) < 1e-9 && Math.Abs(ay - by) < 1e-9;

    public static List<SvgSubpath> Flatten(string d, double tolerance = DefaultTolerance)
    {
        if (!double.IsFinite(tolerance) || tolerance <= 0) tolerance = DefaultTolerance;
        var subpaths = new List<SvgSubpath>();
        var tokenizer = new SvgPathTokenizer(d);

        double curX = 0, curY = 0;
        double subpathStartX = 0, subpathStartY = 0;
        double? prevCubicCtrlX = null, prevCubicCtrlY = null;
        double? prevQuadCtrlX = null, prevQuadCtrlY = null;
        SvgSubpath? current = null;
        // After Z the current point is the closed subpath's start, and a drawing command that is not
        // an M begins a NEW subpath there (SVG spec 9.3.4) instead of extending the closed one.
        var afterClose = false;
        char lastCommand = '\0';

        void StartSubpath(double x, double y)
        {
            current = new SvgSubpath();
            current.Points.Add((x, y));
            subpaths.Add(current);
            subpathStartX = x;
            subpathStartY = y;
            afterClose = false;
        }

        void LineTo(double x, double y)
        {
            if (current is null)
            {
                if (afterClose) StartSubpath(subpathStartX, subpathStartY);
                else StartSubpath(x, y);
            }
            current!.Points.Add((x, y));
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
                        SampleCubic(current ??= StartAt(curX, curY), tolerance, curX, curY, x1, y1, x2, y2, x, y);
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
                        SampleCubic(current ??= StartAt(curX, curY), tolerance, curX, curY, x1, y1, x2, y2, x, y);
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
                        SampleQuadratic(current ??= StartAt(curX, curY), tolerance, curX, curY, x1, y1, x, y);
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
                        SampleQuadratic(current ??= StartAt(curX, curY), tolerance, curX, curY, x1, y1, x, y);
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
                        // Spec F.6.2: identical endpoints omit the arc entirely; a zero radius is a straight line.
                        if (!SamePoint(x, y, curX, curY))
                        {
                            var anySegment = false;
                            var arcStartX = curX; var arcStartY = curY;
                            foreach (var seg in SvgArcMath.ArcToBezierSegments(curX, curY, rx, ry, rot, large, sweep, x, y))
                            {
                                anySegment = true;
                                AppendCubic(sub, tolerance, arcStartX, arcStartY, seg.C1.X, seg.C1.Y, seg.C2.X, seg.C2.Y, seg.End.X, seg.End.Y);
                                arcStartX = seg.End.X; arcStartY = seg.End.Y;
                            }
                            if (!anySegment) sub.Points.Add((x, y));
                        }
                        prevCubicCtrlX = prevCubicCtrlY = prevQuadCtrlX = prevQuadCtrlY = null;
                        curX = x; curY = y;
                        break;
                    }
                    case 'Z':
                    {
                        if (current is not null)
                        {
                            // "L start Z" already put the start point last; adding it again would leave a
                            // zero-length duplicate vertex.
                            var last = current.Points[^1];
                            if (!SamePoint(last.X, last.Y, subpathStartX, subpathStartY))
                                current.Points.Add((subpathStartX, subpathStartY));
                            current.Closed = true;
                        }
                        current = null;
                        afterClose = true;
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

    /// <summary>Curve-preserving counterpart to <see cref="Flatten"/>: parses the same `d` grammar
    /// (identical command coverage, identical S/T reflection and relative-coordinate handling —
    /// literally the same control flow) but builds <see cref="VectorNode"/> anchors/handles instead
    /// of sampling curves into points immediately, so the result stays exactly node-editable rather
    /// than a one-way flattened polyline. Q/T (quadratic) commands are converted to the mathematically
    /// exact equivalent cubic (well-known identity: c1 = p0 + 2/3(q-p0), c2 = p1 + 2/3(q-p1)) — not an
    /// approximation. A (elliptical arc) commands are converted to one or more exact-form cubic Bézier
    /// segments via SvgArcMath.ArcToBezierSegments (≤90° each, the standard practice for fidelity); a
    /// degenerate arc (spec: identical endpoints or non-positive radius) falls back to a straight
    /// line-to, matching the SVG spec's own fallback rule.
    ///
    /// Every node is imported as VectorNodeType.Corner — deliberately never inferred as Smooth, even
    /// where the source used the S/T reflection shorthand implying a smooth join: Smooth is a
    /// *constraint* (VectorPathEditor.MoveHandle keeps the opposite handle collinear once one is
    /// dragged), and guessing it wrong would silently change how the shape reacts to a future edit.
    /// Corner never does that — both handles stay exactly where the source placed them, editable
    /// independently, which is always geometry-safe regardless of what the source intended.
    ///
    /// On any parse failure (malformed data, a command letter this parser doesn't recognize) returns
    /// whatever subpaths were completed so far, mirroring Flatten's own "stop rather than throw"
    /// contract — the caller (SvgImporter) is expected to treat a mismatched subpath count between
    /// this and Flatten as "this path isn't fully representable as an editable VectorPath" and omit
    /// the whole document's VectorPath rather than commit to a partially-editable, silently-wrong
    /// state (see the "no regression" contract on SvgImporter.Import).</summary>
    public static List<VectorSubpath> ParseToVectorSubpaths(string d)
    {
        var subpaths = new List<VectorSubpath>();
        var tokenizer = new SvgPathTokenizer(d);

        double curX = 0, curY = 0;
        double subpathStartX = 0, subpathStartY = 0;
        double? prevCubicCtrlX = null, prevCubicCtrlY = null;
        double? prevQuadCtrlX = null, prevQuadCtrlY = null;
        List<VectorNode>? nodes = null;
        var closed = false;
        var afterClose = false;

        void FinishCurrentSubpath()
        {
            if (nodes is not null) subpaths.Add(new VectorSubpath { Nodes = nodes, IsClosed = closed });
        }

        void StartSubpath(double x, double y)
        {
            FinishCurrentSubpath();
            nodes = [VectorNode.CornerAt(new Position(x, y, 0))];
            closed = false;
            afterClose = false;
            subpathStartX = x; subpathStartY = y;
        }

        void LineTo(double x, double y)
        {
            if (nodes is null)
            {
                // After Z a drawing command without M continues from the closed subpath's start.
                if (afterClose) { StartSubpath(subpathStartX, subpathStartY); nodes!.Add(VectorNode.CornerAt(new Position(x, y, 0))); }
                else StartSubpath(x, y);
            }
            else nodes.Add(VectorNode.CornerAt(new Position(x, y, 0)));
        }

        // Sets the outgoing handle on the node the curve starts FROM (already the last node in the
        // list) and appends the destination node carrying the incoming handle — the same "handle
        // belongs to the segment, split across its two endpoint nodes" convention VectorPath.cs's own
        // boundary comment documents.
        void CurveTo(double c1x, double c1y, double c2x, double c2y, double x, double y)
        {
            if (nodes is null) StartSubpath(curX, curY);
            var list = nodes!;
            list[^1] = list[^1] with { HandleOut = new Position(c1x, c1y, 0) };
            list.Add(new VectorNode(new Position(x, y, 0), new Position(c2x, c2y, 0), null, VectorNodeType.Corner));
        }

        while (tokenizer.TryReadCommand(out var cmd))
        {
            var relative = char.IsLower(cmd);
            var upper = char.ToUpperInvariant(cmd);
            var firstPairInMove = true;

            do
            {
                switch (upper)
                {
                    case 'M':
                    {
                        var x = tokenizer.ReadNumber(); var y = tokenizer.ReadNumber();
                        if (relative) { x += curX; y += curY; }
                        curX = x; curY = y;
                        if (firstPairInMove) StartSubpath(x, y); else LineTo(x, y);
                        firstPairInMove = false;
                        prevCubicCtrlX = prevCubicCtrlY = prevQuadCtrlX = prevQuadCtrlY = null;
                        break;
                    }
                    case 'L':
                    {
                        var x = tokenizer.ReadNumber(); var y = tokenizer.ReadNumber();
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
                        CurveTo(x1, y1, x2, y2, x, y);
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
                        CurveTo(x1, y1, x2, y2, x, y);
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
                        var c1x = curX + 2.0 / 3.0 * (x1 - curX); var c1y = curY + 2.0 / 3.0 * (y1 - curY);
                        var c2x = x + 2.0 / 3.0 * (x1 - x); var c2y = y + 2.0 / 3.0 * (y1 - y);
                        CurveTo(c1x, c1y, c2x, c2y, x, y);
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
                        var c1x = curX + 2.0 / 3.0 * (x1 - curX); var c1y = curY + 2.0 / 3.0 * (y1 - curY);
                        var c2x = x + 2.0 / 3.0 * (x1 - x); var c2y = y + 2.0 / 3.0 * (y1 - y);
                        CurveTo(c1x, c1y, c2x, c2y, x, y);
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
                        // Spec F.6.2: identical endpoints omit the arc segment ENTIRELY (not a
                        // zero-length line-to) -- must check this before the general degenerate-arc
                        // fallback below, which is for the *different* "zero/invalid radius but real
                        // distance" case and legitimately becomes a line.
                        var isNoOp = Math.Abs(x - curX) < 1e-9 && Math.Abs(y - curY) < 1e-9;
                        if (!isNoOp)
                        {
                            var any = false;
                            foreach (var seg in SvgArcMath.ArcToBezierSegments(curX, curY, rx, ry, rot, large, sweep, x, y))
                            {
                                CurveTo(seg.C1.X, seg.C1.Y, seg.C2.X, seg.C2.Y, seg.End.X, seg.End.Y);
                                any = true;
                            }
                            if (!any) LineTo(x, y); // zero/invalid radius, real distance -> straight line, per spec
                        }
                        prevCubicCtrlX = prevCubicCtrlY = prevQuadCtrlX = prevQuadCtrlY = null;
                        curX = x; curY = y;
                        break;
                    }
                    case 'Z':
                    {
                        if (nodes is not null)
                        {
                            closed = true;
                            // "L start Z": the explicit return point and the implicit closing segment would
                            // otherwise leave two coincident nodes. Fold the last node into the first.
                            if (nodes.Count > 2 && nodes[^1].HandleOut is null &&
                                SamePoint(nodes[^1].Anchor.X, nodes[^1].Anchor.Y, nodes[0].Anchor.X, nodes[0].Anchor.Y))
                            {
                                nodes[0] = nodes[0] with { HandleIn = nodes[0].HandleIn ?? nodes[^1].HandleIn };
                                nodes.RemoveAt(nodes.Count - 1);
                            }
                            FinishCurrentSubpath();
                            nodes = null;
                            closed = false;
                        }
                        afterClose = true;
                        curX = subpathStartX; curY = subpathStartY;
                        prevCubicCtrlX = prevCubicCtrlY = prevQuadCtrlX = prevQuadCtrlY = null;
                        break;
                    }
                    default:
                        FinishCurrentSubpath();
                        return subpaths;
                }
            } while (upper != 'Z' && tokenizer.HasMoreNumbers());
        }

        FinishCurrentSubpath();
        return subpaths;
    }

    private static void SampleCubic(SvgSubpath sub, double tolerance, double x0, double y0, double x1, double y1, double x2, double y2, double x3, double y3) =>
        AppendCubic(sub, tolerance, x0, y0, x1, y1, x2, y2, x3, y3);

    /// <summary>Adaptive subdivision of one cubic (its start point is already present in <paramref name="sub"/>).</summary>
    private static void AppendCubic(SvgSubpath sub, double tolerance, double x0, double y0, double x1, double y1, double x2, double y2, double x3, double y3)
    {
        var points = new List<Position>();
        CubicBezier.Flatten(new Position(x0, y0, 0), new Position(x1, y1, 0), new Position(x2, y2, 0), new Position(x3, y3, 0), tolerance, points);
        foreach (var point in points)
            sub.Points.Add((point.X, point.Y));
    }

    /// <summary>Quadratics are elevated to the exactly equivalent cubic, then flattened adaptively.</summary>
    private static void SampleQuadratic(SvgSubpath sub, double tolerance, double x0, double y0, double x1, double y1, double x2, double y2) =>
        AppendCubic(sub, tolerance, x0, y0,
            x0 + 2.0 / 3.0 * (x1 - x0), y0 + 2.0 / 3.0 * (y1 - y0),
            x2 + 2.0 / 3.0 * (x1 - x2), y2 + 2.0 / 3.0 * (y1 - y2),
            x2, y2);
}
