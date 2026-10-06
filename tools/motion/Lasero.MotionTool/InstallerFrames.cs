using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Lasero.App.Controls.Motion;

namespace Lasero.MotionTool;

/// <summary>Writes the static wizard side panels (the intro's hold frame) at the three DPI sizes. The installer itself is
/// deliberately static: no timers, no frame player. The brand animation plays when the app starts.</summary>
internal static class InstallerFrames
{


    public static int Run(Options o)
    {
        var assets = Path.GetFullPath(o.Require("assets"));
        var quick = new IntroRenderer { DrawBackground = true, Timeline = IntroTimeline.Quick };
        // the wizard is static: the intro's hold frame at the three DPI sizes of WizardImageFile
        foreach (var (w, h) in new[] { (164, 314), (192, 386), (246, 459) })
        {
            var bmp = FrameRenderer.Render(w, h, (dc, size) =>
            {
                quick.Render(dc, quick.Timeline.Total, size);
                DecoratePanel(dc, size);
            });
            FrameRenderer.SaveBmp24(bmp, Path.Combine(assets, $"wizard-{w}.bmp"));
            Console.WriteLine($"wizard-{w}.bmp {w}x{h}");
        }
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

}
