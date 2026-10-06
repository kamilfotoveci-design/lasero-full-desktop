using System.Windows;
using System.Windows.Media.Animation;

namespace Lasero.App.Controls.Motion;

/// <summary>
/// Tells a control whether it is worth running right now: loaded, visible, and its window shown and not
/// minimized. Event driven; owns no timer. Detaches everything on Unloaded so nothing outlives the control.
/// </summary>
internal sealed class ActivityGate
{
    private readonly FrameworkElement _owner;
    private Window? _window;

    public ActivityGate(FrameworkElement owner)
    {
        _owner = owner;
        owner.Loaded += OnLoaded;
        owner.Unloaded += OnUnloaded;
        owner.IsVisibleChanged += OnVisibleChanged;
        if (owner.IsLoaded) Hook();
    }

    public event Action<bool>? Changed;

    public bool IsActive { get; private set; }

    private void OnLoaded(object sender, RoutedEventArgs e) { Hook(); Evaluate(); }
    private void OnUnloaded(object sender, RoutedEventArgs e) { Unhook(); Evaluate(); }
    private void OnChanged(object? sender, EventArgs e) => Evaluate();
    private void OnVisibleChanged(object sender, DependencyPropertyChangedEventArgs e) => Evaluate();

    private void Hook()
    {
        Unhook();
        _window = Window.GetWindow(_owner);
        if (_window is null) return;
        _window.StateChanged += OnChanged;
        _window.IsVisibleChanged += OnVisibleChanged;
    }

    private void Unhook()
    {
        if (_window is null) return;
        _window.StateChanged -= OnChanged;
        _window.IsVisibleChanged -= OnVisibleChanged;
        _window = null;
    }

    public void Evaluate()
    {
        var active = _owner.IsLoaded && _owner.IsVisible
            && (_window is null || (_window.IsVisible && _window.WindowState != WindowState.Minimized));
        if (active == IsActive) return;
        IsActive = active;
        Changed?.Invoke(active);
    }
}

/// <summary>
/// Drives one double dependency property (the control's clock, in seconds) with a Storyboard. The only
/// clock in the motion controls: no DispatcherTimer, no CompositionTarget.Rendering hook. A completed
/// one-shot removes its animation, so a finished control costs nothing; a paused or removed one is idle.
/// </summary>
internal sealed class MotionDriver
{
    private readonly FrameworkElement _owner;
    private readonly DependencyProperty _time;
    private double _duration;
    private readonly bool _loop;
    private readonly int _fps;
    private Storyboard? _storyboard;
    private bool _paused;

    public MotionDriver(FrameworkElement owner, DependencyProperty timeProperty, double duration, bool loop, int framesPerSecond)
    {
        _owner = owner; _time = timeProperty; _duration = duration; _loop = loop; _fps = framesPerSecond;
    }

    public double Duration { get => _duration; set => _duration = value; }

    /// <summary>Raised when a one-shot reaches its end (never for loops). The clock is already removed.</summary>
    public event EventHandler? Completed;

    /// <summary>A storyboard clock is attached to the owner (running or paused).</summary>
    public bool HasClock => _storyboard is not null;

    /// <summary>The clock is attached and ticking.</summary>
    public bool IsRunning => _storyboard is not null && !_paused;

    public void Begin(double fromSeconds)
    {
        Stop();
        var from = _loop ? 0 : Math.Clamp(fromSeconds, 0, _duration);
        if (!_loop && from >= _duration) { Finish(); return; }

        var animation = new DoubleAnimation(from, _duration, TimeSpan.FromSeconds(_duration - from))
        {
            FillBehavior = FillBehavior.HoldEnd,
        };
        if (_loop) animation.RepeatBehavior = RepeatBehavior.Forever;
        Timeline.SetDesiredFrameRate(animation, _fps);
        Storyboard.SetTarget(animation, _owner);
        Storyboard.SetTargetProperty(animation, new PropertyPath(_time));

        var sb = new Storyboard();
        sb.Children.Add(animation);
        if (!_loop) sb.Completed += OnCompleted;
        _storyboard = sb;
        _paused = false;
        sb.Begin(_owner, HandoffBehavior.SnapshotAndReplace, true);
    }

    public void Pause()
    {
        if (_storyboard is null || _paused) return;
        _storyboard.Pause(_owner);
        _paused = true;
    }

    public void Resume()
    {
        if (_storyboard is null || !_paused) return;
        _storyboard.Resume(_owner);
        _paused = false;
    }

    /// <summary>Removes the clock. The property keeps its last value.</summary>
    public void Stop()
    {
        var sb = _storyboard;
        if (sb is null) return;
        _storyboard = null;
        _paused = false;
        sb.Completed -= OnCompleted;
        var current = _owner.GetValue(_time);
        sb.Remove(_owner);
        _owner.BeginAnimation(_time, null); // drop any animation layer immediately, not at the next tick
        _owner.SetValue(_time, current);
    }

    private void OnCompleted(object? sender, EventArgs e) => Finish();

    private void Finish()
    {
        var sb = _storyboard;
        _storyboard = null;
        _paused = false;
        if (sb is not null)
        {
            sb.Completed -= OnCompleted;
            sb.Remove(_owner);
            _owner.BeginAnimation(_time, null);
        }
        _owner.SetValue(_time, _duration);
        Completed?.Invoke(this, EventArgs.Empty);
    }
}
