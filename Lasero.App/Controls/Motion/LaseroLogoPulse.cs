using System.Windows;
using System.Windows.Automation;
using System.Windows.Media;

namespace Lasero.App.Controls.Motion;

/// <summary>
/// The compact idle loop for places that wait: the laser mark with a breathing red dot, one thin ripple
/// per breath and a short laser line travelling slowly along a hairline, a 5 second seamless loop. Use it
/// on sign-in, in empty states and on "searching for the laser" screens.
///
/// Behaviour contract: same as <see cref="LaseroIntroAnimation"/> (reduced motion shows the resting mark
/// without a clock, pauses when hidden or minimized, removes its clock on Unloaded) except that it loops
/// and is capped at 24 fps, which is enough for a slow breath and keeps the idle cost very low.
/// Set <see cref="ShowWordmark"/> for the full wordmark with rule and tagline instead of the mark alone.
/// </summary>
public sealed class LaseroLogoPulse : FrameworkElement
{
    public const double PeriodSeconds = PulseRenderer.Period;

    public static readonly DependencyProperty TimeProperty = DependencyProperty.Register(
        nameof(Time), typeof(double), typeof(LaseroLogoPulse),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ShowWordmarkProperty = DependencyProperty.Register(
        nameof(ShowWordmark), typeof(bool), typeof(LaseroLogoPulse),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty IsActiveProperty = DependencyProperty.Register(
        nameof(IsActive), typeof(bool), typeof(LaseroLogoPulse), new PropertyMetadata(true, (d, _) => ((LaseroLogoPulse)d).Refresh()));

    private readonly PulseRenderer _renderer = new();
    private readonly MotionDriver _driver;
    private readonly ActivityGate _gate;

    public LaseroLogoPulse()
    {
        Focusable = false;
        SnapsToDevicePixels = false;
        _driver = new MotionDriver(this, TimeProperty, PeriodSeconds, loop: true, framesPerSecond: 24);
        _gate = new ActivityGate(this);
        _gate.Changed += _ => Refresh();
        Loaded += OnLoaded;
        Unloaded += (_, _) => _driver.Stop();
        AutomationProperties.SetName(this, "LASERO");
    }

    /// <summary>Loop clock in seconds, 0 to <see cref="PeriodSeconds"/>.</summary>
    public double Time
    {
        get => (double)GetValue(TimeProperty);
        set => SetValue(TimeProperty, value);
    }

    /// <summary>Full wordmark, rule and tagline instead of the laser mark alone. Default false.</summary>
    public bool ShowWordmark
    {
        get => (bool)GetValue(ShowWordmarkProperty);
        set => SetValue(ShowWordmarkProperty, value);
    }

    /// <summary>Set false to hold the resting mark and stop the clock (for example while a real result is shown).</summary>
    public bool IsActive
    {
        get => (bool)GetValue(IsActiveProperty);
        set => SetValue(IsActiveProperty, value);
    }

    /// <summary>Read colours from the application theme (default); off for a control on another UI thread.</summary>
    public bool UseThemeColors { get; set; } = true;

    /// <summary>True while a clock is attached and ticking.</summary>
    public bool IsPlaying => _driver.IsRunning;

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (UseThemeColors) _renderer.Palette = MotionPalette.FromResources(this);
        InvalidateVisual();
        Refresh();
    }

    private void Refresh()
    {
        var shouldRun = IsActive && _gate.IsActive && LaseroMotion.AnimationsEnabled;
        if (shouldRun)
        {
            if (_driver.HasClock) _driver.Resume(); else _driver.Begin(0);
        }
        else if (IsLoaded && _driver.HasClock && IsActive && LaseroMotion.AnimationsEnabled)
        {
            _driver.Pause(); // hidden or minimized: keep the phase, spend nothing
        }
        else
        {
            _driver.Stop();
            InvalidateVisual(); // reduced motion or IsActive=false: draw the resting mark
        }
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        if (ShowWordmark)
        {
            var w = double.IsInfinity(availableSize.Width) ? 480 : availableSize.Width;
            var h = double.IsInfinity(availableSize.Height) ? w * 9 / 16 : availableSize.Height;
            return new Size(w, h);
        }
        var side = double.IsInfinity(availableSize.Width) && double.IsInfinity(availableSize.Height) ? 96 : Math.Min(availableSize.Width, availableSize.Height);
        return new Size(double.IsInfinity(availableSize.Width) ? side : availableSize.Width, double.IsInfinity(availableSize.Height) ? side * 1.25 : availableSize.Height);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        var size = new Size(ActualWidth, ActualHeight);
        if (size.Width < 1 || size.Height < 1) return;
        _renderer.PixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        _renderer.ShowWordmark = ShowWordmark;
        _renderer.Static = !(IsActive && LaseroMotion.AnimationsEnabled);
        drawingContext.PushClip(new RectangleGeometry(new Rect(size)));
        _renderer.Render(drawingContext, Time, size);
        drawingContext.Pop();
    }
}
