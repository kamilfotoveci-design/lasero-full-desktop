using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Lasero.App.Converters;

namespace Lasero.App.Input;

/// <summary>
/// Drag-to-change for a numeric TextBox: press on the field and drag left or right with the left
/// button held, and the value moves by <see cref="StepProperty"/> for every <see cref="PixelsPerStep"/>
/// pixels. A plain click (no drag) still focuses the field and selects its text for typing; a field
/// that already has keyboard focus behaves as a normal text box.
///
/// The value goes through the field's own binding (UpdateSource), so validation, clamping in the view
/// model and live previews behave exactly as if the number had been typed. Decimals follow the text
/// already in the field, so "1,000" stays "1,000" and "2" stays whole.
/// </summary>
public static class NumericScrub
{
    private const double PixelsPerStep = 4;
    private const double DragThreshold = 3;

    /// <summary>Change per step. Zero or less turns scrubbing off.</summary>
    public static readonly DependencyProperty StepProperty = DependencyProperty.RegisterAttached(
        "Step", typeof(double), typeof(NumericScrub), new PropertyMetadata(0d, OnStepChanged));

    public static readonly DependencyProperty MinimumProperty = DependencyProperty.RegisterAttached(
        "Minimum", typeof(double), typeof(NumericScrub), new PropertyMetadata(double.NegativeInfinity));

    public static readonly DependencyProperty MaximumProperty = DependencyProperty.RegisterAttached(
        "Maximum", typeof(double), typeof(NumericScrub), new PropertyMetadata(double.PositiveInfinity));

    public static double GetStep(DependencyObject d) => (double)d.GetValue(StepProperty);
    public static void SetStep(DependencyObject d, double value) => d.SetValue(StepProperty, value);
    public static double GetMinimum(DependencyObject d) => (double)d.GetValue(MinimumProperty);
    public static void SetMinimum(DependencyObject d, double value) => d.SetValue(MinimumProperty, value);
    public static double GetMaximum(DependencyObject d) => (double)d.GetValue(MaximumProperty);
    public static void SetMaximum(DependencyObject d, double value) => d.SetValue(MaximumProperty, value);


    /// <summary>A label that forwards the mouse wheel to a numeric field: hovering "Vyhlazení" and scrolling
    /// changes the field's value, same as scrolling over the field itself.</summary>
    public static readonly DependencyProperty TargetProperty = DependencyProperty.RegisterAttached(
        "Target", typeof(TextBox), typeof(NumericScrub), new PropertyMetadata(null, OnTargetChanged));

    public static TextBox? GetTarget(DependencyObject d) => (TextBox?)d.GetValue(TargetProperty);
    public static void SetTarget(DependencyObject d, TextBox? value) => d.SetValue(TargetProperty, value);

    private static void OnTargetChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement element) return;
        element.PreviewMouseWheel -= OnLabelWheel;
        element.Cursor = null;
        if (e.NewValue is null) return;
        element.PreviewMouseWheel += OnLabelWheel;
        element.Cursor = Cursors.SizeWE;
    }

    private static void OnLabelWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is DependencyObject d && GetTarget(d) is { } target)
            e.Handled |= ApplyWheel(target, e.Delta);
    }

    private static void OnBoxWheel(object sender, MouseWheelEventArgs e) =>
        e.Handled |= ApplyWheel((TextBox)sender, e.Delta);

    /// <summary>One wheel notch moves one step (ten with Shift, a tenth with Ctrl).</summary>
    private static bool ApplyWheel(TextBox box, int delta)
    {
        var step = GetStep(box);
        if (step <= 0 || delta == 0 || !box.IsEnabled || box.IsReadOnly) return false;
        if (!DecimalTextConverter.TryParse(box.Text, out var current)) return false;

        var multiplier = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 10 : Keyboard.Modifiers.HasFlag(ModifierKeys.Control) ? 0.1 : 1;
        var notches = Math.Sign(delta);
        var decimals = DecimalsOf(box.Text);
        var value = Math.Clamp(current + notches * step * multiplier, GetMinimum(box), GetMaximum(box));
        value = Math.Round(value, Math.Max(decimals, 3));

        var text = value.ToString("F" + decimals, CultureInfo.CurrentCulture);
        if (box.Text == text) return true;
        box.Text = text;
        box.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
        return true;
    }
    private static readonly DependencyProperty StateProperty = DependencyProperty.RegisterAttached(
        "State", typeof(DragState), typeof(NumericScrub));

    private sealed class DragState
    {
        public Point Start;
        public double StartValue;
        public int Decimals;
        public bool Scrubbing;
    }

    private static void OnStepChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBox box) return;
        box.PreviewMouseLeftButtonDown -= OnMouseDown;
        box.PreviewMouseMove -= OnMouseMove;
        box.PreviewMouseLeftButtonUp -= OnMouseUp;
        box.PreviewMouseWheel -= OnBoxWheel;
        if ((double)e.NewValue <= 0) return;
        box.PreviewMouseLeftButtonDown += OnMouseDown;
        box.PreviewMouseMove += OnMouseMove;
        box.PreviewMouseLeftButtonUp += OnMouseUp;
        box.PreviewMouseWheel += OnBoxWheel;
    }

    private static void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        var box = (TextBox)sender;
        // Already editing: let the caret and text selection work normally.
        if (box.IsKeyboardFocused || !box.IsEnabled || box.IsReadOnly) return;
        if (!DecimalTextConverter.TryParse(box.Text, out var value)) return;

        box.SetValue(StateProperty, new DragState
        {
            Start = e.GetPosition(box),
            StartValue = value,
            Decimals = DecimalsOf(box.Text),
        });
        box.CaptureMouse();
        e.Handled = true;
    }

    private static void OnMouseMove(object sender, MouseEventArgs e)
    {
        var box = (TextBox)sender;
        if (box.GetValue(StateProperty) is not DragState state || e.LeftButton != MouseButtonState.Pressed) return;

        var delta = e.GetPosition(box).X - state.Start.X;
        if (!state.Scrubbing && Math.Abs(delta) < DragThreshold) return;
        state.Scrubbing = true;

        var step = GetStep(box);
        var value = state.StartValue + Math.Round(delta / PixelsPerStep) * step;
        value = Math.Clamp(value, GetMinimum(box), GetMaximum(box));
        value = Math.Round(value, Math.Max(state.Decimals, DecimalsOf(step.ToString(CultureInfo.InvariantCulture))));

        var text = value.ToString("F" + state.Decimals, CultureInfo.CurrentCulture);
        if (box.Text == text) return;
        box.Text = text;
        box.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
    }

    private static void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        var box = (TextBox)sender;
        if (box.GetValue(StateProperty) is not DragState state) return;
        box.ClearValue(StateProperty);
        box.ReleaseMouseCapture();
        e.Handled = true;

        // No drag: it was a click, so go to typing with everything selected.
        if (state.Scrubbing) return;
        box.Focus();
        box.SelectAll();
    }

    private static int DecimalsOf(string text)
    {
        var separator = text.LastIndexOfAny([',', '.']);
        return separator < 0 ? 0 : text.Length - separator - 1;
    }
}
