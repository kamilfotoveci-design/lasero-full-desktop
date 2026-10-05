using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Lasero.MotionTool;

/// <summary>Rasterises DrawingVisual content at 96 dpi with RenderTargetBitmap (deterministic, no window needed).</summary>
internal static class FrameRenderer
{
    public static BitmapSource Render(int width, int height, Action<DrawingContext, Size> draw)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen()) draw(dc, new Size(width, height));
        var rtb = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(visual);
        rtb.Freeze();
        return rtb;
    }

    public static byte[] Pixels(BitmapSource bmp)
    {
        var stride = bmp.PixelWidth * 4;
        var data = new byte[stride * bmp.PixelHeight];
        bmp.CopyPixels(data, stride, 0);
        return data;
    }

    public static void SavePng(BitmapSource bmp, string path)
    {
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using var fs = System.IO.File.Create(path);
        enc.Save(fs);
    }

    public static void SaveBmp24(BitmapSource bmp, string path)
    {
        var enc = new BmpBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(new FormatConvertedBitmap(bmp, PixelFormats.Bgr24, null, 0)));
        using var fs = System.IO.File.Create(path);
        enc.Save(fs);
    }
}
