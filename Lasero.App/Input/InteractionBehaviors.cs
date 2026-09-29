using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using Lasero.App.Components;

namespace Lasero.App.Input;

/// <summary>
/// App-wide interaction behaviour that has to be the same everywhere, registered once at startup as
/// class handlers so no individual view can forget it. The decisions live in
/// <see cref="InteractionRules"/>; this file is only the WPF glue. See docs/interaction-rules.md.
/// </summary>
public static class InteractionBehaviors
{
    /// <summary>One tooltip rhythm for the whole app (docs/interaction-rules.md 1.11).</summary>
    public const int TooltipInitialDelayMs = 500;
    public const int TooltipBetweenDelayMs = 100;
    public const int TooltipShowDurationMs = 8000;

    private static bool _registered;

    public static void Register()
    {
        if (_registered) return;
        _registered = true;

        // Tooltips: the Windows default varies with user settings, so two identical icons could
        // reveal their label at different speeds on different machines.
        ToolTipService.InitialShowDelayProperty.OverrideMetadata(
            typeof(DependencyObject), new FrameworkPropertyMetadata(TooltipInitialDelayMs));
        ToolTipService.BetweenShowDelayProperty.OverrideMetadata(
            typeof(DependencyObject), new FrameworkPropertyMetadata(TooltipBetweenDelayMs));
        ToolTipService.ShowDurationProperty.OverrideMetadata(
            typeof(DependencyObject), new FrameworkPropertyMetadata(TooltipShowDurationMs));

        // Keyboard-only focus ring.
        EventManager.RegisterClassHandler(typeof(UIElement), Keyboard.GotKeyboardFocusEvent,
            new KeyboardFocusChangedEventHandler(OnGotKeyboardFocus), handledEventsToo: true);
        EventManager.RegisterClassHandler(typeof(UIElement), Keyboard.LostKeyboardFocusEvent,
            new KeyboardFocusChangedEventHandler(OnLostKeyboardFocus), handledEventsToo: true);

        // Fields that commit on LostFocus: Enter commits, Esc reverts, a click selects everything.
        EventManager.RegisterClassHandler(typeof(TextBox), UIElement.PreviewKeyDownEvent,
            new KeyEventHandler(OnTextBoxPreviewKeyDown));
        EventManager.RegisterClassHandler(typeof(TextBox), UIElement.PreviewMouseLeftButtonDownEvent,
            new MouseButtonEventHandler(OnTextBoxPreviewMouseDown));

        // Secondary windows: Esc cancels.
        EventManager.RegisterClassHandler(typeof(Window), UIElement.KeyDownEvent,
            new KeyEventHandler(OnWindowKeyDown));
    }

    // ------------------------------------------------------------------ focus ring

    private static void OnGotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (!ReferenceEquals(sender, e.NewFocus) || sender is not DependencyObject target) return;
        FocusVisual.SetIsVisible(target, InputManager.Current.MostRecentInputDevice is KeyboardDevice);
    }

    private static void OnLostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (!ReferenceEquals(sender, e.OldFocus) || sender is not DependencyObject target) return;
        FocusVisual.SetIsVisible(target, false);
    }

    // ------------------------------------------------------------------ numeric / deferred fields

    private static BindingExpression? DeferredTextBinding(TextBox field)
    {
        var expression = field.GetBindingExpression(TextBox.TextProperty);
        return expression is not null && InteractionRules.IsDeferredTrigger(expression.ParentBinding.UpdateSourceTrigger)
            ? expression
            : null;
    }

    private static void OnTextBoxPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is TextBox field && TryHandleFieldKey(field, e.Key)) e.Handled = true;
    }

    /// <summary>
    /// Enter commits a deferred field and Esc reverts an uncommitted edit. Returns true when the key
    /// was consumed. Internal so the behaviour can be exercised against a real TextBox and binding.
    /// </summary>
    internal static bool TryHandleFieldKey(TextBox field, Key key)
    {
        if (key is not (Key.Enter or Key.Escape)) return false;
        var expression = DeferredTextBinding(field);
        if (expression is null) return false;

        switch (InteractionRules.ForFieldKey(key, field.AcceptsReturn, deferredBinding: true, expression.IsDirty))
        {
            case FieldKeyAction.Commit:
                expression.UpdateSource();
                // Text the model refused goes back to what the model holds, instead of the field
                // showing a value that was never applied.
                if (expression.HasError) Revert(field, expression);
                // A dialog with a default button still needs this Enter to reach it, so the field
                // only claims the key inside the main window, where nothing else would use it.
                if (Window.GetWindow(field) is MainWindow)
                {
                    field.SelectAll();
                    return true;
                }
                return false;
            case FieldKeyAction.Revert:
                Revert(field, expression);
                return true;
            default:
                return false;
        }
    }

    private static void Revert(TextBox field, BindingExpression expression)
    {
        Validation.ClearInvalid(expression);
        expression.UpdateTarget();
        field.SelectAll();
    }

    private static void OnTextBoxPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not TextBox { IsKeyboardFocusWithin: false, IsReadOnly: false, AcceptsReturn: false } field) return;
        if (DeferredTextBinding(field) is null) return;

        field.Focus();
        field.SelectAll();
        e.Handled = true;
    }

    // ------------------------------------------------------------------ secondary windows

    private static void OnWindowKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || e.Handled || sender is not Window window) return;
        if (!InteractionRules.WindowClosesOnEscape(window.GetType())) return;

        e.Handled = true;
        window.Close();
    }
}
