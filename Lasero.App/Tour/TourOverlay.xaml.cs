using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using Lasero.App.ViewModels;

namespace Lasero.App.Tour;

/// <summary>
/// The welcome screen and the coach-mark tour. This class only presents: which step is current comes
/// from <see cref="TourSession"/>, where things go from <see cref="TourLayout"/>, what a step says from
/// <see cref="TourSteps"/>, and the screen a step needs is requested through <see cref="Navigate"/>.
/// Nothing here presses a control, connects hardware, starts a job or writes project data.
///
/// Motion follows the Windows animation preference: with animations off, or no hardware tier, every
/// element is placed straight in its end state (the logo fully drawn, the card in place).
/// </summary>
public partial class TourOverlay : UserControl
{
    private const double WordmarkWidth = 300;
    private const double WordmarkHeight = 93;
    private const double EtchWidth = 220;

    private readonly RectangleGeometry _full = new();
    private readonly RectangleGeometry _spot;
    private readonly List<Border> _dots = new();

    private TourSession? _session;
    private FrameworkElement? _target;
    private TourStep? _presented;
    private AppScreen _startScreen = AppScreen.Home;
    private IInputElement? _previousFocus;
    private bool _open;
    private int _presentToken;

    public TourOverlay()
    {
        InitializeComponent();

        var radius = TryFindResource("Radius.Md") is CornerRadius r ? r.TopLeft : 8;
        _spot = new RectangleGeometry(new Rect(0, 0, 0, 0), radius, radius);
        Dim.Data = new CombinedGeometry(GeometryCombineMode.Exclude, _full, _spot);
        SpotRing.Data = _spot;

        Root.SizeChanged += OnRootSizeChanged;
    }

    /// <summary>Raised when the welcome is answered: StartedTour or Skipped.</summary>
    public event Action<WelcomeChoice>? WelcomeAnswered;

    /// <summary>Raised once when a tour ends, with how it ended.</summary>
    public event Action<TourOutcome>? TourEnded;

    /// <summary>Raised after the overlay has fully closed (welcome skipped or tour ended).</summary>
    public event Action? Closed;

    /// <summary>Asks the host to show a screen. The overlay never touches navigation state itself.</summary>
    public Action<AppScreen>? Navigate { get; set; }

    /// <summary>Which screen is showing now; read when a tour starts so it can return there afterwards.</summary>
    public Func<AppScreen>? CurrentScreen { get; set; }

    /// <summary>Where keyboard focus goes when the tour closes and the control that had it is gone, so
    /// canvas shortcuts keep working afterwards.</summary>
    public Action? FocusFallback { get; set; }

    /// <summary>Tests and screenshot runs set this to get static end states regardless of the machine.</summary>
    public bool ForceStatic { get; set; }

    public bool IsOpen => _open;
    public bool IsWelcomeVisible => _open && WelcomeLayer.Visibility == Visibility.Visible;
    public bool IsCoachVisible => _open && CoachLayer.Visibility == Visibility.Visible;
    public TourSession? Session => _session;

    /// <summary>The last computed placement, for tests and for the caret.</summary>
    public TourLayoutResult? LastLayout { get; private set; }

    private bool AnimationsEnabled =>
        !ForceStatic && SystemParameters.ClientAreaAnimation && RenderCapability.Tier > 0;

    // ------------------------------------------------------------------------------------ open / close

    public void ShowWelcome()
    {
        if (_open) return;
        Open();
        CoachLayer.Visibility = Visibility.Collapsed;
        WelcomeLayer.Visibility = Visibility.Visible;
        PlayWelcome();
        FocusLater(StartButton);
    }

    public void StartTour()
    {
        if (!_open) Open();
        _startScreen = CurrentScreen?.Invoke() ?? AppScreen.Home;
        StopWelcomeMotion();
        WelcomeLayer.Visibility = Visibility.Collapsed;
        CoachLayer.Visibility = Visibility.Visible;
        _session = new TourSession();
        _session.Changed += OnSessionChanged;
        BuildDots();
        _spot.BeginAnimation(RectangleGeometry.RectProperty, null);
        _spot.Rect = new Rect(0, 0, 0, 0);
        PresentCurrent(first: true);
    }

    /// <summary>Closes whatever is showing as if skipped. Used when something that must stay reachable
    /// (a running job's Zastavit) would otherwise sit behind the dimming.</summary>
    public void Cancel()
    {
        if (!_open) return;
        if (_session is { IsActive: true }) _session.Skip();
        else if (WelcomeLayer.Visibility == Visibility.Visible) SkipWelcome();
    }

    private void Open()
    {
        _open = true;
        _previousFocus = Keyboard.FocusedElement;
        BeginAnimation(OpacityProperty, null);
        Opacity = 1;
        Visibility = Visibility.Visible;
        UpdateFullGeometry();
    }

    private void CloseOverlay()
    {
        if (!_open) return;
        _open = false;
        _presentToken++;
        StopWelcomeMotion();
        StopHalo();

        void Finish()
        {
            Visibility = Visibility.Collapsed;
            WelcomeLayer.Visibility = Visibility.Collapsed;
            CoachLayer.Visibility = Visibility.Collapsed;
            BeginAnimation(OpacityProperty, null);
            Opacity = 1;
            _target = null;
            _presented = null;
            LastLayout = null;
        }

        if (AnimationsEnabled)
        {
            var fade = new DoubleAnimation(0, Dur("Motion.Base"))
            {
                EasingFunction = EaseOut(),
                FillBehavior = FillBehavior.HoldEnd,
            };
            fade.Completed += (_, _) => { if (!_open) Finish(); };
            BeginAnimation(OpacityProperty, fade);
        }
        else
        {
            Finish();
        }

        RestoreFocus();
        Closed?.Invoke();
    }

    private void RestoreFocus()
    {
        var previous = _previousFocus as UIElement;
        _previousFocus = null;
        if (previous is { IsVisible: true, Focusable: true, IsEnabled: true } && previous != this)
        {
            Keyboard.Focus(previous);
            if (previous.IsKeyboardFocused) return;
        }

        FocusFallback?.Invoke();
    }

    // ------------------------------------------------------------------------------------ welcome

    private void OnStartTourClick(object sender, RoutedEventArgs e)
    {
        WelcomeAnswered?.Invoke(WelcomeChoice.StartedTour);
        StartTour();
    }

    private void OnSkipWelcomeClick(object sender, RoutedEventArgs e) => SkipWelcome();

    private void SkipWelcome()
    {
        WelcomeAnswered?.Invoke(WelcomeChoice.Skipped);
        CloseOverlay();
    }

    private void PlayWelcome()
    {
        StopWelcomeMotion();
        if (!AnimationsEnabled)
        {
            ApplyWelcomeEndState();
            return;
        }

        var ease = EaseOut();
        var panel = Dur("Motion.Panel");

        WelcomeScrim.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, panel) { EasingFunction = ease });
        WelcomeCard.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, panel) { EasingFunction = ease });
        WelcomeScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.97, 1, panel) { EasingFunction = ease });
        WelcomeScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.97, 1, panel) { EasingFunction = ease });
        WelcomeOffset.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(14, 0, panel) { EasingFunction = ease });

        // Four corner marks draw in one after another, the same bracket the framing command traces.
        var brackets = new[] { BracketTl, BracketTr, BracketBr, BracketBl };
        for (var i = 0; i < brackets.Length; i++)
        {
            brackets[i].BeginAnimation(Shape.StrokeDashOffsetProperty, new DoubleAnimation(20, 0, TimeSpan.FromMilliseconds(520))
            {
                BeginTime = TimeSpan.FromMilliseconds(260 + i * 90),
                EasingFunction = ease,
            });
        }

        // A laser pass reveals the wordmark left to right; the beam leads the reveal and then goes out.
        var reveal = TimeSpan.FromMilliseconds(950);
        var revealStart = TimeSpan.FromMilliseconds(520);
        var clip = new RectangleGeometry(new Rect(0, 0, 0, WordmarkHeight));
        WordmarkImage.Clip = clip;
        var clipAnim = new RectAnimation(new Rect(0, 0, 0, WordmarkHeight), new Rect(0, 0, WordmarkWidth, WordmarkHeight), reveal)
        {
            BeginTime = revealStart,
            EasingFunction = ease,
        };
        clipAnim.Completed += (_, _) => WordmarkImage.Clip = null;
        clip.BeginAnimation(RectangleGeometry.RectProperty, clipAnim);

        Beam.BeginAnimation(Canvas.LeftProperty, new DoubleAnimation(0, WordmarkWidth - 2, reveal)
        {
            BeginTime = revealStart,
            EasingFunction = ease,
        });
        var beam = new DoubleAnimationUsingKeyFrames { BeginTime = revealStart };
        beam.KeyFrames.Add(new DiscreteDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        beam.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(reveal)));
        beam.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(reveal + TimeSpan.FromMilliseconds(220))));
        Beam.BeginAnimation(OpacityProperty, beam);

        // The etched line under it, then a slow pulse on the red dot, the only thing that keeps moving.
        EtchLine.BeginAnimation(WidthProperty, new DoubleAnimation(0, EtchWidth, TimeSpan.FromMilliseconds(700))
        {
            BeginTime = TimeSpan.FromMilliseconds(1250),
            EasingFunction = ease,
        });

        var pulseStart = TimeSpan.FromMilliseconds(1700);
        var pulseFade = new DoubleAnimation(0.8, 0, TimeSpan.FromMilliseconds(1500))
        {
            BeginTime = pulseStart,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = ease,
        };
        DotPulse.BeginAnimation(OpacityProperty, pulseFade);
        var pulseGrow = new DoubleAnimation(1, 2.2, TimeSpan.FromMilliseconds(1500))
        {
            BeginTime = pulseStart,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = ease,
        };
        DotPulseScale.BeginAnimation(ScaleTransform.ScaleXProperty, pulseGrow);
        DotPulseScale.BeginAnimation(ScaleTransform.ScaleYProperty, pulseGrow);
    }

    private void ApplyWelcomeEndState()
    {
        WelcomeScrim.Opacity = 1;
        WelcomeCard.Opacity = 1;
        WordmarkImage.Clip = null;
        Beam.Opacity = 0;
        EtchLine.Width = EtchWidth;
        DotPulse.Opacity = 0.45;
        DotPulseScale.ScaleX = DotPulseScale.ScaleY = 1.3;
        WelcomeScale.ScaleX = WelcomeScale.ScaleY = 1;
        WelcomeOffset.Y = 0;
        foreach (var bracket in new[] { BracketTl, BracketTr, BracketBr, BracketBl })
            bracket.StrokeDashOffset = 0;
    }

    private void StopWelcomeMotion()
    {
        WelcomeScrim.BeginAnimation(OpacityProperty, null);
        WelcomeCard.BeginAnimation(OpacityProperty, null);
        WelcomeScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        WelcomeScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        WelcomeOffset.BeginAnimation(TranslateTransform.YProperty, null);
        foreach (var bracket in new[] { BracketTl, BracketTr, BracketBr, BracketBl })
            bracket.BeginAnimation(Shape.StrokeDashOffsetProperty, null);
        Beam.BeginAnimation(Canvas.LeftProperty, null);
        Beam.BeginAnimation(OpacityProperty, null);
        EtchLine.BeginAnimation(WidthProperty, null);
        DotPulse.BeginAnimation(OpacityProperty, null);
        DotPulseScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        DotPulseScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
    }

    // ------------------------------------------------------------------------------------ tour

    private void OnSessionChanged()
    {
        if (_session is null) return;
        if (!_session.IsActive)
        {
            EndTour(_session.Outcome);
            return;
        }

        PresentCurrent(first: false);
    }

    private void EndTour(TourOutcome outcome)
    {
        var returnTo = _startScreen;
        var navigate = Navigate;
        _session = null;
        TourEnded?.Invoke(outcome);
        navigate?.Invoke(returnTo);
        CloseOverlay();
    }

    private void OnNextClick(object sender, RoutedEventArgs e) => _session?.Next();
    private void OnBackClick(object sender, RoutedEventArgs e) => _session?.Back();
    private void OnSkipClick(object sender, RoutedEventArgs e) => _session?.Skip();

    private void OnReplayClick(object sender, RoutedEventArgs e)
    {
        if (_session is null) return;
        _session.Changed -= OnSessionChanged;
        _session = new TourSession();
        _session.Changed += OnSessionChanged;
        PresentCurrent(first: false);
    }

    private void PresentCurrent(bool first)
    {
        var session = _session;
        if (session is null || !session.IsActive) return;

        var step = session.CurrentStep;
        var token = ++_presentToken;

        // The card goes away while the screen changes underneath, then returns with the new content.
        CardTransitionOut();

        if (step is not null) Navigate?.Invoke(step.Screen);
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            if (token != _presentToken || !_open || _session is null) return;
            Present(step, animate: AnimationsEnabled, entrance: true);
            FocusLater(NextButton);
            // Layout that settles a beat after a screen switch (Designer fits its canvas at background
            // priority) is picked up by a second, silent pass.
            Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
            {
                if (token != _presentToken || !_open || _session is null) return;
                Reposition(animate: false);
            }));
        }));
    }

    private void Present(TourStep? step, bool animate, bool entrance)
    {
        _presented = step;
        FillCard(step);
        _target = null;
        Reposition(animate, entrance);
    }

    private void FillCard(TourStep? step)
    {
        var session = _session!;
        BackButton.Visibility = session.CanGoBack && !session.IsFinishCard ? Visibility.Visible : Visibility.Collapsed;
        ReplayButton.Visibility = session.IsFinishCard ? Visibility.Visible : Visibility.Collapsed;
        SkipButton.Visibility = session.IsFinishCard ? Visibility.Collapsed : Visibility.Visible;
        PointsHost.Children.Clear();
        PointsHost.Visibility = Visibility.Collapsed;
        SafetyBox.Visibility = Visibility.Collapsed;

        if (step is null)
        {
            // Closing card.
            StepIcon.IconData = (Geometry)FindResource("Glyph.Check");
            CounterText.Text = string.Empty;
            TitleText.Inlines.Clear();
            TitleText.Inlines.Add(new Run(TourSteps.FinishTitle));
            TitleText.Inlines.Add(new Run(".") { Foreground = (Brush)FindResource("Brush.Brand") });
            BodyText.Text = TourSteps.FinishBody;
            NextLabel.Text = "Dokončit";
            NextButton.SetValue(System.Windows.Automation.AutomationProperties.NameProperty, "Dokončit prohlídku");
            UpdateDots(session.Total);
            Card.SetValue(System.Windows.Automation.AutomationProperties.NameProperty, "Prohlídka dokončena");
            return;
        }

        StepIcon.IconData = (Geometry)FindResource(step.IconKey);
        CounterText.Text = session.Counter;
        TitleText.Inlines.Clear();
        TitleText.Inlines.Add(new Run(step.Title));
        BodyText.Text = step.Body;
        NextLabel.Text = "Další";
        NextButton.SetValue(System.Windows.Automation.AutomationProperties.NameProperty, "Další");

        if (step.Points is { Count: > 0 })
        {
            foreach (var point in step.Points)
            {
                var line = new TextBlock
                {
                    Margin = new Thickness(0, 0, 0, 4),
                    TextWrapping = TextWrapping.Wrap,
                    FontSize = (double)FindResource("Size.Text.Section"),
                    LineHeight = 24,
                    Foreground = (Brush)FindResource("Brush.TextPrimary"),
                };
                line.Inlines.Add(new Run(point.Term) { FontWeight = FontWeights.SemiBold });
                line.Inlines.Add(new Run(" - " + point.Meaning));
                PointsHost.Children.Add(line);
            }
            PointsHost.Visibility = Visibility.Visible;
        }

        if (!string.IsNullOrWhiteSpace(step.SafetyNote))
        {
            SafetyText.Text = step.SafetyNote;
            SafetyBox.Visibility = Visibility.Visible;
        }

        UpdateDots(session.Index);
        Card.SetValue(System.Windows.Automation.AutomationProperties.NameProperty,
            $"Prohlídka, krok {session.Counter}: {step.Title}");
    }

    /// <summary>Recomputes spotlight, card and caret for the step on screen. Called on every step, on
    /// every window size change, and once more after layout has settled.</summary>
    internal void Reposition(bool animate, bool entrance = false)
    {
        if (!_open || CoachLayer.Visibility != Visibility.Visible) return;
        var width = Root.ActualWidth;
        var height = Root.ActualHeight;
        if (width <= 0 || height <= 0) return;

        UpdateFullGeometry();

        Rect? targetRect = null;
        var step = _presented;
        if (step is not null && Window.GetWindow(this) is { } window)
        {
            if (_target is null || !_target.IsVisible)
                _target = TourTargetResolver.Find(window, step.TargetIds);
            if (_target is not null) targetRect = TourTargetResolver.BoundsIn(_target, Root);
        }

        Card.Width = Math.Min(440, Math.Max(240, width - 2 * TourLayout.EdgeMargin));
        Card.Measure(new Size(Card.Width, double.PositiveInfinity));
        var layout = TourLayout.Compute(new Size(width, height), targetRect, Card.DesiredSize,
            step?.Placement ?? TourPlacement.Center);
        LastLayout = layout;

        ApplySpotlight(layout.Spotlight, animate);
        Canvas.SetLeft(Card, layout.Card.Left);
        Canvas.SetTop(Card, layout.Card.Top);
        ApplyCaret(layout);

        if (entrance) CardTransitionIn(layout, animate);
        else if (Card.Opacity < 1 && Card.Visibility == Visibility.Visible && !animate) Card.Opacity = 1;
    }

    private void OnRootSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!_open) return;
        UpdateFullGeometry();
        Reposition(animate: false);
    }

    private void UpdateFullGeometry()
    {
        _full.Rect = new Rect(0, 0, Math.Max(0, Root.ActualWidth), Math.Max(0, Root.ActualHeight));
    }

    private void ApplySpotlight(Rect? spotlight, bool animate)
    {
        if (spotlight is not { } rect)
        {
            SpotRing.Visibility = Visibility.Collapsed;
            SpotHalo.Visibility = Visibility.Collapsed;
            StopHalo();
            AnimateSpotTo(new Rect(Root.ActualWidth / 2, Root.ActualHeight / 2, 0, 0), animate);
            return;
        }

        SpotRing.Visibility = Visibility.Visible;
        AnimateSpotTo(rect, animate);

        var halo = Rect.Inflate(rect, 8, 8);
        SpotHalo.Margin = new Thickness(halo.Left, halo.Top, 0, 0);
        SpotHalo.Width = halo.Width;
        SpotHalo.Height = halo.Height;
        SpotHalo.Visibility = Visibility.Visible;
        if (animate && AnimationsEnabled) StartHalo();
        else StopHalo();
    }

    private void AnimateSpotTo(Rect to, bool animate)
    {
        if (animate && AnimationsEnabled && !_spot.Rect.IsEmpty)
        {
            var from = _spot.Rect;
            _spot.BeginAnimation(RectangleGeometry.RectProperty, new RectAnimation(from, to, Dur("Motion.Spatial"))
            {
                EasingFunction = EaseOut(),
                FillBehavior = FillBehavior.HoldEnd,
            });
        }
        else
        {
            _spot.BeginAnimation(RectangleGeometry.RectProperty, null);
            _spot.Rect = to;
        }
    }

    private void StartHalo()
    {
        var breathe = (Duration)FindResource("Motion.Breathe");
        var fade = new DoubleAnimation(0, 0.55, breathe)
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            BeginTime = TimeSpan.FromMilliseconds(260),
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
        };
        SpotHalo.BeginAnimation(OpacityProperty, fade);
        var grow = new DoubleAnimation(1, 1.025, breathe)
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            BeginTime = TimeSpan.FromMilliseconds(260),
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
        };
        HaloScale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
        HaloScale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
    }

    private void StopHalo()
    {
        SpotHalo.BeginAnimation(OpacityProperty, null);
        HaloScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        HaloScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        SpotHalo.Opacity = 0;
    }

    private void ApplyCaret(TourLayoutResult layout)
    {
        if (layout.Caret == TourCaretSide.None)
        {
            Caret.Visibility = Visibility.Collapsed;
            return;
        }

        Caret.Visibility = Visibility.Visible;
        var card = layout.Card;
        switch (layout.Caret)
        {
            case TourCaretSide.Top:
                Caret.LayoutTransform = Transform.Identity;
                Canvas.SetLeft(Caret, card.Left + layout.CaretOffset - 10);
                Canvas.SetTop(Caret, card.Top - 10);
                break;
            case TourCaretSide.Bottom:
                Caret.LayoutTransform = new RotateTransform(180);
                Canvas.SetLeft(Caret, card.Left + layout.CaretOffset - 10);
                Canvas.SetTop(Caret, card.Bottom - 1);
                break;
            case TourCaretSide.Left:
                Caret.LayoutTransform = new RotateTransform(-90);
                Canvas.SetLeft(Caret, card.Left - 10);
                Canvas.SetTop(Caret, card.Top + layout.CaretOffset - 10);
                break;
            case TourCaretSide.Right:
                Caret.LayoutTransform = new RotateTransform(90);
                Canvas.SetLeft(Caret, card.Right - 1);
                Canvas.SetTop(Caret, card.Top + layout.CaretOffset - 10);
                break;
        }
    }

    private void CardTransitionOut()
    {
        Card.BeginAnimation(OpacityProperty, null);
        CardOffset.BeginAnimation(TranslateTransform.YProperty, null);
        Card.Opacity = 0;
        Caret.Opacity = 0;
    }

    private void CardTransitionIn(TourLayoutResult layout, bool animate)
    {
        Card.BeginAnimation(OpacityProperty, null);
        Caret.BeginAnimation(OpacityProperty, null);
        CardOffset.BeginAnimation(TranslateTransform.YProperty, null);
        if (!animate || !AnimationsEnabled)
        {
            Card.Opacity = 1;
            Caret.Opacity = 1;
            CardOffset.Y = 0;
            return;
        }

        var ease = EaseOut();
        var dur = Dur("Motion.Spatial");
        var begin = TimeSpan.FromMilliseconds(90);
        var from = layout.Placement is TourPlacement.Above ? -10 : 10;
        Card.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, dur) { BeginTime = begin, EasingFunction = ease });
        Caret.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, dur) { BeginTime = begin, EasingFunction = ease });
        CardOffset.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(from, 0, dur) { BeginTime = begin, EasingFunction = ease });
    }

    private void BuildDots()
    {
        DotsHost.Children.Clear();
        _dots.Clear();
        var count = _session?.Total ?? 0;
        for (var i = 0; i < count; i++)
        {
            var dot = new Border
            {
                Width = 6,
                Height = 6,
                Margin = new Thickness(i == 0 ? 0 : 4, 0, 0, 0),
                CornerRadius = (CornerRadius)FindResource("Radius.Pill"),
                Background = (Brush)FindResource("Brush.PanelBorderStrong"),
            };
            _dots.Add(dot);
            DotsHost.Children.Add(dot);
        }
    }

    private void UpdateDots(int index)
    {
        for (var i = 0; i < _dots.Count; i++)
        {
            var current = i == index;
            _dots[i].Width = current ? 18 : 6;
            _dots[i].Background = (Brush)FindResource(current ? "Brush.Accent" : i < index ? "Brush.TextMuted" : "Brush.PanelBorderStrong");
        }
    }

    // ------------------------------------------------------------------------------------ keyboard

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        if (!_open || e.Handled) return;

        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var modifiers = Keyboard.Modifiers;

        // Alt+F4 must still close the window.
        if (key == Key.F4 && (modifiers & ModifierKeys.Alt) != 0) return;

        if (WelcomeLayer.Visibility == Visibility.Visible)
        {
            if (key == Key.Escape && modifiers == ModifierKeys.None)
            {
                SkipWelcome();
                e.Handled = true;
                return;
            }
        }
        else if (_session is { IsActive: true })
        {
            var action = TourKeyRouter.Route(key, modifiers, Keyboard.FocusedElement is Button);
            if (action != TourAction.None)
            {
                _session.Apply(action);
                e.Handled = true;
                return;
            }
        }

        // Tab moves between the card's buttons, Space and Enter press the focused one. Everything else
        // is swallowed so no canvas or window shortcut can act on the design underneath the dimming.
        if (key is Key.Tab or Key.Space or Key.Enter && (modifiers & (ModifierKeys.Control | ModifierKeys.Alt)) == 0) return;
        e.Handled = true;
    }

    // ------------------------------------------------------------------------------------ helpers

    private void FocusLater(UIElement element)
    {
        Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
        {
            if (_open && element.IsVisible) Keyboard.Focus(element);
        }));
    }

    private Duration Dur(string key) =>
        TryFindResource(key) is Duration d ? d : new Duration(TimeSpan.FromMilliseconds(180));

    private IEasingFunction EaseOut() =>
        TryFindResource("Ease.Out") as IEasingFunction ?? new CubicEase { EasingMode = EasingMode.EaseOut };
}
