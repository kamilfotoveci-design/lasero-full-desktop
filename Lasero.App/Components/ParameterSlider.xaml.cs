using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Lasero.App.Components;

/// <summary>
/// One process parameter — power, speed, and anything else with a range and a unit. The numeric
/// field and the slider are two views of the same bound value; the field is what production work
/// relies on, so it is never replaced by the slider alone.
/// </summary>
public partial class ParameterSlider : UserControl
{
    public static readonly DependencyProperty LabelProperty = DependencyProperty.Register(
        nameof(Label), typeof(string), typeof(ParameterSlider), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty UnitProperty = DependencyProperty.Register(
        nameof(Unit), typeof(string), typeof(ParameterSlider), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(ParameterSlider),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnRangeInputChanged));

    public static readonly DependencyProperty MinimumProperty = DependencyProperty.Register(
        nameof(Minimum), typeof(double), typeof(ParameterSlider), new PropertyMetadata(0d, OnRangeInputChanged));

    public static readonly DependencyProperty MaximumProperty = DependencyProperty.Register(
        nameof(Maximum), typeof(double), typeof(ParameterSlider), new PropertyMetadata(100d, OnRangeInputChanged));

    /// <summary>
    /// The range the slider actually spans, which is the configured range widened to include the
    /// current value.
    ///
    /// Without this the slider silently owned the number: typing a feed rate above the slider's
    /// maximum let the Slider coerce it on the way back through the two-way binding, so the value
    /// the operator typed was not the value the layer ended up with — and a laser job ran at a speed
    /// nobody chose. The field is the source of truth, so the track stretches to whatever it says.
    /// </summary>
    public static readonly DependencyProperty EffectiveMinimumProperty = DependencyProperty.Register(
        nameof(EffectiveMinimum), typeof(double), typeof(ParameterSlider), new PropertyMetadata(0d));

    public static readonly DependencyProperty EffectiveMaximumProperty = DependencyProperty.Register(
        nameof(EffectiveMaximum), typeof(double), typeof(ParameterSlider), new PropertyMetadata(100d));

    public double EffectiveMinimum
    {
        get => (double)GetValue(EffectiveMinimumProperty);
        private set => SetValue(EffectiveMinimumProperty, value);
    }

    public double EffectiveMaximum
    {
        get => (double)GetValue(EffectiveMaximumProperty);
        private set => SetValue(EffectiveMaximumProperty, value);
    }

    private static void OnRangeInputChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((ParameterSlider)d).RefreshEffectiveRange();

    private void RefreshEffectiveRange()
    {
        var value = double.IsFinite(Value) ? Value : Minimum;
        var low = Math.Min(Minimum, value);
        var high = Math.Max(Maximum, value);

        // A degenerate range makes Slider place the thumb unpredictably, so keep it non-empty.
        if (high <= low) high = low + 1;

        EffectiveMinimum = low;
        EffectiveMaximum = high;
    }

    public static readonly DependencyProperty TickFrequencyProperty = DependencyProperty.Register(
        nameof(TickFrequency), typeof(double), typeof(ParameterSlider), new PropertyMetadata(1d));

    public static readonly DependencyProperty SmallChangeProperty = DependencyProperty.Register(
        nameof(SmallChange), typeof(double), typeof(ParameterSlider), new PropertyMetadata(1d));

    public static readonly DependencyProperty LargeChangeProperty = DependencyProperty.Register(
        nameof(LargeChange), typeof(double), typeof(ParameterSlider), new PropertyMetadata(10d));

    public static readonly DependencyProperty FieldWidthProperty = DependencyProperty.Register(
        nameof(FieldWidth), typeof(double), typeof(ParameterSlider), new PropertyMetadata(96d));

    public string Label
    {
        get => (string)GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }

    public string Unit
    {
        get => (string)GetValue(UnitProperty);
        set => SetValue(UnitProperty, value);
    }

    public double Value
    {
        get => (double)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public double Minimum
    {
        get => (double)GetValue(MinimumProperty);
        set => SetValue(MinimumProperty, value);
    }

    public double Maximum
    {
        get => (double)GetValue(MaximumProperty);
        set => SetValue(MaximumProperty, value);
    }

    public double TickFrequency
    {
        get => (double)GetValue(TickFrequencyProperty);
        set => SetValue(TickFrequencyProperty, value);
    }

    public double SmallChange
    {
        get => (double)GetValue(SmallChangeProperty);
        set => SetValue(SmallChangeProperty, value);
    }

    public double LargeChange
    {
        get => (double)GetValue(LargeChangeProperty);
        set => SetValue(LargeChangeProperty, value);
    }

    public double FieldWidth
    {
        get => (double)GetValue(FieldWidthProperty);
        set => SetValue(FieldWidthProperty, value);
    }

    public ParameterSlider()
    {
        InitializeComponent();
    }

    // The value field commits on Enter and reverts on Esc through the app-wide field behaviour
    // (Lasero.App.Input.InteractionBehaviors), the same as every other numeric field.
}
