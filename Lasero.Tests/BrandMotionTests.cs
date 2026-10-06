using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Lasero.App;
using Lasero.App.Controls.Motion;
using Xunit;

namespace Lasero.Tests;

/// <summary>
/// Guards for the brand motion (Controls/Motion, tools/motion). Source-level rules
/// first (no timers, no transparency/effects/gradients, copy rules), then the renderer as a pure function
/// of time, then the controls' lifecycle on a real WPF dispatcher: they must stop their clock on
/// Unloaded, pause when hidden or minimized, show the final frame under reduced motion, and the video
/// control must fall back to the vector intro when the media cannot be opened.
/// All tests that touch <see cref="LaseroMotion"/> statics live in this one class so they never overlap.
/// </summary>
[Collection("BrandMotion")]
public sealed class BrandMotionTests
{
    private static Dispatcher Ui => InlineTextEditorRenderTests.Ui;

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "Lasero.App"))) dir = dir.Parent;
        return dir!.FullName;
    }

    private static IEnumerable<string> MotionSources() =>
        Directory.EnumerateFiles(Path.Combine(RepoRoot(), "Lasero.App", "Controls", "Motion"), "*.cs")
            .Where(f => !Path.GetFileName(f).Equals("WordmarkData.cs", StringComparison.Ordinal)); // generated numbers only

    private static void Pump(int milliseconds)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(milliseconds) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }

    private static T OnUi<T>(Func<T> f) => Ui.Invoke(f);
    private static void OnUi(Action a) => Ui.Invoke(a);

    private static Window Host(FrameworkElement content, double width = 640, double height = 360)
    {
        var window = new Window
        {
            Content = content, Width = width, Height = height, ShowActivated = false,
            Left = 80, Top = 80, WindowStyle = WindowStyle.None,
        };
        window.Show();
        return window;
    }

    private sealed class MotionScope : IDisposable
    {
        private readonly bool _force, _reduced;
        public MotionScope(bool animations, bool reduced = false)
        {
            _force = LaseroMotion.ForceAnimations; _reduced = LaseroMotion.ForceReducedMotion;
            LaseroMotion.ForceAnimations = animations;   // do not depend on this machine's Windows animation setting
            LaseroMotion.ForceReducedMotion = reduced;
        }
        public void Dispose() { LaseroMotion.ForceAnimations = _force; LaseroMotion.ForceReducedMotion = _reduced; }
    }

    // ---- source rules ---------------------------------------------------------------------------------

    [Fact]
    public void MotionSourcesUseNoTimersTransparencyEffectsOrGradients()
    {
        var banned = new[]
        {
            "DispatcherTimer", "System.Timers", "System.Threading.Timer", "CompositionTarget.Rendering",
            "AllowsTransparency", "DropShadowEffect", "BlurEffect", "BitmapEffect", "LinearGradientBrush", "RadialGradientBrush",
        };
        foreach (var file in MotionSources())
        {
            var code = File.ReadAllText(file);
            // strip comments so documentation may name what the code avoids
            code = Regex.Replace(code, @"//.*", "");
            code = Regex.Replace(code, @"/\*.*?\*/", "", RegexOptions.Singleline);
            foreach (var word in banned)
                Assert.False(code.Contains(word, StringComparison.Ordinal), $"{Path.GetFileName(file)} uses {word}");
        }
    }

    [Fact]
    public void ControlsReleaseTheirClockWhenUnloaded()
    {
        foreach (var name in new[] { "LaseroIntroAnimation", "LaseroLogoPulse" })
        {
            var code = File.ReadAllText(Path.Combine(RepoRoot(), "Lasero.App", "Controls", "Motion", name + ".cs"));
            Assert.Contains("Unloaded", code);
            Assert.Contains("_driver.Stop()", code);
        }
        var video = File.ReadAllText(Path.Combine(RepoRoot(), "Lasero.App", "Controls", "Motion", "LaseroIntroVideo.cs"));
        Assert.Contains("Unloaded", video);
        Assert.Contains("CancelTimeout()", video);
    }

    [Fact]
    public void TaglineFollowsTheBrandTextRules()
    {
        var tagline = IntroRenderer.TaglineText;
        Assert.Equal("Tvořte s jistotou", tagline);
        Assert.DoesNotContain('?', tagline);
        Assert.DoesNotContain('!', tagline);
        Assert.DoesNotContain("  ", tagline);
    }

    // ---- the renderer is a pure function of time ----------------------------------------------------

    private static BitmapSource Frame(Action<DrawingContext, Size> draw, int w = 960, int h = 540)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen()) draw(dc, new Size(w, h));
        var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(visual);
        return rtb;
    }

    private static byte[] Pixels(BitmapSource bmp)
    {
        var data = new byte[bmp.PixelWidth * bmp.PixelHeight * 4];
        bmp.CopyPixels(data, bmp.PixelWidth * 4, 0);
        return data;
    }

    private static int InkPixels(byte[] bgra)
    {
        var bg = MotionPalette.Default.Background;
        var count = 0;
        for (var i = 0; i < bgra.Length; i += 4)
            if (Math.Abs(bgra[i] - bg.B) + Math.Abs(bgra[i + 1] - bg.G) + Math.Abs(bgra[i + 2] - bg.R) > 24) count++;
        return count;
    }

    [Fact]
    public void IntroStartsEmptyGrowsAndHoldsTheFinalFrame()
    {
        var (empty, mid, late, final, after) = OnUi(() =>
        {
            var r = new IntroRenderer { DrawBackground = true };
            byte[] At(double t) => Pixels(Frame((dc, s) => r.Render(dc, t, s)));
            return (At(0), At(2.0), At(5.0), At(r.Timeline.Total), At(r.Timeline.Total + 5));
        });
        Assert.Equal(0, InkPixels(empty));
        var counts = new[] { InkPixels(mid), InkPixels(late), InkPixels(final) };
        Assert.True(counts[0] > 500, "the wordmark is being drawn at 2 s");
        Assert.True(counts[0] < counts[1] && counts[1] < counts[2], $"ink must grow over time: {string.Join(", ", counts)}");
        Assert.Equal(final, after); // clamped: the hold frame never changes
    }

    [Fact]
    public void SameTimeAlwaysGivesTheSamePixels()
    {
        var (a, b) = OnUi(() =>
        {
            byte[] Render()
            {
                var r = new IntroRenderer { DrawBackground = true };
                return Pixels(Frame((dc, s) => r.Render(dc, 3.37, s)));
            }
            return (Render(), Render());
        });
        Assert.Equal(a, b);
    }

    [Fact]
    public void PulseLoopIsSeamless()
    {
        var (start, wrapped, middle) = OnUi(() =>
        {
            var p = new PulseRenderer { DrawBackground = true, ShowWordmark = true };
            byte[] At(double t) => Pixels(Frame((dc, s) => p.Render(dc, t, s)));
            return (At(0), At(PulseRenderer.Period), At(PulseRenderer.Period * 0.31));
        });
        Assert.Equal(start, wrapped);        // frame N equals frame 0: the video loops without a jump
        Assert.NotEqual(start, middle);      // and it does move
    }

    [Fact]
    public void WordmarkTraceMatchesTheRasterBrandMark()
    {
        // 6 letters + head; counters exist for A, R, O; the head has 3 pieces. Guards a bad re-trace.
        Assert.Equal(7, WordmarkData.Groups.Length);
        Assert.Equal(new[] { "L", "A", "S", "E", "R", "O", "Head" }, WordmarkData.Groups.Select(g => g.Name));
        Assert.Equal(2, WordmarkData.Groups[1].Loops.Length);
        Assert.Equal(2, WordmarkData.Groups[4].Loops.Length);
        Assert.Equal(2, WordmarkData.Groups[5].Loops.Length);
        Assert.Equal(3, WordmarkData.Groups[6].Loops.Length);
    }

    // ---- control lifecycle on a real dispatcher -----------------------------------------------------

    [Fact]
    public void IntroShowsTheFinalFrameWhenAnimationsAreOff()
    {
        using var scope = new MotionScope(animations: false, reduced: true);
        var (time, playing, finished) = OnUi(() =>
        {
            var intro = new LaseroIntroAnimation();
            var window = Host(intro);
            try { Pump(150); return (intro.Time, intro.IsPlaying, intro.IsFinished); }
            finally { window.Close(); }
        });
        Assert.Equal(LaseroIntroAnimation.DurationSeconds, time);
        Assert.False(playing);
        Assert.True(finished);
    }

    [Fact]
    public void IntroPlaysPausesWhenMinimizedAndStopsWhenUnloaded()
    {
        using var scope = new MotionScope(animations: true);
        OnUi(() =>
        {
            var intro = new LaseroIntroAnimation();
            var window = Host(intro);
            try
            {
                Pump(400);
                Assert.True(intro.IsPlaying, "a loaded, visible intro runs its clock");
                Assert.InRange(intro.Time, 0.05, DurationSlack());

                window.WindowState = WindowState.Minimized;
                Pump(100);
                Assert.False(intro.IsPlaying, "minimized window: clock paused");
                var frozen = intro.Time;
                Pump(300);
                Assert.Equal(frozen, intro.Time);

                window.WindowState = WindowState.Normal;
                Pump(300);
                Assert.True(intro.IsPlaying, "restored: clock resumes");
                Assert.True(intro.Time > frozen);

                window.Content = null; // unload
                Pump(100);
                Assert.False(intro.IsPlaying, "unloaded: no clock left attached");
                var atUnload = intro.Time;
                Pump(300);
                Assert.Equal(atUnload, intro.Time);

                window.Content = intro; // back in the tree mid-intro: continues
                Pump(300);
                Assert.True(intro.IsPlaying);
                Assert.True(intro.Time >= atUnload);

                intro.SkipToEnd();
                Assert.False(intro.IsPlaying);
                Assert.Equal(LaseroIntroAnimation.DurationSeconds, intro.Time);
            }
            finally { window.Close(); }
        });
    }

    private static double DurationSlack() => LaseroIntroAnimation.DurationSeconds;

    [Fact]
    public void PulseLoopsWhileVisibleAndIdlesOtherwise()
    {
        using (new MotionScope(animations: true))
        {
            OnUi(() =>
            {
                var pulse = new LaseroLogoPulse { Width = 96, Height = 120 };
                var host = new System.Windows.Controls.Grid();
                host.Children.Add(pulse);
                var window = Host(host);
                try
                {
                    Pump(300);
                    Assert.True(pulse.IsPlaying);
                    pulse.IsActive = false;
                    Assert.False(pulse.IsPlaying, "IsActive=false stops the clock");
                    pulse.IsActive = true;
                    Assert.True(pulse.IsPlaying);
                    pulse.Visibility = Visibility.Collapsed;
                    Pump(100);
                    Assert.False(pulse.IsPlaying, "collapsed: paused");
                    pulse.Visibility = Visibility.Visible;
                    Pump(100);
                    Assert.True(pulse.IsPlaying);
                    host.Children.Clear();
                    Pump(100);
                    Assert.False(pulse.IsPlaying, "unloaded: stopped");
                }
                finally { window.Close(); }
            });
        }

        using (new MotionScope(animations: false, reduced: true))
        {
            var playing = OnUi(() =>
            {
                var pulse = new LaseroLogoPulse { Width = 96, Height = 120 };
                var window = Host(pulse);
                try { Pump(200); return pulse.IsPlaying; }
                finally { window.Close(); }
            });
            Assert.False(playing, "reduced motion: resting mark, no clock");
        }
    }

    [Fact]
    public void VideoFallsBackToVectorWhenTheFileIsMissingOrNotPlayable()
    {
        using var scope = new MotionScope(animations: true);
        var missing = Path.Combine(Path.GetTempPath(), "lasero-no-such-intro.mp4");
        var junk = Path.Combine(Path.GetTempPath(), "lasero-junk-" + Guid.NewGuid().ToString("N") + ".mp4");
        File.WriteAllBytes(junk, new byte[4096]);
        try
        {
            foreach (var source in new[] { missing, junk })
            {
                var (mode, reason, fallbackPlaying) = OnUi(() =>
                {
                    string? why = null;
                    var video = new LaseroIntroVideo { Source = source, OpenTimeout = TimeSpan.FromSeconds(2) };
                    video.FellBack += (_, r) => why = r;
                    var window = Host(video);
                    try
                    {
                        var deadline = DateTime.UtcNow.AddSeconds(6);
                        while (video.Mode != LaseroIntroVideoMode.Vector && DateTime.UtcNow < deadline) Pump(50);
                        Pump(300);
                        return (video.Mode, why, video.Fallback?.IsPlaying ?? false);
                    }
                    finally { window.Close(); }
                });
                Assert.Equal(LaseroIntroVideoMode.Vector, mode);
                Assert.False(string.IsNullOrEmpty(reason));
                Assert.True(fallbackPlaying, "the vector control plays in place of the video");
            }
        }
        finally { File.Delete(junk); }
    }

    [Fact]
    public void VideoUsesTheVectorControlStraightAwayWhenMotionIsReduced()
    {
        using var scope = new MotionScope(animations: false, reduced: true);
        var (mode, time) = OnUi(() =>
        {
            var video = new LaseroIntroVideo();
            var window = Host(video);
            try { Pump(150); return (video.Mode, video.Fallback?.Time ?? -1); }
            finally { window.Close(); }
        });
        Assert.Equal(LaseroIntroVideoMode.Vector, mode);
        Assert.Equal(LaseroIntroAnimation.DurationSeconds, time);
    }

    [Fact]
    public void ShippedVideoOpensOrFallsBackCleanly()
    {
        // On a machine with an H.264 decoder the 720p MP4 opens as Video; on Windows N without the Media
        // Feature Pack it must end up as Vector. Either is correct; Pending after the timeout is the bug.
        using var scope = new MotionScope(animations: true);
        var path = Path.Combine(RepoRoot(), "Lasero.App", "Assets", "Motion", "lasero-intro-720.mp4");
        Assert.True(File.Exists(path), "the shipped intro video is committed");
        var mode = OnUi(() =>
        {
            var video = new LaseroIntroVideo { Source = path, OpenTimeout = TimeSpan.FromSeconds(4) };
            var window = Host(video);
            try
            {
                var deadline = DateTime.UtcNow.AddSeconds(8);
                while (video.Mode == LaseroIntroVideoMode.Pending && DateTime.UtcNow < deadline) Pump(50);
                return video.Mode;
            }
            finally { window.Close(); }
        });
        Assert.NotEqual(LaseroIntroVideoMode.Pending, mode);
    }

    // ---- installer stays calm ------------------------------------------------------------------------

    [Fact]
    public void InstallerIsStaticWithNoTimersOrFramePlayer()
    {
        var script = File.ReadAllText(Path.Combine(RepoRoot(), "installer", "Lasero.iss"));
        Assert.DoesNotContain("SetTimer", script);
        Assert.DoesNotContain("CreateCallback", script);
        Assert.DoesNotContain("dontcopy", script);
        Assert.False(Directory.Exists(Path.Combine(RepoRoot(), "installer", "assets", "anim")));
    }

    // ---- startup splash --------------------------------------------------------------------------------

    private static string AppSource(string relative) => File.ReadAllText(Path.Combine(RepoRoot(), "Lasero.App", relative));

    [Fact]
    public void SplashPaceIsOneConstantAndTheTotalIsAboutFourSeconds()
    {
        Assert.InRange(IntroTimeline.Splash.Total, 4.0, 4.5);
        Assert.InRange(IntroTimeline.SplashSpeedFactor, 1.0, 3.0);
        // every beat finishes before the end, leaving a short hold on the final frame
        var s = IntroTimeline.Splash;
        var lastBeat = s.TaglineStart + 16 * s.TaglineStagger + s.TaglineDuration;
        Assert.True(lastBeat < s.Total - 0.3 && lastBeat > s.Total - 0.8, $"hold = {s.Total - lastBeat:F2}s");
        Assert.True(s.DotStart + s.DotDuration < s.Total);
        Assert.True(StartupSplash.MaxWaitMilliseconds >= (int)(s.Total * 1000));
    }

    [Fact]
    public void SplashIsShownFirstAndNeverBlocksOrOutstaysIts()
    {
        var app = AppSource("App.xaml.cs");
        var splashAt = app.IndexOf("StartupSplash.Start()", StringComparison.Ordinal);
        Assert.True(splashAt > 0);
        Assert.True(splashAt < app.IndexOf("Host.CreateDefaultBuilder", StringComparison.Ordinal), "the splash starts before the host and view models are built");
        Assert.True(splashAt < app.IndexOf("new LoginWindow", StringComparison.Ordinal));
        Assert.Contains("WaitForAnimationAsync", app);          // capped wait, not an open-ended one
        Assert.Contains("StartupSplash.Reveal(window, splash)", app); // cross-fade into the main window
        Assert.Contains("CloseNow()", app);                       // closed on fatal errors
        var splash = AppSource("StartupSplash.cs");
        Assert.Contains("new Thread(", splash);                   // its own UI thread: init cannot stall the animation
        Assert.DoesNotContain("Topmost = true", splash);          // never covers an error dialog
        Assert.DoesNotContain("AllowsTransparency", splash);
        Assert.DoesNotContain("DispatcherTimer", splash);
    }

    [Fact]
    public void SplashIsSkippableHonoursReducedMotionAndTheSetting()
    {
        var splash = AppSource("StartupSplash.cs");
        Assert.Contains("PreviewKeyDown", splash);
        Assert.Contains("PreviewMouseDown", splash);
        Assert.Contains("SkipToEnd()", splash);
        Assert.Contains("StillFrameMilliseconds = 600", splash);
        Assert.Contains("LaseroMotion.AnimationsEnabled", splash);
        Assert.True(new StartupPreferences().ShowIntroAnimation, "default on");
        Assert.Contains("StartupAnimationToggle", AppSource("SettingsWindow.xaml"));
        Assert.Contains("Startup.ShowIntroAnimation = StartupAnimationToggle.IsChecked == true", AppSource("SettingsWindow.xaml.cs"));
        Assert.Contains("Úvodní animace při spuštění", AppSource("SettingsWindow.xaml"));
    }

    [Fact]
    public void SplashIsSkippedForSettingOffProjectFilesAndQuietStarts()
    {
        Assert.True(StartupSplash.ShouldShow(true, Array.Empty<string>()));
        Assert.False(StartupSplash.ShouldShow(false, Array.Empty<string>()));
        Assert.False(StartupSplash.ShouldShow(true, new[] { @"C:\work\box.lasero" }));
        Assert.False(StartupSplash.ShouldShow(true, new[] { "--no-splash" }));
    }

    [Fact]
    public void SplashRunsOnItsOwnThreadAndReleasesItWhenClosed()
    {
        using var scope = new MotionScope(animations: true);
        var before = System.Diagnostics.Process.GetCurrentProcess().Threads.Count;
        var splash = StartupSplash.Start();
        Assert.True(splash.WindowReady.Wait(10_000), "splash window appeared");
        splash.CloseNow();
        Assert.True(splash.AnimationFinished.Wait(5_000));
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (StartupSplash.Current is not null && DateTime.UtcNow < deadline) Thread.Sleep(50);
        Assert.Null(StartupSplash.Current);
    }
}
