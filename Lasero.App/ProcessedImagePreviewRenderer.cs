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
