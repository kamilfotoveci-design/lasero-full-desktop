using System.Drawing;
using System.Runtime.Versioning;
using Lasero.Core.Raster;

namespace Lasero.Core.Import;

/// <summary>
/// Converts a PNG/JPG/BMP photo or logo into a raster-scan G-code engraving program — the workflow
/// real customers actually use (LightBurn calls this "Image" mode). A thin facade over the
/// Lasero.Core.Raster pipeline (BitmapLoader -&gt; ImageProcessor -&gt; RasterPlanner -&gt;
/// GrblRasterGenerator), kept as the stable public entry point so existing callers (SceneObject
/// placement, GCodeViewModel.RegenerateFromScene) don't need to change.
/// </summary>
[SupportedOSPlatform("windows")]
public static class RasterImporter
{
    /// <summary>Reads just the bitmap's pixel dimensions to compute its placed mm size — used to build
    /// the placeholder rectangle a raster SceneObject shows on the design canvas, without scanning pixels.</summary>
    public static (double WidthMm, double HeightMm) GetPlacedSizeMm(string filePath, double targetWidthMm)
    {
        using var bitmap = new Bitmap(filePath);
        var scale = targetWidthMm / bitmap.Width;
        return (targetWidthMm, bitmap.Height * scale);
    }

    /// <summary>Loads + processes (brightness/contrast/invert/tone-mapping) a bitmap without planning
    /// machine moves — used for the import window's live preview.</summary>
    public static ProcessedImage LoadProcessedPreview(string filePath, RasterImportOptions options)
    {
        var source = LoadProcessingSource(filePath, options);
        return Process(source, options);
    }

    public static GrayscaleImage LoadProcessingSource(string filePath, RasterImportOptions options)
    {
        var width = Math.Max(1, (int)Math.Ceiling(options.TargetWidthMm / options.LineIntervalMm));
        int? height = options.TargetHeightMm is { } targetHeight
            ? Math.Max(1, (int)Math.Ceiling(targetHeight / options.LineIntervalMm))
            : null;
        return BitmapLoader.LoadGrayscale(filePath, width, height);
    }

    public static ProcessedImage Process(GrayscaleImage source, RasterImportOptions options) =>
        ImageProcessor.Process(source, ToProcessingOptions(options));

    /// <summary>Loads, processes, and plans a bitmap into machine-agnostic moves + bounds — the single
    /// LaserJob that framing, the canvas bounds overlay, and BuildGCode below all derive from.</summary>
    public static LaserJob BuildLaserJob(string filePath, RasterImportOptions options)
    {
        var processed = LoadProcessedPreview(filePath, options);
        return BuildLaserJob(processed, options);
    }

    public static LaserJob BuildLaserJob(ProcessedImage processed, RasterImportOptions options) =>
        RasterPlanner.Plan(processed, ToPlanOptions(options));

    public static List<string> BuildGCode(string filePath, RasterImportOptions options, double controllerMaximumS)
    {
        var pass = GrblRasterGenerator.Generate(BuildLaserJob(filePath, options), controllerMaximumS);
        var lines = new List<string>(pass.Count * Math.Max(1, options.Passes));
        for (var index = 0; index < Math.Max(1, options.Passes); index++)
            lines.AddRange(pass);
        return lines;
    }

    private static ImageProcessingOptions ToProcessingOptions(RasterImportOptions options) => new()
    {
        Gamma = options.Gamma,
        Exposure = options.Exposure,
        Brightness = options.Brightness,
        Contrast = options.Contrast,
        Highlights = options.Highlights,
        Shadows = options.Shadows,
        BlackPoint = options.BlackPoint,
        WhitePoint = options.WhitePoint,
        Invert = options.Invert,
        NoiseReduction = options.NoiseReduction,
        Sharpen = options.Sharpen,
        EdgeEnhance = options.EdgeEnhance,
        UseDithering = options.UseDithering,
        DitheringAlgorithm = options.DitheringAlgorithm,
        UseThreshold = options.UseThreshold,
        ThresholdValue = options.ThresholdValue,
    };

    private static RasterPlanOptions ToPlanOptions(RasterImportOptions options) => new()
    {
        TargetWidthMm = options.TargetWidthMm,
        TargetHeightMm = options.TargetHeightMm,
        LineIntervalMm = options.LineIntervalMm,
        MinPower = options.MinPower,
        MaxPower = options.MaxPower,
        FeedRatePerMinute = options.FeedRatePerMinute,
        OffsetX = options.OffsetX,
        OffsetY = options.OffsetY,
    };
}
