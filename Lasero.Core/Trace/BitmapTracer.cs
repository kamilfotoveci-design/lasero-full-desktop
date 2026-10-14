using System.Runtime.Versioning;
using Lasero.Core.GCode;
using Lasero.Core.Grbl;
using Lasero.Core.Import;
using Lasero.Core.Layers;
using Lasero.Core.Raster;
using Lasero.Core.Scene;

namespace Lasero.Core.Trace;

/// <summary>
/// Converts a bitmap into node-editable vector geometry, the way a "Trace Image" command does: one
/// grayscale threshold trace and nothing else. The grey values are thresholded with sub-pixel
/// interpolation (see <see cref="SubpixelContourEngine"/>), the contours are smoothed with corners kept
/// sharp, and the result is fitted with as few cubic Beziers as the Optimize tolerance allows
/// (<see cref="CompoundPathBuilder"/>). There is no mode, threshold-method, quality or engine choice;
/// the Potrace/VTracer/Outline/Color engines remain in this folder but are not reachable from here.
///
/// The result is one VectorPath per top-level shape (holes nested and correctly wound), see
/// <see cref="BitmapTraceResult.VectorPaths"/>. <see cref="BitmapTraceResult.Document"/> is the flattened
/// legacy view of the same geometry.
/// </summary>
public static class BitmapTracer
{
    private static readonly RgbColor TraceColor = new(18, 18, 18);

    /// <summary>Swappable purely for tests: the "bitmap to contour hierarchy" step.</summary>
    internal static Func<IContourExtractionEngine> ContourEngineFactory { get; set; } = static () => new SubpixelContourEngine();

    [SupportedOSPlatform("windows")]
    public static BitmapTraceResult Trace(
        string filePath,
        BitmapTraceOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        options ??= new BitmapTraceOptions();
        options.Validate();

        var (sourceWidthPx, sourceHeightPx) = ReadPixelSize(filePath);
        var workingWidth = BitmapTraceOptions.WorkingWidthPx(sourceWidthPx, sourceHeightPx);
        var grayscale = BitmapLoader.LoadGrayscale(filePath, workingWidth);
        cancellationToken.ThrowIfCancellationRequested();

        return Trace(grayscale, options, Path.GetFileName(filePath), cancellationToken);
    }

    /// <summary>Width, in pixels, the tracer works at for a source of the given size (never upscaled).</summary>
    public static int WorkingWidth(int sourceWidthPx, int sourceHeightPx) =>
        BitmapTraceOptions.WorkingWidthPx(sourceWidthPx, sourceHeightPx);

    /// <summary>Decodes the file at the tracer's working resolution, or smaller when
    /// <paramref name="maxLongSidePx"/> is set (a quick preview). Callers cache the result: decoding is the
    /// slow part of a re-trace.</summary>
    [SupportedOSPlatform("windows")]
    public static GrayscaleImage LoadWorkingImage(string filePath, int maxLongSidePx = 0)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        var (width, height) = ReadPixelSize(filePath);
        var working = BitmapTraceOptions.WorkingWidthPx(width, height);
        if (maxLongSidePx > 0)
        {
            var longSide = Math.Max(width, height);
            if (longSide > maxLongSidePx)
                working = Math.Min(working, Math.Max(1, (int)Math.Round(width * (maxLongSidePx / (double)longSide))));
        }
        return BitmapLoader.LoadGrayscale(filePath, working);
    }
    /// <summary>Traces an already decoded grayscale image (0 = black, 255 = white). Needs no GDI+.</summary>
    public static BitmapTraceResult Trace(
        GrayscaleImage grayscale,
        BitmapTraceOptions? options = null,
        string sourceName = "",
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(grayscale);
        options ??= new BitmapTraceOptions();
        options.Validate();

        var scale = options.TargetWidthMm / grayscale.Width;
        var heightMm = grayscale.Height * scale;

        var tree = ContourEngineFactory().Extract(grayscale, options, cancellationToken);
        if (tree.SubpixelScale < 1)
            throw new InvalidDataException("Contour extraction returned an invalid subpixel scale.");

        // Contour coordinates are in 1/SubpixelScale source pixels; the fitter tolerances stay in
        // source-pixel units, so a finer contour grid never loosens the fit.
        var vectorObjects = CompoundPathBuilder.Build(tree, options, TraceColor,
            scale / tree.SubpixelScale, cancellationToken);

        var layer = LayerSettings.CreateDefault(TraceColor, LayerMode.Cut, "Trasovaný vektor");
        var shapes = new List<ImportedShape>();
        foreach (var traced in vectorObjects)
        {
            cancellationToken.ThrowIfCancellationRequested();
            shapes.AddRange(FlattenToShapes(traced.Path, layer, Guid.NewGuid()));
        }

        var layers = vectorObjects.Count == 0 ? new List<LayerSettings>() : new List<LayerSettings> { layer };
        var document = new ImportedDocument
        {
            Shapes = shapes,
            Layers = layers,
            // Keep the full image frame as local bounds so a trace with transparent margins around it
            // keeps its original placement and size.
            BoundingBox = new BoundingBox2D(0, 0, options.TargetWidthMm, heightMm),
            SourceFileName = sourceName,
        };

        return new BitmapTraceResult
        {
            Document = document,
            VectorPaths = vectorObjects,
            PixelWidth = grayscale.Width,
            PixelHeight = grayscale.Height,
            ContourCount = shapes.Count,
            PointCount = shapes.Sum(shape => shape.Points.Count),
            // Editable anchors, not the much denser flattened points used by rendering and toolpaths.
            NodeCount = vectorObjects.Sum(item => item.Path.Subpaths.Sum(subpath => subpath.Nodes.Count)),
        };
    }

    /// <summary>Only reads the source file's own pixel dimensions (to decide the working-resolution
    /// downscale); the pixel data is decoded once, by BitmapLoader.LoadGrayscale.</summary>
    [SupportedOSPlatform("windows")]
    private static (int Width, int Height) ReadPixelSize(string filePath)
    {
        using var image = System.Drawing.Image.FromFile(filePath);
        if (image.Width < 1 || image.Height < 1)
            throw new InvalidDataException("Bitmap does not contain any pixels.");
        return (image.Width, image.Height);
    }

    /// <summary>Flattens one traced object's VectorPath to the legacy ImportedShape shape Document
    /// still exposes (PreferredMode always Cut).</summary>
    private static List<ImportedShape> FlattenToShapes(VectorPath path, LayerSettings layer, Guid geometrySetId)
    {
        var flattened = path.FlattenAll();
        var shapes = new List<ImportedShape>(flattened.Count);
        for (var index = 0; index < flattened.Count; index++)
        {
            var points = flattened[index];
            if (points.Count < 2) continue;
            shapes.Add(new ImportedShape
            {
                GeometrySetId = geometrySetId,
                Points = points,
                IsClosed = path.Subpaths[index].IsClosed,
                LayerId = layer.Id,
                LayerColor = layer.Color,
                PreferredMode = layer.Mode,
            });
        }
        return shapes;
    }
}
