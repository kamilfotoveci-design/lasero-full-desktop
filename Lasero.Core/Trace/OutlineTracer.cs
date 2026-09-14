using Lasero.Core.Grbl;
using Lasero.Core.Layers;
using Lasero.Core.Raster;
using Lasero.Core.Scene;
using OpenCvSharp;

namespace Lasero.Core.Trace;

/// <summary>
/// Edge/outline tracing (BitmapTraceOptions.Mode = Outline): grayscale -> light blur (just enough to
/// keep Canny from doubling every edge on sensor noise, per the "avoid double edges" requirement) ->
/// Canny -> FindContours(List — edges are not nested regions, no hierarchy needed) -> BezierFitter.
/// Deliberately simpler than the FilledShapes pipeline: no hole/compound-path handling, every found
/// edge loop becomes one subpath of a single traced object.
///
/// FindContours always returns a closed boundary walk (even the two sides of a single-pixel-wide edge
/// line trace as one loop around it), so every contour here is fit as a closed subpath — there is no
/// separate open-contour case to detect.
/// </summary>
public static class OutlineTracer
{
    public static IReadOnlyList<TracedVectorObject> Trace(
        GrayscaleImage image, BitmapTraceOptions options, RgbColor color, double scaleMmPerPixel,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(options);
        cancellationToken.ThrowIfCancellationRequested();

        using var gray = ToMat(image);
        using var denoised = new Mat();
        if (Math.Min(image.Width, image.Height) >= 9)
            Cv2.GaussianBlur(gray, denoised, new Size(3, 3), 0);
        else
            gray.CopyTo(denoised);

        cancellationToken.ThrowIfCancellationRequested();
        using var edges = new Mat();
        var highThreshold = Math.Clamp(255 * (0.5 + options.Contrast * 0.25), 40, 255);
        Cv2.Canny(denoised, edges, highThreshold / 2, highThreshold);
        cancellationToken.ThrowIfCancellationRequested();

        Cv2.FindContours(edges, out var contours, out _, RetrievalModes.List, ContourApproximationModes.ApproxNone);
        cancellationToken.ThrowIfCancellationRequested();

        var fitOptions = new BezierFitOptions
        {
            ToleranceMm = options.FitToleranceBasePx * scaleMmPerPixel,
            CornerAngleDegrees = options.CornerAngleThresholdDegrees,
            DedupeDistanceMm = BitmapTraceOptions.DedupeDistancePx * scaleMmPerPixel,
        };

        // A crude arc-length floor (not the area filter FilledShapes mode uses — an edge loop can
        // have near-zero area) so single stray Canny pixels don't become spurious subpaths.
        var minimumPoints = Math.Max(4, (int)(options.MinimumContourAreaPx / 4));

        var subpaths = new List<VectorSubpath>();
        foreach (var contour in contours)
        {
            if (contour.Length < minimumPoints) continue;
            var mmPoints = contour
                .Select(p => new Position(p.X * scaleMmPerPixel, (image.Height - p.Y) * scaleMmPerPixel, 0))
                .ToList();
            var fitted = BezierFitter.FitClosed(mmPoints, fitOptions);
            if (fitted.Nodes.Count >= 3) subpaths.Add(fitted);
        }

        if (subpaths.Count == 0) return [];
        return [new TracedVectorObject(new VectorPath { Subpaths = subpaths }, color)];
    }

    private static Mat ToMat(GrayscaleImage image)
    {
        var data = image.Luminance as byte[] ?? image.Luminance.ToArray();
        return Mat.FromPixelData(image.Height, image.Width, MatType.CV_8UC1, data, image.Width);
    }
}
