using System.IO;
using Lasero.App.ViewModels;
using Lasero.Core.Jobs;

namespace Lasero.Tests;

/// <summary>
/// Pins docs/screen-controls-matrix.md: which persistent controls belong on which screen.
/// Job controls belong to Návrh; a running job's Pozastavit/Zastavit stay reachable everywhere.
/// </summary>
public sealed class ScreenControlsMatrixTests
{
    private static readonly AppScreen[] NonDesigner = { AppScreen.Home, AppScreen.Device, AppScreen.Chat };
    private static readonly JobRunState[] Active =
        { JobRunState.Preparing, JobRunState.Framing, JobRunState.Running, JobRunState.Paused };

    [Fact]
    public void LaunchControlsAndPaletteBelongToDesignerOnly()
    {
        Assert.True(ScreenChrome.ShowLaunchControls(AppScreen.Designer));
        Assert.True(ScreenChrome.ShowLayerPalette(AppScreen.Designer));
        foreach (var screen in NonDesigner)
        {
            Assert.False(ScreenChrome.ShowLaunchControls(screen), screen.ToString());
            Assert.False(ScreenChrome.ShowLayerPalette(screen), screen.ToString());
            Assert.False(ScreenChrome.ShowDeviceSettingsShortcut(screen), screen.ToString());
        }
        Assert.True(ScreenChrome.ShowDeviceSettingsShortcut(AppScreen.Designer));
    }

    [Fact]
    public void RunningJobControlsAreReachableOnEveryScreen()
    {
        foreach (var screen in Enum.GetValues<AppScreen>())
        foreach (var state in Enum.GetValues<JobRunState>())
        {
            var active = Active.Contains(state);
            Assert.Equal(active, ScreenChrome.ShowActiveJobControls(state));
            if (active) Assert.True(ScreenChrome.ShowJobActionZone(screen, state), $"{screen}/{state}");
        }
    }

    [Fact]
    public void IdleHomeShowsNoJobZoneAndNoJobDetails()
    {
        foreach (var screen in NonDesigner)
        {
            Assert.False(ScreenChrome.ShowJobActionZone(screen, JobRunState.Idle));
            Assert.False(ScreenChrome.ShowJobDetails(screen, JobRunState.Idle));
            Assert.False(ScreenChrome.ShowJobDetails(screen, JobRunState.Ready));
        }
    }

    [Fact]
    public void JobOutcomesAreNeverHiddenOnAnyScreen()
    {
        foreach (var screen in Enum.GetValues<AppScreen>())
        foreach (var state in new[]
                 { JobRunState.Completed, JobRunState.Cancelled, JobRunState.Error, JobRunState.Aborted, JobRunState.Faulted })
            Assert.True(ScreenChrome.ShowJobDetails(screen, state), $"{screen}/{state}");
    }

    [Fact]
    public void DesignerAlwaysShowsItsStrip()
    {
        foreach (var state in Enum.GetValues<JobRunState>())
        {
            Assert.True(ScreenChrome.ShowJobDetails(AppScreen.Designer, state));
            Assert.True(ScreenChrome.ShowJobActionZone(AppScreen.Designer, state));
        }
    }

    [Fact]
    public void XamlBindsTheStripToTheMatrix()
    {
        var root = FindRepositoryRoot();
        var main = File.ReadAllText(Path.Combine(root, "Lasero.App", "MainWindow.xaml"));
        var styles = File.ReadAllText(Path.Combine(root, "Lasero.App", "Theme", "SharedUiStyles.xaml"));

        // Rámovat and Spustit collapse on every non-Designer screen through their styles.
        foreach (var style in new[] { "JobIdleActionButton", "JobStartActionButton" })
        {
            var start = styles.IndexOf($"x:Key=\"{style}\"", StringComparison.Ordinal);
            var block = styles.Substring(start, styles.IndexOf("</Style>", start, StringComparison.Ordinal) - start);
            foreach (var screen in new[] { "Home", "Device", "Chat" })
                Assert.Contains($"AppScreen.{screen}}}", block, StringComparison.Ordinal);
        }

        // Pause, resume and stop carry no screen trigger.
        foreach (var style in new[] { "JobRunningActionButton", "JobPausedActionButton", "JobActiveStopButton" })
        {
            var start = styles.IndexOf($"x:Key=\"{style}\"", StringComparison.Ordinal);
            var block = styles.Substring(start, styles.IndexOf("</Style>", start, StringComparison.Ordinal) - start);
            Assert.DoesNotContain("CurrentScreen", block, StringComparison.Ordinal);
        }

        Assert.Contains("Binding ShowStripJobDetails", main, StringComparison.Ordinal);
        Assert.Contains("Binding ShowStripJobActionZone", main, StringComparison.Ordinal);
        Assert.Contains("Binding ShowDeviceSettingsShortcut", main, StringComparison.Ordinal);
        // The palette stays Designer-only, as does KAMIL.
        Assert.Contains("ItemsSource=\"{Binding Scene.LayerPalette}\"", main, StringComparison.Ordinal);
        Assert.Matches(@"Grid\.Column=""1""[^>]*Margin=""20,0,20,0""\s+Visibility=""\{Binding CurrentScreen, Converter=\{StaticResource EnumEqualsVisibility\}, ConverterParameter=Designer\}""", main);
        // The strip keeps one fixed height so nothing jumps between screens or job states.
        Assert.Contains("<RowDefinition Height=\"48\" />", main, StringComparison.Ordinal);
    }

    [Fact]
    public void HomeCarriesNoJobOrMachineMotionControls()
    {
        var home = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "Lasero.App", "Views", "HomeView.xaml"));
        foreach (var forbidden in new[]
                 { "RunFramingCommand", "RunJobCommand", "Jog.", "PauseResume", "AbortCommand", "LayerPalette",
                   "Scene.Undo", "Scene.Redo", "Ovládání stroje", "Rámovat", "Výchozí poloha" })
            Assert.DoesNotContain(forbidden, home, StringComparison.Ordinal);

        // What Home is for.
        foreach (var required in new[]
                 { "NewProjectCommand", "ImportFromHomeCommand", "OpenProjectCommand", "OpenRecentProjectCommand",
                   "OpenDeviceWizardCommand" })
            Assert.Contains(required, home, StringComparison.Ordinal);
    }

    [Fact]
    public void DeviceScreenHasNoImportShortcut()
    {
        var device = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "Lasero.App", "Views", "DeviceView.xaml"));
        Assert.DoesNotContain("GCode.LoadFileCommand", device, StringComparison.Ordinal);
        Assert.DoesNotContain("RunJobCommand", device, StringComparison.Ordinal);
    }

    [Fact]
    public void MatrixDocumentExists()
    {
        var doc = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "docs", "screen-controls-matrix.md"));
        foreach (var screen in new[] { "Home", "Návrh", "Materiály", "Zařízení", "Chat", "Nastavení" })
            Assert.Contains(screen, doc, StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "Lasero.App"))) return directory.FullName;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("Repository root not found.");
    }
}
