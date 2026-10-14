using System.Globalization;
using System.Text;
using System.Xml.Linq;
using Lasero.Core.GCode;
using Lasero.Core.Grbl;
using Lasero.Core.Import;
using Lasero.Core.Layers;
using Lasero.Core.Scene;

namespace Lasero.Core.Export;

/// <summary>Exports the artwork that would actually be produced, in millimetres: hidden objects and
/// objects excluded from output are left out, and so are layers that are hidden or disabled.
/// Editable Bézier paths remain Bézier paths; imported flattened geometry keeps its existing vertices.
/// Raster assets are embedded in the SVG. Each path carries data-lasero-* attributes naming its layer.</summary>
public static class SvgExporter
{
    private static readonly XNamespace Svg = "http://www.w3.org/2000/svg";
    private static readonly XNamespace XLink = "http://www.w3.org/1999/xlink";

    /// <summary>Writes the SVG atomically: a temp file next to the target is completed first, then
    /// moved over it, so a failed export never leaves a half-written or truncated file behind.</summary>
    public static void ExportToFile(IEnumerable<SceneObject> objects, IEnumerable<LayerSettings>? layers, string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var svg = Export(objects, layers);
        var temp = path + ".tmp";
        var moved = false;
        try
        {
            File.WriteAllText(temp, svg, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.Move(temp, path, overwrite: true);
            moved = true;
        }
        finally
        {
            if (!moved && File.Exists(temp)) File.Delete(temp);
        }
    }

    public static string Export(IEnumerable<SceneObject> objects, IEnumerable<LayerSettings>? layers = null)
    {
        ArgumentNullException.ThrowIfNull(objects);
        var layerList = layers?.ToList() ?? [];
        LayerSettings? LayerFor(ImportedShape shape) => shape.LayerId != Guid.Empty
            ? layerList.FirstOrDefault(layer => layer.Id == shape.LayerId)
            : layerList.FirstOrDefault(layer => layer.Color.IsApproximately(shape.LayerColor));
        // A hidden layer is not on the canvas and a disabled layer is not in the job; neither belongs in the file.
        bool IsExported(ImportedShape shape) => LayerFor(shape) is not { } layer || (layer.IsVisible && layer.IsEnabled);

        var visible = objects
            .Where(item => item.IsVisible && item.IncludeInOutput && item.LocalShapes.Any(IsExported))
            .ToList();
        var bounds = visible.Aggregate(BoundingBox2D.Empty, (box, item) =>
        {
            if (item.IsRaster) return box.Include(item.WorldBounds().MinX, item.WorldBounds().MinY)
                .Include(item.WorldBounds().MaxX, item.WorldBounds().MaxY);
            foreach (var shape in item.GetWorldShapes().Where(IsExported))
                foreach (var point in shape.Points) box = box.Include(point.X, point.Y);
            return box;
        });
        if (bounds.IsEmpty) throw new InvalidOperationException("Návrh neobsahuje viditelnou grafiku.");

        var width = Math.Max(bounds.MaxX - bounds.MinX, 0.001);
        var height = Math.Max(bounds.MaxY - bounds.MinY, 0.001);
        // Core coordinates grow upward; SVG coordinates grow downward. Reflect once at export,
        // including Bézier controls, so opening the SVG matches the canvas and reimport round-trips.
        var flipY = bounds.MinY + bounds.MaxY;
        var root = new XElement(Svg + "svg",
            new XAttribute("version", "1.1"), new XAttribute("width", F(width) + "mm"),
            new XAttribute("height", F(height) + "mm"),
            new XAttribute("viewBox", $"{F(bounds.MinX)} {F(bounds.MinY)} {F(width)} {F(height)}"));

        foreach (var item in visible)
        {
            if (item.IsRaster)
            {
                if (item.RasterFilePath is not { } path || !File.Exists(path))
                    throw new FileNotFoundException($"Zdrojový obrázek objektu „{item.Name}“ nebyl nalezen.", item.RasterFilePath);
                var mime = Path.GetExtension(path).ToLowerInvariant() switch
                { ".jpg" or ".jpeg" => "image/jpeg", ".webp" => "image/webp", _ => "image/png" };
                var encoded = Convert.ToBase64String(File.ReadAllBytes(path));
                // Image pixels already grow downward. Reflect their local Y before the Core
                // transform, then reflect the world Y for SVG; the bitmap stays upright.
                Position ImagePoint(double x, double y)
                {
                    var localY = item.LocalBounds.MinY + item.LocalBounds.MaxY - y;
                    var world = item.Transform.Apply(new Position(x, localY, 0), item.LocalPivot);
                    return world with { Y = flipY - world.Y };
                }
                var p0 = ImagePoint(0, 0);
                var px = ImagePoint(1, 0);
                var py = ImagePoint(0, 1);
                var raster = new XElement(Svg + "image", new XAttribute("x", F(item.LocalBounds.MinX)),
                    new XAttribute("y", F(item.LocalBounds.MinY)), new XAttribute("width", F(item.LocalBounds.Width)),
                    new XAttribute("height", F(item.LocalBounds.Height)),
                    new XAttribute("preserveAspectRatio", "none"),
                    new XAttribute("transform", $"matrix({F(px.X-p0.X)} {F(px.Y-p0.Y)} {F(py.X-p0.X)} {F(py.Y-p0.Y)} {F(p0.X)} {F(p0.Y)})"),
                    new XAttribute("href", $"data:{mime};base64,{encoded}"),
                    new XAttribute(XLink + "href", $"data:{mime};base64,{encoded}"));
                root.Add(raster);
                continue;
            }

            var contours = BuildContours(item, flipY).Where(contour => IsExported(contour.Shape)).ToList();
            foreach (var group in contours.GroupBy(contour => (
                contour.Shape.GeometrySetId, contour.Shape.LayerId,
                contour.Shape.LayerColor, contour.Shape.PreferredMode, contour.Shape.IsClosed)))
            {
                var representative = group.First().Shape;
                var layer = LayerFor(representative);
                var mode = layer?.Mode ?? representative.PreferredMode;
                var color = (layer?.Color ?? representative.LayerColor).ToHex();
                var closed = representative.IsClosed;
                // FillAndCut closed shapes get both a fill and a stroke, which the importer reads back
                // as one Fill operation plus one Cut operation.
                var fill = closed && (mode is LayerMode.Fill or LayerMode.FillAndCut) ? color : "none";
                var stroke = !closed || (mode is LayerMode.Cut or LayerMode.FillAndCut) ? color : "none";
                var pathElement = new XElement(Svg + "path", new XAttribute("d", string.Join(" ", group.Select(part => part.Data))),
                    new XAttribute("fill", fill), new XAttribute("fill-rule", "nonzero"),
                    new XAttribute("stroke", stroke), new XAttribute("stroke-width", "0.1"));
                if (layer is not null)
                {
                    pathElement.Add(new XAttribute("data-lasero-layer", layer.Name),
                        new XAttribute("data-lasero-layer-id", layer.Id.ToString("D")),
                        new XAttribute("data-lasero-mode", layer.Mode.ToString()),
                        new XAttribute("data-lasero-speed", F(layer.Speed)),
                        new XAttribute("data-lasero-power", F(layer.Power)));
                }
                root.Add(pathElement);
            }
        }
        return new XDocument(new XDeclaration("1.0", "utf-8", null), root).ToString();
    }

    /// <summary>One entry per contour of the object: its layer-carrying shape and the path data. Curves
    /// stay curves whenever the editable path can be matched to the shapes — one-to-one, or, when the
    /// counts differ (a split or merged outline), by wholesale use of the editable path as long as every
    /// shape belongs to the same layer and compound group. Only otherwise are vertices exported.</summary>
    private static List<(ImportedShape Shape, string Data)> BuildContours(SceneObject item, double flipY)
    {
        var shapes = item.GetWorldShapes();
        var vector = item.GetWorldVectorPath();
        var contours = new List<(ImportedShape Shape, string Data)>();

        if (vector is not null && vector.Subpaths.Count == shapes.Count)
        {
            for (var index = 0; index < shapes.Count; index++)
                contours.Add((shapes[index], PathData(vector.Subpaths[index], flipY)));
        }
        else if (vector is not null && vector.Subpaths.Count > 0 && shapes.Count > 0 && shapes.All(shape =>
                     shape.LayerId == shapes[0].LayerId && shape.LayerColor == shapes[0].LayerColor
                     && shape.PreferredMode == shapes[0].PreferredMode && shape.GeometrySetId == shapes[0].GeometrySetId))
        {
            foreach (var subpath in vector.Subpaths)
                contours.Add((shapes[0] with { IsClosed = subpath.IsClosed }, PathData(subpath, flipY)));
        }
        else
        {
            foreach (var shape in shapes)
                contours.Add((shape, PathData(shape, flipY)));
        }

        contours.RemoveAll(contour => string.IsNullOrWhiteSpace(contour.Data));
        return contours;
    }

    private static string PathData(VectorSubpath sub, double flipY)
    {
        if (sub.Nodes.Count == 0) return string.Empty;
        var first = sub.Nodes[0];
        var data = $"M {F(first.Anchor.X)} {F(flipY - first.Anchor.Y)}";
        for (var i = 0; i < sub.SegmentCount; i++)
        {
            var (a, b) = sub.Segment(i);
            data += VectorSubpath.IsStraightSegment(a, b)
                ? $" L {F(b.Anchor.X)} {F(flipY - b.Anchor.Y)}"
                : $" C {F((a.HandleOut ?? a.Anchor).X)} {F(flipY - (a.HandleOut ?? a.Anchor).Y)} {F((b.HandleIn ?? b.Anchor).X)} {F(flipY - (b.HandleIn ?? b.Anchor).Y)} {F(b.Anchor.X)} {F(flipY - b.Anchor.Y)}";
        }
        return data + (sub.IsClosed ? " Z" : "");
    }

    private static string PathData(ImportedShape shape, double flipY)
    {
        if (shape.Points.Count == 0) return string.Empty;
        return $"M {F(shape.Points[0].X)} {F(flipY - shape.Points[0].Y)}"
            + string.Concat(shape.Points.Skip(1).Select(point => $" L {F(point.X)} {F(flipY - point.Y)}"))
            + (shape.IsClosed ? " Z" : "");
    }

    private static string F(double value) => value.ToString("0.######", CultureInfo.InvariantCulture);
}
