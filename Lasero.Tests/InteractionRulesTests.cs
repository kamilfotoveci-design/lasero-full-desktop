using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Text.RegularExpressions;
using Lasero.App;
using Lasero.App.Input;

namespace Lasero.Tests;

/// <summary>
/// Pins docs/interaction-rules.md: the key-routing tables are pure functions, so the rules can be
/// asserted without a window; the few behaviours that need WPF run against a real TextBox on an STA
/// thread; the theme-level rules are pinned as source assertions, the way ThemeTokenTests does.
/// </summary>
public sealed class InteractionRulesTests
{
    // ------------------------------------------------------------------ Esc peels one layer

    [Fact]
    public void CanvasEscapeTakesTheInnermostLayerFirst()
    {
        var everything = new CanvasEscapeState(true, true, true, true, true, true);
        Assert.Equal(CanvasEscapeAction.CancelGesture, InteractionRules.ForCanvasEscape(everything));

        Assert.Equal(CanvasEscapeAction.ExitInlineTextEdit,
            InteractionRules.ForCanvasEscape(everything with { GestureActive = false }));
        Assert.Equal(CanvasEscapeAction.ExitNodeEdit,
            InteractionRules.ForCanvasEscape(everything with { GestureActive = false, InlineTextEditing = false }));
        Assert.Equal(CanvasEscapeAction.CancelPathNode,
            InteractionRules.ForCanvasEscape(new CanvasEscapeState(false, false, false, true, true, true)));
        Assert.Equal(CanvasEscapeAction.ResetTool,
            InteractionRules.ForCanvasEscape(new CanvasEscapeState(false, false, false, false, true, true)));
        Assert.Equal(CanvasEscapeAction.ClearSelection,
            InteractionRules.ForCanvasEscape(new CanvasEscapeState(false, false, false, false, false, true)));
    }

    [Fact]
    public void CanvasEscapeWithNothingToPeelIsLeftForTheWindow()
    {
        // This is the KAMIL bug: the canvas used to swallow Esc even with nothing to do, so the
        // assistant could never be minimized from canvas focus.
        Assert.Equal(CanvasEscapeAction.None,
            InteractionRules.ForCanvasEscape(new CanvasEscapeState(false, false, false, false, false, false)));
    }

    [Fact]
    public void WindowEscapeWorksWhereverFocusIsAndOnlyOnTheDesigner()
    {
        // Focus on a button or the inspector: the canvas layers still respond.
        Assert.Equal(WindowEscapeAction.CanvasLayers,
            InteractionRules.ForWindowEscape(true, false, canvasHasEscapableState: true, assistantOpen: true));
        // Nothing left on the canvas: the assistant steps back.
        Assert.Equal(WindowEscapeAction.MinimizeAssistant,
            InteractionRules.ForWindowEscape(true, false, canvasHasEscapableState: false, assistantOpen: true));
        Assert.Equal(WindowEscapeAction.None,
            InteractionRules.ForWindowEscape(true, false, canvasHasEscapableState: false, assistantOpen: false));
        // A field with nothing to revert hands focus back before anything else happens.
        Assert.Equal(WindowEscapeAction.LeaveTextField,
            InteractionRules.ForWindowEscape(true, true, canvasHasEscapableState: true, assistantOpen: true));
        // Other screens have no canvas and no assistant.
        Assert.Equal(WindowEscapeAction.None,
            InteractionRules.ForWindowEscape(false, false, true, true));
    }

    [Fact]
    public void SecondaryWindowsCloseOnEscButSessionWindowsDoNot()
    {
        Assert.False(InteractionRules.WindowClosesOnEscape(typeof(MainWindow)));
        Assert.False(InteractionRules.WindowClosesOnEscape(typeof(LoginWindow)));
        Assert.False(InteractionRules.WindowClosesOnEscape(typeof(OnboardingWindow)));
        Assert.False(InteractionRules.WindowClosesOnEscape(typeof(LaseroDialogWindow)));

        Assert.True(InteractionRules.WindowClosesOnEscape(typeof(SettingsWindow)));
        Assert.True(InteractionRules.WindowClosesOnEscape(typeof(RasterImportWindow)));
        Assert.True(InteractionRules.WindowClosesOnEscape(typeof(BitmapTraceWindow)));
        Assert.True(InteractionRules.WindowClosesOnEscape(typeof(OffsetPathWindow)));
        Assert.True(InteractionRules.WindowClosesOnEscape(typeof(MaterialsWindow)));
        Assert.True(InteractionRules.WindowClosesOnEscape(typeof(DeviceSettingsWindow)));
        Assert.True(InteractionRules.WindowClosesOnEscape(typeof(PreviewWindow)));
    }

    // ------------------------------------------------------------------ numeric fields

    [Theory]
    [InlineData(UpdateSourceTrigger.Default, true)]
    [InlineData(UpdateSourceTrigger.LostFocus, true)]
    [InlineData(UpdateSourceTrigger.Explicit, true)]
    [InlineData(UpdateSourceTrigger.PropertyChanged, false)]
    public void OnlyFieldsThatWaitForLostFocusCanHoldAnUncommittedEdit(UpdateSourceTrigger trigger, bool deferred) =>
        Assert.Equal(deferred, InteractionRules.IsDeferredTrigger(trigger));

    [Fact]
    public void FieldKeysCommitOnEnterAndRevertOnEscOnlyWhenThereIsAnEditToRevert()
    {
        Assert.Equal(FieldKeyAction.Commit, InteractionRules.ForFieldKey(Key.Enter, false, true, false));
        Assert.Equal(FieldKeyAction.Commit, InteractionRules.ForFieldKey(Key.Enter, false, true, true));
        Assert.Equal(FieldKeyAction.Revert, InteractionRules.ForFieldKey(Key.Escape, false, true, true));

        // Esc with nothing to revert must travel on, so it can leave the field or close a dialog.
        Assert.Equal(FieldKeyAction.None, InteractionRules.ForFieldKey(Key.Escape, false, true, false));
        // A multi-line field keeps Enter for the new line, a live field never has anything pending.
        Assert.Equal(FieldKeyAction.None, InteractionRules.ForFieldKey(Key.Enter, true, true, true));
        Assert.Equal(FieldKeyAction.None, InteractionRules.ForFieldKey(Key.Enter, false, false, true));
        Assert.Equal(FieldKeyAction.None, InteractionRules.ForFieldKey(Key.Escape, false, false, true));
        Assert.Equal(FieldKeyAction.None, InteractionRules.ForFieldKey(Key.A, false, true, true));
    }

    private sealed class Model : System.ComponentModel.INotifyPropertyChanged
    {
        private double _value = 10;
        public double Value
        {
            get => _value;
            set
            {
                _value = value;
                PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(Value)));
            }
        }

        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    }

    private static void OnSta(Action body)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { body(); }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) throw new Xunit.Sdk.XunitException(failure.ToString());
    }

    private static TextBox BoundField(Model model, UpdateSourceTrigger trigger)
    {
        var field = new TextBox();
        field.SetBinding(TextBox.TextProperty, new Binding(nameof(Model.Value))
        {
            Source = model,
            Mode = BindingMode.TwoWay,
            UpdateSourceTrigger = trigger,
        });
        return field;
    }

    [Fact]
    public void EnterCommitsTheTypedValueAndTheModelSeesItOnce()
    {
        OnSta(() =>
        {
            var model = new Model();
            var field = BoundField(model, UpdateSourceTrigger.LostFocus);
            field.Text = "42";
            Assert.Equal(10, model.Value); // nothing reaches the model while typing

            InteractionBehaviors.TryHandleFieldKey(field, Key.Enter);

            Assert.Equal(42, model.Value);
        });
    }

    [Fact]
    public void EscRevertsAnUncommittedEditAndIsConsumedOnlyThen()
    {
        OnSta(() =>
        {
            var model = new Model();
            var field = BoundField(model, UpdateSourceTrigger.LostFocus);
            Assert.False(InteractionBehaviors.TryHandleFieldKey(field, Key.Escape)); // nothing to revert

            field.Text = "999";
            Assert.True(InteractionBehaviors.TryHandleFieldKey(field, Key.Escape));

            Assert.Equal("10", field.Text);
            Assert.Equal(10, model.Value);
            Assert.False(InteractionBehaviors.TryHandleFieldKey(field, Key.Escape)); // now it travels on
        });
    }

    [Fact]
    public void EnterWithTextTheModelRefusesPutsTheOldValueBack()
    {
        OnSta(() =>
        {
            var model = new Model();
            var field = BoundField(model, UpdateSourceTrigger.LostFocus);
            field.Text = "not a number";

            InteractionBehaviors.TryHandleFieldKey(field, Key.Enter);

            Assert.Equal(10, model.Value);
            Assert.Equal("10", field.Text);
        });
    }

    [Fact]
    public void LiveFieldsAreNotTouched()
    {
        OnSta(() =>
        {
            var model = new Model();
            var field = BoundField(model, UpdateSourceTrigger.PropertyChanged);
            field.Text = "7";

            Assert.False(InteractionBehaviors.TryHandleFieldKey(field, Key.Escape));
            Assert.Equal("7", field.Text);
        });
    }

    // ------------------------------------------------------------------ shortcuts

    [Theory]
    [InlineData(Key.Delete, ModifierKeys.None)]
    [InlineData(Key.D, ModifierKeys.Control)]
    [InlineData(Key.Z, ModifierKeys.Control)]
    [InlineData(Key.Y, ModifierKeys.Control)]
    [InlineData(Key.Z, ModifierKeys.Control | ModifierKeys.Shift)]
    [InlineData(Key.C, ModifierKeys.Control)]
    [InlineData(Key.X, ModifierKeys.Control)]
    [InlineData(Key.V, ModifierKeys.Control)]
    [InlineData(Key.A, ModifierKeys.Control)]
    [InlineData(Key.G, ModifierKeys.Control)]
    [InlineData(Key.T, ModifierKeys.Alt)]
    [InlineData(Key.U, ModifierKeys.Control | ModifierKeys.Shift)]
    public void DesignShortcutsDoNotReachTheHiddenDesignOffTheDesignerScreen(Key key, ModifierKeys modifiers)
    {
        Assert.True(InteractionRules.IsSceneShortcut(key, modifiers));
        Assert.True(InteractionRules.SuppressSceneShortcut(false, key, modifiers, focusInTextInput: false));
        Assert.False(InteractionRules.SuppressSceneShortcut(true, key, modifiers, focusInTextInput: false));
    }

    [Theory]
    [InlineData(Key.Delete, ModifierKeys.None)]
    [InlineData(Key.C, ModifierKeys.Control)]
    [InlineData(Key.V, ModifierKeys.Control)]
    [InlineData(Key.Z, ModifierKeys.Control)]
    [InlineData(Key.A, ModifierKeys.Control)]
    public void TextFieldsKeepTheirOwnEditingKeysOnEveryScreen(Key key, ModifierKeys modifiers) =>
        Assert.False(InteractionRules.SuppressSceneShortcut(false, key, modifiers, focusInTextInput: true));

    [Fact]
    public void KeysThatAreNotDesignShortcutsAreNeverSuppressed()
    {
        Assert.False(InteractionRules.SuppressSceneShortcut(false, Key.S, ModifierKeys.Control, false)); // save
        Assert.False(InteractionRules.SuppressSceneShortcut(false, Key.N, ModifierKeys.Control, false));
        Assert.False(InteractionRules.SuppressSceneShortcut(false, Key.Escape, ModifierKeys.None, false));
        Assert.False(InteractionRules.SuppressSceneShortcut(false, Key.F1, ModifierKeys.None, false));
    }

    [Fact]
    public void MidGestureOnlyDesignChangingShortcutsAreRefused()
    {
        Assert.True(InteractionRules.IsMutatingSceneShortcut(Key.Delete, ModifierKeys.None));
        Assert.True(InteractionRules.IsMutatingSceneShortcut(Key.Z, ModifierKeys.Control));
        Assert.True(InteractionRules.IsMutatingSceneShortcut(Key.V, ModifierKeys.Control));
        Assert.True(InteractionRules.IsMutatingSceneShortcut(Key.D, ModifierKeys.Control));
        Assert.False(InteractionRules.IsMutatingSceneShortcut(Key.C, ModifierKeys.Control));
        Assert.False(InteractionRules.IsMutatingSceneShortcut(Key.A, ModifierKeys.Control));
        Assert.False(InteractionRules.IsMutatingSceneShortcut(Key.Left, ModifierKeys.None));
    }

    // ------------------------------------------------------------------ cursors

    [Fact]
    public void RotateHandleGetsItsOwnCursorAndNeverFailsToBuildOne()
    {
        OnSta(() =>
        {
            var cursor = RotateCursor.Get();
            Assert.NotNull(cursor);
            // Either the drawn rotate cursor, or the stock hand as the documented fallback.
            Assert.Same(cursor, RotateCursor.Get());
        });
    }

    // ------------------------------------------------------------------ source-level rules

    [Fact]
    public void RingTemplatesTriggerOnKeyboardVisibleFocusNotOnPlainFocus()
    {
        var theme = File.ReadAllText(Path.Combine(Root(), "Lasero.App", "Theme", "LaseroTheme.xaml"));
        var shared = File.ReadAllText(Path.Combine(Root(), "Lasero.App", "Theme", "SharedUiStyles.xaml"));

        // Every template that draws a focus ring uses FocusVisual. The only IsKeyboardFocused
        // triggers left are the text inputs' own border, which show on any focus by convention.
        foreach (var source in new[] { theme, shared })
        {
            foreach (Match match in Regex.Matches(source, "Property=\"IsKeyboardFocused\" Value=\"True\">\\s*<Setter ([^>]*)/>"))
            {
                var setter = match.Groups[1].Value;
                Assert.True(
                    setter.Contains("Brush.Accent}", StringComparison.Ordinal) ||
                    setter.Contains("Brush.Field", StringComparison.Ordinal),
                    "A ring-style trigger still uses IsKeyboardFocused: " + setter);
            }
        }

        Assert.Contains("components:FocusVisual.IsVisible", theme, StringComparison.Ordinal);
    }

    [Fact]
    public void NoEasingOvershootsAndDisabledHasOneOpacity()
    {
        var files = Directory.GetFiles(Path.Combine(Root(), "Lasero.App"), "*.xaml", SearchOption.AllDirectories)
            .Concat(Directory.GetFiles(Path.Combine(Root(), "Lasero.App"), "*.cs", SearchOption.AllDirectories))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                        && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"));

        foreach (var file in files)
        {
            var text = File.ReadAllText(file);
            // The one permitted overshoot is the press release (Ease.Release, a slight 0.35 BackEase), keyed and
            // defined once in the theme; nothing else may carry a BackEase.
            if (Path.GetFileName(file) != "LaseroTheme.xaml")
                Assert.DoesNotContain("BackEase", text, StringComparison.Ordinal);
            Assert.DoesNotContain("ElasticEase", text, StringComparison.Ordinal);
            Assert.DoesNotContain("Ease.Spring", text, StringComparison.Ordinal);
        }

        var theme = File.ReadAllText(Path.Combine(Root(), "Lasero.App", "Theme", "LaseroTheme.xaml"));
        var shared = File.ReadAllText(Path.Combine(Root(), "Lasero.App", "Theme", "SharedUiStyles.xaml"));
        Assert.Contains("<system:Double x:Key=\"Opacity.Disabled\">0.45</system:Double>", theme, StringComparison.Ordinal);
        foreach (var source in new[] { theme, shared })
            Assert.DoesNotMatch("Trigger Property=\"IsEnabled\" Value=\"False\">\\s*<Setter Property=\"Opacity\" Value=\"0\\.\\d+\"", source);
    }

    [Fact]
    public void TextInputsShowWhenTheyAreDisabled()
    {
        var theme = File.ReadAllText(Path.Combine(Root(), "Lasero.App", "Theme", "LaseroTheme.xaml"));
        foreach (var type in new[] { "TextBox", "PasswordBox" })
        {
            var start = theme.IndexOf($"<ControlTemplate TargetType=\"{type}\">", StringComparison.Ordinal);
            var template = theme[start..theme.IndexOf("</ControlTemplate>", start, StringComparison.Ordinal)];
            Assert.Contains("Property=\"IsEnabled\" Value=\"False\"", template, StringComparison.Ordinal);
            Assert.Contains("Brush.DisabledSurface", template, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ToggleButtonsShareTheButtonHoverAndPressWash()
    {
        var theme = File.ReadAllText(Path.Combine(Root(), "Lasero.App", "Theme", "LaseroTheme.xaml"));
        foreach (var key in new[] { "Toggle.LayerState", "Toggle.Icon" })
        {
            var start = theme.IndexOf($"<Style x:Key=\"{key}\"", StringComparison.Ordinal);
            var style = theme[start..theme.IndexOf("</Style>", start, StringComparison.Ordinal)];
            Assert.Contains("Brush.HoverWash", style, StringComparison.Ordinal);
            Assert.Contains("To=\"0.05\"", style, StringComparison.Ordinal);
            Assert.Contains("Value=\"0.08\"", style, StringComparison.Ordinal);
            // Hover never borrows the interaction colour: no cobalt text, no selected tint.
            var hover = style[style.IndexOf("Property=\"IsMouseOver\"", StringComparison.Ordinal)..];
            hover = hover[..hover.IndexOf("</Trigger>", StringComparison.Ordinal)];
            Assert.DoesNotContain("AccentText", hover, StringComparison.Ordinal);
            Assert.DoesNotContain("SelectedSurface", hover, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void NumericFieldsShareOneEnterAndEscBehaviourRegisteredAtStartup()
    {
        var root = Root();
        var app = File.ReadAllText(Path.Combine(root, "Lasero.App", "App.xaml.cs"));
        Assert.Contains("InteractionBehaviors.Register()", app, StringComparison.Ordinal);

        // The two hand-copied Enter handlers are gone; a third copy must not grow back.
        foreach (var relative in new[]
                 {
                     Path.Combine("Views", "SelectionPropertiesBar.xaml"),
                     Path.Combine("Components", "ParameterSlider.xaml"),
                 })
            Assert.DoesNotContain("OnValueFieldKeyDown", File.ReadAllText(Path.Combine(root, "Lasero.App", relative)), StringComparison.Ordinal);

        Assert.Equal(500, InteractionBehaviors.TooltipInitialDelayMs);
        Assert.Equal(100, InteractionBehaviors.TooltipBetweenDelayMs);
        Assert.Equal(8000, InteractionBehaviors.TooltipShowDurationMs);
    }

    [Fact]
    public void CanvasEscLeavesInlineTextEditingWithoutTouchingSelectionOrTool()
    {
        var canvas = File.ReadAllText(Path.Combine(Root(), "Lasero.App", "Controls", "SceneCanvas.xaml.cs"));
        var start = canvas.IndexOf("private void OnInlineEditorPreviewKeyDown", StringComparison.Ordinal);
        var handler = canvas[start..canvas.IndexOf("private void OnInlineEditorLostFocus", start, StringComparison.Ordinal)];
        var escape = handler[..handler.IndexOf("Key.Enter", StringComparison.Ordinal)];

        Assert.Contains("CommitInlineTextEdit(applyChanges: true)", escape, StringComparison.Ordinal);
        Assert.DoesNotContain("ActiveTool", escape, StringComparison.Ordinal);
        Assert.DoesNotContain("SelectedObjects.Clear", escape, StringComparison.Ordinal);
    }

    [Fact]
    public void MainWindowRoutesEscAndGatesDesignShortcuts()
    {
        var window = File.ReadAllText(Path.Combine(Root(), "Lasero.App", "MainWindow.xaml.cs"));
        Assert.Contains("KeyDown += OnKeyDown;", window, StringComparison.Ordinal);
        Assert.Contains("InteractionRules.ForWindowEscape(", window, StringComparison.Ordinal);
        Assert.Contains("InteractionRules.SuppressSceneShortcut(", window, StringComparison.Ordinal);
        Assert.Contains("DesignerCanvas.IsPointerGestureActive", window, StringComparison.Ordinal);
        Assert.Contains("_viewModel.Kamil.StepBack()", window, StringComparison.Ordinal);
    }

    [Fact]
    public void KamilPanelStaysBelowTheSelectionBar()
    {
        var host = File.ReadAllText(Path.Combine(Root(), "Lasero.App", "Views", "Kamil", "KamilAssistantHost.xaml.cs"));
        Assert.Contains("new Rect(SafeMargin, ContextBarClearance,", host, StringComparison.Ordinal);

        // The clearance has to cover the bar's real position in MainWindow.xaml: 30px inset plus a
        // bar that is up to 68px tall (text selected, scrollbar showing), plus a gap.
        var window = File.ReadAllText(Path.Combine(Root(), "Lasero.App", "MainWindow.xaml"));
        Assert.Contains("Margin=\"12,30,12,0\"", window, StringComparison.Ordinal);
        Assert.True(Lasero.App.Views.Kamil.KamilAssistantHost.ContextBarClearance >= 30 + 68);
    }

    private static string Root()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "Lasero.App")))
                return directory.FullName;
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Repository root was not found.");
    }
}
