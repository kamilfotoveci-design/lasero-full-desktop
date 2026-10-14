using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Lasero.Core.Grbl;
using Lasero.Core.Scene;

namespace Lasero.Core.Import.Svg;

/// <summary>A basic SVG shape in its own local coordinates: the flattened polyline used for rendering and
/// toolpaths, and the editable node form (real Bézier arcs for circles, ellipses and rounded corners).</summary>
internal sealed record SvgShape(SvgSubpath Flat, VectorSubpath Curve);

/// <summary>Builds the basic SVG shape elements (everything except &lt;path&gt;) in the element's own local coordinate space.</summary>
internal static class SvgShapeFlattener
{
    /// <summary>Four-arc circle approximation constant: control handle length as a fraction of the radius.</summary>
    private const double Kappa = 0.5522847498307936;

    private static readonly Regex NumberPattern = new(@"[-+]?(?:\d+\.?\d*|\.\d+)(?:[eE][-+]?\d+)?", RegexOptions.Compiled);

    /// <param name="tolerance">Flatness tolerance in user units for curved shapes.</param>
    public static SvgShape? Build(XElement el, double tolerance, Action<string>? warn = null)
    {
        double L(string attribute) => SvgLength.Get(el, attribute, 0, warn);
        return el.Name.LocalName switch
        {
            "rect" => BuildRect(el, tolerance, warn),
            "circle" => BuildEllipse(L("cx"), L("cy"), L("r"), L("r"), tolerance),
            "ellipse" => BuildEllipse(L("cx"), L("cy"), L("rx"), L("ry"), tolerance),
            "line" => BuildPolyline([(L("x1"), L("y1")), (L("x2"), L("y2"))], closed: false),
            "polyline" => BuildPolyline(ReadPoints(el, warn), closed: false),
            "polygon" => BuildPolyline(ReadPoints(el, warn), closed: true),
            _ => null,
        };
    }

    private static SvgShape? BuildRect(XElement el, double tolerance, Action<string>? warn)
    {
        var x = SvgLength.Get(el, "x", 0, warn); var y = SvgLength.Get(el, "y", 0, warn);
        var w = SvgLength.Get(el, "width", 0, warn); var h = SvgLength.Get(el, "height", 0, warn);
        if (w <= 0 || h <= 0) return null; // the spec disables rendering of a rect without a positive size
        var rx = Math.Max(0, SvgLength.Get(el, "rx", 0, warn));
        var ry = Math.Max(0, SvgLength.Get(el, "ry", 0, warn));

        if (rx <= 0 && ry <= 0)
            return BuildPolyline([(x, y), (x + w, y), (x + w, y + h), (x, y + h)], closed: true);

        if (rx <= 0) rx = ry;
        if (ry <= 0) ry = rx;
        rx = Math.Min(rx, w / 2);
        ry = Math.Min(ry, h / 2);
        var kx = Kappa * rx;
        var ky = Kappa * ry;

        // Eight anchors, going clockwise (screen) from the end of the top-left corner arc. Each corner
        // arc is one cubic whose handles sit on the two adjacent anchors.
        var raw = new List<VectorNode>
        {
            Node(x + rx, y, handleIn: (x + rx - kx, y)),
            Node(x + w - rx, y, handleOut: (x + w - rx + kx, y)),
            Node(x + w, y + ry, handleIn: (x + w, y + ry - ky)),
            Node(x + w, y + h - ry, handleOut: (x + w, y + h - ry + ky)),
            Node(x + w - rx, y + h, handleIn: (x + w - rx + kx, y + h)),
            Node(x + rx, y + h, handleOut: (x + rx - kx, y + h)),
            Node(x, y + h - ry, handleIn: (x, y + h - ry + ky)),
            Node(x, y + ry, handleOut: (x, y + ry - ky)),
        };

        // A radius of exactly half the side makes the straight piece between two anchors zero-length;
        // fuse such neighbours so no degenerate node survives.
        var nodes = new List<VectorNode>();
        foreach (var node in raw)
        {
            if (nodes.Count > 0 && SamePoint(nodes[^1].Anchor, node.Anchor))
                nodes[^1] = nodes[^1] with { HandleIn = nodes[^1].HandleIn ?? node.HandleIn, HandleOut = node.HandleOut ?? nodes[^1].HandleOut };
            else
                nodes.Add(node);
        }

        return FromCurve(new VectorSubpath { Nodes = nodes, IsClosed = true }, tolerance);
    }

    private static SvgShape? BuildEllipse(double cx, double cy, double rx, double ry, double tolerance)
    {
        if (rx <= 0 || ry <= 0) return null;
        var kx = Kappa * rx;
        var ky = Kappa * ry;
        // Starts at 3 o'clock and runs clockwise on screen, the direction the SVG spec defines.
        var nodes = new List<VectorNode>
        {
            Node(cx + rx, cy, handleIn: (cx + rx, cy - ky), handleOut: (cx + rx, cy + ky)),
            Node(cx, cy + ry, handleIn: (cx + kx, cy + ry), handleOut: (cx - kx, cy + ry)),
            Node(cx - rx, cy, handleIn: (cx - rx, cy + ky), handleOut: (cx - rx, cy - ky)),
            Node(cx, cy - ry, handleIn: (cx - kx, cy - ry), handleOut: (cx + kx, cy - ry)),
        };
        return FromCurve(new VectorSubpath { Nodes = nodes, IsClosed = true }, tolerance);
    }

    private static SvgShape BuildPolyline(IReadOnlyList<(double X, double Y)> points, bool closed)
    {
        var flat = new SvgSubpath { Closed = closed };
        foreach (var point in points) flat.Points.Add(point);
        if (closed && flat.Points.Count > 0)
            flat.Points.Add(flat.Points[0]);
        var nodes = points.Select(p => VectorNode.CornerAt(new Position(p.X, p.Y, 0))).ToList();
        return new SvgShape(flat, new VectorSubpath { Nodes = nodes, IsClosed = closed });
    }

    private static SvgShape FromCurve(VectorSubpath curve, double tolerance)
    {
        var flat = new SvgSubpath { Closed = curve.IsClosed };
        foreach (var point in curve.Flatten(tolerance))
            flat.Points.Add((point.X, point.Y));
        return new SvgShape(flat, curve);
    }

    private static VectorNode Node(double x, double y, (double X, double Y)? handleIn = null, (double X, double Y)? handleOut = null) =>
        new(new Position(x, y, 0),
            handleIn is { } hi ? new Position(hi.X, hi.Y, 0) : null,
            handleOut is { } ho ? new Position(ho.X, ho.Y, 0) : null,
            VectorNodeType.Corner);

    private static bool SamePoint(Position a, Position b) =>
        Math.Abs(a.X - b.X) < 1e-9 && Math.Abs(a.Y - b.Y) < 1e-9;

    private static List<(double X, double Y)> ReadPoints(XElement el, Action<string>? warn)
    {
        var raw = (string?)el.Attribute("points") ?? string.Empty;
        var numbers = NumberPattern.Matches(raw)
            .Select(match => double.Parse(match.Value, NumberStyles.Float, CultureInfo.InvariantCulture))
            .ToArray();
        if (numbers.Length % 2 != 0)
            warn?.Invoke($"Prvek <{el.Name.LocalName}> má lichý počet souřadnic; poslední hodnota byla ignorována.");
        var points = new List<(double X, double Y)>();
        for (var i = 0; i + 1 < numbers.Length; i += 2)
            points.Add((numbers[i], numbers[i + 1]));
        return points;
    }
}
