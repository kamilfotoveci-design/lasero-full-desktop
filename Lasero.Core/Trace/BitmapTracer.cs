using System.Runtime.Versioning;
using Lasero.Core.GCode;
using Lasero.Core.Grbl;
using Lasero.Core.Import;
using Lasero.Core.Layers;
using Lasero.Core.Raster;
using Lasero.Core.Scene;

namespace Lasero.Core.Trace;

/// <summary>
/// Converts a bitmap into node-editable vector geometry. FilledShapes uses an installed Potrace
/// executable when available, otherwise the bundled VTracer binary engine; Outline uses OpenCV
/// Canny edge extraction and Bezier fitting. Color uses VTracer's quantized cutout regions. The
/// legacy filled-contour engine remains a fallback if neither vectorizer is available. The result is one VectorPath per
/// traced object/region — see BitmapTraceResult.VectorPaths — which SceneViewModel.ReplaceRasterWithTrace
/// wires through VectorPathSceneFactory.Create so traced objects land in the Node Edit tool exactly
/// like anything else built from a VectorPath.
///
/// Document (the flattened ImportedShape/ImportedDocument view) is still populated from the same
/// VectorPaths for legacy consumers. The WPF trace preview uses the authoritative VectorPaths directly
/// so it displays the exact fitted cubic geometry that will be applied to the scene.
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

        IReadOnlyList<TracedVectorObject> vectorObjects = options.Mode switch
        {
            TraceMode.Outline => OutlineTracer.Trace(grayscale, options, TraceColor, scale, cancellationToken),
            TraceMode.Color => VTracerColorTracer.Trace(filePath, options, color: true, cancellationToken),
            TraceMode.FilledShapes => TraceFilledShapes(filePath, grayscale, options, scale, cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(options), "Unsupported bitmap trace mode."),
        };

        // The scene's legacy layer resolver intentionally merges approximately equal colors. Apply
        // the same palette grouping before constructing objects and layers, so the editor's preview,
        // color metadata, and eventual machine layer IDs always agree.
        if (options.Mode == TraceMode.Color)
            vectorObjects = NormalizeColorPalette(vectorObjects);

        var layerMode = options.Mode == TraceMode.Color ? LayerMode.Fill : LayerMode.Cut;
        var layers = vectorObjects.Select(item => item.Color).Distinct().Select(color =>
        {
            var layer = LayerSettings.CreateDefault(color, layerMode,
                options.Mode == TraceMode.Color ? $"Trasování {color.ToHex()}" : "Trasovaný vektor");
            // An opaque white image background is a real VTracer region. Keep it visible and
            // editable, but do not send an entire white rectangle to a laser by default.
            if (options.Mode == TraceMode.Color && color.R >= 245 && color.G >= 245 && color.B >= 245)
                layer.IsEnabled = false;
            return layer;
        }).ToList();
        var layerByColor = layers.ToDictionary(layer => layer.Color);
        var shapes = new List<ImportedShape>();
        foreach (var traced in vectorObjects)
        {
            cancellationToken.ThrowIfCancellationRequested();
            shapes.AddRange(FlattenToShapes(traced.Path, layerByColor[traced.Color], Guid.NewGuid()));
        }

        var document = new ImportedDocument
        {
            Shapes = shapes,
            Layers = layers,
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
            // Keep the UI's complexity indicator honest: these are editable anchors, not the much
            // denser flattened points used internally by rendering and toolpath consumers.
            NodeCount = vectorObjects.Sum(item => item.Path.Subpaths.Sum(subpath => subpath.Nodes.Count)),
        };
    }

    [SupportedOSPlatform("windows")]
    private static IReadOnlyList<TracedVectorObject> TraceFilledShapes(
        string filePath, GrayscaleImage grayscale, BitmapTraceOptions options, double scale,
        CancellationToken cancellationToken)
    {
        if (PotraceCliTracer.IsAvailable && options.ThresholdMode != ThresholdMode.Adaptive)
            return PotraceCliTracer.Trace(grayscale, options, TraceColor, scale, cancellationToken);

        // VTracer handles the common logo/text case with true cubic paths and works out of the box.
        // Its current CLI cannot honor inversion or contrast, so retain the existing engine for
        // those explicitly requested settings until both preprocessing paths have parity.
        if (VTracerColorTracer.IsAvailable && !options.Invert && options.Contrast == 0)
        {
            var effective = options.ThresholdMode == ThresholdMode.Auto
                // Otsu's selected bucket belongs to the dark region (OpenCV BinaryInv uses <=).
                // VTracer's manual threshold uses <, so advance by one to retain black bucket 0.
                ? options with { Threshold = (byte)Math.Min(255, ComputeOtsuThreshold(grayscale.Luminance) + 1), ThresholdMode = ThresholdMode.Manual }
                : options;
            return VTracerColorTracer.Trace(filePath, effective, color: false, cancellationToken);
        }

        return BuildFilledShapes(grayscale, options, scale, cancellationToken);
    }

    private static IReadOnlyList<TracedVectorObject> NormalizeColorPalette(IReadOnlyList<TracedVectorObject> objects)
    {
        var palette = new List<RgbColor>();
        var result = new List<TracedVectorObject>(objects.Count);
        foreach (var item in objects)
        {
            RgbColor? matched = null;
            foreach (var existing in palette)
                if (existing.IsApproximately(item.Color)) { matched = existing; break; }
            if (matched is null)
            {
                matched = item.Color;
                palette.Add(item.Color);
            }
            result.Add(item with { Color = matched.Value });
        }
        return result;
    }

    private static byte ComputeOtsuThreshold(IReadOnlyList<byte> pixels)
    {
        Span<long> histogram = stackalloc long[256];
        foreach (var value in pixels) histogram[value]++;
        long sum = 0, backgroundCount = 0, backgroundSum = 0;
        for (var index = 0; index < histogram.Length; index++) sum += index * histogram[index];
        double bestVariance = -1;
        byte bestThreshold = 128;
        for (var index = 0; index < histogram.Length; index++)
        {
            backgroundCount += histogram[index];
            backgroundSum += index * histogram[index];
            var foregroundCount = pixels.Count - backgroundCount;
            if (backgroundCount == 0 || foregroundCount == 0) continue;
            var meanDifference = (double)backgroundSum / backgroundCount -
                (double)(sum - backgroundSum) / foregroundCount;
            var variance = (double)backgroundCount * foregroundCount * meanDifference * meanDifference;
            if (variance <= bestVariance) continue;
            bestVariance = variance;
            bestThreshold = (byte)index;
        }
        return bestThreshold;
    }

    private static IReadOnlyList<TracedVectorObject> BuildFilledShapes(
        GrayscaleImage grayscale, BitmapTraceOptions options, double sourceScaleMmPerPixel,
        CancellationToken cancellationToken)
    {
        var tree = ContourEngineFactory().Extract(grayscale, options, cancellationToken);
        if (tree.SubpixelScale < 1)
            throw new InvalidDataException("Contour extraction returned an invalid subpixel scale.");

        // Geometry coordinates use the extractor's working pixels; the fitter tolerances stay in
        // source-pixel units so a half-pixel contour grid increases precision without loosening fit.
        return CompoundPathBuilder.Build(tree, options, TraceColor,
            sourceScaleMmPerPixel / tree.SubpixelScale, cancellationToken);
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
