using System.Globalization;
using System.Xml.Linq;
using Lasero.Core.GCode;
using Lasero.Core.Grbl;
using Lasero.Core.Import;
using Lasero.Core.Layers;
using Lasero.Core.Scene;

namespace Lasero.Core.Export;

/// <summary>Exports visible scene artwork in millimetres. Editable Bézier paths remain Bézier paths;
/// imported flattened geometry keeps its existing vertices. Raster assets are embedded in the SVG.</summary>
public static class SvgExporter
{
    private static readonly XNamespace Svg = "http://www.w3.org/2000/svg";
    private static readonly XNamespace XLink = "http://www.w3.org/1999/xlink";

    public static string Export(IEnumerable<SceneObject> objects, IEnumerable<LayerSettings>? layers = null)
    {
        ArgumentNullException.ThrowIfNull(objects);
        var layerList = layers?.ToList() ?? [];
        LayerSettings? LayerFor(ImportedShape shape) => shape.LayerId != Guid.Empty
            ? layerList.FirstOrDefault(layer => layer.Id == shape.LayerId)
            : layerList.FirstOrDefault(layer => layer.Color.IsApproximately(shape.LayerColor));
        bool IsVisible(ImportedShape shape) => LayerFor(shape)?.IsVisible ?? true;

        var visible = objects.Where(item => item.IsVisible && item.LocalShapes.Any(IsVisible)).ToList();
        var bounds = visible.Aggregate(BoundingBox2D.Empty, (box, item) =>
        {
            if (item.IsRaster) return box.Include(item.WorldBounds().MinX, item.WorldBounds().MinY)
                .Include(item.WorldBounds().MaxX, item.WorldBounds().MaxY);
            foreach (var shape in item.GetWorldShapes().Where(IsVisible))
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

            var shapes = item.GetWorldShapes();
            var vector = item.GetWorldVectorPath();
            var aligned = vector is not null && vector.Subpaths.Count == shapes.Count;
            var contours = shapes.Select((shape, index) => new
            {
                Shape = shape,
                Data = aligned ? PathData(vector!.Subpaths[index], flipY) : PathData(shape, flipY),
            }).Where(contour => IsVisible(contour.Shape) && !string.IsNullOrWhiteSpace(contour.Data));
            foreach (var group in contours.GroupBy(contour => (
                contour.Shape.GeometrySetId, contour.Shape.LayerId,
                contour.Shape.LayerColor, contour.Shape.PreferredMode, contour.Shape.IsClosed)))
            {
                var representative = group.First().Shape;
                var layer = LayerFor(representative);
                var mode = layer?.Mode ?? representative.PreferredMode;
                var color = (layer?.Color ?? representative.LayerColor).ToHex();
                var closed = representative.IsClosed;
                var fill = closed && (mode is LayerMode.Fill or LayerMode.FillAndCut) ? color : "none";
                var stroke = !closed || (mode is LayerMode.Cut or LayerMode.FillAndCut) ? color : "none";
                root.Add(new XElement(Svg + "path", new XAttribute("d", string.Join(" ", group.Select(part => part.Data))),
                    new XAttribute("fill", fill), new XAttribute("fill-rule", "nonzero"),
                    new XAttribute("stroke", stroke), new XAttribute("stroke-width", "0.1")));
            }
        }
        return new XDocument(new XDeclaration("1.0", "utf-8", null), root).ToString();
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
