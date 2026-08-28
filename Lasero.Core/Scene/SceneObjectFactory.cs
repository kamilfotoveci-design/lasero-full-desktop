using System.Runtime.Versioning;
using Lasero.Core.GCode;
using Lasero.Core.Grbl;
using Lasero.Core.Import;

namespace Lasero.Core.Scene;

/// <summary>
/// Wraps an SvgImporter/RasterImporter result into a SceneObject. ImportedShape points are already
/// absolute mm — no coordinate rewrite is needed, since a SceneObject with the default Identity
/// transform reproduces its LocalShapes' points exactly (Apply(p, pivot) with Identity is a no-op
/// regardless of where the pivot sits). The pivot is placed at the import's own bounding-box center,
/// so later rotate/scale operations pivot around the shape's natural center rather than its corner.
/// </summary>
public static class SceneObjectFactory
{
    public static SceneObject FromImportedDocument(ImportedDocument document, string name)
    {
        var bounds = document.BoundingBox.IsEmpty ? new BoundingBox2D(0, 0, 0, 0) : document.BoundingBox;
        var pivot = new Position((bounds.MinX + bounds.MaxX) / 2, (bounds.MinY + bounds.MaxY) / 2, 0);

        return new SceneObject
        {
            Name = name,
            LocalShapes = document.Shapes,
            LocalPivot = pivot,
            LocalBounds = bounds,
        };
    }

    /// <summary>Placeholder color for a raster object's outline shape — chosen so it never accidentally
    /// matches a real Cut/Fill layer's color (see SceneDocument.ToolpathBuilder matching by IsApproximately),
    /// which keeps the outline purely a canvas-display/hit-testing aid, never burned into cut/fill G-code.</summary>
    public static readonly Layers.RgbColor RasterPlaceholderColor = new(0x80, 0x80, 0x80);
    // Kept separate from ordinary SVG colors so a black vector fill and a bitmap never accidentally
    // share processing settings. The color is an internal layer identity; the canvas still renders
    // the actual processed grayscale pixels.
    public static readonly Layers.RgbColor RasterEngravingColor = new(0x7F, 0x7F, 0x7E);

    public static Layers.LayerSettings CreateRasterLayer(RasterImportOptions options) => new()
    {
        Color = RasterEngravingColor,
        Name = "Bitmapa",
        Mode = Layers.LayerMode.Fill,
        Speed = options.FeedRatePerMinute,
        Power = options.MaxPower,
        Passes = options.Passes,
        FillLineIntervalMm = options.LineIntervalMm,
        IsEnabled = true,
        IsRaster = true,
    };

    /// <summary>Wraps a raster (PNG/JPG/BMP) import into a SceneObject. Its LocalShapes is just a
    /// rectangle outline for canvas display and resize handles — the actual engraving G-code is produced
    /// by RasterImporter directly from the file (see GCodeViewModel.RegenerateFromScene), not from this outline.</summary>
    [SupportedOSPlatform("windows")]
    public static SceneObject FromRaster(string filePath, RasterImportOptions options, string name, Layers.LayerSettings layer)
    {
        var (widthMm, heightMm) = RasterImporter.GetPlacedSizeMm(filePath, options.TargetWidthMm);
        var bounds = new BoundingBox2D(0, 0, widthMm, heightMm);
        var pivot = new Position(widthMm / 2, heightMm / 2, 0);

        var outline = new ImportedShape
        {
            Points =
            [
                new Position(0, 0, 0),
                new Position(widthMm, 0, 0),
                new Position(widthMm, heightMm, 0),
                new Position(0, heightMm, 0),
                new Position(0, 0, 0),
            ],
            IsClosed = true,
            LayerId = layer.Id,
            LayerColor = layer.Color,
            PreferredMode = Layers.LayerMode.Cut,
        };

        return new SceneObject
        {
            Name = name,
            LocalShapes = [outline],
            LocalPivot = pivot,
            LocalBounds = bounds,
            RasterFilePath = filePath,
            RasterOptions = options,
        };
    }
}
