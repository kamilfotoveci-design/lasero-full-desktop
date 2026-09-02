using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Lasero.App.ViewModels;

namespace Lasero.App.Views.Kamil;

/// <summary>
/// Drives the assistant's three shapes and the motion between them.
///
/// The transitions animate one surface rather than swapping three controls, because the whole point
/// of the interaction is that the pill *is* the composer *is* the panel. Width and height carry the
/// change of shape; opacity and a small vertical offset carry the change of content. Everything is
/// short, eased out, and never bounces — this sits on top of a precision tool.
/// </summary>
public partial class KamilAssistantHost : UserControl
{
    // Geometry. Fixed per state so a transition is a single interpolation with a known destination,
    // rather than a measure pass the animation has to chase.
    private const double PillWidth = 176;
    private const double PillHeight = 48;
    private const double QuickWidth = 440;
    private const double QuickHeight = 156;
    private const double ExpandedWidth = 424;
    private const double ExpandedMaxHeight = 640;
    private const double ExpandedMinHeight = 340;

    /// <summary>Room left for the machine strip and the window's own chrome. The panel is clamped to
    /// what is left, so Rámovat, Spustit, Pauza and Zastavit are never covered on a short window.</summary>
    private const double ReservedVerticalChrome = 24;

    private static readonly Duration ShapeDuration = TimeSpan.FromMilliseconds(210);
    private static readonly Duration ContentDuration = TimeSpan.FromMilliseconds(130);

    private KamilAssistantViewModel? _viewModel;
    private INotifyCollectionChanged? _messages;
    private IInputElement? _focusBeforeOpening;

    public KamilAssistantHost()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    /// <summary>Honours the system's "show animations in Windows" setting, and the graphics tier —
    /// on a software-rendered session the transitions would be a stutter rather than a cue. Either
    /// way the state change itself is identical; only the motion is skipped.</summary>
    private static bool AnimationsEnabled => SystemParameters.ClientAreaAnimation
        && System.Windows.Media.RenderCapability.Tier > 0;

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is not KamilAssistantViewModel viewModel) return;
        if (ReferenceEquals(_viewModel, viewModel)) return;

        DetachViewModel();
        _viewModel = viewModel;
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        _viewModel.ComposerFocusRequested += OnComposerFocusRequested;
        _viewModel.FocusReturnRequested += OnFocusReturnRequested;

        _messages = _viewModel.Chat.Messages;
        _messages.CollectionChanged += OnMessagesChanged;

        ApplyState(_viewModel.State, animate: false);
    }

    private void OnUnloaded(object sender, RoutedEventArgs e) => DetachViewModel();

    private void DetachViewModel()
    {
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
            _viewModel.ComposerFocusRequested -= OnComposerFocusRequested;
            _viewModel.FocusReturnRequested -= OnFocusReturnRequested;
            _viewModel = null;
        }

        if (_messages is not null)
        {
            _messages.CollectionChanged -= OnMessagesChanged;
            _messages = null;
        }
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(KamilAssistantViewModel.State) || _viewModel is null) return;
        ApplyState(_viewModel.State, animate: true);
    }

    private void OnMessagesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // Sending from Quick Ask opens the panel: the answer has nowhere to appear otherwise, and the
        // operator asked a question precisely because they want to read one.
        if (e.Action == NotifyCollectionChangedAction.Add &&
            _viewModel is { State: KamilAssistantState.QuickAsk })
        {
            _viewModel.ExpandCommand.Execute(null);
        }

        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => MessageScroll.ScrollToEnd()));
    }

    // ------------------------------------------------------------------
    // State → geometry and motion
    // ------------------------------------------------------------------

    private void ApplyState(KamilAssistantState state, bool animate)
    {
        var (width, height) = MeasureState(state);

        Root.Visibility = state == KamilAssistantState.Hidden ? Visibility.Collapsed : Visibility.Visible;
        if (state == KamilAssistantState.Hidden) return;

        SetLayerVisibility(state);

        if (!animate || !AnimationsEnabled)
        {
            Surface.BeginAnimation(WidthProperty, null);
            Surface.BeginAnimation(HeightProperty, null);
            Surface.Width = width;
            Surface.Height = height;
            SetLayerOpacity(state, immediate: true);
            return;
        }

        AnimateSurface(width, height);
        SetLayerOpacity(state, immediate: false);
    }

    private (double Width, double Height) MeasureState(KamilAssistantState state) => state switch
    {
        KamilAssistantState.QuickAsk => (QuickWidth, QuickHeight),
        KamilAssistantState.Expanded => (ExpandedWidth, AvailableExpandedHeight()),
        _ => (PillWidth, PillHeight),
    };

    /// <summary>
    /// The tallest the panel may be here and now. Bounded by the host's own height, which is the
    /// workspace between the title bar and the machine strip — so the panel shrinks on a 768px screen
    /// instead of running off the top or sitting over the job controls.
    /// </summary>
    private double AvailableExpandedHeight()
    {
        var host = ActualHeight > 0 ? ActualHeight : ExpandedMaxHeight;
        var room = host - Root.Margin.Top - Root.Margin.Bottom - ReservedVerticalChrome;
        return Math.Clamp(Math.Min(ExpandedMaxHeight, room), ExpandedMinHeight, ExpandedMaxHeight);
    }

    private void AnimateSurface(double width, double height)
    {
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };

        Surface.Width = Surface.ActualWidth > 0 ? Surface.ActualWidth : Surface.Width;
        Surface.Height = Surface.ActualHeight > 0 ? Surface.ActualHeight : Surface.Height;

        Surface.BeginAnimation(WidthProperty, new DoubleAnimation
        {
            To = width,
            Duration = ShapeDuration,
            EasingFunction = ease,
            FillBehavior = FillBehavior.HoldEnd,
        });

        Surface.BeginAnimation(HeightProperty, new DoubleAnimation
        {
            To = height,
            Duration = ShapeDuration,
            EasingFunction = ease,
            FillBehavior = FillBehavior.HoldEnd,
        });
    }

    /// <summary>
    /// Which layer is hit-testable. Kept separate from opacity because a fully transparent layer
    /// still swallows clicks, and during a crossfade the incoming layer must be the one that
    /// responds even while the outgoing one is still visible.
    /// </summary>
    private void SetLayerVisibility(KamilAssistantState state)
    {
        PillLayer.Visibility = state == KamilAssistantState.Minimized ? Visibility.Visible : Visibility.Collapsed;
        QuickLayer.Visibility = state == KamilAssistantState.QuickAsk ? Visibility.Visible : Visibility.Collapsed;
        ExpandedLayer.Visibility = state == KamilAssistantState.Expanded ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// Content crossfade. The incoming layer also rises a few pixels into place — the same direction
    /// the panel grows — so opening reads as one movement rather than a fade laid over a resize.
    /// </summary>
    private void SetLayerOpacity(KamilAssistantState state, bool immediate)
    {
        Fade(PillLayer, PillOffset, state == KamilAssistantState.Minimized, immediate);
        Fade(QuickLayer, QuickOffset, state == KamilAssistantState.QuickAsk, immediate);
        Fade(ExpandedLayer, ExpandedOffset, state == KamilAssistantState.Expanded, immediate);
    }

    private static void Fade(UIElement layer, System.Windows.Media.TranslateTransform offset, bool visible, bool immediate)
    {
        if (immediate)
        {
            layer.BeginAnimation(OpacityProperty, null);
            offset.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty, null);
            layer.Opacity = visible ? 1 : 0;
            offset.Y = 0;
            return;
        }

        layer.BeginAnimation(OpacityProperty, new DoubleAnimation
        {
            To = visible ? 1 : 0,
            Duration = ContentDuration,
            // Incoming content waits for the surface to be most of the way to its new shape, so text
            // never appears in a box that is still visibly the wrong size.
            BeginTime = visible ? TimeSpan.FromMilliseconds(70) : TimeSpan.Zero,
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.HoldEnd,
        });

        offset.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty, new DoubleAnimation
        {
            From = visible ? 6 : 0,
            To = visible ? 0 : 4,
            Duration = ContentDuration,
            BeginTime = visible ? TimeSpan.FromMilliseconds(70) : TimeSpan.Zero,
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.HoldEnd,
        });
    }

    // ------------------------------------------------------------------
    // Focus and keyboard
    // ------------------------------------------------------------------

    private void OnComposerFocusRequested()
    {
        _focusBeforeOpening ??= Keyboard.FocusedElement;

        // After the layout pass, or the composer is not yet visible and Focus() is a no-op.
        Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
        {
            if (_viewModel is null) return;
            switch (_viewModel.State)
            {
                case KamilAssistantState.QuickAsk:
                    QuickComposer.FocusInput();
                    break;
                case KamilAssistantState.Expanded:
                    ExpandedComposer.FocusInput();
                    break;
            }
        }));
    }

    /// <summary>Hands the keyboard back to whatever the operator was using before the assistant took
    /// it — usually the canvas. Without this, closing the panel leaves focus nowhere and the editor's
    /// single-key tool shortcuts stop working.</summary>
    private void OnFocusReturnRequested()
    {
        var target = _focusBeforeOpening;
        _focusBeforeOpening = null;
        if (target is null) return;

        Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
        {
            if (target is UIElement { IsVisible: true }) Keyboard.Focus(target);
        }));
    }

    /// <summary>Esc steps back one shape at a time. Handled here rather than as a window-level
    /// binding so it only applies while the assistant actually holds the keyboard.</summary>
    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        if (e.Key != Key.Escape || _viewModel is null) return;
        if (_viewModel.State is KamilAssistantState.Minimized or KamilAssistantState.Hidden) return;

        _viewModel.StepBack();
        e.Handled = true;
    }
}
