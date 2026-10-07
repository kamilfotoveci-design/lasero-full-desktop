using System.IO;
using Xunit;

namespace Lasero.Tests;

/// <summary>
/// Tests must never run the real application start against the developer's profile. The WPF tests create an
/// <c>App</c> for its theme resources, and the Application constructor queues OnStartup on the dispatcher, so
/// without the guard every test run opened the real log, session.dat and settings and showed a splash.
/// </summary>
public sealed class TestIsolationTests
{
    private static string Root()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "Lasero.App")))
            directory = directory.Parent;
        return directory!.FullName;
    }

    [Fact]
    public void OnStartupReturnsBeforeAnythingTouchesTheRealProfile()
    {
        var source = File.ReadAllText(Path.Combine(Root(), "Lasero.App", "App.xaml.cs"));
        var start = source.IndexOf("protected override async void OnStartup", StringComparison.Ordinal);
        var guard = source.IndexOf("if (!IsRealApplicationProcess())", start, StringComparison.Ordinal);
        Assert.True(guard > start);
        foreach (var touch in new[] { "AppSettingsStore.CreateDefault()", "LocalApplicationData", "StartupSplash.Start()", "new LoginWindow", "TryResumeSessionAsync" })
            Assert.True(source.IndexOf(touch, start, StringComparison.Ordinal) > guard, touch + " runs before the test-process guard");
    }

    [Fact]
    public void HomeShowsExactlyOneTipOfTheDay()
    {
        var home = File.ReadAllText(Path.Combine(Root(), "Lasero.App", "Views", "HomeView.xaml"));
        Assert.Equal(1, System.Text.RegularExpressions.Regex.Matches(home, "AutomationId=\"HomeTipOfDay\"").Count);
        Assert.DoesNotContain("TipOfDayText", home);
    }
}
