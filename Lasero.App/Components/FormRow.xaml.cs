using System.Windows;
using System.Windows.Controls;

namespace Lasero.App.Components;

/// <summary>Muted label left, value right, one row. See FormRow.xaml.</summary>
public partial class FormRow : UserControl
{
    public static readonly DependencyProperty LabelProperty = DependencyProperty.Register(
        nameof(Label), typeof(string), typeof(FormRow), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(object), typeof(FormRow), new PropertyMetadata(null));

    public static readonly DependencyProperty ShowRuleProperty = DependencyProperty.Register(
        nameof(ShowRule), typeof(bool), typeof(FormRow),
        new PropertyMetadata(false, (d, e) => ((FormRow)d).Rule.BorderThickness = new Thickness(0, 0, 0, (bool)e.NewValue ? 1 : 0)));

    public FormRow() => InitializeComponent();

    public string Label { get => (string)GetValue(LabelProperty); set => SetValue(LabelProperty, value); }

    /// <summary>A string, or any element (a status badge).</summary>
    public object? Value { get => GetValue(ValueProperty); set => SetValue(ValueProperty, value); }

    public bool ShowRule { get => (bool)GetValue(ShowRuleProperty); set => SetValue(ShowRuleProperty, value); }
}
