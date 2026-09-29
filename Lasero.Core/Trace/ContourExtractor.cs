using Lasero.Core.Raster;
using OpenCvSharp;

namespace Lasero.Core.Trace;

/// <summary>One pixel coordinate in the working-resolution raster ContourExtractor ran on. A local
/// type rather than OpenCvSharp.Point so nothing outside this file needs an OpenCvSharp reference.</summary>
public readonly record struct PixelPoint(int X, int Y);

/// <summary>One contour from FindContours' RETR_TREE hierarchy, with its real parent/child nesting
/// preserved (not the signed-area guessing SceneViewModel.NormalizeNestedCompoundPaths falls back to
/// for shapes that never had real hierarchy). Depth 0/2/4/... are filled regions ("outer" contours or
/// islands nested inside a deeper hole); depth 1/3/5/... are the holes cut into whichever contour
/// contains them — exactly RETR_TREE's own even/odd nesting convention.</summary>
public sealed record RawContour
{
    public required IReadOnlyList<PixelPoint> Points { get; init; }
    public required int Depth { get; init; }
    public required IReadOnlyList<RawContour> Children { get; init; }
    public bool IsHole => Depth % 2 == 1;
}

/// <summary>Every top-level (depth 0) contour found in one working-resolution raster, each with its
/// full descendant tree attached. One root here becomes one traced object in CompoundPathBuilder.</summary>
public sealed record ContourTree
{
    public required IReadOnlyList<RawContour> Roots { get; init; }
    public required int Width { get; init; }
    public required int Height { get; init; }
    /// <summary>Working contour coordinates per source pixel. A value above one means contours
    /// were extracted from an interpolated raster to retain fractional-pixel edge positions.</summary>
    public int SubpixelScale { get; init; } = 1;
}

/// <summary>The "bitmap -> hierarchy" boundary BitmapTracer's DI seam is built around — see
/// BitmapTracer.cs for why (mirrors BackgroundRemovalService's constructor-injected engine-factory
/// pattern). OpenCvContourExtractionEngine is the only real implementation; tests can substitute a
/// fake that returns a hand-built ContourTree without needing OpenCV or a real image file.</summary>
public interface IContourExtractionEngine
{
    ContourTree Extract(GrayscaleImage image, BitmapTraceOptions options, CancellationToken cancellationToken = default);
}

public sealed class OpenCvContourExtractionEngine : IContourExtractionEngine
{
    public ContourTree Extract(GrayscaleImage image, BitmapTraceOptions options, CancellationToken cancellationToken = default)
        => ContourExtractor.Extract(image, options, cancellationToken);
}

/// <summary>OpenCV-backed contour extraction: contrast + light denoise + threshold (Manual/Auto/
/// Adaptive, see BitmapTraceOptions.ThresholdMode) + FindContours(Tree, ApproxNone — full point
/// density, curve fitting downstream needs it) + a minimum-area noise filter. Every Mat is scoped in
/// a using block; nothing here leaks native memory.</summary>
public static class ContourExtractor
{
    public const int SubpixelScale = 2;

    public static ContourTree Extract(GrayscaleImage image, BitmapTraceOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(options);
        cancellationToken.ThrowIfCancellationRequested();

        using var gray = ToMat(image);
        using var contrasted = new Mat();
        Cv2.ConvertScaleAbs(gray, contrasted, options.ContrastAlpha, 0);

        using var denoised = new Mat();
        if (Math.Min(image.Width, image.Height) >= 9)
            Cv2.GaussianBlur(contrasted, denoised, new Size(3, 3), 0);
        else
            contrasted.CopyTo(denoised);

        cancellationToken.ThrowIfCancellationRequested();
        // FindContours on a thresholded source can only return integer pixel coordinates. Upscale
        // the softly denoised luminance first, then threshold it, so the boundary can move in
        // half-pixel increments. This improves curve fidelity without relaxing Bezier fit error.
        using var subpixel = new Mat();
        Cv2.Resize(denoised, subpixel,
            new Size(checked(image.Width * SubpixelScale), checked(image.Height * SubpixelScale)),
            interpolation: InterpolationFlags.Linear);
        using var binary = Threshold(subpixel, options);
        cancellationToken.ThrowIfCancellationRequested();

        Cv2.FindContours(binary, out var contours, out var hierarchy, RetrievalModes.Tree, ContourApproximationModes.ApproxNone);
        cancellationToken.ThrowIfCancellationRequested();

        var roots = BuildTree(contours, hierarchy, options.MinimumContourAreaPx * SubpixelScale * SubpixelScale);
        return new ContourTree
        {
            Roots = roots,
            Width = checked(image.Width * SubpixelScale),
            Height = checked(image.Height * SubpixelScale),
            SubpixelScale = SubpixelScale,
        };
    }

    private static Mat ToMat(GrayscaleImage image)
    {
        var data = image.Luminance as byte[] ?? image.Luminance.ToArray();
        return Mat.FromPixelData(image.Height, image.Width, MatType.CV_8UC1, data, image.Width);
    }

    /// <summary>Foreground (ink) always comes out as 255 regardless of mode: dark-on-light source
    /// pixels are foreground unless Invert asks for the opposite convention, matching the sense the
    /// legacy Manual-only tracer already used (see the old ReadForeground's `luma < threshold`).</summary>
    private static Mat Threshold(Mat source, BitmapTraceOptions options)
    {
        var destination = new Mat();
        var foregroundIsDark = !options.Invert;
        var binaryType = foregroundIsDark ? ThresholdTypes.BinaryInv : ThresholdTypes.Binary;

        switch (options.ThresholdMode)
        {
            case ThresholdMode.Auto:
                Cv2.Threshold(source, destination, 0, 255, binaryType | ThresholdTypes.Otsu);
                break;
            case ThresholdMode.Adaptive:
                var blockSize = Math.Max(3, (Math.Min(source.Width, source.Height) / 8) | 1);
                Cv2.AdaptiveThreshold(source, destination, 255, AdaptiveThresholdTypes.GaussianC, binaryType, blockSize, 4);
                break;
            default:
                Cv2.Threshold(source, destination, options.Threshold, 255, binaryType);
                break;
        }

        return destination;
    }

    private static List<RawContour> BuildTree(Point[][] contours, HierarchyIndex[] hierarchy, double minimumAreaPx)
    {
        var childrenByParent = new Dictionary<int, List<int>>();
        var topLevel = new List<int>();
        for (var i = 0; i < hierarchy.Length; i++)
        {
            var parent = hierarchy[i].Parent;
            if (parent < 0)
            {
                topLevel.Add(i);
                continue;
            }
            if (!childrenByParent.TryGetValue(parent, out var siblings))
                childrenByParent[parent] = siblings = [];
            siblings.Add(i);
        }

        return BuildLevel(topLevel, depth: 0, contours, childrenByParent, minimumAreaPx);
    }

    private static List<RawContour> BuildLevel(
        IReadOnlyList<int> indices, int depth, Point[][] contours,
        IReadOnlyDictionary<int, List<int>> childrenByParent, double minimumAreaPx)
    {
        var result = new List<RawContour>();
        foreach (var index in indices)
        {
            // A speckle below the noise floor is dropped along with anything nested inside it — a
            // stray one-pixel hole inside a genuine shape is exactly as much noise as a stray filled
            // speckle sitting on its own.
            var area = Cv2.ContourArea(contours[index]);
            if (area < minimumAreaPx) continue;

            var children = childrenByParent.TryGetValue(index, out var childIndices)
                ? BuildLevel(childIndices, depth + 1, contours, childrenByParent, minimumAreaPx)
                : [];

            result.Add(new RawContour
            {
                Points = contours[index].Select(p => new PixelPoint(p.X, p.Y)).ToList(),
                Depth = depth,
                Children = children,
            });
        }
        return result;
    }
}
