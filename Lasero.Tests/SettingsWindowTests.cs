using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using Lasero.App;
using Xunit;

namespace Lasero.Tests;

/// <summary>
/// Nastavení, rendered for real: the editor pages show what the preferences hold, a typed value reaches the
/// live preferences only on "Uložit a zavřít", and a value the editor refuses blocks the save with a reason.
/// </summary>
[Collection("WpfUi")]
public sealed class SettingsWindowTests
{
    private static void WithWindow(Action<ShellHarness, SettingsWindow> body, string? renderName = null)
    {
        InlineTextEditorRenderTests.Ui.Invoke(() =>
        {
            using var shell = new ShellHarness(1366, 768);
            var window = new SettingsWindow(shell.ViewModel, _ => true)
            {
                Width = 980, Height = 760, ShowActivated = false, ShowInTaskbar = false,
                WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = -20000,
            };
            window.Show();
            ShellHarness.Pump();
            window.UpdateLayout();
            try
            {
                body(shell, window);
                if (renderName is not null) RenderAllTabs(window, renderName);
            }
            finally { if (window.IsVisible) window.Close(); }
        });
    }

    private static void RenderAllTabs(SettingsWindow window, string name)
    {
        var tabs = Find<TabControl>(window);
        for (var index = 0; index < tabs.Items.Count; index++)
        {
            tabs.SelectedIndex = index;
            ShellHarness.Pump();
            window.UpdateLayout();
            ShellHarness.Pump();
            var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(
                (int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(window);
            ShellHarness.Save(bitmap, $"{name}-{index}");
        }
    }

    private static T Find<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T match) return match;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var found = TryFind<T>(VisualTreeHelper.GetChild(root, i));
            if (found is not null) return found;
        }
        throw new InvalidOperationException($"{typeof(T).Name} not found");
    }

    private static T? TryFind<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T match) return match;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var found = TryFind<T>(VisualTreeHelper.GetChild(root, i));
            if (found is not null) return found;
        }
        return null;
    }

    private static TextBox Field(SettingsWindow window, string name) => (TextBox)window.FindName(name);

    private static void Type(TextBox field, string text)
    {
        field.Text = text;
        BindingOperations.GetBindingExpression(field, TextBox.TextProperty)!.UpdateSource(); // what leaving the field does
    }

    private static void ClickSave(SettingsWindow window)
    {
        var save = (Button)window.FindName("SaveButton");
        save.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
    }

    [Fact]
    public void TheEditorFieldsShowWhatThePreferencesHold()
    {
        InlineTextEditorRenderTests.Ui.Invoke(() =>
        {
            using var shell = new ShellHarness(1366, 768);
            shell.ViewModel.Settings.Editor.ArrowStepMm = 2;
            shell.ViewModel.Settings.Editor.AutosaveMinutes = 5;
            shell.ViewModel.Settings.Editor.ShowGrid = false;
            var window = new SettingsWindow(shell.ViewModel, _ => true);
            Assert.Equal("2", Field(window, "ArrowStepInput").Text);
            Assert.Equal("5", Field(window, "AutosaveInput").Text);
            Assert.Equal("6", Field(window, "ClickToleranceInput").Text);
            Assert.Equal("0.05", Field(window, "CtrlArrowStepInput").Text);
            var showGrid = (CheckBox)window.FindName("ShowGridToggle");
            Assert.False(showGrid.IsChecked);
        });
    }


    [Fact]
    public void ATypedValueReachesTheLivePreferencesOnlyOnSave()
    {
        WithWindow((shell, window) =>
        {
            var before = shell.ViewModel.Settings.Editor;
            Type(Field(window, "ArrowStepInput"), "2,5");
            Type(Field(window, "AutosaveInput"), "10");

            Assert.Equal(0.5, shell.ViewModel.Settings.Editor.ArrowStepMm); // still the old value while the dialog is open

            ClickSave(window);

            Assert.False(window.IsVisible);
            Assert.NotSame(before, shell.ViewModel.Settings.Editor);
            Assert.Equal(2.5, shell.ViewModel.Settings.Editor.ArrowStepMm);
            Assert.Equal(TimeSpan.FromMinutes(10), shell.ViewModel.Settings.Editor.AutosaveInterval);
        });
    }

    [Fact]
    public void ClosingWithoutSavingLeavesTheEditorAsItWas()
    {
        WithWindow((shell, window) =>
        {
            Type(Field(window, "ArrowStepInput"), "9");

            window.Close();

            Assert.Equal(0.5, shell.ViewModel.Settings.Editor.ArrowStepMm);
        });
    }

    [Fact]
    public void AValueTheEditorRefusesBlocksTheSaveAndSaysWhy()
    {
        WithWindow((shell, window) =>
        {
            var field = Field(window, "ClickToleranceInput");
            Type(field, "500");

            Assert.True(Validation.GetHasError(field));
            var message = (TextBlock)window.FindName("SettingsErrorText");
            Assert.Contains("px", message.Text);

            ClickSave(window);

            Assert.True(window.IsVisible);
            Assert.Equal(6, shell.ViewModel.Settings.Editor.ClickTolerancePx);

            Type(field, "8");
            Assert.False(Validation.GetHasError(field));
            Assert.Equal(string.Empty, message.Text);

            ClickSave(window);
            Assert.False(window.IsVisible);
            Assert.Equal(8, shell.ViewModel.Settings.Editor.ClickTolerancePx);
        });
    }

    [Fact]
    public void TheFixedGridSpacingIsOnlyEditableWhenTheAutomaticSpacingIsOff()
    {
        WithWindow((shell, window) =>
        {
            var spacing = Field(window, "GridSpacingInput");
            var automatic = (CheckBox)window.FindName("AutomaticGridToggle");
            ((TabControl)window.FindName("SettingsTabs")).SelectedIndex = 1;
            ShellHarness.Pump();

            Assert.True(automatic.IsChecked);
            Assert.False(spacing.IsEnabled);

            automatic.IsChecked = false;
            Assert.True(spacing.IsEnabled);
        });
    }

    [Fact]
    public void TheSafetySwitchesStillWriteTheSameSettingsAsBefore()
    {
        WithWindow((shell, window) =>
        {
            Assert.True(shell.ViewModel.Settings.Safety.RequireFramingBeforeStart);
            ((CheckBox)window.FindName("RequireFramingToggle")).IsChecked = false;
            ((CheckBox)window.FindName("StartupAnimationToggle")).IsChecked = false;

            ClickSave(window);

            Assert.False(shell.ViewModel.Settings.Safety.RequireFramingBeforeStart);
            Assert.False(shell.ViewModel.Settings.Startup.ShowIntroAnimation);
        });
    }

    [Fact]
    public void RenderEveryTab()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("LASERO_RENDER_OUT"))) return;
        WithWindow((_, _) => { }, "settings");
    }
}
