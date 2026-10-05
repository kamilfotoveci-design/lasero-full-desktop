using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32.SafeHandles;

namespace Lasero.App.Input;

/// <summary>
/// The cursor over the rotate handle: a two-headed arc, drawn at startup rather than shipped as a
/// binary asset. WPF has no rotate cursor of its own, and reusing the hand that also means "node"
/// and "pan" gave three different actions one shape. If anything about building it fails the
/// caller gets the stock hand back, so a missing cursor can never break the canvas.
/// </summary>
public static class RotateCursor
{
    private const int Size = 32;
    private static Cursor? _cached;

    public static Cursor Get() => _cached ??= TryBuild() ?? Cursors.Hand;

    private static Cursor? TryBuild()
    {
        try
        {
            var pixels = Render();
            var handle = CreateNativeIcon(pixels);
            return handle.IsInvalid ? null : CursorInteropHelper.Create(handle);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static byte[] Render()
    {
        var geometry = Geometry.Parse("M 9,21 A 10,10 0 0 1 9,11 M 23,11 A 10,10 0 0 1 23,21");
        var arrowHeads = Geometry.Parse("M 5,11 L 9,8 L 10,13 Z M 27,21 L 23,24 L 22,19 Z");
        var dark = new SolidColorBrush(Color.FromRgb(0x1D, 0x1D, 0x1F));
        var light = Brushes.White;
        dark.Freeze();

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            // A white halo first, so the mark reads on the dark canvas as well as the light one.
            var halo = new Pen(light, 4.5) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
            var line = new Pen(dark, 2) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
            dc.DrawGeometry(null, halo, geometry);
            dc.DrawGeometry(light, halo, arrowHeads);
            dc.DrawGeometry(null, line, geometry);
            dc.DrawGeometry(dark, line, arrowHeads);
        }

        var target = new RenderTargetBitmap(Size, Size, 96, 96, PixelFormats.Pbgra32);
        target.Render(visual);
        var pixels = new byte[Size * Size * 4];
        target.CopyPixels(pixels, Size * 4, 0);
        return pixels;
    }

    private static SafeIconHandle CreateNativeIcon(byte[] bgraPixels)
    {
        var color = CreateBitmap(Size, Size, 1, 32, bgraPixels);
        var mask = CreateBitmap(Size, Size, 1, 1, new byte[Size * Size / 8]);
        try
        {
            var info = new IconInfo { IsIcon = false, HotspotX = Size / 2, HotspotY = Size / 2, Mask = mask, Color = color };
            return new SafeIconHandle(CreateIconIndirect(ref info));
        }
        finally
        {
            DeleteObject(color);
            DeleteObject(mask);
        }
    }

    private sealed class SafeIconHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public SafeIconHandle(IntPtr handle) : base(ownsHandle: true) => SetHandle(handle);
        protected override bool ReleaseHandle() => DestroyIcon(handle);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IconInfo
    {
        [MarshalAs(UnmanagedType.Bool)] public bool IsIcon;
        public int HotspotX;
        public int HotspotY;
        public IntPtr Mask;
        public IntPtr Color;
    }

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateBitmap(int width, int height, uint planes, uint bitsPerPixel, byte[] bits);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr handle);

    [DllImport("user32.dll")]
    private static extern IntPtr CreateIconIndirect(ref IconInfo info);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr handle);
}
