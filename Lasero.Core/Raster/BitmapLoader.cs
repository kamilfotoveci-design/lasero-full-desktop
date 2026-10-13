using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Lasero.Core.Raster;

/// <summary>The only place System.Drawing/GDI+ is touched in the raster pipeline — everything past
/// this point (ImageProcessor, RasterPlanner) is pure data. Reads via LockBits + Marshal.Copy for
/// speed on large images without requiring unsafe code; never writes back to the source file.</summary>
[SupportedOSPlatform("windows")]
public static class BitmapLoader
{
    public static GrayscaleImage LoadGrayscale(string filePath)
        => LoadGrayscaleCore(filePath, targetWidth: null, targetHeight: null);

    /// <summary>Decodes directly into the resolution the laser can reproduce. Downsampling before
    /// filters and dithering avoids doing photographic work on millions of pixels that would later
    /// collapse into the same physical dot.</summary>
    public static GrayscaleImage LoadGrayscale(string filePath, int targetWidth, int? targetHeight = null)
        => LoadGrayscaleCore(filePath, Math.Max(1, targetWidth), targetHeight is null ? null : Math.Max(1, targetHeight.Value));

    private static GrayscaleImage LoadGrayscaleCore(string filePath, int? targetWidth, int? targetHeight)
    {
        using var original = new Bitmap(filePath);
        var width = Math.Min(original.Width, targetWidth ?? original.Width);
        var proportionalHeight = (int)Math.Round(original.Height * (width / (double)original.Width));
        var height = Math.Min(original.Height, targetHeight ?? proportionalHeight);
        // Same size: read the pixels directly. Drawing through GDI+ here, whatever the interpolation and
        // pixel-offset mode, is a resampling step that can blur or half-pixel shift every edge, and an
        // edge-exact trace would then sit off the picture the user sees.
        if (width == original.Width && height == original.Height) return ReadPixels(original);

        using var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bitmap))
        {
            // Composite onto white before reading luminance. The surface starts fully transparent,
            // so without this every transparent pixel of a PNG logo came back as (0,0,0) — luminance
            // 0, which downstream means "full power". A logo with a transparent background was
            // therefore scheduled to be burnt as a solid filled rectangle. White is the correct
            // backdrop: it is the value the rest of the pipeline already treats as "leave alone".
            g.Clear(Color.White);
            g.CompositingQuality = CompositingQuality.HighQuality;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            // Downscale in source pixels (not the image's DPI) and mirror at the borders, so the edge
            // rows do not fade into the transparent surface around the image.
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            using var attributes = new ImageAttributes();
            attributes.SetWrapMode(WrapMode.TileFlipXY);
            g.DrawImage(original, new Rectangle(0, 0, width, height), 0, 0, original.Width, original.Height, GraphicsUnit.Pixel, attributes);
        }

        var luminance = new byte[width * height];

        var data = bitmap.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var stride = data.Stride;
            var row = new byte[stride];
            for (int y = 0; y < height; y++)
            {
                Marshal.Copy(data.Scan0 + y * stride, row, 0, stride);
                for (int x = 0; x < width; x++)
                {
                    var b = row[x * 4 + 0];
                    var green = row[x * 4 + 1];
                    var r = row[x * 4 + 2];
                    var luma = 0.299 * r + 0.587 * green + 0.114 * b;
                    luminance[y * width + x] = (byte)Math.Clamp(Math.Round(luma), 0, 255);
                }
            }
        }
        finally
        {
            bitmap.UnlockBits(data);
        }

        return new GrayscaleImage { Width = width, Height = height, Luminance = luminance };
    }

    /// <summary>Luminance of every pixel of an image at its own size, composited onto white (transparent
    /// pixels read as paper, see the note in LoadGrayscaleCore). LockBits converts any source format to
    /// 32 bpp ARGB without resampling, so pixel (x, y) of the file is element y * width + x.</summary>
    private static GrayscaleImage ReadPixels(Bitmap original)
    {
        var width = original.Width;
        var height = original.Height;
        var luminance = new byte[width * height];

        var data = original.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var stride = data.Stride;
            var row = new byte[Math.Abs(stride)];
            for (var y = 0; y < height; y++)
            {
                Marshal.Copy(data.Scan0 + y * stride, row, 0, row.Length);
                for (var x = 0; x < width; x++)
                {
                    var alpha = row[x * 4 + 3];
                    var b = Over(row[x * 4 + 0], alpha);
                    var g = Over(row[x * 4 + 1], alpha);
                    var r = Over(row[x * 4 + 2], alpha);
                    luminance[y * width + x] = (byte)Math.Clamp(Math.Round(0.299 * r + 0.587 * g + 0.114 * b), 0, 255);
                }
            }
        }
        finally
        {
            original.UnlockBits(data);
        }

        return new GrayscaleImage { Width = width, Height = height, Luminance = luminance };
    }

    /// <summary>A colour channel over a white backdrop.</summary>
    private static int Over(byte channel, byte alpha) => (channel * alpha + 255 * (255 - alpha) + 127) / 255;
}
