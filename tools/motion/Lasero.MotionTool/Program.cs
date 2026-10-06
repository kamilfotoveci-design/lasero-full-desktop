using System.Runtime.InteropServices;
using System.Windows;

namespace Lasero.MotionTool;

/// <summary>
/// Entry point of the motion tool. Commands (first argument):
///   trace-wordmark   re-trace Assets/LaseroWordmark.png into Controls/Motion/WordmarkData.cs
///   render-video     encode the brand animation to H.264 through ffmpeg (frames are piped, never stored)
///   render-poster    write the hold frame as PNG
///   render-installer encode the Inno Setup wizard frame sequences (8-bit BMP)
///   preview          open the preview harness window (default when no argument is given)
/// A WinExe is used so the preview window needs no console; console commands attach to the parent console.
/// </summary>
public static class Program
{
    [DllImport("kernel32.dll")] private static extern bool AttachConsole(int pid);

    [STAThread]
    public static int Main(string[] args)
    {
        AttachConsole(-1);
        var command = args.Length > 0 ? args[0] : "preview";
        var options = Options.Parse(args.Skip(1));
        try
        {
            switch (command)
            {
                case "trace-wordmark": return WordmarkTracer.Run(options);
                case "render-video": return VideoRenderer.Run(options);
                case "render-poster": return VideoRenderer.RunPoster(options);
                case "render-installer": return InstallerFrames.Run(options);
                case "splash": return SplashDemo.Run(options);
                case "preview":
                    return new Application().Run(new PreviewWindow());
                default:
                    Console.Error.WriteLine("Unknown command: " + command);
                    return 2;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
    }
}

internal sealed class Options
{
    private readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);

    public static Options Parse(IEnumerable<string> args)
    {
        var o = new Options();
        string? key = null;
        foreach (var a in args)
        {
            if (a.StartsWith("--")) { key = a[2..]; o._values[key] = "true"; }
            else if (key is not null) { o._values[key] = a; key = null; }
        }
        return o;
    }

    public string Get(string name, string fallback = "") => _values.TryGetValue(name, out var v) ? v : fallback;
    public int GetInt(string name, int fallback) => _values.TryGetValue(name, out var v) && int.TryParse(v, out var i) ? i : fallback;
    public double GetDouble(string name, double fallback) =>
        _values.TryGetValue(name, out var v) && double.TryParse(v, System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : fallback;
    public string Require(string name) => _values.TryGetValue(name, out var v) ? v : throw new ArgumentException("Missing --" + name);
}
