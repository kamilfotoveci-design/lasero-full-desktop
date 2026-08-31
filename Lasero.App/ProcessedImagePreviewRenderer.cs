using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Lasero.Core.Import;
using Lasero.Core.Raster;

namespace Lasero.App;

/// <summary>Renders a Core ProcessedImage (0..1 power fraction per pixel) as a WPF bitmap for the
/// raster import preview — the only place PowerFraction gets turned into pixels on screen, keeping
/// Lasero.Core free of any WPF/System.Windows.Media dependency.</summary>
public static class ProcessedImagePreviewRenderer
{
    public static BitmapSource RenderFile(string filePath, RasterImportOptions options) =>
        Render(RasterImporter.LoadProcessedPreview(filePath, options));

    /// <summary>
    /// Continuous-tone version for the scene canvas.
    ///
    /// Dithering is on by default because a diode laser is effectively one-bit, but a dithered image
    /// shown at canvas scale averages out into a featureless grey block — the operator loses any way
    /// to recognise their own artwork or judge its placement. The import dialog still previews the
    /// real dithered result at a useful magnification; here the job is "is that my picture, and is it
    /// in the right place", which continuous tone answers and a dither pattern does not.
    /// Only the on-screen preview changes; the G-code path keeps the operator's actual options.
    /// </summary>
    public static BitmapSource RenderFileForCanvas(string filePath, RasterImportOptions options) =>
        Render(RasterImporter.LoadProcessedPreview(filePath, options with
        {
            UseDithering = false,
            UseThreshold = false,
        }));

    public static BitmapSource Render(ProcessedImage image)
    {
        var pixels = new byte[image.Width * image.Height];
        for (int i = 0; i < pixels.Length; i++)
        {
            // Black = full power (will be burnt), white = untouched — matches the classic
            // "what the engraving will look like" preview convention.
            var gray = 255.0 - Math.Clamp(image.PowerFraction[i], 0, 1) * 255.0;
            pixels[i] = (byte)Math.Round(gray);
        }

        var bitmap = BitmapSource.Create(
            image.Width, image.Height, 96, 96, PixelFormats.Gray8, null, pixels, image.Width);
        bitmap.Freeze();
        return bitmap;
    }
}
