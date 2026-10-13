using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Lasero.Core.Import;
using Lasero.Core.Raster;

namespace Lasero.Avalonia.Services;

/// <summary>
/// Port of Lasero.App/ProcessedImagePreviewRenderer.cs. Renders a Core ProcessedImage (0..1 power
/// fraction per pixel) as an Avalonia bitmap for the raster import preview — the only place
/// PowerFraction gets turned into pixels on screen, keeping Lasero.Core free of any UI-toolkit
/// dependency. WPF used PixelFormats.Gray8 directly; Avalonia's WriteableBitmap has no single-channel
/// format, so this writes the same value into all three color channels of Bgra8888 instead — same
/// visual result (a gray pixel), just a wider pixel format underneath.
/// </summary>
[SupportedOSPlatform("windows")]
public static class ProcessedImagePreviewRenderer
{
    public static Bitmap RenderFile(string filePath, RasterImportOptions options) =>
        Render(RasterImporter.LoadProcessedPreview(filePath, options));

    /// <summary>
    /// Continuous-tone version for the scene canvas — see the WPF original's remarks on why dithering
    /// is deliberately bypassed here (a dithered preview at canvas scale reads as a featureless grey
    /// block, which defeats "is that my picture, and is it in the right place"). Loads the raw file
    /// directly rather than routing through BitmapLoader, which composites transparent pixels onto
    /// white for the engraving pipeline — that would hide a successfully removed background again.
    /// </summary>
    public static Bitmap RenderFileForCanvas(string filePath)
    {
        using var stream = File.OpenRead(filePath);
        return new Bitmap(stream);
    }

    public static WriteableBitmap Render(ProcessedImage image)
    {
        var bitmap = new WriteableBitmap(
            new PixelSize(image.Width, image.Height),
            new Vector(96, 96),
            PixelFormat.Bgra8888,
            AlphaFormat.Opaque);

        using var frameBuffer = bitmap.Lock();
        var stride = frameBuffer.RowBytes;
        var row = new byte[stride];
        for (var y = 0; y < image.Height; y++)
        {
            for (var x = 0; x < image.Width; x++)
            {
                // Black = full power (will be burnt), white = untouched — matches the classic
                // "what the engraving will look like" preview convention.
                var gray = (byte)Math.Round(255.0 - Math.Clamp(image.PowerFraction[y * image.Width + x], 0, 1) * 255.0);
                var offset = x * 4;
                row[offset + 0] = gray;
                row[offset + 1] = gray;
                row[offset + 2] = gray;
                row[offset + 3] = 255;
            }

            Marshal.Copy(row, 0, frameBuffer.Address + y * stride, stride);
        }

        return bitmap;
    }
}
