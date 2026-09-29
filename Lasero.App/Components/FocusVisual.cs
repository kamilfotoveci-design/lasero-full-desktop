using System.Windows;

namespace Lasero.App.Components;

/// <summary>
/// True on the element that holds keyboard focus only when focus arrived from the keyboard.
///
/// <c>IsKeyboardFocused</c> cannot tell a Tab from a click, so every template that drew its ring from
/// it kept a cobalt outline on a control the mouse had just pressed, until focus moved elsewhere.
/// This is the equivalent of CSS :focus-visible: the ring templates trigger on it instead, and
/// <see cref="Lasero.App.Input.InteractionBehaviors"/> keeps it current from the most recent input
/// device. Text inputs deliberately keep triggering on plain focus, as text inputs do everywhere.
/// </summary>
public static class FocusVisual
{
    private static readonly DependencyPropertyKey IsVisiblePropertyKey =
        DependencyProperty.RegisterAttachedReadOnly(
            "IsVisible", typeof(bool), typeof(FocusVisual), new FrameworkPropertyMetadata(false));

    public static readonly DependencyProperty IsVisibleProperty = IsVisiblePropertyKey.DependencyProperty;

    public static bool GetIsVisible(DependencyObject element) => (bool)element.GetValue(IsVisibleProperty);

    internal static void SetIsVisible(DependencyObject element, bool value) =>
        element.SetValue(IsVisiblePropertyKey, value);
}
