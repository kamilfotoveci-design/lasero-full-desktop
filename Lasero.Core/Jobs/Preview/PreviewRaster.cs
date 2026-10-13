using Lasero.Core.Layers;

namespace Lasero.Core.Jobs;

/// <summary>
/// A small software canvas the job preview draws its lines into: an opaque 32-bit pixel buffer (0xFFRRGGBB)
/// with anti-aliased lines that are one device pixel wide at any zoom. A job can hold hundreds of thousands of
/// segments, so the preview paints them here and hands the finished buffer to one bitmap instead of creating
/// a UI element, or even a geometry, per segment.
/// </summary>
public sealed class PreviewRaster
{
    public PreviewRaster(int width, int height)
    {
        Width = Math.Max(1, width);
        Height = Math.Max(1, height);
        Pixels = new int[Width * Height];
    }

    public int Width { get; }
    public int Height { get; }
    public int[] Pixels { get; }

    public static int Pack(RgbColor color) => unchecked((int)0xFF000000) | (color.R << 16) | (color.G << 8) | color.B;

    public void Fill(RgbColor color) => Array.Fill(Pixels, Pack(color));

    public void CopyFrom(PreviewRaster source)
    {
        if (source.Width != Width || source.Height != Height)
            throw new ArgumentException("Plátna musí mít stejný rozměr.", nameof(source));
        Array.Copy(source.Pixels, Pixels, Pixels.Length);
    }

    public RgbColor GetPixel(int x, int y)
    {
        var value = Pixels[y * Width + x];
        return new RgbColor((byte)(value >> 16), (byte)(value >> 8), (byte)value);
    }

    /// <summary>
    /// Draws an anti-aliased line of one pixel width, blended over what is there. Coordinates are in pixels with
    /// pixel centres at half integers. <paramref name="alpha"/> 1 is the full colour. Lines partly or wholly
    /// outside the canvas are clipped; lines with no length draw nothing.
    /// </summary>
    public void DrawLine(double x0, double y0, double x1, double y1, RgbColor color, double alpha = 1)
    {
        if (!(alpha > 0)) return;
        if (!double.IsFinite(x0) || !double.IsFinite(y0) || !double.IsFinite(x1) || !double.IsFinite(y1)) return;
        if (!Clip(ref x0, ref y0, ref x1, ref y1)) return;
        if (Math.Abs(x1 - x0) < 1e-9 && Math.Abs(y1 - y0) < 1e-9) return;

        var steep = Math.Abs(y1 - y0) > Math.Abs(x1 - x0);
        if (steep)
        {
            (x0, y0) = (y0, x0);
            (x1, y1) = (y1, x1);
        }

        if (x0 > x1)
        {
            (x0, x1) = (x1, x0);
            (y0, y1) = (y1, y0);
        }

        var dx = x1 - x0;
        var gradient = (y1 - y0) / dx;
        var first = (int)Math.Floor(x0);
        var last = (int)Math.Floor(x1);
        for (var x = first; x <= last; x++)
        {
            var left = Math.Max(x, x0);
            var right = Math.Min(x + 1, x1);
            var weight = right - left;
            if (weight <= 0) continue;

            var y = y0 + gradient * ((left + right) * 0.5 - x0) - 0.5;
            var row = (int)Math.Floor(y);
            var fraction = y - row;
            var strength = Math.Min(1, alpha) * weight;
            Blend(steep, x, row, color, strength * (1 - fraction));
            Blend(steep, x, row + 1, color, strength * fraction);
        }
    }

    private void Blend(bool steep, int x, int y, RgbColor color, double strength)
    {
        var px = steep ? y : x;
        var py = steep ? x : y;
        if ((uint)px >= (uint)Width || (uint)py >= (uint)Height) return;

        var weight = (int)(strength * 256 + 0.5);
        if (weight <= 0) return;
        if (weight > 256) weight = 256;

        ref var pixel = ref Pixels[py * Width + px];
        var destination = pixel;
        var r = (destination >> 16) & 0xFF;
        var g = (destination >> 8) & 0xFF;
        var b = destination & 0xFF;
        r += ((color.R - r) * weight) >> 8;
        g += ((color.G - g) * weight) >> 8;
        b += ((color.B - b) * weight) >> 8;
        pixel = unchecked((int)0xFF000000) | (r << 16) | (g << 8) | b;
    }

    /// <summary>Liang-Barsky clip against the canvas grown by one pixel, so edge pixels keep their anti-aliasing.</summary>
    private bool Clip(ref double x0, ref double y0, ref double x1, ref double y1)
    {
        double minX = -1, minY = -1, maxX = Width + 1, maxY = Height + 1;
        var dx = x1 - x0;
        var dy = y1 - y0;
        double t0 = 0, t1 = 1;
        if (!ClipTest(-dx, x0 - minX, ref t0, ref t1) ||
            !ClipTest(dx, maxX - x0, ref t0, ref t1) ||
            !ClipTest(-dy, y0 - minY, ref t0, ref t1) ||
            !ClipTest(dy, maxY - y0, ref t0, ref t1))
            return false;

        var startX = x0;
        var startY = y0;
        if (t1 < 1)
        {
            x1 = startX + t1 * dx;
            y1 = startY + t1 * dy;
        }

        if (t0 > 0)
        {
            x0 = startX + t0 * dx;
            y0 = startY + t0 * dy;
        }

        return true;
    }

    private static bool ClipTest(double p, double q, ref double t0, ref double t1)
    {
        if (p == 0) return q >= 0;
        var r = q / p;
        if (p < 0)
        {
            if (r > t1) return false;
            if (r > t0) t0 = r;
        }
        else
        {
            if (r < t0) return false;
            if (r < t1) t1 = r;
        }

        return true;
    }
}
