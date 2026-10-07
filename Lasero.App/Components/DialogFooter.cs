using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Lasero.App.Components;

/// <summary>
/// The footer of every dialog and window with a confirm step. Children are the buttons, in reading order: Cancel
/// first (left), any secondary choice, the primary last (right). They are right aligned, 8 px apart, vertically
/// centred in a band that is at least Layout.DialogFooterHeight (72) high, on the field gray with a hairline above.
/// A child marked <see cref="IsLeadingProperty"/> (a hint sentence, an error message, a secondary tool such as
/// "Zkontrolovat oblast") sits at the left edge and wraps in the room the buttons leave. A collapsed button takes no
/// space and leaves no gap. Primary is graphite, a destructive primary is red; that choice is the button style's,
/// the footer only places them.
/// </summary>
public sealed class DialogFooter : Panel
{
    public const double Spacing = 8;
    public const double SidePadding = 24;
    public const double VerticalPadding = 16;
    public const double MinHeight = 72; // Layout.DialogFooterHeight

    public static readonly DependencyProperty ShowHairlineProperty = DependencyProperty.Register(
        nameof(ShowHairline), typeof(bool), typeof(DialogFooter),
        new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty IsLeadingProperty = DependencyProperty.RegisterAttached(
        "IsLeading", typeof(bool), typeof(DialogFooter),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsParentMeasure));

    public static bool GetIsLeading(DependencyObject element) => (bool)element.GetValue(IsLeadingProperty);

    public static void SetIsLeading(DependencyObject element, bool value) => element.SetValue(IsLeadingProperty, value);

    public DialogFooter()
    {
        SnapsToDevicePixels = true;
        UseLayoutRounding = true;
    }

    public bool ShowHairline { get => (bool)GetValue(ShowHairlineProperty); set => SetValue(ShowHairlineProperty, value); }

    protected override Size MeasureOverride(Size availableSize)
    {
        var rightWidth = 0.0;
        var height = 0.0;
        var visibleRight = 0;
        foreach (UIElement child in InternalChildren)
        {
            if (GetIsLeading(child)) continue;
            child.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            if (child.Visibility == Visibility.Collapsed) continue;
            rightWidth += child.DesiredSize.Width;
            height = Math.Max(height, child.DesiredSize.Height);
            visibleRight++;
        }

        if (visibleRight > 1) rightWidth += Spacing * (visibleRight - 1);

        var total = double.IsInfinity(availableSize.Width) ? double.PositiveInfinity : availableSize.Width;
        var leadingRoom = double.IsInfinity(total)
            ? double.PositiveInfinity
            : Math.Max(0, total - 2 * SidePadding - rightWidth - (visibleRight > 0 ? 2 * Spacing : 0));
        var leadingWidth = 0.0;
        foreach (UIElement child in InternalChildren)
        {
            if (!GetIsLeading(child)) continue;
            child.Measure(new Size(leadingRoom, double.PositiveInfinity));
            if (child.Visibility == Visibility.Collapsed) continue;
            leadingWidth = Math.Max(leadingWidth, child.DesiredSize.Width);
            height = Math.Max(height, child.DesiredSize.Height);
        }

        var measuredWidth = leadingWidth + rightWidth + 2 * SidePadding + (leadingWidth > 0 && rightWidth > 0 ? 2 * Spacing : 0);
        return new Size(double.IsInfinity(total) ? measuredWidth : total, Math.Max(MinHeight, height + 2 * VerticalPadding));
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var x = finalSize.Width - SidePadding;
        for (var i = InternalChildren.Count - 1; i >= 0; i--)
        {
            var child = InternalChildren[i];
            if (child.Visibility == Visibility.Collapsed || GetIsLeading(child)) continue;
            var size = child.DesiredSize;
            x -= size.Width;
            child.Arrange(new Rect(Math.Max(0, x), (finalSize.Height - size.Height) / 2, size.Width, size.Height));
            x -= Spacing;
        }

        foreach (UIElement child in InternalChildren)
        {
            if (child.Visibility == Visibility.Collapsed || !GetIsLeading(child)) continue;
            var size = child.DesiredSize;
            child.Arrange(new Rect(SidePadding, (finalSize.Height - size.Height) / 2, size.Width, size.Height));
        }

        return finalSize;
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        var fill = Background ?? TryFindResource("Brush.Field") as Brush;
        if (fill is not null) dc.DrawRectangle(fill, null, new Rect(0, 0, ActualWidth, ActualHeight));
        if (ShowHairline && TryFindResource("Brush.PanelBorder") is Brush rule)
            dc.DrawRectangle(rule, null, new Rect(0, 0, ActualWidth, 1));
    }
}
