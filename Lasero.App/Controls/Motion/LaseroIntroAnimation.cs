using System.Windows;
using System.Windows.Automation;
using System.Windows.Media;

namespace Lasero.App.Controls.Motion;

/// <summary>
/// The 8 second brand intro as a vector control: a laser traces the LASERO wordmark stroke by stroke,
/// the red dot ignites, a faint workbench grid and ruler fade in, the tagline settles and the frame
/// holds. Everything is drawn from <see cref="IntroRenderer"/> (the same source the MP4 and the installer
/// frames come from), so it is crisp at any size and DPI and needs no media codec.
///
/// Behaviour contract (see docs/motion-integration.md):
/// * transparent: draws no background, sits on whatever the host paints (the welcome background)
/// * <see cref="LaseroMotion.AnimationsEnabled"/> false: shows the final frame, starts no clock
/// * pauses while the window is minimized or the control is not visible; removes its clock on Unloaded
/// * after the last frame the clock is removed: a finished intro costs no CPU
/// * AutoPlay (default) starts when loaded; call <see cref="Play"/> to restart, <see cref="SkipToEnd"/> to finish
/// </summary>
public sealed class LaseroIntroAnimation : FrameworkElement
{
    /// <summary>Length of the animation in seconds.</summary>
    public const double DurationSeconds = 8.0;

    public static readonly DependencyProperty TimeProperty = DependencyProperty.Register(
        nameof(Time), typeof(double), typeof(LaseroIntroAnimation),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty AutoPlayProperty = DependencyProperty.Register(
        nameof(AutoPlay), typeof(bool), typeof(LaseroIntroAnimation), new PropertyMetadata(true));

    private readonly IntroRenderer _renderer = new();
    private readonly MotionDriver _driver;
    private readonly ActivityGate _gate;
    private bool _wantPlaying;
    private bool _started;

    public LaseroIntroAnimation()
    {
        Focusable = false;
        SnapsToDevicePixels = false;
        _driver = new MotionDriver(this, TimeProperty, DurationSeconds, loop: false, framesPerSecond: 60);
        _driver.Completed += (_, _) => { _wantPlaying = false; Completed?.Invoke(this, EventArgs.Empty); };
        _gate = new ActivityGate(this);
        _gate.Changed += OnActiveChanged;
        Loaded += OnLoaded;
        Unloaded += (_, _) => { _driver.Stop(); }; // the gate also reports inactive; this is the explicit guarantee
        AutomationProperties.SetName(this, "LASERO");
    }

    /// <summary>Animation clock in seconds, 0 to <see cref="DurationSeconds"/>. Setting it by hand shows any frame.</summary>
    public double Time
    {
        get => (double)GetValue(TimeProperty);
        set => SetValue(TimeProperty, value);
    }

    /// <summary>Start playing when the control is first loaded. Default true.</summary>
    public bool AutoPlay
    {
        get => (bool)GetValue(AutoPlayProperty);
        set => SetValue(AutoPlayProperty, value);
    }

    /// <summary>True while a clock is attached and ticking.</summary>
    public bool IsPlaying => _driver.IsRunning;

    /// <summary>True once the final frame is showing (played to the end, skipped, or reduced motion).</summary>
    public bool IsFinished => Time >= DurationSeconds;

    /// <summary>Raised once when the last frame is reached by playing. Not raised by <see cref="SkipToEnd"/>.</summary>
    public event EventHandler? Completed;

    /// <summary>Restart from the first frame (or jump to the last one when motion is reduced).</summary>
    public void Play()
    {
        _driver.Stop();
        if (!LaseroMotion.AnimationsEnabled)
        {
            _wantPlaying = false;
            Time = DurationSeconds;
            return;
        }
        Time = 0;
        _wantPlaying = true;
        if (_gate.IsActive) _driver.Begin(0);
    }

    /// <summary>Jump to the hold frame and stop the clock.</summary>
    public void SkipToEnd()
    {
        _wantPlaying = false;
        _driver.Stop();
        Time = DurationSeconds;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _renderer.Palette = MotionPalette.FromResources(this);
        InvalidateVisual();
        if (!_started)
        {
            _started = true;
            if (AutoPlay) Play();
            else if (!LaseroMotion.AnimationsEnabled) Time = DurationSeconds;
        }
        else if (_wantPlaying && _gate.IsActive && !_driver.HasClock)
        {
            _driver.Begin(Time); // returning to the tree mid-intro: continue where it was
        }
    }

    private void OnActiveChanged(bool active)
    {
        if (!_wantPlaying) return;
        if (active)
        {
            if (_driver.HasClock) _driver.Resume(); else _driver.Begin(Time);
        }
        else if (IsLoaded) _driver.Pause();
        else _driver.Stop();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var w = double.IsInfinity(availableSize.Width) ? (double.IsInfinity(availableSize.Height) ? 640 : availableSize.Height * 16 / 9) : availableSize.Width;
        var h = double.IsInfinity(availableSize.Height) ? w * 9 / 16 : availableSize.Height;
        return new Size(w, h);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        var size = new Size(ActualWidth, ActualHeight);
        if (size.Width < 1 || size.Height < 1) return;
        _renderer.PixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        drawingContext.PushClip(new RectangleGeometry(new Rect(size)));
        _renderer.Render(drawingContext, Time, size);
        drawingContext.Pop();
    }
}
