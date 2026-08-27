using System.Globalization;
using System.Xml.Linq;
using Lasero.Core.GCode;
using Lasero.Core.Grbl;
using Lasero.Core.Import.Svg;
using Lasero.Core.Layers;

namespace Lasero.Core.Import;

/// <summary>
/// Imports an SVG design into shapes + auto-detected layers, using the same
/// color convention lasero-app's own lightburn-export.js already establishes
/// for designs made in the web app (red stroke → Cut, black fill → Fill) —
/// so a design downloaded from lasero.net imports with correct settings
/// automatically. Any other color present gets its own layer too, same as
/// LightBurn's own per-color layer matching, so hand-authored SVGs from
/// Inkscape/Illustrator still import sensibly.
/// </summary>
public static class SvgImporter
{
    public static ImportedDocument Import(string svgText, double targetWidthMm, string? sourceFileName = null)
    {
        var doc = XDocument.Parse(svgText);
        var svg = doc.Root ?? throw new InvalidDataException("Neplatný soubor SVG: chybí kořenový element <svg>.");

        var (viewBoxX, viewBoxY, viewBoxW, viewBoxH) = ReadViewBox(svg);
        var scale = targetWidthMm / viewBoxW;
        var heightMm = viewBoxH * scale;

        var shapes = new List<ImportedShape>();
        Walk(svg, SvgTransform.Identity, fill: "black", stroke: "none", shapes, viewBoxX, viewBoxY, scale, heightMm);

        var bbox = BoundingBox2D.Empty;
        foreach (var shape in shapes)
            foreach (var p in shape.Points)
                bbox = bbox.Include(p.X, p.Y);

        var layers = BuildLayers(shapes);
        shapes = shapes.Select(shape => shape with
        {
            LayerId = layers.First(layer => layer.Color.IsApproximately(shape.LayerColor)).Id,
        }).ToList();

        return new ImportedDocument
        {
            Shapes = shapes,
            Layers = layers,
            BoundingBox = bbox,
            SourceFileName = sourceFileName,
        };
    }

    private static (double X, double Y, double W, double H) ReadViewBox(XElement svg)
    {
        var vb = (string?)svg.Attribute("viewBox");
        if (!string.IsNullOrWhiteSpace(vb))
        {
            var parts = vb.Split([' ', ','], StringSplitOptions.RemoveEmptyEntries)
                .Select(s => double.Parse(s, NumberStyles.Float, CultureInfo.InvariantCulture)).ToArray();
            if (parts.Length == 4 && parts[2] > 0 && parts[3] > 0)
                return (parts[0], parts[1], parts[2], parts[3]);
        }

        var w = ParseLength((string?)svg.Attribute("width")) ?? 100;
        var h = ParseLength((string?)svg.Attribute("height")) ?? 100;
        return (0, 0, w, h);
    }

    private static double? ParseLength(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var digits = new string(value.TakeWhile(c => char.IsDigit(c) || c is '.' or '-').ToArray());
        return double.TryParse(digits, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;
    }

    private static readonly HashSet<string> NonRenderedContainers = ["defs", "clippath", "mask", "symbol", "pattern", "style", "title", "desc", "metadata"];
    private static readonly HashSet<string> ShapeElements = ["path", "rect", "circle", "ellipse", "line", "polyline", "polygon"];

    private static void Walk(
        XElement el, SvgTransform parentTransform, string fill, string stroke,
        List<ImportedShape> output, double viewBoxX, double viewBoxY, double scale, double heightMm)
    {
        var name = el.Name.LocalName.ToLowerInvariant();
        if (NonRenderedContainers.Contains(name)) return;

        var localTransform = SvgTransform.Parse((string?)el.Attribute("transform"));
        var worldTransform = parentTransform.Multiply(localTransform);

        var (elFill, elStroke) = ReadPaint(el, fill, stroke);

        if (ShapeElements.Contains(name))
        {
            var subpaths = name == "path"
                ? SvgPathParser.Flatten((string?)el.Attribute("d") ?? string.Empty)
                : (SvgShapeFlattener.Flatten(el) is { } single ? [single] : []);

            foreach (var sub in subpaths)
            {
                if (sub.Points.Count < 2) continue;

                var mmPoints = sub.Points
                    .Select(p =>
                    {
                        var (wx, wy) = worldTransform.Apply(p.X, p.Y);
                        var mmX = (wx - viewBoxX) * scale;
                        var mmY = heightMm - (wy - viewBoxY) * scale; // SVG Y grows down; ours grows up.
                        return new Position(mmX, mmY, 0);
                    })
                    .ToList();

                var (layerColor, mode) = Classify(elFill, elStroke);
                if (layerColor is null) continue;

                output.Add(new ImportedShape
                {
                    Points = mmPoints,
                    IsClosed = sub.Closed,
                    LayerColor = layerColor.Value,
                    PreferredMode = mode,
                });
            }
        }

        foreach (var child in el.Elements())
            Walk(child, worldTransform, elFill, elStroke, output, viewBoxX, viewBoxY, scale, heightMm);
    }

    private static (string Fill, string Stroke) ReadPaint(XElement el, string inheritedFill, string inheritedStroke)
    {
        var fill = inheritedFill;
        var stroke = inheritedStroke;

        var fillAttr = (string?)el.Attribute("fill");
        if (!string.IsNullOrWhiteSpace(fillAttr)) fill = fillAttr;
        var strokeAttr = (string?)el.Attribute("stroke");
        if (!string.IsNullOrWhiteSpace(strokeAttr)) stroke = strokeAttr;

        var style = (string?)el.Attribute("style");
        if (!string.IsNullOrWhiteSpace(style))
        {
            foreach (var decl in style.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                var kv = decl.Split(':', 2, StringSplitOptions.TrimEntries);
                if (kv.Length != 2) continue;
                if (kv[0] == "fill") fill = kv[1];
                else if (kv[0] == "stroke") stroke = kv[1];
            }
        }

        return (fill, stroke);
    }

    /// <summary>Mirrors lasero-app's lightburn-export.js classify(): red stroke → Cut, black fill (or no stroke) → Fill, otherwise fall back per-color.</summary>
    private static (RgbColor? Color, LayerMode Mode) Classify(string fill, string stroke)
    {
        var hasStroke = RgbColor.TryParse(stroke, out var strokeColor);
        var hasFill = RgbColor.TryParse(fill, out var fillColor);

        if (hasStroke && strokeColor.IsApproximately(RgbColor.Red, tolerance: 40))
            return (RgbColor.Red, LayerMode.Cut);
        if (hasFill && (fillColor.IsApproximately(RgbColor.Black, tolerance: 25) || !hasStroke))
            return (fillColor, LayerMode.Fill);
        if (hasStroke)
            return (strokeColor, LayerMode.Cut);
        if (hasFill)
            return (fillColor, LayerMode.Fill);
        return (null, LayerMode.Cut);
    }

    private static List<LayerSettings> BuildLayers(List<ImportedShape> shapes)
    {
        var layers = new List<LayerSettings>();
        foreach (var shape in shapes)
        {
            if (layers.Any(l => l.Color.IsApproximately(shape.LayerColor)))
                continue;

            var name = shape.LayerColor.IsApproximately(RgbColor.Red) ? "Rez"
                : shape.LayerColor.IsApproximately(RgbColor.Black) ? "Gravír"
                : shape.LayerColor.ToHex();

            layers.Add(LayerSettings.CreateDefault(shape.LayerColor, shape.PreferredMode, name));
        }
        return layers;
    }
}
