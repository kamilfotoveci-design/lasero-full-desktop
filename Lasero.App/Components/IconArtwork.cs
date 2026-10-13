using System.Windows;
using System.Windows.Media;

namespace Lasero.App.Components;

/// <summary>
/// Rendering hints stored on a shared icon geometry. This keeps the existing semantic Glyph keys
/// and all their call sites intact while allowing size-specific, filled Fluent artwork.
/// </summary>
public static class IconArtwork
{
    public static readonly DependencyProperty IsFilledProperty = DependencyProperty.RegisterAttached(
        "IsFilled", typeof(bool), typeof(IconArtwork), new PropertyMetadata(false));

    public static readonly DependencyProperty NativeSizeProperty = DependencyProperty.RegisterAttached(
        "NativeSize", typeof(double), typeof(IconArtwork), new PropertyMetadata(24d));

    public static readonly DependencyProperty Variant16Property = DependencyProperty.RegisterAttached(
        "Variant16", typeof(Geometry), typeof(IconArtwork), new PropertyMetadata(null));

    public static readonly DependencyProperty Variant20Property = DependencyProperty.RegisterAttached(
        "Variant20", typeof(Geometry), typeof(IconArtwork), new PropertyMetadata(null));

    public static bool GetIsFilled(DependencyObject element) => (bool)element.GetValue(IsFilledProperty);
    public static void SetIsFilled(DependencyObject element, bool value) => element.SetValue(IsFilledProperty, value);

    public static double GetNativeSize(DependencyObject element) => (double)element.GetValue(NativeSizeProperty);
    public static void SetNativeSize(DependencyObject element, double value) => element.SetValue(NativeSizeProperty, value);

    public static Geometry? GetVariant16(DependencyObject element) => (Geometry?)element.GetValue(Variant16Property);
    public static void SetVariant16(DependencyObject element, Geometry? value) => element.SetValue(Variant16Property, value);

    public static Geometry? GetVariant20(DependencyObject element) => (Geometry?)element.GetValue(Variant20Property);
    public static void SetVariant20(DependencyObject element, Geometry? value) => element.SetValue(Variant20Property, value);
}
