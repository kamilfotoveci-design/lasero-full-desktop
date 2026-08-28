using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Lasero.App.Components;

/// <summary>
/// The product's one state pill. Replaces the ad-hoc Ellipse+TextBlock indicators that were
/// duplicated with slightly different markup in the command bar, the Home device card and the
/// machine panel, and the colour-triggered TextBlock that reported job state in the status strip.
///
/// Presentation only. A pill can report that the machine is in alarm; it never decides what the
/// operator is allowed to do about it.
/// </summary>
public partial class StatusBadge : UserControl
{
    public static readonly DependencyProperty KindProperty = DependencyProperty.Register(
        nameof(Kind), typeof(StatePillKind), typeof(StatusBadge), new PropertyMetadata(StatePillKind.Neutral));

    public static readonly DependencyProperty StatusTextProperty = DependencyProperty.Register(
        nameof(StatusText), typeof(string), typeof(StatusBadge), new PropertyMetadata(string.Empty));

    /// <summary>Optional glyph that distinguishes the state by shape. Left unset, the pill shows a
    /// dot instead — which is correct where the words already carry the distinction.</summary>
    public static readonly DependencyProperty IconDataProperty = DependencyProperty.Register(
        nameof(IconData), typeof(Geometry), typeof(StatusBadge), new PropertyMetadata(null));

    public StatusBadge()
    {
        InitializeComponent();
    }

    public StatePillKind Kind
    {
        get => (StatePillKind)GetValue(KindProperty);
        set => SetValue(KindProperty, value);
    }

    public string StatusText
    {
        get => (string)GetValue(StatusTextProperty);
        set => SetValue(StatusTextProperty, value);
    }

    public Geometry? IconData
    {
        get => (Geometry?)GetValue(IconDataProperty);
        set => SetValue(IconDataProperty, value);
    }
}
