using System.IO;
using Lasero.App.Controls.Motion;

namespace Lasero.MotionTool;

/// <summary>
/// Renders the brand animation to H.264 by piping raw frames into ffmpeg: no frame files are ever written.
///   render-video --kind intro|loop --out file.mp4 [--width 1920 --height 1080 --fps 30 --crf 20]
///   render-poster --out file.png [--width 1920 --height 1080]
/// intro = the 8 s IntroRenderer timeline; loop = one 5 s PulseRenderer period (first frame follows the last).
/// </summary>
internal static class VideoRenderer
{
    public static int Run(Options o)
    {
        var kind = o.Get("kind", "intro");
        var w = o.GetInt("width", 1920); var h = o.GetInt("height", 1080); var fps = o.GetInt("fps", 30);
        var crf = o.GetInt("crf", 20);
        var outFile = Path.GetFullPath(o.Require("out"));
        Directory.CreateDirectory(Path.GetDirectoryName(outFile)!);
        var seconds = kind == "loop" ? PulseRenderer.Period : IntroTimeline.Full.Total;
        var frames = (int)Math.Round(seconds * fps);

        var intro = new IntroRenderer { DrawBackground = true };
        var pulse = new PulseRenderer { DrawBackground = true, ShowWordmark = true };

        // yuv420p + faststart for broad player and MediaElement compatibility; BT.709 tagged so the warm white matches the UI
        var args = $"-c:v libx264 -preset slower -crf {crf} -tune animation -profile:v high -level 4.1 " +
                   $"-vf scale=out_color_matrix=bt709:out_range=tv,format=yuv420p -colorspace bt709 -color_primaries bt709 -color_trc bt709 " +
                   $"-movflags +faststart -an \"{outFile}\"";
        var ffmpeg = Ffmpeg.Find(o);
        // the codec arguments without the output file, to calibrate the background against the real encoder
        var codecOnly = args[..args.LastIndexOf(" -movflags")];
        var ground = BackgroundMatch.Find(ffmpeg, codecOnly, MotionPalette.Default.Background);
        intro.Palette = MotionPalette.Default with { Background = ground };
        pulse.Palette = intro.Palette;
        using var ff = Ffmpeg.StartRawInput(ffmpeg, w, h, fps, args);
        var stream = ff.StandardInput.BaseStream;
        for (var i = 0; i < frames; i++)
        {
            var t = i / (double)fps;
            var bmp = FrameRenderer.Render(w, h, (dc, size) =>
            {
                if (kind == "loop") pulse.Render(dc, t, size); else intro.Render(dc, t, size);
            });
            var px = FrameRenderer.Pixels(bmp);
            stream.Write(px, 0, px.Length);
        }
        Ffmpeg.Finish(ff);
        Console.WriteLine($"{kind}: {frames} frames {w}x{h}@{fps} -> {outFile} ({new FileInfo(outFile).Length / 1024} KB)");
        return 0;
    }

    public static int RunPoster(Options o)
    {
        var w = o.GetInt("width", 1920); var h = o.GetInt("height", 1080);
        var outFile = Path.GetFullPath(o.Require("out"));
        Directory.CreateDirectory(Path.GetDirectoryName(outFile)!);
        var intro = new IntroRenderer { DrawBackground = true };
        var bmp = FrameRenderer.Render(w, h, (dc, size) => intro.Render(dc, o.GetDouble("t", intro.Timeline.Total), size));
        FrameRenderer.SavePng(bmp, outFile);
        Console.WriteLine($"poster {w}x{h} -> {outFile} ({new FileInfo(outFile).Length / 1024} KB)");
        return 0;
    }
}
