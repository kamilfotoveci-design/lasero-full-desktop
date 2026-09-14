using System.Runtime.Versioning;
using Lasero.Core.GCode;
using Lasero.Core.Grbl;
using Lasero.Core.Import;
using Lasero.Core.Layers;
using Lasero.Core.Raster;
using Lasero.Core.Scene;

namespace Lasero.Core.Trace;

/// <summary>
/// Converts a bitmap into node-editable vector geometry. Two modes (see BitmapTraceOptions.Mode):
/// FilledShapes runs ContourExtractor (OpenCV threshold + FindContours' real hierarchy) through
/// CompoundPathBuilder (BezierFitter per contour, correctly wound holes); Outline runs a simpler
/// Canny-edge pipeline through the same BezierFitter. Either way the result is one VectorPath per
/// traced object/region — see BitmapTraceResult.VectorPaths — which SceneViewModel.ReplaceRasterWithTrace
/// wires through VectorPathSceneFactory.Create so traced objects land in the Node Edit tool exactly
/// like anything else built from a VectorPath.
///
/// Document (the flattened ImportedShape/ImportedDocument view) is still populated from the same
/// VectorPaths so BitmapTraceViewModel's live preview (both Lasero.App and Lasero.Avalonia — neither
/// touched by this pass) keeps working unchanged; it is a projection of VectorPaths, not a second
/// source of truth.
/// </summary>
public static class BitmapTracer
{
    private static readonly RgbColor TraceColor = new(18, 18, 18);

    /// <summary>Swappable purely for tests. Mirrors BackgroundRemovalService's constructor-injected
    /// Func&lt;string, IBackgroundRemovalInferenceEngine&gt; DI seam, adapted to a static entry point:
    /// every real caller (BitmapTraceViewModel in both Lasero.App and Lasero.Avalonia) invokes Trace as
    /// a plain static method, so the swappable factory lives behind that call rather than behind a
    /// constructor. Real tracing always uses OpenCvContourExtractionEngine; a test can substitute a
    /// fake IContourExtractionEngine that returns a hand-built ContourTree.</summary>
    internal static Func<IContourExtractionEngine> ContourEngineFactory { get; set; } = static () => new OpenCvContourExtractionEngine();

    [SupportedOSPlatform("windows")]
    public static BitmapTraceResult Trace(
        string filePath,
        BitmapTraceOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        options ??= new BitmapTraceOptions();
        options.Validate();

        var sourceWidthPx = ReadPixelWidth(filePath);
        var workingWidth = options.WorkingWidthPx(sourceWidthPx);
        var grayscale = BitmapLoader.LoadGrayscale(filePath, workingWidth);
        cancellationToken.ThrowIfCancellationRequested();

        var scale = options.TargetWidthMm / grayscale.Width;
        var heightMm = grayscale.Height * scale;

        var vectorObjects = options.Mode == TraceMode.Outline
            ? OutlineTracer.Trace(grayscale, options, TraceColor, scale, cancellationToken)
            : CompoundPathBuilder.Build(
                ContourEngineFactory().Extract(grayscale, options, cancellationToken),
                options, TraceColor, scale, cancellationToken);

        var layer = LayerSettings.CreateDefault(TraceColor, LayerMode.Cut, "Trasovaný vektor");
        var shapes = new List<ImportedShape>();
        foreach (var traced in vectorObjects)
        {
            cancellationToken.ThrowIfCancellationRequested();
            shapes.AddRange(FlattenToShapes(traced.Path, traced.Color, Guid.NewGuid()));
        }

        var document = new ImportedDocument
        {
            Shapes = shapes,
            Layers = [layer],
            // Keep the full working-resolution frame as local bounds, exactly as the legacy tracer
            // did, so a trace with transparent margins around it keeps its original placement/size.
            BoundingBox = new BoundingBox2D(0, 0, options.TargetWidthMm, heightMm),
            SourceFileName = Path.GetFileName(filePath),
        };

        return new BitmapTraceResult
        {
            Document = document,
            VectorPaths = vectorObjects,
            PixelWidth = grayscale.Width,
            PixelHeight = grayscale.Height,
            ContourCount = shapes.Count,
            PointCount = shapes.Sum(shape => shape.Points.Count),
        };
    }

    /// <summary>Only reads the source file's own pixel dimensions (to decide the working-resolution
    /// downscale) — the actual pixel data is decoded once, by BitmapLoader.LoadGrayscale.</summary>
    [SupportedOSPlatform("windows")]
    private static int ReadPixelWidth(string filePath)
    {
        using var image = System.Drawing.Image.FromFile(filePath);
        if (image.Width < 1 || image.Height < 1)
            throw new InvalidDataException("Bitmap does not contain any pixels.");
        return image.Width;
    }

    /// <summary>Flattens one traced object's VectorPath to the legacy ImportedShape shape Document
    /// still exposes — the same flatten VectorPathSceneFactory.BuildShapes does for any other
    /// VectorPath, kept independent here since that method is private to VectorPathSceneFactory (which
    /// this pass leaves untouched) and Document's contract (PreferredMode always Cut, matching every
    /// trace before this pass) is deliberately not the same as a freshly-drawn vector path's.</summary>
    private static List<ImportedShape> FlattenToShapes(VectorPath path, RgbColor color, Guid geometrySetId)
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
                LayerColor = color,
                PreferredMode = LayerMode.Cut,
            });
        }
        return shapes;
    }
}
