using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Lasero.App.Components;

/// <summary>One numbered step in Home's "Jak začít" strip. Three of these sit side by side, so the
/// number badge, title, body and icon geometry live here once instead of being repeated per card.</summary>
public partial class StepCard : UserControl
{
    public static readonly DependencyProperty StepNumberProperty = DependencyProperty.Register(
        nameof(StepNumber), typeof(string), typeof(StepCard), new PropertyMetadata("1"));

    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(
        nameof(Title), typeof(string), typeof(StepCard), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty DescriptionProperty = DependencyProperty.Register(
        nameof(Description), typeof(string), typeof(StepCard), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty IconDataProperty = DependencyProperty.Register(
        nameof(IconData), typeof(Geometry), typeof(StepCard), new PropertyMetadata(null));

    public StepCard() => InitializeComponent();

    public string StepNumber
    {
        get => (string)GetValue(StepNumberProperty);
        set => SetValue(StepNumberProperty, value);
    }

    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public string Description
    {
        get => (string)GetValue(DescriptionProperty);
        set => SetValue(DescriptionProperty, value);
    }

    public Geometry? IconData
    {
        get => (Geometry?)GetValue(IconDataProperty);
        set => SetValue(IconDataProperty, value);
    }
}
