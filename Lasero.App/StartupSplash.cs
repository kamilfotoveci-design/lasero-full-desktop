using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Lasero.App.Controls.Motion;
using Serilog;

namespace Lasero.App;

/// <summary>
/// The startup splash: a borderless, centred window that plays the compact (~4.1 s) brand intro the moment
/// the process starts, then cross-fades into the main window.
///
/// Why its own UI thread: the first seconds of startup (DI container, settings, stores, session resume)
/// run on the main dispatcher and would stall every frame of an animation that lives there. The splash
/// window therefore owns a dedicated STA thread with its own dispatcher; its animation keeps its 60 fps
/// no matter how busy the main thread is. It touches no application state: colours are the shipped
/// defaults (no theme brushes, which belong to the main thread), and the only calls across threads are
/// <see cref="FadeOutAndClose"/>, <see cref="CloseNow"/> and the <see cref="AnimationFinished"/> task.
///
/// Rules: appears before anything else is built; skippable with any key, click or Esc; honours the system
/// animation setting (a still frame for about 0.6 s); never topmost, so an error dialog is never covered, and
/// closed immediately by <see cref="CloseNow"/> when something goes wrong; releases its thread when closed.
/// </summary>
public sealed class StartupSplash
{
    public static StartupSplash? Current { get; private set; }

    /// <summary>Length of a still frame when Windows animations are off.</summary>
    public const int StillFrameMilliseconds = 600;
    /// <summary>The longest the main window ever waits for the splash to finish.</summary>
    public const int MaxWaitMilliseconds = 4500;
    /// <summary>The splash closes itself unconditionally this long after it started, whatever else is happening.</summary>
    public const int WatchdogMilliseconds = 8000;
    /// <summary>Test seam: the watchdog delay actually used (defaults to <see cref="WatchdogMilliseconds"/>).</summary>
    internal static int WatchdogDelayMilliseconds = WatchdogMilliseconds;

    /// <summary>True if <paramref name="task"/> completed within <paramref name="milliseconds"/>; never throws for the task.</summary>
    internal static async Task<bool> CompletedWithin(Task task, int milliseconds)
    {
        var first = await Task.WhenAny(task, Task.Delay(milliseconds)).ConfigureAwait(true);
        return ReferenceEquals(first, task);
    }

    private readonly TaskCompletionSource _animationFinished = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _windowReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private SplashWindow? _window;
    private Dispatcher? _dispatcher;

    private StartupSplash() { }

    /// <summary>Completes when the intro has played to its last frame, was skipped, or the still frame has been shown.</summary>
    public Task AnimationFinished => _animationFinished.Task;

    /// <summary>Completes once the splash window is on screen (for tests and the preview harness).</summary>
    public Task WindowReady => _windowReady.Task;

    /// <summary>Whether to show it at all: the setting is on, the app is not being opened for a file (a double-clicked
    /// .lasero project should open straight away) and no one asked for a quiet start.</summary>
    public static bool ShouldShow(bool settingEnabled, IEnumerable<string> args)
    {
        if (!settingEnabled) return false;
        foreach (var a in args)
        {
            if (string.Equals(Path.GetExtension(a), ".lasero", StringComparison.OrdinalIgnoreCase)) return false;
            if (a.Equals("--no-splash", StringComparison.OrdinalIgnoreCase) || a.Equals("/nosplash", StringComparison.OrdinalIgnoreCase)) return false;
        }
        return true;
    }

    /// <summary>Starts the splash on its own thread and returns at once; the window appears a few tens of milliseconds later.</summary>
    public static StartupSplash Start()
    {
        var splash = new StartupSplash();
        Current = splash;
        Log.Information("Splash: start");
        var thread = new Thread(splash.Run) { Name = "LASERO startup splash", IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return splash;
    }

    private void Run()
    {
        try
        {
            _dispatcher = Dispatcher.CurrentDispatcher;
            var window = new SplashWindow(this);
            _window = window;
            window.Closed += (_, _) =>
            {
                _closed = true;
                Log.Information("Splash: closed");
                _animationFinished.TrySetResult();
                Dispatcher.CurrentDispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
            };
            window.Show();
            Log.Information("Splash: window shown");
            StartWatchdog();
            _windowReady.TrySetResult();
            Dispatcher.Run();
        }
        catch
        {
            // the splash is decoration: it must never be the reason the app fails to start
            _animationFinished.TrySetResult();
            _windowReady.TrySetResult();
        }
        finally
        {
            if (ReferenceEquals(Current, this)) Current = null;
        }
    }


    private void StartWatchdog()
    {
        var dispatcher = _dispatcher;
        _ = Task.Delay(WatchdogDelayMilliseconds).ContinueWith(_ =>
        {
            if (_closed) return;
            Log.Warning("Splash: watchdog closing the splash after {Ms} ms", WatchdogDelayMilliseconds);
            dispatcher?.BeginInvoke(new Action(() => _window?.Close()));
            _animationFinished.TrySetResult();
        }, TaskScheduler.Default);
    }

    private volatile bool _closed;
    internal void MarkFinished() { Log.Information("Splash: animation finished"); _animationFinished.TrySetResult(); }

    /// <summary>Waits for the animation, but never longer than <see cref="MaxWaitMilliseconds"/>.</summary>
    public async Task WaitForAnimationAsync()
    {
        var done = await Task.WhenAny(AnimationFinished, Task.Delay(MaxWaitMilliseconds)).ConfigureAwait(true);
        Log.Information("Splash: wait ended, animation finished={Finished}", ReferenceEquals(done, AnimationFinished));
    }

    /// <summary>Fades the splash out and closes it. Safe from any thread, safe to call twice.</summary>
    public void FadeOutAndClose()
    {
        var d = _dispatcher;
        if (d is null) return;
        d.BeginInvoke(() => _window?.FadeOutAndClose());
    }

    /// <summary>Closes it immediately, without fading (errors, shutdown).</summary>
    public void CloseNow()
    {
        var d = _dispatcher;
        if (d is null) return;
        d.BeginInvoke(() => _window?.Close());
        _animationFinished.TrySetResult();
    }

    /// <summary>Shows the main window with a short fade-in while the splash fades away.</summary>
    public static void Reveal(Window main, StartupSplash? splash)
    {
        // The splash starts leaving BEFORE the main window is shown. Showing the main window can open a modal dialog
        // (the recovery prompt runs from its Loaded handler) and the call does not return until that is answered;
        // with the old order the splash stayed up over the dialog and the app looked frozen.
        Log.Information("Splash: reveal main window");
        splash?.FadeOutAndClose();
        if (splash is not null && main.Content is UIElement root && LaseroMotion.AnimationsEnabled)
        {
            root.Opacity = 0;
            var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
            Timeline.SetDesiredFrameRate(fade, 60);
            fade.Completed += (_, _) => { root.BeginAnimation(UIElement.OpacityProperty, null); root.Opacity = 1; };
            root.BeginAnimation(UIElement.OpacityProperty, fade);
        }
        main.Show();
    }

    // -----------------------------------------------------------------------------------------------

    private sealed class SplashWindow : Window
    {
        private readonly StartupSplash _owner;
        private readonly Grid _root = new();
        private readonly LaseroIntroAnimation _intro;
        private readonly string? _tracePath = Environment.GetEnvironmentVariable("LASERO_SPLASH_TRACE");
        private readonly List<double> _frameTimes = new();
        private bool _fading;
        private bool _settled;

        public SplashWindow(StartupSplash owner)
        {
            _owner = owner;
            var pal = MotionPalette.Default;
            Title = "LASERO";
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            Topmost = false;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Width = 720; Height = 420;
            UseLayoutRounding = true;
            SnapsToDevicePixels = true;
            Background = new SolidColorBrush(pal.Background);
            AutomationProperties.SetName(this, "LASERO se spouští");

            _intro = new LaseroIntroAnimation { Compact = true, UseThemeColors = false, AutoPlay = false, Margin = new Thickness(0) };
            _intro.Completed += (_, _) => Settle();
            _root.Children.Add(_intro);
            Content = _root;

            WindowFrameHook.Attach(this); // DWM rounded corners and hairline on Windows 11, nothing on Windows 10
            PreviewKeyDown += (_, _) => Skip();
            PreviewMouseDown += (_, _) => Skip();
            Loaded += OnLoaded;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            if (!LaseroMotion.AnimationsEnabled)
            {
                _intro.SkipToEnd(); // a still frame, then on
                _ = Task.Delay(StillFrameMilliseconds).ContinueWith(_ => Dispatcher.BeginInvoke(new Action(Settle)), TaskScheduler.Default);
                return;
            }
            if (_tracePath is not null) CompositionTarget.Rendering += OnRendering;
            _intro.Play();
        }

        private void OnRendering(object? sender, EventArgs e)
        {
            if (e is RenderingEventArgs args) _frameTimes.Add(args.RenderingTime.TotalMilliseconds);
        }

        private void Skip()
        {
            if (_settled) return;
            _intro.SkipToEnd();
            Settle();
        }

        /// <summary>Intro finished (or skipped): tell the app, and keep the held frame breathing until the app is ready.</summary>
        private void Settle()
        {
            if (_settled) return;
            _settled = true;
            _owner.MarkFinished();
            if (_fading) return;
            if (!LaseroMotion.AnimationsEnabled) return; // a still frame stays still
            var idle = new LaseroLogoPulse { ShowWordmark = true, UseThemeColors = false };
            _root.Children.Add(idle);
            _root.Children.Remove(_intro); // same picture underneath; one animated element at a time
        }

        public void FadeOutAndClose()
        {
            if (_fading) return;
            _fading = true;
            if (!LaseroMotion.AnimationsEnabled || !IsVisible) { Close(); return; }
            var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(220)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
            Timeline.SetDesiredFrameRate(fade, 60);
            fade.Completed += (_, _) => Close();
            _root.BeginAnimation(UIElement.OpacityProperty, fade);
        }

        protected override void OnClosed(EventArgs e)
        {
            CompositionTarget.Rendering -= OnRendering;
            _intro.SkipToEnd();
            WriteTrace();
            base.OnClosed(e);
        }

        private void WriteTrace()
        {
            if (_tracePath is null || _frameTimes.Count < 3) return;
            var gaps = new List<double>();
            for (var i = 1; i < _frameTimes.Count; i++)
            {
                var g = _frameTimes[i] - _frameTimes[i - 1];
                if (g > 0) gaps.Add(g); // WPF raises Rendering once per composition pass; duplicates have gap 0
            }
            gaps.Sort();
            double Pct(double p) => gaps[(int)Math.Floor((gaps.Count - 1) * p)];
            var mean = gaps.Average();
            try
            {
                File.WriteAllText(_tracePath,
                    $"frames={gaps.Count} mean={mean:F2}ms ({1000 / mean:F1} fps) p50={Pct(0.5):F2} p95={Pct(0.95):F2} p99={Pct(0.99):F2} max={gaps[^1]:F2}\n" +
                    string.Join(",", _frameTimes.Select(t => t.ToString("F1", System.Globalization.CultureInfo.InvariantCulture))));
            }
            catch (IOException) { }
        }
    }
}
