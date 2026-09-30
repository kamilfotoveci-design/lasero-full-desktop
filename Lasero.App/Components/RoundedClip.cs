using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Lasero.App.Components;

/// <summary>
/// Clips a child to the rounded inner edge of the <see cref="Border"/> that hosts it.
///
/// WPF does not do this on its own: a Border's <c>CornerRadius</c> rounds only the Border's own
/// background and stroke, and <c>ClipToBounds</c> clips to the rectangle. So a thumbnail, a raised
/// well or a row hover wash placed inside a rounded card paints straight over the rounded corners
/// and the stroke, and the card reads as square. Set <c>RoundedClip.ToParentBorder="True"</c> on the
/// card's direct child and the child follows the card's radius, inset by its border thickness so the
/// stroke stays whole.
/// </summary>
public static class RoundedClip
{
    public static readonly DependencyProperty ToParentBorderProperty = DependencyProperty.RegisterAttached(
        "ToParentBorder", typeof(bool), typeof(RoundedClip), new PropertyMetadata(false, OnToParentBorderChanged));

    public static bool GetToParentBorder(DependencyObject element) => (bool)element.GetValue(ToParentBorderProperty);

    public static void SetToParentBorder(DependencyObject element, bool value) =>
        element.SetValue(ToParentBorderProperty, value);

    private static void OnToParentBorderChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement element) return;

        element.SizeChanged -= OnElementSizeChanged;
        element.Loaded -= OnElementLoaded;

        if (e.NewValue is true)
        {
            element.SizeChanged += OnElementSizeChanged;
            element.Loaded += OnElementLoaded;
            Update(element);
        }
        else
        {
            element.ClearValue(UIElement.ClipProperty);
        }
    }

    private static void OnElementSizeChanged(object sender, SizeChangedEventArgs e) => Update((FrameworkElement)sender);

    private static void OnElementLoaded(object sender, RoutedEventArgs e) => Update((FrameworkElement)sender);

    private static void Update(FrameworkElement element)
    {
        if (element.ActualWidth <= 0 || element.ActualHeight <= 0) return;
        if ((element.Parent ?? VisualTreeHelper.GetParent(element)) is not Border border) return;

        element.Clip = BuildClip(new Size(element.ActualWidth, element.ActualHeight), border.CornerRadius, border.BorderThickness);
    }

    /// <summary>The rounded rectangle of <paramref name="size"/> whose corners follow the parent's outer
    /// <paramref name="outer"/> radii reduced by the border <paramref name="thickness"/> on each side.
    /// Public so the geometry is testable without a window.</summary>
    public static Geometry BuildClip(Size size, CornerRadius outer, Thickness thickness)
    {
        var tl = Inner(outer.TopLeft, thickness.Left, thickness.Top);
        var tr = Inner(outer.TopRight, thickness.Right, thickness.Top);
        var br = Inner(outer.BottomRight, thickness.Right, thickness.Bottom);
        var bl = Inner(outer.BottomLeft, thickness.Left, thickness.Bottom);

        var w = size.Width;
        var h = size.Height;
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(new Point(tl.X, 0), isFilled: true, isClosed: true);
            ctx.LineTo(new Point(w - tr.X, 0), false, false);
            if (tr.X > 0 && tr.Y > 0) ctx.ArcTo(new Point(w, tr.Y), tr.ToSize(), 0, false, SweepDirection.Clockwise, false, false);
            ctx.LineTo(new Point(w, h - br.Y), false, false);
            if (br.X > 0 && br.Y > 0) ctx.ArcTo(new Point(w - br.X, h), br.ToSize(), 0, false, SweepDirection.Clockwise, false, false);
            ctx.LineTo(new Point(bl.X, h), false, false);
            if (bl.X > 0 && bl.Y > 0) ctx.ArcTo(new Point(0, h - bl.Y), bl.ToSize(), 0, false, SweepDirection.Clockwise, false, false);
            ctx.LineTo(new Point(0, tl.Y), false, false);
            if (tl.X > 0 && tl.Y > 0) ctx.ArcTo(new Point(tl.X, 0), tl.ToSize(), 0, false, SweepDirection.Clockwise, false, false);
        }

        geometry.Freeze();
        return geometry;
    }

    /// <summary>Inner corner radius for one corner: horizontal and vertical radii are each reduced by the
    /// border edge they run along, never below zero.</summary>
    public static Vector InnerRadius(double outer, double horizontalEdge, double verticalEdge) =>
        Inner(outer, horizontalEdge, verticalEdge);

    private static Vector Inner(double outer, double horizontalEdge, double verticalEdge) =>
        new(Math.Max(0, outer - horizontalEdge), Math.Max(0, outer - verticalEdge));

    private static Size ToSize(this Vector v) => new(v.X, v.Y);
}
