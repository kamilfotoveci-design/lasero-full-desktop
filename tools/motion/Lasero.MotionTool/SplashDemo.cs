using Lasero.App;

namespace Lasero.MotionTool;

/// <summary>
/// Runs the real startup splash in isolation: splash [--busy ms] [--wait-after ms] [--force].
/// --busy blocks THIS (main) thread for that long right after the splash starts, standing in for the DI
/// container, stores and session resume, to prove the splash animation does not depend on the main thread.
/// Set LASERO_SPLASH_TRACE=path to get frame pacing (mean/p95/max) written when the splash closes.
/// </summary>
internal static class SplashDemo
{
    public static int Run(Options o)
    {
        Lasero.App.Controls.Motion.LaseroMotion.ForceAnimations = o.Get("force") == "true"; // this PC has Windows animations off
        var started = DateTime.UtcNow;
        var splash = StartupSplash.Start();
        var busy = o.GetInt("busy", 0);
        if (busy > 0) Thread.Sleep(busy);
        splash.WaitForAnimationAsync().GetAwaiter().GetResult();
        Console.WriteLine($"animation finished after {(DateTime.UtcNow - started).TotalMilliseconds:F0} ms");
        splash.FadeOutAndClose();
        Thread.Sleep(o.GetInt("wait-after", 600));
        Console.WriteLine("splash closed");
        return 0;
    }
}
