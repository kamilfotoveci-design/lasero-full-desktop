using System.Runtime.Versioning;
using Lasero.Core.Import;
using Lasero.Core.Layers;
using Lasero.Core.Scene;

namespace Lasero.Core.Jobs;

/// <summary>
/// Turns a scene into the G-code lines of its job. This is the one implementation of that walk: the
/// application calls it on the live scene (synchronously, where a job is about to be checked and sent)
/// and on an immutable <see cref="Snapshot"/> from a worker thread (so a big scene never freezes the
/// window while its job is prepared). Same function, same output; only the input copy differs.
/// </summary>
[SupportedOSPlatform("windows")]
public static class SceneJobBuilder
{
    /// <summary>
    /// A copy of the scene that no editing can change while a worker reads it. Geometry lists are
    /// immutable and shared; objects and layers (which are mutable) are copied field for field,
    /// including object ids, so grouping by id behaves exactly as on the live scene.
    /// </summary>
    public static SceneDocument Snapshot(SceneDocument scene)
    {
        ArgumentNullException.ThrowIfNull(scene);
        var copy = new SceneDocument();
        foreach (var layer in scene.Layers)
        {
            copy.Layers.Add(new LayerSettings
            {
                Id = layer.Id,
                Color = layer.Color,
                Name = layer.Name,
                Mode = layer.Mode,
                Speed = layer.Speed,
                Power = layer.Power,
                Passes = layer.Passes,
                FillLineIntervalMm = layer.FillLineIntervalMm,
                IsEnabled = layer.IsEnabled,
                IsVisible = layer.IsVisible,
                IsRaster = layer.IsRaster,
                MaterialLabel = layer.MaterialLabel,
            });
        }

        foreach (var item in scene.Objects) copy.Objects.Add(item.SnapshotCopy());
        return copy;
    }

    /// <summary>
    /// The G-code of every enabled layer in the operator's manufacturing order. Vector layers go through
    /// <see cref="ToolpathBuilder"/>, bitmap layers through <see cref="RasterImporter"/>.
    /// </summary>
    public static List<string> BuildLines(
        SceneDocument scene, double controllerMaximumS, double offsetX, double offsetY,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scene);
        var lines = new List<string>();
        var document = scene.ToImportedDocument(offsetX, offsetY);

        // The layer list is the manufacturing order for both vectors and bitmaps. Raster jobs used
        // to be appended after every vector regardless of the order shown in the UI.
        foreach (var layer in scene.Layers.Where(item => item.IsEnabled))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (layer.IsRaster)
            {
                foreach (var obj in scene.Objects.Where(item =>
                             item.IsVisible && item.IncludeInOutput && item.IsRaster && UsesLayer(item, layer)))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var outputOptions = obj.BuildRasterOutputOptions(layer, offsetX, offsetY);
                    if (outputOptions is not null)
                        lines.AddRange(RasterImporter.BuildGCode(obj.RasterFilePath!, outputOptions, controllerMaximumS));
                }

                continue;
            }

            var shapes = document.Shapes.Where(shape => shape.LayerId != Guid.Empty
                ? shape.LayerId == layer.Id
                : shape.LayerColor.IsApproximately(layer.Color)).ToList();
            if (shapes.Count == 0) continue;

            lines.AddRange(ToolpathBuilder.BuildGCode(new ImportedDocument
            {
                Shapes = shapes,
                Layers = [layer],
                BoundingBox = document.BoundingBox,
                SourceFileName = document.SourceFileName,
            }, controllerMaximumS));
        }

        return lines;
    }

    public static bool UsesLayer(SceneObject item, LayerSettings layer) =>
        item.LocalShapes.Any(shape => shape.LayerId != Guid.Empty
            ? shape.LayerId == layer.Id
            : shape.LayerColor.IsApproximately(layer.Color)) ||
        layer.IsRaster && item.IsRaster && item.LocalShapes.All(shape => shape.LayerId == Guid.Empty);
}
