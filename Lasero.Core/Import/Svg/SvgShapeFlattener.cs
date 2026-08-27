using System.Globalization;
using System.Xml.Linq;

namespace Lasero.Core.Import.Svg;

/// <summary>Flattens the basic SVG shape elements (everything except &lt;path&gt;) into polylines, in the element's own local coordinate space.</summary>
internal static class SvgShapeFlattener
{
    private const int CircleSteps = 64;

    public static SvgSubpath? Flatten(XElement el)
    {
        return el.Name.LocalName switch
        {
            "rect" => FlattenRect(el),
            "circle" => FlattenEllipse(D(el, "cx"), D(el, "cy"), D(el, "r"), D(el, "r")),
            "ellipse" => FlattenEllipse(D(el, "cx"), D(el, "cy"), D(el, "rx"), D(el, "ry")),
            "line" => FlattenLine(el),
            "polyline" => FlattenPoly(el, closed: false),
            "polygon" => FlattenPoly(el, closed: true),
            _ => null,
        };
    }

    private static SvgSubpath FlattenRect(XElement el)
    {
        var x = D(el, "x"); var y = D(el, "y");
        var w = D(el, "width"); var h = D(el, "height");
        var rx = D(el, "rx"); var ry = D(el, "ry");
        if (rx <= 0 && ry <= 0)
        {
            var sub = new SvgSubpath { Closed = true };
            sub.Points.Add((x, y));
            sub.Points.Add((x + w, y));
            sub.Points.Add((x + w, y + h));
            sub.Points.Add((x, y + h));
            sub.Points.Add((x, y));
            return sub;
        }

        if (rx <= 0) rx = ry;
        if (ry <= 0) ry = rx;
        rx = Math.Min(rx, w / 2);
        ry = Math.Min(ry, h / 2);

        var result = new SvgSubpath { Closed = true };
        void Arc(double cx, double cy, double startDeg, double endDeg)
        {
            for (int i = 0; i <= 8; i++)
            {
                var t = (startDeg + (endDeg - startDeg) * i / 8) * Math.PI / 180;
                result.Points.Add((cx + rx * Math.Cos(t), cy + ry * Math.Sin(t)));
            }
        }

        result.Points.Add((x + rx, y));
        result.Points.Add((x + w - rx, y));
        Arc(x + w - rx, y + ry, -90, 0);
        result.Points.Add((x + w, y + h - ry));
        Arc(x + w - rx, y + h - ry, 0, 90);
        result.Points.Add((x + rx, y + h));
        Arc(x + rx, y + h - ry, 90, 180);
        result.Points.Add((x, y + ry));
        Arc(x + rx, y + ry, 180, 270);
        return result;
    }

    private static SvgSubpath FlattenEllipse(double cx, double cy, double rx, double ry)
    {
        var sub = new SvgSubpath { Closed = true };
        for (int i = 0; i <= CircleSteps; i++)
        {
            var t = 2 * Math.PI * i / CircleSteps;
            sub.Points.Add((cx + rx * Math.Cos(t), cy + ry * Math.Sin(t)));
        }
        return sub;
    }

    private static SvgSubpath FlattenLine(XElement el)
    {
        var sub = new SvgSubpath { Closed = false };
        sub.Points.Add((D(el, "x1"), D(el, "y1")));
        sub.Points.Add((D(el, "x2"), D(el, "y2")));
        return sub;
    }

    private static SvgSubpath FlattenPoly(XElement el, bool closed)
    {
        var sub = new SvgSubpath { Closed = closed };
        var raw = (string?)el.Attribute("points") ?? string.Empty;
        var numbers = raw.Split([' ', ',', '\t', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries)
            .Select(s => double.Parse(s, NumberStyles.Float, CultureInfo.InvariantCulture))
            .ToArray();
        for (int i = 0; i + 1 < numbers.Length; i += 2)
            sub.Points.Add((numbers[i], numbers[i + 1]));
        if (closed && sub.Points.Count > 0)
            sub.Points.Add(sub.Points[0]);
        return sub;
    }

    private static double D(XElement el, string attr) =>
        double.TryParse((string?)el.Attribute(attr), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;
}
