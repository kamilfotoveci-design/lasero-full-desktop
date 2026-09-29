using System.Windows;
using System.Windows.Controls;

namespace Lasero.App.Components;

/// <summary>
/// Renders the original LASERO assistant artwork at an explicit integer layout size. All call sites
/// share one cached bitmap resource, so changing the canonical artwork changes every avatar.
/// </summary>
public partial class LaseroAvatar : UserControl
{
    public static readonly DependencyProperty SizeProperty = DependencyProperty.Register(
        nameof(Size), typeof(double), typeof(LaseroAvatar),
        new FrameworkPropertyMetadata(24d, FrameworkPropertyMetadataOptions.AffectsMeasure),
        value => value is double size && double.IsFinite(size) && size > 0 && size == Math.Round(size));

    public double Size
    {
        get => (double)GetValue(SizeProperty);
        set => SetValue(SizeProperty, value);
    }

    public LaseroAvatar()
    {
        InitializeComponent();
    }
}
