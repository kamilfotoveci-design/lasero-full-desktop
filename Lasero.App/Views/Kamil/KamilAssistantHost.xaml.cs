using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Lasero.App.ViewModels;

namespace Lasero.App.Views.Kamil;

public sealed class AssistantSizeChangedEventArgs : EventArgs
{
    public AssistantSizeChangedEventArgs(double width, double height) => (Width, Height) = (width, height);
    public double Width { get; }
    public double Height { get; }
}

/// <summary>
/// Drives the assistant's three shapes and the motion between them.
///
/// The transitions animate one surface rather than swapping three controls, because the whole point
/// of the interaction is that the pill *is* the composer *is* the panel. Width and height carry the
/// change of shape; opacity and a small vertical offset carry the change of content. Everything is
/// short and high-damping — the final settle is tactile, never a mascot bounce.
/// </summary>
public partial class KamilAssistantHost : UserControl
{
    // Geometry. Fixed per state so a transition is a single interpolation with a known destination,
    // rather than a measure pass the animation has to chase.
    private const double PillWidth = 48;
    private const double PillHeight = 48;
    private const double QuickWidth = 400;
    private const double QuickHeight = 200;
    private const double ExpandedWidth = 420;
    private const double ExpandedMaxHeight = 640;
    private const double ExpandedMinHeight = 500;
    private const double ResizeMinWidth = 320;
    private const double ResizeMinHeight = 360;
    private const double ResizeMaxWidth = 720;
    private const double ResizeMaxHeight = 760;

    // Shape duration depends on which pair of states is involved — QuickAsk to/from Expanded is the
    // biggest change of shape and reads better slightly slower than the other two, which both move a
    // 48px badge a comparatively short distance.
    private static readonly Duration ShapeDurationDefault = TimeSpan.FromMilliseconds(260);
    private static readonly Duration ShapeDurationToExpanded = TimeSpan.FromMilliseconds(320);
    private static readonly Duration ContentDuration = TimeSpan.FromMilliseconds(170);

    private KamilAssistantViewModel? _viewModel;
    private INotifyCollectionChanged? _messages;
    private IInputElement? _focusBeforeOpening;
    private Point _dragStart;
    private Point _dragOrigin;
    private PopoverDirection _openDirection = PopoverDirection.Left;
    private bool _isDragging;
    private bool _isResizing;

    private const double DragThreshold = 4;

    public event EventHandler<AssistantSizeChangedEventArgs>? AssistantResized;

    // The state this control last actually rendered, so a transition can pick its own duration (see
    // <see cref="ShapeDurationFor"/>) instead of every transition running at the same speed.
    private KamilAssistantState _lastRenderedState = KamilAssistantState.Minimized;

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
        SizeChanged += OnHostSizeChanged;

        if (Window.GetWindow(this) is Window window)
        {
            window.SizeChanged += OnWindowSizeChanged;
            if (window.FindName("DesignerInspector") is FrameworkElement inspector)
                inspector.SizeChanged += OnBoundarySizeChanged;
            if (window.FindName("CanvasViewControls") is FrameworkElement controls)
                controls.SizeChanged += OnBoundarySizeChanged;
        }

        ApplyState(_viewModel.State, animate: false);
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(RepositionForLayout));
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        SizeChanged -= OnHostSizeChanged;
        if (Window.GetWindow(this) is Window window)
        {
            window.SizeChanged -= OnWindowSizeChanged;
            if (window.FindName("DesignerInspector") is FrameworkElement inspector)
                inspector.SizeChanged -= OnBoundarySizeChanged;
            if (window.FindName("CanvasViewControls") is FrameworkElement controls)
                controls.SizeChanged -= OnBoundarySizeChanged;
        }

        DetachViewModel();
    }

    /// <summary>The head itself has a fixed home spot and never moves — only the open panel
    /// (QuickAsk/Expanded) can be dragged, and it always resets to that fixed spot on close.</summary>
    private void OnDragMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_viewModel is null or { State: KamilAssistantState.Minimized or KamilAssistantState.Hidden }) return;

        _dragStart = e.GetPosition(Root);
        _dragOrigin = CurrentSurfacePosition();
        _isDragging = false;
    }

    private void OnDragMouseMove(object sender, MouseEventArgs e)
    {
        if (_viewModel is null or { State: KamilAssistantState.Minimized or KamilAssistantState.Hidden }) return;
        if (e.LeftButton != MouseButtonState.Pressed) return;

        var current = e.GetPosition(Root);
        var delta = current - _dragStart;
        if (!_isDragging && delta.Length < DragThreshold) return;

        if (!_isDragging)
        {
            _isDragging = true;
            CaptureMouse();
        }

        var size = CurrentSurfaceSize();
        var bounds = GetUsableBounds(size);
        // The panel may be dragged anywhere on the canvas except onto the head: dropping it there
        // would cover the send button and the head itself.
        SetSurfacePosition(AvoidHead(ClampPosition(_dragOrigin + delta, size, bounds), size, HomeAnchor(), bounds));
    }

    private void OnDragMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isDragging) return;

        _isDragging = false;
        ReleaseMouseCapture();
        e.Handled = true;
    }

    /// <summary>A plain click on the Expanded header's own background minimizes the assistant, the
    /// same as its Minimize button — the header also doubles as the panel's drag handle
    /// (<see cref="OnDragMouseLeftButtonDown"/>/<see cref="OnDragMouseMove"/>), so this only fires for
    /// a real click: <see cref="_isDragging"/> is set the moment the pointer moves past
    /// <see cref="DragThreshold"/>, and a drag's own MouseUp already marks the event handled before
    /// bubbling here.</summary>
    private void OnHeaderClick(object sender, MouseButtonEventArgs e)
    {
        if (_isDragging || _viewModel is null) return;
        _viewModel.MinimizeCommand.Execute(null);
    }

    // A click on the switcher button while its popup is open first closes the popup (StaysOpen=False
    // treats it as an outside click); without this the same click would reopen it at once.
    private DateTime _sessionsClosedAt = DateTime.MinValue;

    private void OnSessionsButtonClick(object sender, RoutedEventArgs e)
    {
        if ((DateTime.UtcNow - _sessionsClosedAt).TotalMilliseconds < 250) return;
        _viewModel?.Chat.CancelDeleteChatCommand.Execute(null);
        SessionsPopup.IsOpen = true;
    }

    private void OnSessionsPopupClosed(object? sender, EventArgs e)
    {
        _sessionsClosedAt = DateTime.UtcNow;
        _viewModel?.Chat.CancelDeleteChatCommand.Execute(null);
    }

    private void OnSessionRowClick(object sender, RoutedEventArgs e) => SessionsPopup.IsOpen = false;

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
        // Expanded is also reached by a rapid double-click (QuickAsk -> Expanded). Starting a
        // second size animation before the first one settles leaves WPF rendering an intermediate
        // surface outside the host. Open the full conversation atomically; the small QuickAsk state
        // may still use its gentle transition.
        ApplyState(_viewModel.State, animate: _viewModel.State != KamilAssistantState.Expanded);
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
        var targetSize = MeasureSurface(width, height);
        var duration = ShapeDurationFor(_lastRenderedState, state);

        Root.Visibility = state == KamilAssistantState.Hidden ? Visibility.Collapsed : Visibility.Visible;
        if (state == KamilAssistantState.Hidden) return;

        var anchor = HomeAnchor();
        SetPersistentAvatarPosition(state != KamilAssistantState.Minimized, anchor);

        var from = _lastRenderedState;
        var currentPosition = CurrentSurfacePosition();

        var targetPosition = state == KamilAssistantState.Minimized
            ? anchor
            : PositionPopover(targetSize, anchor);

        SetLayerVisibility(state);

        if (!animate || !AnimationsEnabled)
        {
            Surface.BeginAnimation(WidthProperty, null);
            Surface.BeginAnimation(HeightProperty, null);
            Surface.BeginAnimation(OpacityProperty, null);
            SurfaceScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            SurfaceScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            SurfaceOffset.BeginAnimation(TranslateTransform.XProperty, null);
            SurfaceOffset.BeginAnimation(TranslateTransform.YProperty, null);
            Surface.Width = width;
            Surface.Height = height;
            SetSurfacePosition(targetPosition);
            Surface.Opacity = 1;
            SurfaceScale.ScaleX = 1;
            SurfaceScale.ScaleY = 1;
            SurfaceOffset.X = 0;
            SurfaceOffset.Y = 0;
            SetLayerOpacity(state, immediate: true);
            _lastRenderedState = state;
            return;
        }

        var opening = from == KamilAssistantState.Minimized && state != KamilAssistantState.Minimized;
        var closing = state == KamilAssistantState.Minimized && from != KamilAssistantState.Minimized;
        SetSurfacePosition(targetPosition);
        AnimateSurface(width, height, duration, currentPosition, targetPosition, opening, closing);
        SetLayerOpacity(state, immediate: false);
        _lastRenderedState = state;
    }

    private (double Width, double Height) MeasureState(KamilAssistantState state) => state switch
    {
        // QuickHeight is a fixed constant, but the room above the avatar is not — clamp it the same
        // way SavedExpandedHeight() already clamps Expanded, otherwise a modestly-sized window lets
        // PositionPopover's own clamp silently overlap the avatar instead of shrinking to fit above it.
        KamilAssistantState.QuickAsk => (QuickWidth, Math.Min(QuickHeight, AvailableHeightAboveAvatar(QuickWidth, QuickHeight))),
        KamilAssistantState.Expanded => (SavedExpandedWidth(), SavedExpandedHeight()),
        _ => (PillWidth, PillHeight),
    };

    private double SavedExpandedWidth() => Window.GetWindow(this)?.DataContext is MainViewModel main
        ? Math.Clamp(main.SettingsStore.Current.Workspace.ClampedAssistantWidth, ResizeMinWidth, ResizeMaxWidth)
        : ExpandedWidth;

    private double SavedExpandedHeight()
    {
        var available = AvailableExpandedHeight();
        var minimum = Math.Min(ResizeMinHeight, available);
        var maximum = Math.Min(ResizeMaxHeight, available);

        return Window.GetWindow(this)?.DataContext is MainViewModel main
            ? Math.Clamp(main.SettingsStore.Current.Workspace.ClampedAssistantHeight, minimum, maximum)
            : available;
    }

    private void OnResizeDragDelta(object sender, DragDeltaEventArgs e)
    {
        if (_viewModel?.State != KamilAssistantState.Expanded) return;
        _isResizing = true;

        var current = CurrentSurfaceSize();
        var bounds = GetUsableBounds(current);
        var maxWidth = Math.Max(1, bounds.Width);
        var maxHeight = Math.Max(1, bounds.Height);
        var minWidth = Math.Min(ResizeMinWidth, maxWidth);
        var minHeight = Math.Min(ResizeMinHeight, maxHeight);
        var width = Math.Clamp(current.Width + e.HorizontalChange, minWidth, Math.Min(ResizeMaxWidth, maxWidth));
        var height = Math.Clamp(current.Height + e.VerticalChange, minHeight, Math.Min(ResizeMaxHeight, maxHeight));

        Surface.BeginAnimation(WidthProperty, null);
        Surface.BeginAnimation(HeightProperty, null);
        Surface.Width = width;
        Surface.Height = height;
        SetSurfacePosition(PositionPopover(new Size(width, height), HomeAnchor()));
    }

    private void OnResizeDragCompleted(object sender, DragCompletedEventArgs e)
    {
        if (!_isResizing) return;
        _isResizing = false;
        var size = CurrentSurfaceSize();
        AssistantResized?.Invoke(this, new AssistantSizeChangedEventArgs(size.Width, size.Height));
    }

    /// <summary>Only QuickAsk &lt;-&gt; Expanded — the biggest change of shape — runs slower than the
    /// other two transitions. Every other pair (including a same-state reapply) uses the default.</summary>
    private static Duration ShapeDurationFor(KamilAssistantState from, KamilAssistantState to) =>
        (from, to) is (KamilAssistantState.QuickAsk, KamilAssistantState.Expanded)
            or (KamilAssistantState.Expanded, KamilAssistantState.QuickAsk)
            ? ShapeDurationToExpanded
            : ShapeDurationDefault;

    /// <summary>
    /// The tallest the panel may be here and now.
    ///
    /// This control is Bottom/Right-aligned and sized to its own content (<see cref="Surface"/>'s
    /// explicit Width/Height), so its own <c>ActualHeight</c> is not "room available" — it is
    /// whatever the panel currently measures, which made the old clamp circular and meant Expanded
    /// could never actually grow past <see cref="ExpandedMinHeight"/> no matter how tall the window
    /// was. The figure that *is* the real workspace height is the parent Grid's — the "Main editor"
    /// row in MainWindow.xaml, Height="*" between the 60px title bar row and the 48px machine-strip
    /// row — because this control is declared as a direct, unstretched child of it.
    ///
    /// The bottom reservation is this control's own <c>Margin</c> (the live
    /// <see cref="Lasero.App.Converters.AssistantClearanceConverter"/> output bound in
    /// MainWindow.xaml), not <see cref="Root"/>'s — <c>Root</c> is the internal Grid one level below
    /// this control and never had a margin of its own; the real margin lives on this UserControl.
    /// </summary>
    private double AvailableExpandedHeight()
    {
        var room = Math.Max(1, AvailableHeightAboveAvatar(ExpandedWidth, ExpandedMinHeight));
        return Math.Min(ExpandedMaxHeight, room);
    }

    /// <summary>How tall an above-the-avatar popover (QuickAsk or Expanded) may grow before its own
    /// bottom edge would cross the fixed avatar's top edge (minus <see cref="AnchorGap"/>). Both states
    /// open from <see cref="PositionPopover"/> anchored above/left of <see cref="HomeAnchor"/>, so the
    /// real ceiling on their height is the room between the workspace top and that anchor line — not
    /// <see cref="GetUsableBounds"/>'s own bottom margin, which only fences the avatar's own home
    /// position and says nothing about a taller panel opening above it. Using the raw canvas bounds
    /// here let a QuickAsk/Expanded panel taller than that gap get silently pinned to the workspace top
    /// by <see cref="ClampPosition"/> and overlap the avatar and its own composer instead of stopping
    /// short of it — this is the fix for that.</summary>
    private double AvailableHeightAboveAvatar(double width, double minHeightForBoundsProbe)
    {
        var bounds = GetUsableBounds(new Size(width, minHeightForBoundsProbe));
        return Math.Max(0, bounds.Height - PillHeight - AnchorGap);
    }

    private Size MeasureSurface(double width, double height)
    {
        var oldWidth = Surface.Width;
        var oldHeight = Surface.Height;
        // A still-running Width/Height animation from the PREVIOUS transition holds the animated
        // (base-value-overriding) value through a plain property set — Measure() below would then
        // report that old, mid-flight size instead of the new target, silently mispositioning a
        // fast-following transition (e.g. the auto-expand-on-existing-messages path landing while
        // QuickAsk's own opening animation is still in flight) for the wrong footprint.
        Surface.BeginAnimation(WidthProperty, null);
        Surface.BeginAnimation(HeightProperty, null);
        Surface.Width = width;
        Surface.Height = height;
        Surface.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var measured = new Size(
            Surface.DesiredSize.Width > 0 ? Surface.DesiredSize.Width : width,
            Surface.DesiredSize.Height > 0 ? Surface.DesiredSize.Height : height);
        Surface.Width = oldWidth;
        Surface.Height = oldHeight;
        return measured;
    }

    private void AnimateSurface(
        double width,
        double height,
        Duration duration,
        Point currentPosition,
        Point targetPosition,
        bool opening,
        bool closing)
    {
        var ease = (IEasingFunction)FindResource("Ease.Out");

        Surface.Width = Surface.ActualWidth > 0 ? Surface.ActualWidth : Surface.Width;
        Surface.Height = Surface.ActualHeight > 0 ? Surface.ActualHeight : Surface.Height;

        Surface.BeginAnimation(WidthProperty, new DoubleAnimation
        {
            To = width,
            Duration = duration,
            EasingFunction = ease,
            FillBehavior = FillBehavior.HoldEnd,
        });

        Surface.BeginAnimation(HeightProperty, new DoubleAnimation
        {
            To = height,
            Duration = duration,
            EasingFunction = ease,
            FillBehavior = FillBehavior.HoldEnd,
        });

        var startX = closing
            ? Math.Clamp(currentPosition.X - targetPosition.X, -4, 4)
            : opening ? OpeningOffsetX() : 0;
        var startY = closing
            ? Math.Clamp(currentPosition.Y - targetPosition.Y, -4, 4)
            : opening ? OpeningOffsetY() : 0;

        SurfaceOffset.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation
        {
            From = startX,
            To = 0,
            Duration = duration,
            EasingFunction = ease,
            FillBehavior = FillBehavior.HoldEnd,
        });
        SurfaceOffset.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation
        {
            From = startY,
            To = 0,
            Duration = duration,
            EasingFunction = ease,
            FillBehavior = FillBehavior.HoldEnd,
        });

        SurfaceScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation
        {
            From = opening ? 0.97 : 1,
            To = closing ? 0.98 : 1,
            Duration = duration,
            EasingFunction = ease,
            FillBehavior = FillBehavior.HoldEnd,
        });
        SurfaceScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation
        {
            From = opening ? 0.97 : 1,
            To = closing ? 0.98 : 1,
            Duration = duration,
            EasingFunction = ease,
            FillBehavior = FillBehavior.HoldEnd,
        });

        if (opening)
        {
            Surface.Opacity = 0;
            Surface.BeginAnimation(OpacityProperty, new DoubleAnimation
            {
                From = 0,
                To = 1,
                Duration = duration,
                EasingFunction = ease,
                FillBehavior = FillBehavior.HoldEnd,
            });
        }
        else if (closing)
        {
            var fadeOut = new DoubleAnimation
            {
                From = 1,
                To = 0,
                Duration = TimeSpan.FromMilliseconds(120),
                EasingFunction = ease,
                FillBehavior = FillBehavior.HoldEnd,
            };
            fadeOut.Completed += (_, _) =>
            {
                if (_viewModel?.State != KamilAssistantState.Minimized) return;
                Surface.BeginAnimation(OpacityProperty, null);
                Surface.Opacity = 1;
                SurfaceScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                SurfaceScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
                SurfaceOffset.BeginAnimation(TranslateTransform.XProperty, null);
                SurfaceOffset.BeginAnimation(TranslateTransform.YProperty, null);
                SurfaceScale.ScaleX = 1;
                SurfaceScale.ScaleY = 1;
                SurfaceOffset.X = 0;
                SurfaceOffset.Y = 0;
            };
            Surface.BeginAnimation(OpacityProperty, fadeOut);
        }
    }

    private double OpeningOffsetX() => _openDirection switch
    {
        PopoverDirection.Left => 6,
        PopoverDirection.Right => -6,
        _ => 0,
    };

    private double OpeningOffsetY() => _openDirection switch
    {
        PopoverDirection.Above => 6,
        PopoverDirection.Below => -6,
        _ => 0,
    };

    private Point CurrentSurfacePosition() => new(
        double.IsNaN(Canvas.GetLeft(Surface)) ? 0 : Canvas.GetLeft(Surface),
        double.IsNaN(Canvas.GetTop(Surface)) ? 0 : Canvas.GetTop(Surface));

    private Size CurrentSurfaceSize() => new(
        Surface.ActualWidth > 0 ? Surface.ActualWidth : Surface.Width,
        Surface.ActualHeight > 0 ? Surface.ActualHeight : Surface.Height);

    private void SetSurfacePosition(Point position)
    {
        Canvas.SetLeft(Surface, position.X);
        Canvas.SetTop(Surface, position.Y);
    }

    private void SetPersistentAvatarPosition(bool visible, Point anchor)
    {
        Canvas.SetLeft(PersistentAvatarLayer, anchor.X);
        Canvas.SetTop(PersistentAvatarLayer, anchor.Y);
        PersistentAvatarLayer.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>The rectangle the head and the panel may occupy. The host's own Margin (see
    /// <see cref="Lasero.App.Converters.AssistantClearanceConverter"/>) already stops its right edge
    /// 20px short of the inspector and its bottom edge clear of the zoom/undo cluster, so the head
    /// rests flush against the bottom-right corner of this control and only the left/top edges get a
    /// safety inset. Reserving the inspector or the cluster a second time here is what once pushed the
    /// head into the middle of the canvas.</summary>
    private Rect GetUsableBounds(Size surfaceSize)
    {
        var width = Root.ActualWidth > 0 ? Root.ActualWidth : ActualWidth;
        var height = Root.ActualHeight > 0 ? Root.ActualHeight : ActualHeight;
        // The top edge sits below the floating selection bar (MainWindow.xaml: 30px inset plus the
        // bar's height, see ContextBarClearance). Without it an Expanded panel at 1366 x 768 rose over the bar and covered
        // its X, Y, size and rotation fields (docs/ui-review-2026-09-29.md P2-2). The panel now gets
        // shorter instead of covering them, and the head cannot be parked over the bar either.
        return new Rect(SafeMargin, ContextBarClearance,
            Math.Max(0, width - SafeMargin), Math.Max(0, height - ContextBarClearance));
    }

    public static Point ClampPosition(Point position, Size size, Rect bounds)
    {
        var maxX = Math.Max(bounds.Left, bounds.Right - size.Width);
        var maxY = Math.Max(bounds.Top, bounds.Bottom - size.Height);
        return new Point(Math.Clamp(position.X, bounds.Left, maxX), Math.Clamp(position.Y, bounds.Top, maxY));
    }

    /// <summary>The head's one fixed position — bottom-right of the usable canvas, clear of the
    /// inspector and the zoom/undo cluster. Recomputed from live layout rather than stored, so it
    /// tracks a resize or an inspector-width change without needing to remember anything.</summary>
    private Point HomeAnchor()
    {
        var bounds = GetUsableBounds(new Size(PillWidth, PillHeight));
        return HeadOrigin(bounds.Right, bounds.Bottom);
    }

    /// <summary>Top-left of the resting head inside a host whose usable area ends at
    /// (<paramref name="right"/>, <paramref name="bottom"/>). Pure, so the placement is unit-testable.</summary>
    public static Point HeadOrigin(double right, double bottom) => new(right - PillWidth, bottom - PillHeight);

    public static Size HeadSize => new(PillWidth, PillHeight);

    /// <summary>Grows up and to the left of the fixed head, clear of it on both axes by
    /// <see cref="AnchorGap"/>. The position is intentionally fixed to this direction: the panel
    /// always grows up and left from the head, so opening it never jumps to a different side of the
    /// canvas. <paramref name="avatarAnchor"/> must be the SAME point the persistent avatar was (or is
    /// about to be) positioned at in this same call — recomputing a second, independent
    /// <see cref="HomeAnchor"/> here let the two silently diverge whenever layout settled between the
    /// avatar's last reposition and this call (e.g. the zoom/undo cluster or the inspector column
    /// finishing its own measure a frame later), which is what let the panel overlap the avatar despite
    /// each individually looking correct.</summary>
    private Point PositionPopover(Size size, Point avatarAnchor)
    {
        _openDirection = PopoverDirection.Above;
        return ComputePopoverPosition(size, avatarAnchor, GetUsableBounds(size));
    }

    /// <summary>Where the open panel rests: up and to the left of the head, clear of it by
    /// <see cref="AnchorGap"/>. Pure, so the "never covers the head" guarantee is unit-testable for
    /// every window size and panel size.</summary>
    public static Point ComputePopoverPosition(Size size, Point headOrigin, Rect bounds)
    {
        var head = new Rect(headOrigin, HeadSize);
        var position = ClampPosition(new Point(head.Left - AnchorGap - size.Width, head.Top - AnchorGap - size.Height), size, bounds);
        return AvoidHead(position, size, headOrigin, bounds);
    }

    /// <summary>Final invariant, checked against the head's own rect (plus the gap) rather than
    /// trusted from the arithmetic that produced <paramref name="position"/>: the panel never overlaps
    /// the head, whether it got there by layout, a stale measure or a drag. The smaller of "move up
    /// above the head" and "move left of the head" wins; if neither fits the bounds the panel is
    /// pinned to the edge, which cannot happen while the height stays within <see cref="AvailableHeightAboveAvatar"/>.</summary>
    public static Point AvoidHead(Point position, Size size, Point headOrigin, Rect bounds)
    {
        var zone = new Rect(headOrigin.X - AnchorGap, headOrigin.Y - AnchorGap, PillWidth + 2 * AnchorGap, PillHeight + 2 * AnchorGap);
        var panel = new Rect(position, size);
        if (!Overlaps(panel, zone)) return position;

        var up = new Point(position.X, zone.Top - size.Height);
        var left = new Point(zone.Left - size.Width, position.Y);
        var upFits = up.Y >= bounds.Top;
        var leftFits = left.X >= bounds.Left;
        if (upFits && (!leftFits || Math.Abs(up.Y - position.Y) <= Math.Abs(left.X - position.X))) return up;
        if (leftFits) return left;
        return ClampPosition(up, size, bounds);
    }

    /// <summary>Overlap with positive area; Rect.IntersectsWith also counts shared edges, and sitting
    /// exactly on the gap line is the resting position, not an overlap.</summary>
    public static bool Overlaps(Rect a, Rect b) =>
        a.Left < b.Right - 0.01 && a.Right > b.Left + 0.01 && a.Top < b.Bottom - 0.01 && a.Bottom > b.Top + 0.01;

    private void RepositionForLayout()
    {
        if (_viewModel is null || _viewModel.State == KamilAssistantState.Hidden || Root.ActualWidth <= 0) return;
        if (_isDragging) return;

        // Re-derive size from the ViewModel's current state via MeasureState — the same source of
        // truth ApplyState itself uses — rather than trusting CurrentSurfaceSize() (the Surface's own
        // live Width/Height). A SizeChanged event (window/inspector/canvas-controls resize) can
        // schedule this method via Dispatcher.BeginInvoke and have it run in the narrow window between
        // the ViewModel's State changing and ApplyState's own Surface.Width/Height assignment landing —
        // reading the not-yet-updated Surface size at that moment silently positions the panel for the
        // PREVIOUS state's (smaller) footprint while the surface itself is about to grow into the NEW
        // state's footprint, which is what let an Expanded panel end up positioned for QuickAsk's
        // 400×132 box and then visually overlap the avatar once it grew to its real ~420×560 size.
        // MeasureState/SavedExpandedHeight() already clamp Expanded's height against available room, so
        // no separate re-check is needed here.
        var (measuredWidth, measuredHeight) = MeasureState(_viewModel.State);
        var size = new Size(measuredWidth, measuredHeight);
        if (Surface.Width != measuredWidth || Surface.Height != measuredHeight)
        {
            Surface.BeginAnimation(WidthProperty, null);
            Surface.BeginAnimation(HeightProperty, null);
            Surface.Width = measuredWidth;
            Surface.Height = measuredHeight;
        }

        var anchor = HomeAnchor();
        SetPersistentAvatarPosition(_viewModel.State != KamilAssistantState.Minimized, anchor);
        var position = _viewModel.State == KamilAssistantState.Minimized
            ? anchor
            : PositionPopover(size, anchor);
        SetSurfacePosition(position);
    }

    /// <summary>Last line of defence, run after every layout-driven and animation-driven placement:
    /// if the surface as actually laid out still touches the head, move it clear. Reads the live
    /// position and size, so it also repairs a placement made from a stale measure.</summary>
    private void EnforceClearOfHead()
    {
        if (_viewModel is null or { State: KamilAssistantState.Minimized or KamilAssistantState.Hidden }) return;
        if (Root.ActualWidth <= 0 || _isDragging) return;

        var size = new Size(
            double.IsNaN(Surface.Width) ? CurrentSurfaceSize().Width : Surface.Width,
            double.IsNaN(Surface.Height) ? CurrentSurfaceSize().Height : Surface.Height);
        var current = CurrentSurfaceSize();
        if (current.Width > 0 && current.Height > 0) size = new Size(Math.Max(size.Width, current.Width), Math.Max(size.Height, current.Height));
        var bounds = GetUsableBounds(size);
        var anchor = HomeAnchor();
        var corrected = AvoidHead(CurrentSurfacePosition(), size, anchor, bounds);
        if (corrected != CurrentSurfacePosition()) SetSurfacePosition(corrected);
    }

    private void OnHostSizeChanged(object? sender, SizeChangedEventArgs e) => ScheduleReposition();

    private void OnWindowSizeChanged(object? sender, SizeChangedEventArgs e) => ScheduleReposition();

    private void OnBoundarySizeChanged(object? sender, SizeChangedEventArgs e) => ScheduleReposition();

    private void ScheduleReposition() =>
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            RepositionForLayout();
            EnforceClearOfHead();
        }));

    private enum PopoverDirection
    {
        Above,
        Left,
        Right,
        Below,
    }

    private const double SafeMargin = 16;

    /// <summary>Distance from the top of the workspace to just under the selection bar: the 30px inset
    /// in MainWindow.xaml, the bar's height, and a gap. The bar is tallest, about 68px, when a text
    /// object is selected, because its horizontal scrollbar appears; that is the case measured here.</summary>
    internal const double ContextBarClearance = 30 + 68 + 8;
    private const double AnchorGap = 12;

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
            EasingFunction = (IEasingFunction)Application.Current.FindResource("Ease.Out"),
            FillBehavior = FillBehavior.HoldEnd,
        });

        offset.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty, new DoubleAnimation
        {
            From = visible ? 6 : 0,
            To = visible ? 0 : 4,
            Duration = ContentDuration,
            BeginTime = visible ? TimeSpan.FromMilliseconds(70) : TimeSpan.Zero,
            EasingFunction = (IEasingFunction)Application.Current.FindResource("Ease.Out"),
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
