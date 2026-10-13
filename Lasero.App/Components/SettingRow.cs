using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Markup;

namespace Lasero.App.Components;

/// <summary>
/// One setting: the label (Body) and an optional one-line explanation (Meta, muted) on the left, the control on
/// the right (a switch, a typed number, a value). Rows of one group are separated by a hairline and the last one
/// drops it (<see cref="ShowRule"/>). A row is at least 56 px high so a switch or a field never touches its
/// neighbours. Code only, for the same reason as <see cref="InspectorSection"/>: the control is named by the
/// window that uses it, and a UserControl whose own XAML registers names cannot also host named children.
/// </summary>
[ContentProperty(nameof(Field))]
public sealed class SettingRow : UserControl
{
    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(
        nameof(Title), typeof(string), typeof(SettingRow), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty HintProperty = DependencyProperty.Register(
        nameof(Hint), typeof(string), typeof(SettingRow),
        new PropertyMetadata(string.Empty, (d, e) =>
            ((SettingRow)d)._hint.Visibility = string.IsNullOrWhiteSpace((string?)e.NewValue) ? Visibility.Collapsed : Visibility.Visible));

    public static readonly DependencyProperty FieldProperty = DependencyProperty.Register(
        nameof(Field), typeof(object), typeof(SettingRow), new PropertyMetadata(null));

    public static readonly DependencyProperty ShowRuleProperty = DependencyProperty.Register(
        nameof(ShowRule), typeof(bool), typeof(SettingRow),
        new PropertyMetadata(true, (d, e) => ((SettingRow)d)._frame.BorderThickness = new Thickness(0, 0, 0, (bool)e.NewValue ? 1 : 0)));

    private readonly Border _frame = new() { BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(0, 8, 0, 8) };
    private readonly TextBlock _hint = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0), Visibility = Visibility.Collapsed };

    public SettingRow()
    {
        Focusable = false;
        _frame.SetResourceReference(Border.BorderBrushProperty, "Brush.PanelBorder");
        _hint.SetResourceReference(FrameworkElement.StyleProperty, "FieldLabel");

        var title = new TextBlock { TextWrapping = TextWrapping.Wrap };
        title.SetBinding(TextBlock.TextProperty, new Binding { Source = this, Path = new PropertyPath(TitleProperty) });
        _hint.SetBinding(TextBlock.TextProperty, new Binding { Source = this, Path = new PropertyPath(HintProperty) });
        var labels = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        labels.Children.Add(title);
        labels.Children.Add(_hint);

        var field = new ContentPresenter
        {
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(24, 0, 0, 0),
        };
        field.SetBinding(ContentPresenter.ContentProperty, new Binding { Source = this, Path = new PropertyPath(FieldProperty) });
        Grid.SetColumn(field, 1);

        var grid = new Grid { MinHeight = 40 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(labels);
        grid.Children.Add(field);

        _frame.Child = grid;
        Content = _frame;
    }

    public string Title { get => (string)GetValue(TitleProperty); set => SetValue(TitleProperty, value); }

    public string Hint { get => (string)GetValue(HintProperty); set => SetValue(HintProperty, value); }

    /// <summary>The control: a switch, a number field, a value.</summary>
    public object? Field { get => GetValue(FieldProperty); set => SetValue(FieldProperty, value); }

    /// <summary>The hairline below the row. The last row of a group turns it off.</summary>
    public bool ShowRule { get => (bool)GetValue(ShowRuleProperty); set => SetValue(ShowRuleProperty, value); }
}
