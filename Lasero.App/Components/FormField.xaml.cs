using System.Windows;
using System.Windows.Controls;

namespace Lasero.App.Components;

/// <summary>Label above, control, helper text below. See FormField.xaml.</summary>
public partial class FormField : UserControl
{
    public static readonly DependencyProperty LabelProperty = DependencyProperty.Register(
        nameof(Label), typeof(string), typeof(FormField), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty HelperProperty = DependencyProperty.Register(
        nameof(Helper), typeof(string), typeof(FormField),
        new PropertyMetadata(string.Empty, (d, e) =>
            ((FormField)d).HelperText.Visibility = string.IsNullOrWhiteSpace((string?)e.NewValue) ? Visibility.Collapsed : Visibility.Visible));

    public static readonly DependencyProperty FieldProperty = DependencyProperty.Register(
        nameof(Field), typeof(object), typeof(FormField), new PropertyMetadata(null));

    public FormField() => InitializeComponent();

    public string Label { get => (string)GetValue(LabelProperty); set => SetValue(LabelProperty, value); }

    public string Helper { get => (string)GetValue(HelperProperty); set => SetValue(HelperProperty, value); }

    /// <summary>The input: a TextBox, ComboBox, slider.</summary>
    public object? Field { get => GetValue(FieldProperty); set => SetValue(FieldProperty, value); }
}
