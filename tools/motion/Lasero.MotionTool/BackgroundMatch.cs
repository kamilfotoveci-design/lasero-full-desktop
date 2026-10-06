using System.IO;
using System.Windows;
using System.Windows.Media;

namespace Lasero.MotionTool;

/// <summary>
/// 8-bit limited-range YUV cannot represent every RGB colour: the warm-white ground #F7F6F3 comes back from any
/// H.264 decoder (ffmpeg and Windows Media Foundation agree) a couple of levels off, which shows as a faint
/// box when the video sits on the real UI background. This finds, empirically with the very same encoder
/// settings, the RGB value to paint so that the decoded video background equals the UI colour exactly.
/// </summary>
internal static class BackgroundMatch
{
    public static Color Find(string ffmpeg, string codecArgs, Color target)
    {
        const int cell = 8, cols = 19, span = 3; // 7 values per channel, 343 candidates in a 19x19 grid of 8x8 cells
        var candidates = new List<(int dr, int dg, int db)>();
        for (var r = -span; r <= span; r++)
            for (var g = -span; g <= span; g++)
                for (var b = -span; b <= span; b++) candidates.Add((r, g, b));

        int Clamp(int v) => Math.Clamp(v, 0, 255);
        var side = cols * cell;
        var bmp = FrameRenderer.Render(side, side, (dc, size) =>
        {
            dc.DrawRectangle(Brushes.White, null, new Rect(size));
            for (var i = 0; i < candidates.Count; i++)
            {
                var c = candidates[i];
                var color = Color.FromRgb((byte)Clamp(target.R + c.dr), (byte)Clamp(target.G + c.dg), (byte)Clamp(target.B + c.db));
                var brush = new SolidColorBrush(color); brush.Freeze();
                dc.DrawRectangle(brush, null, new Rect(i % cols * cell, i / cols * cell, cell, cell));
            }
        });

        var clip = Path.Combine(Path.GetTempPath(), "lasero-bgmatch-" + Guid.NewGuid().ToString("N") + ".mp4");
        try
        {
            using (var ff = Ffmpeg.StartRawInput(ffmpeg, side, side, 1, codecArgs + $" -frames:v 1 \"{clip}\""))
            {
                var px = FrameRenderer.Pixels(bmp);
                ff.StandardInput.BaseStream.Write(px, 0, px.Length);
                Ffmpeg.Finish(ff);
            }
            var rgb = FfmpegCapture.Run(ffmpeg, $"-v error -i \"{clip}\" -frames:v 1 -f rawvideo -pix_fmt rgb24 -");
            (int err, Color color)? best = null;
            for (var i = 0; i < candidates.Count; i++)
            {
                // centre pixel of the cell: far from the 2x2 chroma blocks that touch neighbouring colours
                var x = i % cols * cell + cell / 2 - 1; var y = i / cols * cell + cell / 2 - 1;
                var o = (y * side + x) * 3;
                var err = Math.Abs(rgb[o] - target.R) + Math.Abs(rgb[o + 1] - target.G) + Math.Abs(rgb[o + 2] - target.B);
                var c = candidates[i];
                var paint = Color.FromRgb((byte)Clamp(target.R + c.dr), (byte)Clamp(target.G + c.dg), (byte)Clamp(target.B + c.db));
                var distance = Math.Abs(c.dr) + Math.Abs(c.dg) + Math.Abs(c.db);
                if (best is null || err < best.Value.err || (err == best.Value.err && distance < Dist(best.Value.color, target)))
                    best = (err, paint);
            }
            Console.WriteLine($"background match: paint #{best!.Value.color.R:X2}{best.Value.color.G:X2}{best.Value.color.B:X2} to decode as #{target.R:X2}{target.G:X2}{target.B:X2} (residual {best.Value.err})");
            return best.Value.color;
        }
        finally { try { File.Delete(clip); } catch (IOException) { } }
    }

    private static int Dist(Color a, Color b) => Math.Abs(a.R - b.R) + Math.Abs(a.G - b.G) + Math.Abs(a.B - b.B);
}
