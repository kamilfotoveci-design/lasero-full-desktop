using System.Globalization;
using System.Xml.Linq;
using Lasero.Core.GCode;
using Lasero.Core.Grbl;
using Lasero.Core.Import.Svg;
using Lasero.Core.Layers;
using Lasero.Core.Scene;

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
        var vectorSubpaths = new List<VectorSubpath>();
        var vectorPathValid = new VectorPathValidity();
        Walk(svg, SvgTransform.Identity, fill: "black", stroke: "none", shapes, viewBoxX, viewBoxY, scale, heightMm, vectorSubpaths, vectorPathValid);

        var bbox = BoundingBox2D.Empty;
        foreach (var shape in shapes)
            foreach (var p in shape.Points)
                bbox = bbox.Include(p.X, p.Y);

        var layers = BuildLayers(shapes);
        shapes = shapes.Select(shape => shape with
        {
            LayerId = layers.First(layer => layer.Color.IsApproximately(shape.LayerColor)).Id,
        }).ToList();

        // "All or nothing" on purpose: a VectorPath must be a fully faithful editable stand-in for
        // the whole imported document, per the "authoritative representation" rule (docs/engineering/
        // ENGINEERING_WORKFLOW.md #5) — one subpath the curve-preserving parser couldn't represent
        // would mean Node Edit mode shows nodes that don't match what's actually rendered for that
        // part of the artwork. Never blocks the import itself; the object just isn't node-editable.
        var vectorPath = vectorPathValid.Value && vectorSubpaths.Count > 0
            ? new VectorPath { Subpaths = vectorSubpaths }
            : null;

        return new ImportedDocument
        {
            Shapes = shapes,
            Layers = layers,
            BoundingBox = bbox,
            SourceFileName = sourceFileName,
            VectorPath = vectorPath,
        };
    }

    /// <summary>Mutable by design (a plain bool can't be threaded through the recursive Walk() calls
    /// without a `ref` parameter on every frame) — set false the first time any shape's geometry can't
    /// be faithfully represented as an editable VectorPath subpath, and never set back to true.</summary>
    private sealed class VectorPathValidity
    {
        public bool Value = true;
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
        List<ImportedShape> output, double viewBoxX, double viewBoxY, double scale, double heightMm,
        List<VectorSubpath> vectorSubpaths, VectorPathValidity vectorPathValid)
    {
        var name = el.Name.LocalName.ToLowerInvariant();
        if (NonRenderedContainers.Contains(name)) return;

        var localTransform = SvgTransform.Parse((string?)el.Attribute("transform"));
        var worldTransform = parentTransform.Multiply(localTransform);

        var (elFill, elStroke) = ReadPaint(el, fill, stroke);

        Position ToMm(double x, double y)
        {
            var (wx, wy) = worldTransform.Apply(x, y);
            var mmX = (wx - viewBoxX) * scale;
            var mmY = heightMm - (wy - viewBoxY) * scale; // SVG Y grows down; ours grows up.
            return new Position(mmX, mmY, 0);
        }

        if (ShapeElements.Contains(name))
        {
            var d = name == "path" ? (string?)el.Attribute("d") ?? string.Empty : null;
            var subpaths = name == "path"
                ? SvgPathParser.Flatten(d!)
                : (SvgShapeFlattener.Flatten(el) is { } single ? [single] : []);

            foreach (var sub in subpaths)
            {
                if (sub.Points.Count < 2) continue;

                var mmPoints = sub.Points.Select(p => ToMm(p.X, p.Y)).ToList();

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

            // Curve-preserving pass, kept independent of the flatten pass above (see VectorPathValidity's
            // own comment) — a <path> gets its real Bézier handles; every other shape element (rect,
            // circle, polygon, ...) is already just a flattened polygon even in today's pipeline, so
            // wrapping those same points as Corner nodes loses nothing and makes them editable too.
            if (vectorPathValid.Value)
            {
                if (name == "path")
                {
                    List<VectorSubpath> curveSubpaths;
                    try { curveSubpaths = SvgPathParser.ParseToVectorSubpaths(d!); }
                    catch { curveSubpaths = []; }

                    if (curveSubpaths.Count != subpaths.Count)
                    {
                        vectorPathValid.Value = false; // parser disagreed with Flatten -- don't trust either half
                    }
                    else
                    {
                        foreach (var sub in curveSubpaths)
                        {
                            if (sub.Nodes.Count < 2) continue;
                            var mmNodes = sub.Nodes.Select(n => new VectorNode(
                                ToMm(n.Anchor.X, n.Anchor.Y),
                                n.HandleIn is { } hi ? ToMm(hi.X, hi.Y) : null,
                                n.HandleOut is { } ho ? ToMm(ho.X, ho.Y) : null,
                                n.Type)).ToList();
                            vectorSubpaths.Add(new VectorSubpath { Nodes = mmNodes, IsClosed = sub.IsClosed });
                        }
                    }
                }
                else
                {
                    foreach (var sub in subpaths)
                    {
                        if (sub.Points.Count < 2) continue;
                        var points = sub.Points;
                        // A closed primitive's flattened points already repeat the first point at the
                        // end (SvgShapeFlattener's own convention, matching every other closed shape in
                        // this codebase) -- VectorSubpath re-adds that wrap automatically when flattened,
                        // so keeping it here would double it up.
                        if (sub.Closed && points.Count > 2 &&
                            Math.Abs(points[0].X - points[^1].X) < 1e-9 && Math.Abs(points[0].Y - points[^1].Y) < 1e-9)
                            points = points[..^1];
                        var nodes = points.Select(p => VectorNode.CornerAt(ToMm(p.X, p.Y))).ToList();
                        vectorSubpaths.Add(new VectorSubpath { Nodes = nodes, IsClosed = sub.Closed });
                    }
                }
            }
        }

        foreach (var child in el.Elements())
            Walk(child, worldTransform, elFill, elStroke, output, viewBoxX, viewBoxY, scale, heightMm, vectorSubpaths, vectorPathValid);
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
