using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml;
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
/// Inkscape/Illustrator still import sensibly. An element that has both a visible
/// fill and a visible stroke yields both operations (a Fill shape and a Cut shape).
/// </summary>
public static class SvgImporter
{
    private static readonly XNamespace XLink = "http://www.w3.org/1999/xlink";

    /// <summary>Flatness of curved geometry in the output, in millimetres.</summary>
    private const double FlattenToleranceMm = 0.02;

    private const int MaxUseDepth = 16;

    /// <param name="targetWidthMm">Width the artwork is scaled to, unless <paramref name="useDocumentSize"/>
    /// is set and the file declares its own physical size.</param>
    /// <param name="useDocumentSize">Use the physical size written in the SVG (width/height with units,
    /// px at 96 dpi). Falls back to <paramref name="targetWidthMm"/> when the file declares none.</param>
    public static ImportedDocument Import(string svgText, double targetWidthMm, string? sourceFileName = null, bool useDocumentSize = false)
    {
        var doc = ParseXml(svgText);
        var svg = doc.Root ?? throw new InvalidDataException("Neplatný soubor SVG: chybí kořenový element <svg>.");
        if (!svg.Name.LocalName.Equals("svg", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Neplatný soubor SVG: kořenový element není <svg>.");

        var ids = new Dictionary<string, XElement>(StringComparer.Ordinal);
        foreach (var element in svg.DescendantsAndSelf())
            if ((string?)element.Attribute("id") is { Length: > 0 } id)
                ids.TryAdd(id, element);

        var styles = SvgStyleSheet.Build(svg);
        var ctx = new ImportContext(svg, styles, ids);
        if (styles.UnsupportedSelectors.Count > 0)
            ctx.Warn($"CSS selektory „{string.Join("“, „", styles.UnsupportedSelectors.Distinct().Take(3))}“ nejsou podporovány a byly ignorovány.");
        if (styles.SkippedAtRules > 0)
            ctx.Warn("CSS pravidla začínající @ (například @media) byla ignorována.");

        var (viewBoxX, viewBoxY, viewBoxW, viewBoxH, physicalWidthMm, physicalHeightMm) = ReadDocumentSize(svg);
        double mmPerUnit;
        if (useDocumentSize && (physicalWidthMm is not null || physicalHeightMm is not null))
            mmPerUnit = physicalWidthMm is { } pw ? pw / viewBoxW : physicalHeightMm!.Value / viewBoxH;
        else
        {
            if (!double.IsFinite(targetWidthMm) || targetWidthMm <= 0)
                throw new ArgumentOutOfRangeException(nameof(targetWidthMm), "Cílová šířka musí být kladné číslo.");
            mmPerUnit = targetWidthMm / viewBoxW;
        }
        if (!double.IsFinite(mmPerUnit) || mmPerUnit <= 0)
            throw new InvalidDataException("Neplatný soubor SVG: velikost kresby se nepodařilo určit.");

        ctx.ViewBoxX = viewBoxX;
        ctx.ViewBoxY = viewBoxY;
        ctx.Scale = mmPerUnit;
        ctx.HeightMm = viewBoxH * mmPerUnit;

        Walk(svg, SvgTransform.Identity, new Inherited("black", "none", false, false, "black"), ctx);

        var shapes = ctx.Shapes;
        var bbox = BoundingBox2D.Empty;
        foreach (var shape in shapes)
            foreach (var p in shape.Points)
                bbox = bbox.Include(p.X, p.Y);

        var layers = BuildLayers(shapes);
        shapes = shapes.Select(shape => shape with
        {
            LayerId = layers.First(layer => layer.Mode == shape.PreferredMode && layer.Color.IsApproximately(shape.LayerColor)).Id,
        }).ToList();

        var firstShape = shapes.FirstOrDefault();
        var hasUniformEditableMetadata = firstShape is not null
            && shapes.Count == ctx.VectorSubpaths.Count
            && shapes.All(shape => shape.LayerId == firstShape.LayerId
                && shape.LayerColor == firstShape.LayerColor
                && shape.PreferredMode == firstShape.PreferredMode
                && shape.GeometrySetId == firstShape.GeometrySetId);

        // "All or nothing" on purpose: a VectorPath must be a fully faithful editable stand-in for
        // the whole imported document, per the "authoritative representation" rule (docs/engineering/
        // ENGINEERING_WORKFLOW.md #5) — one subpath the curve-preserving parser couldn't represent
        // would mean Node Edit mode shows nodes that don't match what's actually rendered for that
        // part of the artwork. Never blocks the import itself; the object just isn't node-editable.
        var vectorPath = ctx.VectorPathValid && hasUniformEditableMetadata
            ? new VectorPath { Subpaths = ctx.VectorSubpaths }
            : null;

        if (shapes.Count == 0)
            ctx.Warn("SVG neobsahuje žádnou viditelnou kreslenou geometrii s použitelnou barvou obrysu nebo výplně.");

        return new ImportedDocument
        {
            Shapes = shapes,
            Layers = layers,
            BoundingBox = bbox,
            SourceFileName = sourceFileName,
            VectorPath = vectorPath,
            ImportWarnings = ctx.Warnings,
        };
    }

    /// <summary>Parses the SVG text. DTDs stay enabled because Illustrator files declare entities for
    /// their namespaces, but nothing external is ever fetched and entity expansion is capped so a
    /// crafted "billion laughs" file fails instead of exhausting memory.</summary>
    private static XDocument ParseXml(string svgText)
    {
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Parse,
            XmlResolver = null,
            MaxCharactersFromEntities = 1_000_000,
        };
        using var text = new StringReader(svgText);
        using var reader = XmlReader.Create(text, settings);
        return XDocument.Load(reader);
    }

    private sealed class ImportContext(XElement root, SvgStyleSheet styles, Dictionary<string, XElement> ids)
    {
        public XElement Root { get; } = root;
        public SvgStyleSheet Styles { get; } = styles;
        public Dictionary<string, XElement> ById { get; } = ids;
        public List<string> Warnings { get; } = [];
        private readonly HashSet<string> _seenWarnings = [];

        public List<ImportedShape> Shapes { get; set; } = [];
        public List<VectorSubpath> VectorSubpaths { get; } = [];
        /// <summary>Set false the first time any shape's geometry can't be faithfully represented as an
        /// editable VectorPath subpath, and never set back to true.</summary>
        public bool VectorPathValid { get; set; } = true;

        public double ViewBoxX { get; set; }
        public double ViewBoxY { get; set; }
        public double Scale { get; set; } = 1;
        public double HeightMm { get; set; }
        public HashSet<XElement> UseStack { get; } = [];

        public void Warn(string message)
        {
            if (_seenWarnings.Add(message)) Warnings.Add(message);
        }
    }

    /// <summary>Presentation state that CSS inherits down the element tree.</summary>
    private readonly record struct Inherited(string Fill, string Stroke, bool EvenOdd, bool Hidden, string Color);

    private static (double X, double Y, double W, double H)? ParseViewBox(XElement element)
    {
        var raw = (string?)element.Attribute("viewBox");
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var parts = NumberPattern.Matches(raw)
            .Select(match => double.Parse(match.Value, NumberStyles.Float, CultureInfo.InvariantCulture))
            .ToArray();
        return parts.Length == 4 && parts[2] > 0 && parts[3] > 0 ? (parts[0], parts[1], parts[2], parts[3]) : null;
    }

    private static readonly Regex NumberPattern = new(@"[-+]?(?:\d+\.?\d*|\.\d+)(?:[eE][-+]?\d+)?", RegexOptions.Compiled);

    /// <summary>The user-unit canvas (viewBox, or width/height when there is none) plus the physical size
    /// in millimetres when the file states one. Percent and missing sizes have no physical meaning.</summary>
    private static (double X, double Y, double W, double H, double? PhysicalWidthMm, double? PhysicalHeightMm) ReadDocumentSize(XElement svg)
    {
        var hasWidth = SvgLength.TryParse((string?)svg.Attribute("width"), out var widthPx);
        var hasHeight = SvgLength.TryParse((string?)svg.Attribute("height"), out var heightPx);
        if (hasWidth && widthPx <= 0) hasWidth = false;
        if (hasHeight && heightPx <= 0) hasHeight = false;
        double? physicalWidth = hasWidth ? widthPx / SvgLength.PxPerMm : null;
        double? physicalHeight = hasHeight ? heightPx / SvgLength.PxPerMm : null;

        if (ParseViewBox(svg) is { } vb)
            return (vb.X, vb.Y, vb.W, vb.H, physicalWidth, physicalHeight);

        var w = hasWidth ? widthPx : 100;
        var h = hasHeight ? heightPx : 100;
        return (0, 0, w, h, physicalWidth, physicalHeight);
    }

    private static readonly HashSet<string> NonRenderedContainers =
        ["defs", "clippath", "mask", "symbol", "pattern", "style", "title", "desc", "metadata", "lineargradient", "radialgradient", "filter", "marker"];
    private static readonly HashSet<string> ShapeElements = ["path", "rect", "circle", "ellipse", "line", "polyline", "polygon"];

    private static void Walk(XElement el, SvgTransform parentTransform, Inherited state, ImportContext ctx)
    {
        var name = el.Name.LocalName.ToLowerInvariant();
        if (NonRenderedContainers.Contains(name)) return;

        var props = ReadProperties(el, ctx);
        string? Prop(string key) => props.TryGetValue(key, out var value) ? value : null;

        if (string.Equals(Prop("display"), "none", StringComparison.OrdinalIgnoreCase)) return;

        var hiddenHere = Prop("visibility")?.ToLowerInvariant() switch
        {
            "hidden" or "collapse" => true,
            "visible" => false,
            _ => state.Hidden,
        };
        if (!hiddenHere)
        {
            if (name is "text" or "image" or "foreignobject")
                throw new InvalidDataException(
                    $"SVG obsahuje nepodporovaný viditelný prvek <{name}>"
                    + (name == "text" ? " (text je potřeba v původním programu převést na křivky)" : string.Empty)
                    + ". Import byl zastaven, aby v návrhu nechyběla grafika.");
            var clip = Prop("clip-path");
            var mask = Prop("mask");
            if ((!string.IsNullOrWhiteSpace(clip) && !string.Equals(clip, "none", StringComparison.OrdinalIgnoreCase)) ||
                (!string.IsNullOrWhiteSpace(mask) && !string.Equals(mask, "none", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException(
                    $"SVG používá ořezovou cestu (clip-path) nebo masku (mask) u prvku <{name}>, kterou LASERO neumí bezpečně importovat.");
            if (Prop("filter") is { Length: > 0 } filter && !filter.Equals("none", StringComparison.OrdinalIgnoreCase))
                ctx.Warn("Filtry (filter) nejsou podporovány a byly ignorovány.");
        }

        var localTransform = SvgTransform.Parse((string?)el.Attribute("transform"), ctx.Warn);
        var worldTransform = parentTransform.Multiply(localTransform);
        if (name == "svg" && !ReferenceEquals(el, ctx.Root))
            worldTransform = worldTransform.Multiply(NestedViewport(el, ctx));

        if (worldTransform.IsDegenerate)
        {
            if (!hiddenHere)
                ctx.Warn($"Prvek <{name}> má degenerovanou transformaci (například scale(0)) a byl vynechán.");
            return;
        }

        var elFill = ReadInherited(Prop("fill"), state.Fill);
        var elStroke = ReadInherited(Prop("stroke"), state.Stroke);
        var elColor = ReadInherited(Prop("color"), state.Color);
        var elEvenOdd = Prop("fill-rule")?.Trim().ToLowerInvariant() switch
        {
            "evenodd" => true,
            "nonzero" => false,
            _ => state.EvenOdd,
        };
        var childState = new Inherited(elFill, elStroke, elEvenOdd, hiddenHere, elColor);

        if (name == "use")
        {
            if (!hiddenHere) WalkUse(el, worldTransform, childState, ctx);
            return;
        }

        if (!hiddenHere && ShapeElements.Contains(name))
            EmitShapes(el, name, worldTransform, elFill, elStroke, elColor, elEvenOdd, ctx);

        foreach (var child in el.Elements())
            Walk(child, worldTransform, childState, ctx);
    }

    private static string ReadInherited(string? value, string inherited) =>
        string.IsNullOrWhiteSpace(value) || value.Trim().Equals("inherit", StringComparison.OrdinalIgnoreCase)
            ? inherited
            : value.Trim();

    /// <summary>Presentation attributes, then the style sheet, then the inline style attribute —
    /// later sources win, which is the SVG/CSS cascade.</summary>
    private static Dictionary<string, string> ReadProperties(XElement el, ImportContext ctx)
    {
        var props = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var attribute in el.Attributes())
            if (attribute.Name.Namespace == XNamespace.None && attribute.Name.LocalName != "style")
                props[attribute.Name.LocalName] = attribute.Value.Trim();
        if (ctx.Styles.HasRules)
            foreach (var declaration in ctx.Styles.Match(el))
                props[declaration.Key] = declaration.Value.Trim();
        var inline = (string?)el.Attribute("style");
        if (!string.IsNullOrWhiteSpace(inline))
            foreach (var declaration in SvgStyleSheet.ParseDeclarations(inline))
                props[declaration.Key] = declaration.Value.Trim();
        return props;
    }

    /// <summary>Basic &lt;use&gt;: instantiates the referenced element (or a symbol's children) at x/y, under
    /// the use element's own transform and inherited style. Cycles and runaway nesting are refused.</summary>
    private static void WalkUse(XElement use, SvgTransform world, Inherited state, ImportContext ctx)
    {
        var href = ((string?)use.Attribute(XLink + "href") ?? (string?)use.Attribute("href"))?.Trim();
        if (string.IsNullOrEmpty(href) || href[0] != '#')
        {
            ctx.Warn("Prvek <use> odkazuje mimo soubor nebo nemá odkaz a byl vynechán.");
            return;
        }
        if (!ctx.ById.TryGetValue(href[1..], out var target))
        {
            ctx.Warn($"Prvek <use> odkazuje na neexistující id „{href[1..]}“ a byl vynechán.");
            return;
        }
        if (ctx.UseStack.Count >= MaxUseDepth || ctx.UseStack.Contains(target))
        {
            ctx.Warn($"Prvek <use> odkazující na „{href[1..]}“ se cyklí nebo je vnořen příliš hluboko a byl vynechán.");
            return;
        }

        var x = SvgLength.Get(use, "x", 0, ctx.Warn);
        var y = SvgLength.Get(use, "y", 0, ctx.Warn);
        var placed = world.Multiply(new SvgTransform(1, 0, 0, 1, x, y));

        ctx.UseStack.Add(target);
        try
        {
            if (target.Name.LocalName.Equals("symbol", StringComparison.OrdinalIgnoreCase))
            {
                if (target.Attribute("viewBox") is not null)
                    ctx.Warn("viewBox prvku <symbol> se při vkládání přes <use> nepoužije; kresba může mít jiné měřítko.");
                foreach (var child in target.Elements())
                    Walk(child, placed, state, ctx);
            }
            else
            {
                Walk(target, placed, state, ctx);
            }
        }
        finally
        {
            ctx.UseStack.Remove(target);
        }
    }

    /// <summary>Viewport transform of a nested &lt;svg&gt;: x/y offset plus viewBox fitted into width/height
    /// following preserveAspectRatio (meet/slice and the nine alignments, or none). Overflow clipping
    /// is not applied.</summary>
    private static SvgTransform NestedViewport(XElement el, ImportContext ctx)
    {
        var x = SvgLength.Get(el, "x", 0, ctx.Warn);
        var y = SvgLength.Get(el, "y", 0, ctx.Warn);
        var offset = new SvgTransform(1, 0, 0, 1, x, y);
        if (ParseViewBox(el) is not { } vb) return offset;

        var hasWidth = SvgLength.TryParse((string?)el.Attribute("width"), out var width) && width > 0;
        var hasHeight = SvgLength.TryParse((string?)el.Attribute("height"), out var height) && height > 0;
        if (!hasWidth && !hasHeight) return offset;
        if (!hasWidth) width = hasHeight ? vb.W * height / vb.H : vb.W;
        if (!hasHeight) height = vb.H * width / vb.W;

        var tokens = ((string?)el.Attribute("preserveAspectRatio") ?? "xMidYMid meet")
            .Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries)
            .Where(token => token != "defer")
            .ToArray();
        var align = tokens.Length > 0 ? tokens[0] : "xMidYMid";
        var slice = tokens.Length > 1 && tokens[1] == "slice";

        var sx = width / vb.W;
        var sy = height / vb.H;
        if (align != "none")
        {
            var uniform = slice ? Math.Max(sx, sy) : Math.Min(sx, sy);
            sx = sy = uniform;
        }
        var spareX = width - vb.W * sx;
        var spareY = height - vb.H * sy;
        var fractionX = align.Contains("xMid") ? 0.5 : align.Contains("xMax") ? 1 : 0;
        var fractionY = align.Contains("YMid") ? 0.5 : align.Contains("YMax") ? 1 : 0;

        return offset
            .Multiply(new SvgTransform(1, 0, 0, 1, spareX * fractionX, spareY * fractionY))
            .Multiply(new SvgTransform(sx, 0, 0, sy, -vb.X * sx, -vb.Y * sy));
    }

    private static void EmitShapes(
        XElement el, string name, SvgTransform world, string fillPaint, string strokePaint, string currentColor,
        bool evenOdd, ImportContext ctx)
    {
        Position ToMm(double x, double y)
        {
            var (wx, wy) = world.Apply(x, y);
            var mmX = (wx - ctx.ViewBoxX) * ctx.Scale;
            var mmY = ctx.HeightMm - (wy - ctx.ViewBoxY) * ctx.Scale; // SVG Y grows down; ours grows up.
            return new Position(mmX, mmY, 0);
        }

        // Flatness is chosen in output millimetres, so convert it into this element's local units.
        var localUnitToMm = ctx.Scale * Math.Sqrt(Math.Abs(world.Determinant));
        var tolerance = double.IsFinite(localUnitToMm) && localUnitToMm > 0
            ? Math.Clamp(FlattenToleranceMm / localUnitToMm, 1e-6, 1e6)
            : SvgPathParser.DefaultTolerance;

        var fillColor = ResolvePaint(fillPaint, currentColor, ctx, "výplň");
        var strokeColor = ResolvePaint(strokePaint, currentColor, ctx, "obrys");
        if (strokeColor is { } candidate && candidate.IsApproximately(RgbColor.Red))
            strokeColor = RgbColor.Red; // lasero-app convention: red outline = Cut

        string? d = null;
        List<SvgSubpath> subpaths;
        IReadOnlyList<VectorSubpath>? shapeCurves = null;
        if (name == "path")
        {
            d = (string?)el.Attribute("d") ?? string.Empty;
            subpaths = SvgPathParser.Flatten(d, tolerance);
        }
        else if (SvgShapeFlattener.Build(el, tolerance, ctx.Warn) is { } built)
        {
            subpaths = [built.Flat];
            shapeCurves = [built.Curve];
        }
        else
        {
            subpaths = [];
            shapeCurves = [];
        }

        // fill-rule="evenodd" is mapped onto the winding convention the rest of Lasero uses
        // (NonZero per compound group, docs/fill-winding-contract.md): rings are re-wound by
        // nesting depth so the hole/island structure the author drew is preserved. A nonzero
        // (default) element keeps its source winding untouched.
        var reverseFlags = evenOdd ? EvenOddReverseFlags(subpaths, ToMm) : null;
        if (reverseFlags is not null)
            for (var index = 0; index < subpaths.Count; index++)
                if (reverseFlags[index]) subpaths[index].Points.Reverse();

        foreach (var sub in subpaths)
        {
            var emitted = 0;
            if (sub.Points.Count >= 2)
            {
                var mmPoints = sub.Points.Select(p => ToMm(p.X, p.Y)).ToList();
                // An open outline cannot be filled by the laser, so its fill is only kept when it is
                // the sole thing the element asks for (it then still shows up as geometry on the canvas).
                if (fillColor is { } fill && (sub.Closed || strokeColor is null))
                {
                    ctx.Shapes.Add(new ImportedShape
                    {
                        Points = mmPoints, IsClosed = sub.Closed, LayerColor = fill, PreferredMode = LayerMode.Fill,
                    });
                    emitted++;
                }
                if (strokeColor is { } stroke)
                {
                    ctx.Shapes.Add(new ImportedShape
                    {
                        Points = mmPoints, IsClosed = sub.Closed, LayerColor = stroke, PreferredMode = LayerMode.Cut,
                    });
                    emitted++;
                }
            }
            // One subpath must map to exactly one shape for the editable VectorPath to line up.
            if (emitted != 1) ctx.VectorPathValid = false;
        }

        // Curve-preserving pass, kept independent of the flatten pass above — a <path> gets its real
        // Bézier handles; circles, ellipses and rounded rectangles get real arc nodes; the straight-edged
        // primitives (rect, polygon, polyline, line) are corner nodes.
        if (!ctx.VectorPathValid) return;

        List<VectorSubpath> curves;
        if (name == "path")
        {
            try { curves = SvgPathParser.ParseToVectorSubpaths(d!); }
            catch (Exception ex) when (ex is FormatException or InvalidOperationException or ArgumentException or IndexOutOfRangeException)
            {
                ctx.Warn("Křivky cesty se nepodařilo zachovat; cesta byla zploštěna a nebude upravitelná po uzlech.");
                curves = [];
            }
        }
        else
        {
            curves = shapeCurves!.ToList();
        }

        if (curves.Count != subpaths.Count)
        {
            ctx.VectorPathValid = false; // parser disagreed with Flatten -- don't trust either half
            return;
        }
        for (var index = 0; index < curves.Count; index++)
        {
            var curve = curves[index];
            if (curve.Nodes.Count < 2)
            {
                ctx.VectorPathValid = false;
                continue;
            }
            if (reverseFlags is not null && reverseFlags[index]) curve = ReverseSubpath(curve);
            var mmNodes = curve.Nodes.Select(n => new VectorNode(
                ToMm(n.Anchor.X, n.Anchor.Y),
                n.HandleIn is { } hi ? ToMm(hi.X, hi.Y) : null,
                n.HandleOut is { } ho ? ToMm(ho.X, ho.Y) : null,
                n.Type)).ToList();
            ctx.VectorSubpaths.Add(new VectorSubpath { Nodes = mmNodes, IsClosed = curve.IsClosed });
        }
    }

    /// <summary>Turns a paint value into a colour, or null for "paints nothing". Anything that is not
    /// understood is reported instead of silently vanishing.</summary>
    private static RgbColor? ResolvePaint(string paint, string currentColor, ImportContext ctx, string role)
    {
        switch (RgbColor.Parse(paint, out var color))
        {
            case ColorParseStatus.Color:
                return color;
            case ColorParseStatus.None:
                return null;
            case ColorParseStatus.CurrentColor:
                return RgbColor.TryParse(currentColor, out var current) ? current : null;
            case ColorParseStatus.Reference:
                return ResolveReference(paint, ctx, role);
            default:
                ctx.Warn($"Barva „{paint}“ ({role}) není rozpoznána; objekty s touto barvou nebyly naimportovány.");
                return null;
        }
    }

    private static readonly Regex UrlReference = new(@"url\(\s*['""]?#([^'"")\s]+)['""]?\s*\)\s*(.*)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>A gradient has no single laser equivalent: its first stop colour is used and the
    /// operator is told. Patterns and missing servers paint nothing.</summary>
    private static RgbColor? ResolveReference(string paint, ImportContext ctx, string role)
    {
        var match = UrlReference.Match(paint);
        var id = match.Success ? match.Groups[1].Value : paint;
        if (match.Success && match.Groups[2].Value.Trim() is { Length: > 0 } fallback
            && !ctx.ById.ContainsKey(id) && RgbColor.TryParse(fallback, out var fallbackColor))
        {
            ctx.Warn($"Odkaz „{id}“ ({role}) neexistuje; použita záložní barva {fallbackColor.ToHex()}.");
            return fallbackColor;
        }

        if (!match.Success || !ctx.ById.TryGetValue(id, out var server))
        {
            ctx.Warn($"Odkaz na barvu „{paint}“ ({role}) nelze vyřešit; objekty nebyly naimportovány.");
            return null;
        }

        var kind = server.Name.LocalName.ToLowerInvariant();
        if (kind is "lineargradient" or "radialgradient")
        {
            var stop = FirstStop(server, ctx);
            if (stop is not null)
            {
                var stopProps = ReadProperties(stop, ctx);
                var stopColor = stopProps.TryGetValue("stop-color", out var raw) ? raw : "black";
                if (RgbColor.TryParse(stopColor, out var color))
                {
                    ctx.Warn($"Přechod „{id}“ ({role}) byl nahrazen první barvou {color.ToHex()}.");
                    return color;
                }
            }
            ctx.Warn($"Přechod „{id}“ ({role}) nemá použitelnou barvu; objekty nebyly naimportovány.");
            return null;
        }

        ctx.Warn($"Výplň nebo obrys vzorem „{id}“ není podporován; objekty nebyly naimportovány.");
        return null;
    }

    private static XElement? FirstStop(XElement gradient, ImportContext ctx)
    {
        var current = gradient;
        for (var hop = 0; hop < 5 && current is not null; hop++)
        {
            var stop = current.Elements().FirstOrDefault(e => e.Name.LocalName.Equals("stop", StringComparison.OrdinalIgnoreCase));
            if (stop is not null) return stop;
            var href = ((string?)current.Attribute(XLink + "href") ?? (string?)current.Attribute("href"))?.Trim();
            if (string.IsNullOrEmpty(href) || href[0] != '#' || !ctx.ById.TryGetValue(href[1..], out var next)) return null;
            current = next;
        }
        return null;
    }

    /// <summary>For one element's subpaths: which closed rings must be reversed so that even nesting
    /// depth is wound positive (fill) and odd depth negative (hole) in mm space. Open or degenerate
    /// subpaths are never reversed. Depth = number of other closed rings containing the ring.</summary>
    private static bool[] EvenOddReverseFlags(IReadOnlyList<SvgSubpath> subpaths, Func<double, double, Position> toMm)
    {
        var rings = subpaths
            .Select(sub => sub.Closed && sub.Points.Count >= 3
                ? sub.Points.Select(p => toMm(p.X, p.Y)).ToList()
                : null)
            .ToList();
        var flags = new bool[subpaths.Count];
        for (var i = 0; i < rings.Count; i++)
        {
            if (rings[i] is not { } ring) continue;
            var depth = 0;
            for (var j = 0; j < rings.Count; j++)
                if (j != i && rings[j] is { } other && Contains(other, ring[0])) depth++;
            var area = 0d;
            for (var k = 0; k < ring.Count; k++)
            {
                var a = ring[k];
                var b = ring[(k + 1) % ring.Count];
                area += a.X * b.Y - b.X * a.Y;
            }
            var wantPositive = depth % 2 == 0;
            flags[i] = area != 0 && (area > 0) != wantPositive;
        }
        return flags;
    }

    private static bool Contains(IReadOnlyList<Position> ring, Position point)
    {
        var inside = false;
        for (int current = 0, previous = ring.Count - 1; current < ring.Count; previous = current++)
        {
            var c = ring[current];
            var p = ring[previous];
            if ((c.Y > point.Y) == (p.Y > point.Y)) continue;
            if (point.X < (p.X - c.X) * (point.Y - c.Y) / (p.Y - c.Y) + c.X) inside = !inside;
        }
        return inside;
    }

    private static VectorSubpath ReverseSubpath(VectorSubpath subpath) => subpath with
    {
        Nodes = subpath.Nodes.Reverse().Select(node => node with { HandleIn = node.HandleOut, HandleOut = node.HandleIn }).ToList(),
    };

    /// <summary>One layer per distinct colour AND operation: a black fill and a black outline on the
    /// same artwork are different jobs and must not share one layer.</summary>
    private static List<LayerSettings> BuildLayers(List<ImportedShape> shapes)
    {
        var layers = new List<LayerSettings>();
        foreach (var shape in shapes)
        {
            if (layers.Any(l => l.Mode == shape.PreferredMode && l.Color.IsApproximately(shape.LayerColor)))
                continue;

            var name = shape.PreferredMode == LayerMode.Cut && shape.LayerColor.IsApproximately(RgbColor.Red) ? "Rez"
                : shape.PreferredMode == LayerMode.Fill && shape.LayerColor.IsApproximately(RgbColor.Black) ? "Gravír"
                : shape.LayerColor.ToHex();

            layers.Add(LayerSettings.CreateDefault(shape.LayerColor, shape.PreferredMode, name));
        }

        // Same colour used for both operations: say which is which in the layer list.
        foreach (var layer in layers)
        {
            if (layer.Name is "Rez" or "Gravír") continue;
            if (layers.Any(other => !ReferenceEquals(other, layer) && other.Color.IsApproximately(layer.Color)))
                layer.Name += layer.Mode == LayerMode.Cut ? " · čára" : " · výplň";
        }
        return layers;
    }
}
