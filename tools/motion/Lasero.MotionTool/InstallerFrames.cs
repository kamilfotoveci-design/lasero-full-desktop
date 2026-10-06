using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Lasero.App.Controls.Motion;

namespace Lasero.MotionTool;

/// <summary>
/// The animated installer is a flip-book: Inno Setup cannot play video, so the same renderers that make the
/// intro are sampled into a few dozen small 8-bit BMPs that the wizard script cycles (installer/Lasero.iss).
///   panel-00..23   welcome/finish side panel, the Quick intro (2.8 s) played once
///   loop-00..15    the panel's idle breath, looped after the intro
///   small-00..23   header badge pulse for the inner pages
///   banner-00..19  wordmark drawn in proportion to install progress
/// Also rewrites the static wizard-164/192/246.bmp as the intro's hold frame, so the static fallback and
/// the end of the animation are the same picture. Frames are quantised with ffmpeg palettegen (no dither).
/// </summary>
internal static class InstallerFrames
{
    public const int PanelW = 246, PanelH = 459;
    public const int IntroFrames = 24, LoopFrames = 16, SmallFrames = 24, BannerFrames = 20;
    public const int SmallSize = 96, BannerW = 460, BannerH = 110;

    private static readonly Color PageWhite = Colors.White;

    public static int Run(Options o)
    {
        var outDir = Path.GetFullPath(o.Require("out"));
        var assets = Path.GetFullPath(o.Require("assets"));
        var logo = Path.GetFullPath(o.Require("logo"));
        var ffmpeg = Ffmpeg.Find(o);
        Directory.CreateDirectory(outDir);
        foreach (var old in Directory.EnumerateFiles(outDir, "*.bmp")) File.Delete(old);

        // --- side panel: intro then breathing loop -------------------------------------------------
        var quick = new IntroRenderer { DrawBackground = true, Timeline = IntroTimeline.Quick };
        var pulse = new PulseRenderer { DrawBackground = true, ShowWordmark = true };
        void Panel(DrawingContext dc, Size size, Action<DrawingContext, Size> body)
        {
            body(dc, size);
            DecoratePanel(dc, size);
        }
        Encode(ffmpeg, PanelW, PanelH, outDir, "panel", IntroFrames, i => FrameRenderer.Render(PanelW, PanelH, (dc, size) =>
            Panel(dc, size, (d, s) => quick.Render(d, quick.Timeline.Total * i / (IntroFrames - 1), s))));
        Encode(ffmpeg, PanelW, PanelH, outDir, "loop", LoopFrames, i => FrameRenderer.Render(PanelW, PanelH, (dc, size) =>
            Panel(dc, size, (d, s) => pulse.Render(d, PulseRenderer.Period * i / LoopFrames, s))));

        // --- static fallback panels = the hold frame, three DPI sizes ------------------------------------
        foreach (var (w, h) in new[] { (164, 314), (192, 386), (246, 459) })
        {
            var bmp = FrameRenderer.Render(w, h, (dc, size) => Panel(dc, size, (d, s) => quick.Render(d, quick.Timeline.Total, s)));
            FrameRenderer.SaveBmp24(bmp, Path.Combine(assets, $"wizard-{w}.bmp"));
        }

        // --- header badge: the tile with a breathing dot ---------------------------------------------
        var tile = TileArt.Load(logo);
        Encode(ffmpeg, SmallSize, SmallSize, outDir, "small", SmallFrames, i => FrameRenderer.Render(SmallSize, SmallSize, (dc, size) =>
            tile.Draw(dc, size, i / (double)SmallFrames)));

        // --- install progress banner: the wordmark is drawn as the install advances ---------------------
        var banner = new IntroRenderer
        {
            DrawBackground = true, DrawGrid = false, DrawTagline = false, MarkOnly = true, Timeline = IntroTimeline.Quick,
            Palette = MotionPalette.Default with { Background = PageWhite },
        };
        Encode(ffmpeg, BannerW, BannerH, outDir, "banner", BannerFrames, i => FrameRenderer.Render(BannerW, BannerH, (dc, size) =>
            banner.Render(dc, 2.30 * i / (BannerFrames - 1), size)));

        var total = Directory.EnumerateFiles(outDir, "*.bmp").Sum(f => new FileInfo(f).Length);
        Console.WriteLine($"frames in {outDir}: {Directory.EnumerateFiles(outDir, "*.bmp").Count()} files, {total / 1024} KB raw (before Inno LZMA)");
        return 0;
    }

    /// <summary>Quiet footer and the right-edge hairline of the old panel, kept so the static fallback looks the same.</summary>
    private static void DecoratePanel(DrawingContext dc, Size size)
    {
        var pal = MotionPalette.Default;
        dc.DrawRectangle(MotionPalette.Solid(Color.FromRgb(0xE5, 0xE5, 0xEA)), null, new Rect(size.Width - 1, 0, 1, size.Height));
        var ft = new FormattedText("Testovací verze", CultureInfo.GetCultureInfo("cs-CZ"), FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe UI Variable Text, Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal),
            size.Width * 8.0 / 164, MotionPalette.Solid(pal.Muted), 1.0);
        dc.DrawText(ft, new Point(size.Width * 20.0 / 164, size.Height - size.Width * 28.0 / 164));
    }

    /// <summary>Renders <paramref name="count"/> frames, quantises them as a set and writes name-NN.bmp (8-bit).</summary>
    private static void Encode(string ffmpeg, int w, int h, string outDir, string name, int count, Func<int, BitmapSource> frame)
    {
        var pattern = Path.Combine(outDir, name + "-%02d.bmp");
        var args = $"-vf \"split[a][b];[a]palettegen=max_colors=128:stats_mode=full[p];[b][p]paletteuse=dither=none\" -pix_fmt pal8 -start_number 0 \"{pattern}\"";
        using var ff = Ffmpeg.StartRawInput(ffmpeg, w, h, 10, args);
        var stream = ff.StandardInput.BaseStream;
        for (var i = 0; i < count; i++)
        {
            var px = FrameRenderer.Pixels(frame(i));
            stream.Write(px, 0, px.Length);
        }
        Ffmpeg.Finish(ff);
        Console.WriteLine($"{name}: {count} frames {w}x{h}");
    }
}

/// <summary>The dark app tile (Assets/Lasero.png) cropped square on white, with a breathing dot drawn over its red dot.</summary>
internal sealed class TileArt
{
    private BitmapSource _tile = null!;
    private Point _dot; // in tile pixel space
    private double _dotR;

    public static TileArt Load(string path)
    {
        var decoder = BitmapDecoder.Create(new Uri(path), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        var src = new FormatConvertedBitmap(decoder.Frames[0], PixelFormats.Bgra32, null, 0);
        int w = src.PixelWidth, h = src.PixelHeight;
        var px = new byte[w * h * 4];
        src.CopyPixels(px, w * 4, 0);
        int minX = w, minY = h, maxX = 0, maxY = 0, maxRedY = 0;
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                var i = (y * w + x) * 4;
                int b = px[i], g = px[i + 1], r = px[i + 2], a = px[i + 3];
                if (a > 128 && r + g + b < 200)
                {
                    minX = Math.Min(minX, x); maxX = Math.Max(maxX, x); minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
                }
                if (a > 128 && r > 200 && g < 60 && b < 60) maxRedY = Math.Max(maxRedY, y);
            }
        double sx = 0, sy = 0, n = 0; int dx0 = w, dx1 = 0;
        for (var y = Math.Max(0, maxRedY - 55); y <= maxRedY; y++)
            for (var x = 0; x < w; x++)
            {
                var i = (y * w + x) * 4;
                if (px[i + 3] > 128 && px[i + 2] > 200 && px[i + 1] < 60 && px[i] < 60) { sx += x; sy += y; n++; dx0 = Math.Min(dx0, x); dx1 = Math.Max(dx1, x); }
            }
        var tw = maxX - minX + 1; var th = maxY - minY + 1; var side = Math.Max(tw, th);
        var tile = new CroppedBitmap(src, new Int32Rect(minX, minY, tw, th));
        var art = new TileArt { _tile = tile, _dot = new Point(sx / n - minX + (side - tw) / 2.0, sy / n - minY + (side - th) / 2.0), _dotR = (dx1 - dx0 + 1) / 2.0 };
        art._side = side; art._tw = tw; art._th = th;
        return art;
    }

    private int _side, _tw, _th;

    public void Draw(DrawingContext dc, Size size, double phase)
    {
        dc.DrawRectangle(Brushes.White, null, new Rect(size));
        var margin = size.Width * 0.10;
        var target = size.Width - 2 * margin;
        var k = target / _side;
        var offX = margin + (_side - _tw) / 2.0 * k;
        var offY = margin + (_side - _th) / 2.0 * k;
        dc.DrawImage(_tile, new Rect(offX, offY, _tw * k, _th * k));

        // dot centre in output space (tile space includes the centring pad)
        var c = new Point(margin + _dot.X * k, margin + _dot.Y * k);
        var breath = (phase * 2) % 1.0;
        var swell = 0.5 - 0.5 * Math.Cos(2 * Math.PI * breath);
        var red = Color.FromRgb(0xFF, 0x00, 0x00); // the tile art's own red
        var r = _dotR * k * (1 + 0.14 * swell);
        dc.DrawEllipse(new SolidColorBrush(red), null, c, r, r);
        var ring = (breath + 0.5) % 1.0;
        var rr = _dotR * k * (1.14 + 1.6 * MotionEase.OutQuart(ring));
        var pen = new Pen(new SolidColorBrush(Color.FromArgb((byte)(255 * 0.55 * Math.Pow(1 - ring, 1.5)), 0xFF, 0x30, 0x30)), Math.Max(1, size.Width * 0.012));
        dc.DrawEllipse(null, pen, c, rr, rr);
    }
}
