using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Lasero.App.ViewModels;

namespace Lasero.App.Views;

/// <summary>
/// The Designer workspace's 56px rail. Screens above and below, drawing tools in the middle, with a
/// hairline between the groups. Materials and Settings open windows rather than switching screens,
/// so those two go through the window the same way the full sidebar's entries do.
///
/// The shape tool button is the one entry here with real interaction state: a short click activates
/// whichever shape it remembers (Scene.CurrentShapeTool) instantly, a press-and-hold or a right-click
/// opens a picker for the other seven. All of that state lives in this file, driven by
/// PreviewMouseLeftButtonDown/Up and a single DispatcherTimer for the long-press threshold — no
/// polling, no handlers re-attached on Loaded (the handlers below are wired once, in XAML).
/// </summary>
public partial class DesignerToolRail : UserControl
{
    private const int LongPressThresholdMs = 400;
    private const double MoveCancelThresholdDips = 6;

    private DispatcherTimer? _longPressTimer;
    private Point _pressStartPoint;
    private bool _isPressed;
    private bool _longPressTriggered;

    public DesignerToolRail()
    {
        InitializeComponent();

        ShapePickerPopup.PlacementTarget = ShapeToolButton;
        ShapePickerPopup.CustomPopupPlacementCallback = PlaceShapePicker;

        Unloaded += (_, _) =>
        {
            StopLongPressTimer();
            ClosePicker();
        };
    }

    private MainViewModel? Vm => DataContext as MainViewModel;

    private void OnMaterialsClick(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is MainWindow window) window.OpenMaterials();
    }

    private void OnSettingsClick(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is MainWindow window) window.OpenSettings();
    }

    private void OnChatClick(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is MainWindow window) window.OpenKamilInDesigner();
    }

    // ---------------------------------------------------------------------------------------
    // Shape tool button: click vs long-press
    // ---------------------------------------------------------------------------------------

    private void OnShapeButtonPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _isPressed = true;
        _longPressTriggered = false;
        _pressStartPoint = e.GetPosition(ShapeToolButton);
        ShapeToolButton.CaptureMouse();
        StartLongPressTimer();
    }

    private void OnShapeButtonPreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (!_isPressed || _longPressTriggered) return;

        var delta = e.GetPosition(ShapeToolButton) - _pressStartPoint;
        if (Math.Abs(delta.X) > MoveCancelThresholdDips || Math.Abs(delta.Y) > MoveCancelThresholdDips)
            CancelPress();
    }

    private void OnShapeButtonMouseLeave(object sender, MouseEventArgs e)
    {
        // The picker is already open by the time the pointer could leave after a long press, so this
        // only ever cancels a pending short click — an accidental drag off the button, matching how a
        // normal Button aborts its Click if you drag off it before releasing.
        if (_isPressed && !_longPressTriggered) CancelPress();
    }

    private void OnShapeButtonPreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        StopLongPressTimer();
        if (ShapeToolButton.IsMouseCaptured) ShapeToolButton.ReleaseMouseCapture();

        if (_longPressTriggered)
        {
            // The picker is already open; releasing the button must not also draw a rectangle.
            _isPressed = false;
            _longPressTriggered = false;
            e.Handled = true;
            return;
        }

        if (!_isPressed) return; // cancelled by drag or mouse leave
        _isPressed = false;

        ActivateShape(Vm?.Scene.CurrentShapeTool ?? DesignerTool.Rectangle);
    }

    private void OnShapeButtonMouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        CancelPress();
        OpenPicker();
    }

    private void OnShapeButtonKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter && e.Key != Key.Space) return;
        e.Handled = true;
        ActivateShape(Vm?.Scene.CurrentShapeTool ?? DesignerTool.Rectangle);
    }

    private void ActivateShape(DesignerTool tool)
    {
        if (Vm is null) return;
        if (Vm.Scene.ActivateToolCommand.CanExecute(tool)) Vm.Scene.ActivateToolCommand.Execute(tool);
        FocusCanvas();
    }

    private void StartLongPressTimer()
    {
        StopLongPressTimer();
        _longPressTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(LongPressThresholdMs) };
        _longPressTimer.Tick += OnLongPressTimerTick;
        _longPressTimer.Start();
    }

    private void OnLongPressTimerTick(object? sender, EventArgs e)
    {
        StopLongPressTimer();
        if (!_isPressed) return; // already released or cancelled before the threshold

        _longPressTriggered = true;
        OpenPicker();
    }

    private void StopLongPressTimer()
    {
        if (_longPressTimer is null) return;
        _longPressTimer.Stop();
        _longPressTimer.Tick -= OnLongPressTimerTick;
        _longPressTimer = null;
    }

    private void CancelPress()
    {
        StopLongPressTimer();
        _isPressed = false;
        _longPressTriggered = false;
        if (ShapeToolButton.IsMouseCaptured) ShapeToolButton.ReleaseMouseCapture();
    }

    // ---------------------------------------------------------------------------------------
    // Shape picker: positioning, open animation, selection, dismissal
    // ---------------------------------------------------------------------------------------

    private void OpenPicker()
    {
        if (ShapePickerPopup.IsOpen) return;
        ShapePickerPopup.IsOpen = true;
    }

    private void ClosePicker()
    {
        if (ShapePickerPopup.IsOpen) ShapePickerPopup.IsOpen = false;
    }

    /// <summary>
    /// Two candidates: to the right of the button (the rail sits on the left edge, so this is almost
    /// always where it lands) and, failing that, to the left. WPF tries each in turn and keeps the
    /// first that fits fully on the current monitor, so nothing here hard-codes screen coordinates —
    /// it degrades the same way at any window size or DPI.
    /// </summary>
    private static CustomPopupPlacement[] PlaceShapePicker(Size popupSize, Size targetSize, Point offset)
    {
        const double gap = 6;
        return
        [
            new CustomPopupPlacement(new Point(targetSize.Width + gap, 0), PopupPrimaryAxis.None),
            new CustomPopupPlacement(new Point(-popupSize.Width - gap, 0), PopupPrimaryAxis.None),
        ];
    }

    private void OnShapePickerOpened(object? sender, EventArgs e)
    {
        var duration = TimeSpan.FromMilliseconds(140);
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };

        ShapePickerSurface.Opacity = 0;
        ShapePickerScale.ScaleX = 0.97;
        ShapePickerScale.ScaleY = 0.97;
        ShapePickerTranslate.X = -3;

        ShapePickerSurface.BeginAnimation(UIElement.OpacityProperty,
            new DoubleAnimation(0, 1, duration) { EasingFunction = ease });
        ShapePickerScale.BeginAnimation(ScaleTransform.ScaleXProperty,
            new DoubleAnimation(0.97, 1, duration) { EasingFunction = ease });
        ShapePickerScale.BeginAnimation(ScaleTransform.ScaleYProperty,
            new DoubleAnimation(0.97, 1, duration) { EasingFunction = ease });
        ShapePickerTranslate.BeginAnimation(TranslateTransform.XProperty,
            new DoubleAnimation(-3, 0, duration) { EasingFunction = ease });

        Dispatcher.BeginInvoke(new Action(() => ShapePickerSurface.Focus()), DispatcherPriority.Input);
    }

    private void OnShapePickerClosed(object? sender, EventArgs e)
    {
        // Nothing to tear down: the popup content is reused between opens (WPF keeps it alive), and
        // OnShapePickerOpened resets opacity/scale/translate at the start of every open, so a picker
        // dismissed mid-animation never leaves a half-faded surface behind next time it shows.
    }

    private void OnShapePickerKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        e.Handled = true; // Esc closes the picker only — it must not also reach canvas Escape handling.
        ClosePicker();
        FocusCanvas();
    }

    private void OnShapePickerItemClick(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleButton { Tag: DesignerTool tool } && Vm is not null
            && Vm.Scene.ActivateToolCommand.CanExecute(tool))
        {
            Vm.Scene.ActivateToolCommand.Execute(tool);
        }

        ClosePicker();
        FocusCanvas();
    }

    private void FocusCanvas()
    {
        if (Window.GetWindow(this) is MainWindow window) window.FocusCanvas();
    }
}
