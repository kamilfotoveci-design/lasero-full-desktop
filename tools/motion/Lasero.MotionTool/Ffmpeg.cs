using System.Diagnostics;
using System.IO;

namespace Lasero.MotionTool;

internal static class Ffmpeg
{
    /// <summary>--ffmpeg path, else PATH, else the WinGet (Gyan.FFmpeg) package folder.</summary>
    public static string Find(Options o)
    {
        var given = o.Get("ffmpeg");
        if (given.Length > 0) return given;
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            try { var p = Path.Combine(dir.Trim('"'), "ffmpeg.exe"); if (File.Exists(p)) return p; } catch (ArgumentException) { }
        }
        var winget = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WinGet", "Packages");
        if (Directory.Exists(winget))
        {
            var hit = Directory.EnumerateFiles(winget, "ffmpeg.exe", SearchOption.AllDirectories).FirstOrDefault();
            if (hit is not null) return hit;
        }
        throw new FileNotFoundException("ffmpeg.exe not found. Install it (winget install Gyan.FFmpeg) or pass --ffmpeg <path>.");
    }

    /// <summary>Starts ffmpeg reading raw BGRA frames from stdin.</summary>
    public static Process StartRawInput(string exe, int width, int height, int fps, string outputArgs)
    {
        var psi = new ProcessStartInfo(exe)
        {
            Arguments = $"-y -hide_banner -loglevel error -f rawvideo -pix_fmt bgra -s {width}x{height} -r {fps} -i - {outputArgs}",
            RedirectStandardInput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        return Process.Start(psi) ?? throw new InvalidOperationException("ffmpeg did not start");
    }

    public static void Finish(Process p)
    {
        p.StandardInput.BaseStream.Flush();
        p.StandardInput.Close();
        var err = p.StandardError.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0) throw new InvalidOperationException("ffmpeg failed: " + err);
    }
}
