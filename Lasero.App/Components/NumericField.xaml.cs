using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;

namespace Lasero.App.Components;

/// <summary>A typed number with an integrated unit. See NumericField.xaml.</summary>
public partial class NumericField : UserControl
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(NumericField),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    public static readonly DependencyProperty UnitProperty = DependencyProperty.Register(
        nameof(Unit), typeof(string), typeof(NumericField), new PropertyMetadata(string.Empty));

    public NumericField()
    {
        InitializeComponent();
        // Enter commits the typed value the way leaving the field does, and entering the field selects
        // its content so a new value replaces the old one in one keystroke sequence.
        Input.PreviewKeyDown += OnInputKeyDown;
        Input.GotKeyboardFocus += (_, _) => Input.SelectAll();
        Input.PreviewMouseLeftButtonDown += OnInputMouseDown;
    }

    public double Value
    {
        get => (double)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public string Unit
    {
        get => (string)GetValue(UnitProperty);
        set => SetValue(UnitProperty, value);
    }

    private void OnInputKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Enter or Key.Return)) return;
        Input.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
        Input.SelectAll();
        e.Handled = true;
    }

    private void OnInputMouseDown(object sender, MouseButtonEventArgs e)
    {
        // The first click only focuses the field and selects everything; a click into an already focused
        // field places the caret as usual.
        if (Input.IsKeyboardFocusWithin) return;
        Input.Focus();
        e.Handled = true;
    }
}
