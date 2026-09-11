namespace Lasero.Core.BackgroundRemoval;

/// <summary>
/// Pure pixel math — no disk/GDI+ dependency, mirroring how GrayscaleImage/ProcessedImage keep the
/// raster pipeline's actual number-crunching unit-testable with small hand-built arrays instead of
/// real image files or a live ONNX run. Everything upstream (bitmap decode, inference) hands this a
/// plain byte/float buffer; everything downstream (BackgroundRemovalService) re-encodes the result.
/// </summary>
public static class BackgroundMaskCompositor
{
    /// <summary>
    /// Multiplies each pixel's alpha channel by its mask value, producing a new buffer with a
    /// transparent background — a mask value of 1 keeps the pixel fully opaque, 0 makes it fully
    /// transparent, and anything between softens the edge instead of leaving a hard cutout.
    ///
    /// bgra is 32bppArgb byte order (B,G,R,A per pixel), matching BitmapLoader's own convention for
    /// reading GDI+ bitmaps via LockBits. mask is row-major, one value per pixel, 0..1.
    /// </summary>
    public static byte[] ApplyMask(IReadOnlyList<byte> bgra, int width, int height, IReadOnlyList<float> mask)
    {
        ArgumentNullException.ThrowIfNull(bgra);
        ArgumentNullException.ThrowIfNull(mask);
        if (width <= 0 || height <= 0)
            throw new ArgumentOutOfRangeException(nameof(width), "Width and height must be positive.");
        if (bgra.Count != width * height * 4)
            throw new ArgumentException("Pixel buffer size does not match width*height*4.", nameof(bgra));
        if (mask.Count != width * height)
            throw new ArgumentException("Mask size does not match width*height.", nameof(mask));

        var result = new byte[bgra.Count];
        for (var pixel = 0; pixel < width * height; pixel++)
        {
            var offset = pixel * 4;
            result[offset + 0] = bgra[offset + 0];
            result[offset + 1] = bgra[offset + 1];
            result[offset + 2] = bgra[offset + 2];
            var factor = Math.Clamp(mask[pixel], 0f, 1f);
            result[offset + 3] = (byte)Math.Round(bgra[offset + 3] * factor);
        }
        return result;
    }
}
