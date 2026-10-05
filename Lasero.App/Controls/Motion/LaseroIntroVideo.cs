using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Lasero.App.Controls.Motion;

/// <summary>How <see cref="LaseroIntroVideo"/> is currently presenting the intro.</summary>
public enum LaseroIntroVideoMode
{
    /// <summary>Not started, or waiting for the media to open.</summary>
    Pending,
    /// <summary>The H.264 file opened and is playing (or finished).</summary>
    Video,
    /// <summary>The file is missing or could not be decoded; the vector <see cref="LaseroIntroAnimation"/> is shown instead.</summary>
    Vector,
}

/// <summary>
/// Hero video for places that have room for it (Home hero, About). Plays the pre-rendered 720p MP4 of the
/// intro through MediaElement and falls back to the vector <see cref="LaseroIntroAnimation"/> automatically
/// when the file is missing, the media fails to open (Windows N editions without the Media Feature Pack, no
/// H.264 decoder) or does not open within <see cref="OpenTimeout"/>. With reduced motion it skips the video
/// entirely and shows the vector final frame. The user never sees an error, only the same intro drawn
/// by the vector control.
///
/// Paused while the window is minimized or the control hidden; the media is closed on Unloaded.
/// The video is silent (the intro has no sound track).
/// </summary>
public sealed class LaseroIntroVideo : Grid
{
    /// <summary>Default location of the shipped 720p intro, next to the executable.</summary>
    public static string DefaultSource => Path.Combine(AppContext.BaseDirectory, "Assets", "Motion", "lasero-intro-720.mp4");

    public static readonly DependencyProperty SourceProperty = DependencyProperty.Register(
        nameof(Source), typeof(string), typeof(LaseroIntroVideo), new PropertyMetadata(null));

    public static readonly DependencyProperty LoopProperty = DependencyProperty.Register(
        nameof(Loop), typeof(bool), typeof(LaseroIntroVideo), new PropertyMetadata(false));

    public static readonly DependencyProperty AutoPlayProperty = DependencyProperty.Register(
        nameof(AutoPlay), typeof(bool), typeof(LaseroIntroVideo), new PropertyMetadata(true));

    private readonly ActivityGate _gate;
    private MediaElement? _media;
    private LaseroIntroAnimation? _fallback;
    private CancellationTokenSource? _openTimeout;
    private bool _started;
    private bool _mediaPlaying;

    public LaseroIntroVideo()
    {
        Focusable = false;
        ClipToBounds = true;
        _gate = new ActivityGate(this);
        _gate.Changed += OnActiveChanged;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    /// <summary>Path of the MP4. Null or empty uses <see cref="DefaultSource"/>.</summary>
    public string? Source
    {
        get => (string?)GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    /// <summary>Repeat the video (use the seamless loop file). Default false: play once and hold the last frame.</summary>
    public bool Loop
    {
        get => (bool)GetValue(LoopProperty);
        set => SetValue(LoopProperty, value);
    }

    /// <summary>Start when loaded. Default true.</summary>
    public bool AutoPlay
    {
        get => (bool)GetValue(AutoPlayProperty);
        set => SetValue(AutoPlayProperty, value);
    }

    /// <summary>How long to wait for the media to open before switching to the vector control.</summary>
    public TimeSpan OpenTimeout { get; set; } = TimeSpan.FromSeconds(3);

    public LaseroIntroVideoMode Mode { get; private set; } = LaseroIntroVideoMode.Pending;

    /// <summary>The vector fallback when <see cref="Mode"/> is Vector, otherwise null.</summary>
    public LaseroIntroAnimation? Fallback => _fallback;

    /// <summary>Raised when the intro has finished (video ended, or the vector fallback reached its last frame).</summary>
    public event EventHandler? Completed;

    /// <summary>Raised when the video could not be used and the vector control took over. Argument is the reason.</summary>
    public event EventHandler<string>? FellBack;

    /// <summary>Start (or restart) the intro.</summary>
    public void Play()
    {
        CancelTimeout();
        DisposeMedia();
        DisposeFallback();
        Mode = LaseroIntroVideoMode.Pending;

        if (!LaseroMotion.AnimationsEnabled) { UseFallback("animations are turned off"); return; }

        var path = string.IsNullOrWhiteSpace(Source) ? DefaultSource : Source!;
        if (!File.Exists(path)) { UseFallback("video file not found"); return; }

        try
        {
            var media = new MediaElement
            {
                LoadedBehavior = MediaState.Manual,
                UnloadedBehavior = MediaState.Close,
                IsMuted = true,
                Stretch = Stretch.Uniform,
                ScrubbingEnabled = false,
                Visibility = Visibility.Hidden, // shown only once the first frame is ready: no flash of a black box
            };
            media.MediaOpened += OnMediaOpened;
            media.MediaFailed += OnMediaFailed;
            media.MediaEnded += OnMediaEnded;
            _media = media;
            Children.Add(media);
            media.Source = new Uri(path, UriKind.Absolute);
            media.Play();
            ArmTimeout();
        }
        catch (Exception ex)
        {
            UseFallback("media element could not start: " + ex.Message);
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (!_started)
        {
            _started = true;
            if (AutoPlay) Play();
        }
        else if (Mode == LaseroIntroVideoMode.Pending && _media is null && _fallback is null && AutoPlay)
        {
            Play(); // came back to the tree after Unloaded closed the media
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        CancelTimeout();
        if (Mode == LaseroIntroVideoMode.Video)
        {
            // MediaElement.UnloadedBehavior=Close already released the file; forget the element so a
            // later Loaded starts a clean playback.
            DisposeMedia();
            Mode = LaseroIntroVideoMode.Pending;
        }
    }

    private void OnActiveChanged(bool active)
    {
        if (_media is null || Mode != LaseroIntroVideoMode.Video) return;
        try
        {
            if (active && _mediaPlaying) _media.Play();
            else if (!active) _media.Pause();
        }
        catch (InvalidOperationException) { /* media already closed */ }
    }

    private void OnMediaOpened(object sender, RoutedEventArgs e)
    {
        CancelTimeout();
        if (_media is null) return;
        Mode = LaseroIntroVideoMode.Video;
        _mediaPlaying = true;
        _media.Visibility = Visibility.Visible;
        if (!_gate.IsActive) _media.Pause();
    }

    private void OnMediaFailed(object? sender, ExceptionRoutedEventArgs e) =>
        UseFallback("media failed: " + (e.ErrorException?.Message ?? "unknown"));

    private void OnMediaEnded(object sender, RoutedEventArgs e)
    {
        if (_media is null) return;
        if (Loop)
        {
            _media.Position = TimeSpan.Zero;
            _media.Play();
            return;
        }
        _mediaPlaying = false;
        Completed?.Invoke(this, EventArgs.Empty);
    }

    private void ArmTimeout()
    {
        var cts = new CancellationTokenSource();
        _openTimeout = cts;
        var dispatcher = Dispatcher;
        _ = Task.Delay(OpenTimeout, cts.Token).ContinueWith(t =>
        {
            if (t.IsCanceled) return;
            dispatcher.BeginInvoke(() =>
            {
                if (!cts.IsCancellationRequested && Mode == LaseroIntroVideoMode.Pending) UseFallback("media did not open in time");
            });
        }, TaskScheduler.Default);
    }

    private void CancelTimeout()
    {
        var cts = _openTimeout;
        _openTimeout = null;
        if (cts is null) return;
        cts.Cancel();
        cts.Dispose();
    }

    private void UseFallback(string reason)
    {
        CancelTimeout();
        DisposeMedia();
        DisposeFallback();
        Mode = LaseroIntroVideoMode.Vector;
        var vector = new LaseroIntroAnimation { AutoPlay = true };
        vector.Completed += (_, _) => Completed?.Invoke(this, EventArgs.Empty);
        _fallback = vector;
        Children.Add(vector);
        FellBack?.Invoke(this, reason);
    }

    private void DisposeMedia()
    {
        var media = _media;
        _media = null;
        _mediaPlaying = false;
        if (media is null) return;
        media.MediaOpened -= OnMediaOpened;
        media.MediaFailed -= OnMediaFailed;
        media.MediaEnded -= OnMediaEnded;
        try { media.Stop(); media.Close(); } catch (InvalidOperationException) { }
        Children.Remove(media);
    }

    private void DisposeFallback()
    {
        var vector = _fallback;
        _fallback = null;
        if (vector is null) return;
        vector.SkipToEnd();
        Children.Remove(vector);
    }
}
