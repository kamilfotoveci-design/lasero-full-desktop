using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using Lasero.App.ViewModels;
using Lasero.App.Views;
using Xunit;

namespace Lasero.Tests;

/// <summary>
/// The device settings live on the Zařízení screen. These build the whole shell against a real view model and drive the
/// screen the way the operator does: the one tab strip, the draft that survives a look at another tab, Uložit and Zrušit.
/// A clean compile does not prove any of it (WPF reports XAML errors at run time).
/// </summary>
[Collection("WpfUi")]
public sealed class DeviceSettingsViewRenderTests
{
    private static System.Windows.Threading.Dispatcher Ui => InlineTextEditorRenderTests.Ui;

    private static T Find<T>(DependencyObject root) where T : DependencyObject => LayoutAssertions.Descendants<T>(root).First();

    private static void Invoke(Button button) =>
        ((IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)).Invoke();

    private static Button ButtonLabelled(DependencyObject root, string text) =>
        LayoutAssertions.Descendants<Button>(root).First(button =>
            (button.Content as string) == text
            || (button.Content is DependencyObject content && LayoutAssertions.Descendants<TextBlock>(content).Any(t => t.Text == text)));

    [Fact]
    public void EveryTabOfTheStripShowsItsPage()
    {
        Ui.Invoke(() =>
        {
            using var shell = new ShellHarness(1366, 768);
            var vm = shell.ViewModel;
            vm.CurrentScreen = AppScreen.Device;
            shell.Settle();
            var device = Find<DeviceView>(shell.Window);
            var strip = (TabControl)device.FindName("DeviceTabs");
            var page = (DeviceSettingsView)device.FindName("SettingsPage");
            Assert.Equal(5, strip.Items.Count);

            foreach (var tab in Enum.GetValues<DeviceScreenTab>())
            {
                vm.DeviceTab = tab;
                shell.Settle();
                Assert.Equal((int)tab, strip.SelectedIndex);
                Assert.Equal(tab != DeviceScreenTab.Connection, LayoutAssertions.IsShown(page));
                // The sticky bar is on every settings page and nowhere else.
                var save = ButtonLabelled(page, "Uložit");
                Assert.Equal(tab != DeviceScreenTab.Connection, LayoutAssertions.IsShown(save));
            }
        });
    }

    [Fact]
    public void ClickingATabSelectsItsPage()
    {
        Ui.Invoke(() =>
        {
            using var shell = new ShellHarness(1366, 768);
            var vm = shell.ViewModel;
            vm.CurrentScreen = AppScreen.Device;
            shell.Settle();
            var strip = (TabControl)Find<DeviceView>(shell.Window).FindName("DeviceTabs");

            ((TabItem)strip.Items[3]).IsSelected = true;
            shell.Settle();

            Assert.Equal(DeviceScreenTab.GCode, vm.DeviceTab);
        });
    }

    [Fact]
    public void TheShellCanOpenTheScreenOnTheSettingsTab()
    {
        Ui.Invoke(() =>
        {
            using var shell = new ShellHarness(1366, 768);
            var vm = shell.ViewModel;
            vm.CurrentScreen = AppScreen.Designer;
            shell.Settle();

            vm.ShowDeviceSettings();
            shell.Settle();
            Assert.Equal(AppScreen.Device, vm.CurrentScreen);
            Assert.Equal(DeviceScreenTab.Dimensions, vm.DeviceTab);
            Assert.Equal(1, ((TabControl)Find<DeviceView>(shell.Window).FindName("DeviceTabs")).SelectedIndex);

            vm.CurrentScreen = AppScreen.Home;
            vm.ShowDeviceSettings(DeviceScreenTab.More);
            Assert.Equal(DeviceScreenTab.More, vm.DeviceTab);
            // The connection page is not a settings page: asking for it opens the first settings page.
            vm.ShowDeviceSettings(DeviceScreenTab.Connection);
            Assert.Equal(DeviceScreenTab.Dimensions, vm.DeviceTab);
        });
    }

    [Fact]
    public void ADraftSurvivesLeavingTheTabAndSaveNeedsNoWindow()
    {
        Ui.Invoke(() =>
        {
            using var shell = new ShellHarness(1366, 768);
            var vm = shell.ViewModel;
            vm.ShowDeviceSettings();
            shell.Settle();
            var page = (DeviceSettingsView)Find<DeviceView>(shell.Window).FindName("SettingsPage");
            Assert.False(page.IsDirty);

            page.Draft.WorkAreaWidthMm = 410;
            Assert.True(page.IsDirty);
            vm.DeviceTab = DeviceScreenTab.Connection;
            shell.Settle();
            vm.DeviceTab = DeviceScreenTab.Basic;
            shell.Settle();
            Assert.Equal(410, page.Draft.WorkAreaWidthMm);
            Assert.NotEqual(410, vm.Connection.WorkAreaWidthMm);

            Invoke(ButtonLabelled(page, "Uložit"));
            shell.Settle();
            Assert.Equal(410, vm.Connection.WorkAreaWidthMm);
            Assert.False(page.IsDirty);
            var saved = (TextBlock)page.FindName("SavedText");
            Assert.Equal(Visibility.Visible, saved.Visibility);
            Assert.Equal(string.Empty, ((TextBlock)page.FindName("SettingsErrorText")).Text);
        });
    }

    [Fact]
    public void ZrusitBringsBackTheSavedValuesAndSaveRefusesAnInvalidDraft()
    {
        Ui.Invoke(() =>
        {
            using var shell = new ShellHarness(1366, 768);
            var vm = shell.ViewModel;
            vm.ShowDeviceSettings();
            shell.Settle();
            var page = (DeviceSettingsView)Find<DeviceView>(shell.Window).FindName("SettingsPage");
            var saved = vm.Connection.WorkAreaWidthMm;

            page.Draft.WorkAreaWidthMm = 0;
            Invoke(ButtonLabelled(page, "Uložit"));
            Assert.Equal(saved, vm.Connection.WorkAreaWidthMm);
            Assert.Contains("Rozměry / Jednotky", ((TextBlock)page.FindName("SettingsErrorText")).Text);

            Invoke(ButtonLabelled(page, "Zrušit"));
            Assert.Equal(saved, page.Draft.WorkAreaWidthMm);
            Assert.False(page.IsDirty);
            Assert.Equal(string.Empty, ((TextBlock)page.FindName("SettingsErrorText")).Text);
        });
    }

    [Fact]
    public void EnteringTheScreenDropsAnUnsavedDraftAndAnUntouchedOneFollowsLiveChanges()
    {
        Ui.Invoke(() =>
        {
            using var shell = new ShellHarness(1366, 768);
            var vm = shell.ViewModel;
            vm.ShowDeviceSettings();
            shell.Settle();
            var page = (DeviceSettingsView)Find<DeviceView>(shell.Window).FindName("SettingsPage");

            page.Draft.JogStepMm = 7.5;
            vm.CurrentScreen = AppScreen.Home;
            shell.Settle();
            vm.ShowDeviceSettings();
            shell.Settle();
            Assert.NotEqual(7.5, page.Draft.JogStepMm);
            Assert.False(page.IsDirty);

            // The jog step is also changed live on the connection page; an untouched draft shows the new value.
            vm.DeviceTab = DeviceScreenTab.Connection;
            shell.Settle();
            vm.Jog.StepSizeMm = vm.Jog.StepSizePresets.First(step => step != vm.Jog.StepSizeMm);
            vm.DeviceTab = DeviceScreenTab.More;
            shell.Settle();
            Assert.Equal(vm.Jog.StepSizeMm, page.Draft.JogStepMm);
        });
    }

    [Fact]
    public void ASwitchThatIsAlreadyOnRendersOnAndEverySwitchHasAName()
    {
        Ui.Invoke(() =>
        {
            using var shell = new ShellHarness(1366, 768);
            shell.ViewModel.Jog.EnableZAxis = true;
            shell.ViewModel.DeviceTab = DeviceScreenTab.Dimensions;
            shell.ViewModel.CurrentScreen = AppScreen.Device;
            shell.Settle();
            var page = Find<DeviceSettingsView>(shell.Window);

            var switches = LayoutAssertions.Descendants<System.Windows.Controls.Primitives.ToggleButton>(page).ToList();
            Assert.NotEmpty(switches);
            Assert.Contains(switches, toggle => toggle.IsChecked == true && toggle.ActualWidth > 0);
            Assert.All(switches.Where(toggle => toggle.ActualWidth > 0),
                toggle => Assert.False(string.IsNullOrWhiteSpace(System.Windows.Automation.AutomationProperties.GetName(toggle)),
                    "a switch has no accessible name"));
        });
    }
}
