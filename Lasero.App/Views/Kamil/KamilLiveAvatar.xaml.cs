using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Lasero.App.Controls.Motion;

namespace Lasero.App.Views.Kamil;

/// <summary>
/// A small, non-interactive portrait renderer. Conversation events belong to its owner;
/// this control only displays the requested expression and owns its visible motion clocks.
/// </summary>
public partial class KamilLiveAvatar : UserControl
{
    public static readonly DependencyProperty NeutralSourceProperty = RegisterSource(nameof(NeutralSource));
    public static readonly DependencyProperty BlinkSourceProperty = RegisterSource(nameof(BlinkSource));
    public static readonly DependencyProperty AttentiveSourceProperty = RegisterSource(nameof(AttentiveSource));
    public static readonly DependencyProperty ThinkingSourceProperty = RegisterSource(nameof(ThinkingSource));
    public static readonly DependencyProperty RespondingSourceProperty = RegisterSource(nameof(RespondingSource));
    public static readonly DependencyProperty HappySourceProperty = RegisterSource(nameof(HappySource));
    public static readonly DependencyProperty ErrorSourceProperty = RegisterSource(nameof(ErrorSource));

    private readonly DispatcherTimer _blinkTimer;
    private readonly ImageSource? _fallbackSource;
    private Window? _ownerWindow;
    private KamilAvatarState _state = KamilAvatarState.Idle;
    private bool _requestedVisible = true;
    private bool _reducedMotion;
    private Image? _activeExpression;
    private ImageSource? _displayedExpressionSource;
    private DoubleAnimationUsingKeyFrames? _blinkAnimation;
    private EventHandler? _blinkCompleted;
    private bool _idleBreathing;
    private bool _watchingSystemParameters;

    public KamilLiveAvatar()
    {
        InitializeComponent();
        _fallbackSource = NeutralImage.Source;
        _blinkTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher);
        _blinkTimer.Tick += OnBlinkTimerTick;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        IsVisibleChanged += OnIsVisibleChanged;
    }

    public ImageSource? NeutralSource
    {
        get => (ImageSource?)GetValue(NeutralSourceProperty);
        set => SetValue(NeutralSourceProperty, value);
    }

    public ImageSource? BlinkSource
    {
        get => (ImageSource?)GetValue(BlinkSourceProperty);
        set => SetValue(BlinkSourceProperty, value);
    }

    public ImageSource? AttentiveSource
    {
        get => (ImageSource?)GetValue(AttentiveSourceProperty);
        set => SetValue(AttentiveSourceProperty, value);
    }

    public ImageSource? ThinkingSource
    {
        get => (ImageSource?)GetValue(ThinkingSourceProperty);
        set => SetValue(ThinkingSourceProperty, value);
    }

    public ImageSource? RespondingSource
    {
        get => (ImageSource?)GetValue(RespondingSourceProperty);
        set => SetValue(RespondingSourceProperty, value);
    }

    public ImageSource? HappySource
    {
        get => (ImageSource?)GetValue(HappySourceProperty);
        set => SetValue(HappySourceProperty, value);
    }

    public ImageSource? ErrorSource
    {
        get => (ImageSource?)GetValue(ErrorSourceProperty);
        set => SetValue(ErrorSourceProperty, value);
    }

    /// <summary>The stable conversational expression. Blink requests leave this value unchanged.</summary>
    public KamilAvatarState AvatarState => _state;

    public void SetAvatarState(KamilAvatarState state)
    {
        if (!Dispatcher.CheckAccess())
        {
            if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
            Dispatcher.BeginInvoke(new Action(() => SetAvatarState(state)));
            return;
        }

        if (state == KamilAvatarState.Blink)
        {
            StartBlink();
            return;
        }

        if (_state == state) return;
        _state = state;
        RefreshVisuals(animate: true);
    }

    /// <summary>Call when the host switches screens or otherwise suppresses this instance.</summary>
    public void SetAvatarVisible(bool visible)
    {
        if (!Dispatcher.CheckAccess())
        {
            if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
            Dispatcher.BeginInvoke(new Action(() => SetAvatarVisible(visible)));
            return;
        }

        if (_requestedVisible == visible) return;
        _requestedVisible = visible;
        RefreshVisuals(animate: false);
    }

    /// <summary>Explicit accessibility override in addition to the shared Windows motion gate.</summary>
    public void SetReducedMotion(bool reducedMotion)
    {
        if (!Dispatcher.CheckAccess())
        {
            if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
            Dispatcher.BeginInvoke(new Action(() => SetReducedMotion(reducedMotion)));
            return;
        }

        if (_reducedMotion == reducedMotion) return;
        _reducedMotion = reducedMotion;
        RefreshVisuals(animate: false);
    }

    private static DependencyProperty RegisterSource(string name) =>
        DependencyProperty.Register(name, typeof(ImageSource), typeof(KamilLiveAvatar),
            new PropertyMetadata(null, OnSourceChanged));

    private static void OnSourceChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs args)
    {
        var avatar = (KamilLiveAvatar)dependencyObject;
        // A XAML property can be assigned while InitializeComponent is still constructing names.
        if (avatar.NeutralImage is null) return;
        avatar.RefreshVisuals(animate: false);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        AttachWindow();
        if (!_watchingSystemParameters)
        {
            SystemParameters.StaticPropertyChanged += OnSystemParametersChanged;
            _watchingSystemParameters = true;
        }
        RefreshVisuals(animate: false);
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_watchingSystemParameters)
        {
            SystemParameters.StaticPropertyChanged -= OnSystemParametersChanged;
            _watchingSystemParameters = false;
        }
        if (_ownerWindow is not null)
        {
            _ownerWindow.StateChanged -= OnWindowStateChanged;
            _ownerWindow = null;
        }

        StopBlink();
        StopIdleBreathing();
        StopPoseAnimations();
        StopExpressionAnimations();
    }

    private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e) =>
        RefreshVisuals(animate: false);

    private void AttachWindow()
    {
        var window = Window.GetWindow(this);
        if (ReferenceEquals(window, _ownerWindow)) return;
        if (_ownerWindow is not null) _ownerWindow.StateChanged -= OnWindowStateChanged;
        _ownerWindow = window;
        if (_ownerWindow is not null) _ownerWindow.StateChanged += OnWindowStateChanged;
    }

    private void OnWindowStateChanged(object? sender, EventArgs e) => RefreshVisuals(animate: false);

    private void OnSystemParametersChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SystemParameters.ClientAreaAnimation))
            RefreshVisuals(animate: false);
    }

    private bool CanAnimate => IsLoaded && IsVisible && _requestedVisible &&
        _state != KamilAvatarState.Hidden && !_reducedMotion && LaseroMotion.AnimationsEnabled &&
        _ownerWindow?.WindowState != WindowState.Minimized;

    private void RefreshVisuals(bool animate)
    {
        if (NeutralImage is null || _blinkTimer is null) return;

        var shouldShow = _requestedVisible && _state != KamilAvatarState.Hidden;
        if (!shouldShow)
        {
            StopBlink();
            StopIdleBreathing();
            StopPoseAnimations();
            StopExpressionAnimations();
            PortraitRoot.Visibility = Visibility.Collapsed;
            return;
        }

        PortraitRoot.Visibility = Visibility.Visible;
        NeutralImage.Source = NeutralSource ?? _fallbackSource;
        BlinkImage.Source = BlinkSource;

        var motion = animate && CanAnimate;
        ShowExpression(SourceForState(_state), motion);
        ShowPose(_state, motion);

        if (CanAnimate && _state is (KamilAvatarState.Idle or KamilAvatarState.Attentive) && BlinkSource is not null)
            ScheduleBlink();
        else
            StopBlink();

        if (CanAnimate && _state == KamilAvatarState.Idle)
            StartIdleBreathing();
        else
            StopIdleBreathing();
    }

    private ImageSource? SourceForState(KamilAvatarState state) => state switch
    {
        KamilAvatarState.Attentive => AttentiveSource,
        KamilAvatarState.Thinking => ThinkingSource,
        KamilAvatarState.Responding => RespondingSource,
        KamilAvatarState.Happy => HappySource,
        KamilAvatarState.Error => ErrorSource,
        _ => null,
    };

    private void ShowExpression(ImageSource? source, bool animate)
    {
        if (ReferenceEquals(source, _displayedExpressionSource))
        {
            if (!animate) SetExpressionImmediately(source);
            return;
        }

        _displayedExpressionSource = source;
        if (!animate)
        {
            SetExpressionImmediately(source);
            return;
        }

        var outgoing = _activeExpression;
        var incoming = ReferenceEquals(outgoing, ExpressionA) ? ExpressionB : ExpressionA;
        var outgoingOpacity = outgoing?.Opacity ?? 0;
        ExpressionA.BeginAnimation(OpacityProperty, null);
        ExpressionB.BeginAnimation(OpacityProperty, null);
        if (outgoing is not null) outgoing.Opacity = outgoingOpacity;
        incoming.Opacity = 0;
        incoming.Source = source;

        if (outgoing is not null)
            AnimateOpacity(outgoing, outgoingOpacity, 0);

        if (source is not null)
            AnimateOpacity(incoming, 0, 1);

        _activeExpression = source is null ? null : incoming;
    }

    private void SetExpressionImmediately(ImageSource? source)
    {
        StopExpressionAnimations();
        ExpressionA.Source = source;
        ExpressionA.Opacity = source is null ? 0 : 1;
        ExpressionB.Source = null;
        ExpressionB.Opacity = 0;
        _activeExpression = source is null ? null : ExpressionA;
        _displayedExpressionSource = source;
    }

    private static void AnimateOpacity(Image image, double from, double to)
    {
        image.Opacity = to;
        image.BeginAnimation(OpacityProperty, new DoubleAnimation(from, to,
            TimeSpan.FromMilliseconds(190))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.Stop,
        });
    }

    private void ShowPose(KamilAvatarState state, bool animate)
    {
        var (angle, offsetY) = state switch
        {
            KamilAvatarState.Attentive => (-1.2, -0.3),
            KamilAvatarState.Thinking => (1.8, 0.5),
            KamilAvatarState.Responding => (-0.8, -0.6),
            KamilAvatarState.Happy => (-1.4, -0.8),
            KamilAvatarState.Error => (0.7, 0.6),
            _ => (0d, 0d),
        };

        AnimateValue(HeadRotation, RotateTransform.AngleProperty, angle, animate);
        AnimateValue(HeadTranslation, TranslateTransform.YProperty, offsetY, animate);
    }

    private static void AnimateValue(Animatable target, DependencyProperty property, double value, bool animate)
    {
        var oldValue = (double)target.GetValue(property);
        target.BeginAnimation(property, null);
        target.SetValue(property, value);
        if (!animate || Math.Abs(oldValue - value) < 0.001) return;

        target.BeginAnimation(property, new DoubleAnimation(oldValue, value,
            TimeSpan.FromMilliseconds(230))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.Stop,
        });
    }

    private void StopPoseAnimations()
    {
        HeadRotation.BeginAnimation(RotateTransform.AngleProperty, null);
        HeadTranslation.BeginAnimation(TranslateTransform.YProperty, null);
    }

    private void StopExpressionAnimations()
    {
        ExpressionA.BeginAnimation(OpacityProperty, null);
        ExpressionB.BeginAnimation(OpacityProperty, null);
    }

    private void StartIdleBreathing()
    {
        if (_idleBreathing || !CanAnimate) return;

        // A sub-percent composition transform reads as quiet presence at the larger chat size,
        // while remaining effectively still at the 48-DIP canvas anchor. It never changes layout.
        var breath = new DoubleAnimationUsingKeyFrames
        {
            Duration = TimeSpan.FromMilliseconds(3800),
            RepeatBehavior = RepeatBehavior.Forever,
        };
        breath.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        breath.KeyFrames.Add(new EasingDoubleKeyFrame(1.004, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(1850)))
        {
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
        });
        breath.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(3800)))
        {
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
        });

        IdleScale.BeginAnimation(ScaleTransform.ScaleXProperty, breath);
        IdleScale.BeginAnimation(ScaleTransform.ScaleYProperty, breath);
        _idleBreathing = true;
    }

    private void StopIdleBreathing()
    {
        if (!_idleBreathing) return;
        IdleScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        IdleScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        IdleScale.ScaleX = 1;
        IdleScale.ScaleY = 1;
        _idleBreathing = false;
    }

    private void ScheduleBlink()
    {
        if (_blinkTimer.IsEnabled || _blinkAnimation is not null || !CanAnimate || BlinkSource is null) return;
        _blinkTimer.Interval = TimeSpan.FromMilliseconds(Random.Shared.Next(3400, 7100));
        _blinkTimer.Start();
    }

    private void OnBlinkTimerTick(object? sender, EventArgs e)
    {
        _blinkTimer.Stop();
        StartBlink();
    }

    private void StartBlink()
    {
        if (!CanAnimate || _state is not (KamilAvatarState.Idle or KamilAvatarState.Attentive) ||
            BlinkSource is null || _blinkAnimation is not null) return;

        _blinkTimer.Stop();
        BlinkImage.Source = BlinkSource;
        BlinkImage.Opacity = 0;
        var animation = new DoubleAnimationUsingKeyFrames { FillBehavior = FillBehavior.Stop };
        animation.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        animation.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(85))));
        animation.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(125))));
        animation.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(215))));

        EventHandler? completed = null;
        completed = (_, _) =>
        {
            animation.Completed -= completed;
            if (!ReferenceEquals(_blinkAnimation, animation)) return;
            _blinkAnimation = null;
            _blinkCompleted = null;
            BlinkImage.BeginAnimation(OpacityProperty, null);
            BlinkImage.Opacity = 0;
            ScheduleBlink();
        };
        _blinkAnimation = animation;
        _blinkCompleted = completed;
        animation.Completed += completed;
        BlinkImage.BeginAnimation(OpacityProperty, animation);
    }

    private void StopBlink()
    {
        _blinkTimer.Stop();
        if (_blinkAnimation is not null && _blinkCompleted is not null)
            _blinkAnimation.Completed -= _blinkCompleted;
        _blinkAnimation = null;
        _blinkCompleted = null;
        BlinkImage.BeginAnimation(OpacityProperty, null);
        BlinkImage.Opacity = 0;
    }
}
