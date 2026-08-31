using System.Windows;

namespace Lasero.App.Components;

/// <summary>
/// Opt-outs for the shared Button template, for the cases where its default is right everywhere else.
/// </summary>
public static class ButtonChrome
{
    /// <summary>
    /// Keeps a disabled button's surface bare.
    /// <para>
    /// The template paints a flat disabled surface so an unavailable control keeps a legible shape
    /// instead of looking half-rendered. That is right for a button that has a surface to begin with.
    /// On a ghost button it is backwards: idle draws nothing, so the only boxes in the row end up
    /// being the commands you cannot use — in the editor strip, Zpět and Znovu were the loudest
    /// things on screen with no history to undo. Where this is set, disabled is told by the dimmed
    /// icon and caption alone.
    /// </para>
    /// </summary>
    public static readonly DependencyProperty SuppressDisabledSurfaceProperty =
        DependencyProperty.RegisterAttached(
            "SuppressDisabledSurface",
            typeof(bool),
            typeof(ButtonChrome),
            new FrameworkPropertyMetadata(false));

    public static void SetSuppressDisabledSurface(DependencyObject element, bool value) =>
        element.SetValue(SuppressDisabledSurfaceProperty, value);

    public static bool GetSuppressDisabledSurface(DependencyObject element) =>
        (bool)element.GetValue(SuppressDisabledSurfaceProperty);
}
