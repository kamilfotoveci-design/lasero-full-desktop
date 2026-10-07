using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace Lasero.Tests;

/// <summary>Visual-tree helpers for the render-level layout assertions.</summary>
internal static class LayoutAssertions
{
    public static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }

    public static T? Ancestor<T>(DependencyObject start) where T : DependencyObject
    {
        for (var current = VisualTreeHelper.GetParent(start); current is not null; current = VisualTreeHelper.GetParent(current))
            if (current is T match) return match;
        return null;
    }

    /// <summary>The element's bounds in the coordinates of <paramref name="relativeTo"/>.</summary>
    public static Rect BoundsIn(FrameworkElement element, Visual relativeTo) =>
        element.TransformToVisual(relativeTo).TransformBounds(new Rect(0, 0, element.ActualWidth, element.ActualHeight));

    public static bool IsShown(UIElement element) => element.IsVisible && element is FrameworkElement { ActualWidth: > 0, ActualHeight: > 0 };

    /// <summary>
    /// Labels that do not fit their box: a visible, non-wrapping TextBlock inside a button whose text is wider than the
    /// space it was given (cut off or trimmed with an ellipsis). A cut-off command name is a defect on a machine screen.
    /// </summary>
    public static List<string> ClippedButtonLabels(DependencyObject root)
    {
        var clipped = new List<string>();
        foreach (var text in Descendants<TextBlock>(root))
        {
            if (!IsShown(text) || text.TextWrapping != TextWrapping.NoWrap || string.IsNullOrWhiteSpace(text.Text)) continue;
            if (Ancestor<ButtonBase>(text) is null) continue;
            var formatted = new FormattedText(
                text.Text, CultureInfo.CurrentUICulture, text.FlowDirection,
                new Typeface(text.FontFamily, text.FontStyle, text.FontWeight, text.FontStretch),
                text.FontSize, Brushes.Black, null, TextOptions.GetTextFormattingMode(text), VisualTreeHelper.GetDpi(text).PixelsPerDip);
            if (formatted.WidthIncludingTrailingWhitespace > text.ActualWidth * 1.04 + 2)
                clipped.Add($"\"{text.Text}\" needs {formatted.WidthIncludingTrailingWhitespace:0} px, has {text.ActualWidth:0}");
        }

        return clipped;
    }

    /// <summary>Visible non-wrapping text wider than its box. Text that opts into trimming is not counted unless asked.</summary>
    public static List<string> ClippedTexts(DependencyObject root, bool includeTrimmed = false) =>
        Descendants<TextBlock>(root)
            .Where(text => IsShown(text) && !string.IsNullOrWhiteSpace(text.Text)
                           && (includeTrimmed || text.TextTrimming == TextTrimming.None) && IsClipped(text))
            .Select(text => $"\"{text.Text}\" has {text.ActualWidth:0} px, needs {Needed(text):0}")
            .ToList();

    /// <summary>The same check for any text (strip messages, header lines), with trimming counted as clipping.</summary>
    public static bool IsClipped(TextBlock text)
    {
        if (text.TextWrapping != TextWrapping.NoWrap) return false;
        var formatted = new FormattedText(
            text.Text, CultureInfo.CurrentUICulture, text.FlowDirection,
            new Typeface(text.FontFamily, text.FontStyle, text.FontWeight, text.FontStretch),
            text.FontSize, Brushes.Black, null, TextOptions.GetTextFormattingMode(text), VisualTreeHelper.GetDpi(text).PixelsPerDip);
        return formatted.WidthIncludingTrailingWhitespace > text.ActualWidth * 1.04 + 2;
    }
    private static double Needed(TextBlock text) => new FormattedText(
        text.Text, CultureInfo.CurrentUICulture, text.FlowDirection,
        new Typeface(text.FontFamily, text.FontStyle, text.FontWeight, text.FontStretch),
        text.FontSize, Brushes.Black, null, TextOptions.GetTextFormattingMode(text), VisualTreeHelper.GetDpi(text).PixelsPerDip).WidthIncludingTrailingWhitespace;
}
