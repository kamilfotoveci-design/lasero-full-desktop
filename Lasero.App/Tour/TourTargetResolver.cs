using System.Windows;
using System.Windows.Automation;
using System.Windows.Media;

namespace Lasero.App.Tour;

/// <summary>
/// Finds a tour target by name in the live visual tree. A target is addressed by x:Name or
/// AutomationProperties.AutomationId, never by coordinates, so the spotlight survives any layout, window
/// size or DPI. The first id of a step that resolves to an element that is actually shown wins.
/// </summary>
public static class TourTargetResolver
{
    public static FrameworkElement? Find(DependencyObject root, IEnumerable<string> ids)
    {
        foreach (var id in ids)
        {
            var match = FindById(root, id);
            if (match is not null) return match;
        }

        return null;
    }

    /// <summary>First element named <paramref name="id"/> that is visible and has a size, in visual order.
    /// A collapsed screen keeps its controls in the tree, so visibility has to be checked, not just the name.</summary>
    public static FrameworkElement? FindById(DependencyObject root, string id)
    {
        if (root is FrameworkElement fe && IsMatch(fe, id) && IsShown(fe)) return fe;

        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var found = FindById(VisualTreeHelper.GetChild(root, i), id);
            if (found is not null) return found;
        }

        return null;
    }

    /// <summary>Every element in the tree carrying this id, shown or not; for tests that need to know the
    /// id is declared exactly where it is expected.</summary>
    public static IReadOnlyList<FrameworkElement> FindAllById(DependencyObject root, string id)
    {
        var result = new List<FrameworkElement>();
        Collect(root, id, result);
        return result;
    }

    private static void Collect(DependencyObject node, string id, List<FrameworkElement> result)
    {
        if (node is FrameworkElement fe && IsMatch(fe, id)) result.Add(fe);
        var count = VisualTreeHelper.GetChildrenCount(node);
        for (var i = 0; i < count; i++) Collect(VisualTreeHelper.GetChild(node, i), id, result);
    }

    private static bool IsMatch(FrameworkElement element, string id) =>
        element.Name == id || AutomationProperties.GetAutomationId(element) == id;

    private static bool IsShown(FrameworkElement element) =>
        element.IsVisible && element.ActualWidth > 0 && element.ActualHeight > 0;

    /// <summary>The target's bounds in <paramref name="relativeTo"/>'s coordinate space (any two visuals of
    /// one window share a root), or null when the two are not in the same tree.</summary>
    public static Rect? BoundsIn(FrameworkElement target, UIElement relativeTo)
    {
        try
        {
            var transform = target.TransformToVisual(relativeTo);
            return transform.TransformBounds(new Rect(0, 0, target.ActualWidth, target.ActualHeight));
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }
}
