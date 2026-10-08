using System.IO;
using System.Text.RegularExpressions;
using Lasero.App.ViewModels;
using Lasero.Core.Grbl;
using Xunit;

namespace Lasero.Tests;

/// <summary>
/// The customer chooses Automaticky or a COM port. There is no engraver-model picker anywhere: the machine
/// is recognised from the controller and falls back to generic GRBL, shown as read-only text.
/// </summary>
public sealed class ConnectionPortChoiceTests
{
    private static readonly Regex XmlComment = new(@"<!--.*?-->", RegexOptions.Singleline | RegexOptions.Compiled);

    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "Lasero.App"))) dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("repository root not found");
    }

    private static IEnumerable<string> CustomerXaml() =>
        Directory.EnumerateFiles(Path.Combine(Root(), "Lasero.App"), "*.xaml", SearchOption.AllDirectories)
            .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                     && !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"));

    [Fact]
    public void NoCustomerViewBindsAMachineModelPicker()
    {
        foreach (var file in CustomerXaml())
        {
            var text = XmlComment.Replace(File.ReadAllText(file), "");
            Assert.DoesNotContain("CompatibilityOptions", text, StringComparison.Ordinal);
            Assert.DoesNotContain("SelectedCompatibility", text, StringComparison.Ordinal);
            Assert.DoesNotContain("Model a stav kompatibility", text, StringComparison.Ordinal);
            Assert.DoesNotContain("Upřesnit model", text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void TheDetectedMachineIsShownAsReadOnlyTextOnEveryConnectionSurface()
    {
        foreach (var relative in new[] { "Views/MachinePanelView.xaml", "Views/DeviceView.xaml", "Views/DeviceSetup/DeviceWizardOverlay.xaml" })
        {
            var text = File.ReadAllText(Path.Combine(new[] { Root(), "Lasero.App" }.Concat(relative.Split('/')).ToArray()));
            Assert.Contains("Connection.DetectedMachineText", text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ThePortListIsBoundToTheFriendlyPortOptionsNotTheRawNames()
    {
        foreach (var relative in new[] { "Views/MachinePanelView.xaml", "Views/DeviceView.xaml" })
        {
            var text = File.ReadAllText(Path.Combine(new[] { Root(), "Lasero.App" }.Concat(relative.Split('/')).ToArray()));
            Assert.Contains("Connection.PortOptions", text, StringComparison.Ordinal);
            Assert.Contains("Connection.SelectedPortOption", text, StringComparison.Ordinal);
            // Zařízení has the one Připojit as the status card primary (DeviceSetupViewModel), the machine panel binds it directly.
            Assert.Contains(relative.EndsWith("DeviceView.xaml", StringComparison.Ordinal) ? "DeviceSetup.PrimaryCommand" : "Connection.ConnectSelectedCommand", text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void TheModelProfilesStayInCodeForDetectionAndSafety()
    {
        // The catalogue is not deleted: GrblConnection still refuses an unverified profile, and the
        // detection path (KnownMachineProfiles) is untouched.
        var core = File.ReadAllText(Path.Combine(Root(), "Lasero.Core", "Machines", "MachineCompatibility.cs"));
        Assert.Contains("algolaser-pixi", core, StringComparison.Ordinal);
        Assert.Contains("RequireDirectConnection", File.ReadAllText(Path.Combine(Root(), "Lasero.Core", "Grbl", "GrblConnection.cs")), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("COM3", "USB-SERIAL CH340", "COM3 - USB-SERIAL CH340")]
    [InlineData("COM7", null, "COM7")]
    public void FriendlyLabelsAreCOMNumberThenTheDeviceDescription(string port, string? description, string expected)
    {
        var map = new Dictionary<string, string>();
        if (description is not null) map[port] = description;
        Assert.Equal(expected, SerialPortDescriptions.Label(port, map));
    }

    [Fact]
    public void AutomatikyIsTheFirstOptionAndTheRecordKnowsItIsAutomatic()
    {
        Assert.True(PortOption.Automatic.IsAutomatic);
        Assert.Equal("Automaticky", PortOption.Automatic.Display);
        Assert.False(new PortOption("COM3", "COM3 - USB-SERIAL CH340").IsAutomatic);
    }

    [Fact]
    public void ConnectionStringsFollowTheBrandTextRules()
    {
        var vm = File.ReadAllText(Path.Combine(Root(), "Lasero.App", "ViewModels", "ConnectionViewModel.cs"));
        foreach (var text in new[] { "Připojit", "Zjištěno: ", "Obecný GRBL", "Automaticky" })
        {
            Assert.Contains(text, vm, StringComparison.Ordinal);
            Assert.DoesNotContain("?", text);
            Assert.DoesNotContain("!", text);
        }
    }
}
