using System.IO;
using System.Xml.Linq;
using Lasero.App.ViewModels;

namespace Lasero.Tests;

/// <summary>
/// Pins the "Redundant entry points" decisions in docs/screen-controls-matrix.md: one primary
/// call-to-action per screen state, in one place. Source level, no WPF needed.
/// </summary>
public sealed class RedundantEntryPointsTests
{
    private static string Root => FindRepositoryRoot();
    private static string Read(params string[] parts) => File.ReadAllText(Path.Combine([Root, "Lasero.App", .. parts]));

    // ------------------------------------------------------------------ the owner's report

    [Fact]
    public void ImportIsOnlyTheRailToolAndNoStaticEmptyCardRemainsInDesigner()
    {
        var overlay = Read("Views", "CanvasHintOverlay.xaml");
        var overlayCode = Read("Views", "CanvasHintOverlay.xaml.cs");
        Assert.DoesNotContain("EmptyState", overlay, StringComparison.Ordinal);
        Assert.DoesNotContain("EmptyCard", overlay + overlayCode, StringComparison.Ordinal);
        Assert.DoesNotContain("CtaText", overlay, StringComparison.Ordinal);
        Assert.DoesNotContain("Importovat grafiku", overlay, StringComparison.Ordinal);
        Assert.DoesNotContain("Plátno je prázdné", Read("MainWindow.xaml"), StringComparison.Ordinal);
        Assert.DoesNotContain("Plátno je prázdné", Read("Views", "DesignerInspectorView.xaml"), StringComparison.Ordinal);
        var rail = Read("Views", "DesignerToolRail.xaml");
        var inspector = Read("Views", "DesignerInspectorView.xaml");

        Assert.Contains("Command=\"{Binding GCode.LoadFileCommand}\"", rail, StringComparison.Ordinal);
        Assert.Contains("ToolTip=\"Importovat SVG, obrázek nebo G-code\"", rail, StringComparison.Ordinal);

        // The inspector explains, it never acts: no import button in the empty Operace state.
        Assert.DoesNotContain("LoadFileCommand", inspector, StringComparison.Ordinal);
        Assert.DoesNotContain("Importovat grafiku", inspector, StringComparison.Ordinal);
    }

    [Fact]
    public void EmptyOperationsStateIsBriefNeutralAndReadable()
    {
        var inspector = Read("Views", "DesignerInspectorView.xaml");
        const string text = "Operace se vytvoří samy, jakmile se do návrhu přidá tvar, text nebo obrázek";

        var at = inspector.IndexOf(text, StringComparison.Ordinal);
        Assert.True(at > 0, "The empty Operace explanation is missing.");
        var element = inspector.Substring(inspector.LastIndexOf("<TextBlock", at, StringComparison.Ordinal),
            inspector.IndexOf("/>", at, StringComparison.Ordinal) - inspector.LastIndexOf("<TextBlock", at, StringComparison.Ordinal));
        Assert.Contains("FontSize=\"{StaticResource Size.Text.Explain}\"", element, StringComparison.Ordinal);
        Assert.DoesNotMatch(@"[?!]", text);
    }

    [Fact]
    public void EmptyCanvasTipIsOnlyDrivenByEmptyDesignerAndWithdrawnOnInteraction()
    {
        var triggers = Read("Tour", "GuidanceTriggers.cs");
        Assert.Contains("viewModel.CurrentScreen == AppScreen.Designer", triggers, StringComparison.Ordinal);
        Assert.Contains("viewModel.Scene.Objects.Count == 0", triggers, StringComparison.Ordinal);
        Assert.Contains("viewModel.Scene.ActiveTool == DesignerTool.Select", triggers, StringComparison.Ordinal);
        Assert.Contains("canvas.PreviewMouseDown", triggers, StringComparison.Ordinal);
        Assert.Contains("TimeSpan.FromSeconds(1.5)", triggers, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ strip connect

    [Fact]
    public void StripConnectIsOnlyWhereTheScreenHasNoConnectOfItsOwn()
    {
        Assert.False(ScreenChrome.ShowStripConnect(AppScreen.Home));
        Assert.False(ScreenChrome.ShowStripConnect(AppScreen.Device));
        Assert.True(ScreenChrome.ShowStripConnect(AppScreen.Designer));
        Assert.True(ScreenChrome.ShowStripConnect(AppScreen.Chat));

        Assert.Contains("Binding ShowStripConnect", Read("MainWindow.xaml"), StringComparison.Ordinal);
        // The screens that dropped the strip button must still offer their own.
        Assert.Contains("OpenDeviceWizardCommand", Read("Views", "HomeView.xaml"), StringComparison.Ordinal);
        Assert.Contains("PrimaryCommand=\"{Binding DeviceSetup.PrimaryCommand}\"", Read("Views", "DeviceView.xaml"), StringComparison.Ordinal);
    }

    [Fact]
    public void DeviceScreenHasOneDisconnectOnePrimaryConnectAndOneHomingControl()
    {
        var device = Read("Views", "DeviceView.xaml");
        // Disconnect lives in the status card (DeviceSetup.SecondaryCommand), not again in the port card.
        Assert.DoesNotContain("Connection.DisconnectCommand", device, StringComparison.Ordinal);
        // The one connect button is the status card's primary, which runs the selected port (Automaticky scans, a chosen COM port
        // connects to it). The port card below it only chooses; it carries no connect button of its own.
        Assert.DoesNotContain("Connection.ConnectSelectedCommand", device, StringComparison.Ordinal);
        Assert.DoesNotContain("Connection.SmartConnectCommand", device, StringComparison.Ordinal);
        Assert.DoesNotContain("Connection.ConnectCommand", device, StringComparison.Ordinal);
        Assert.Equal(1, System.Text.RegularExpressions.Regex.Matches(device, @"PrimaryCommand=""{Binding DeviceSetup.PrimaryCommand}""").Count);
        // Homing moves the whole machine: one labelled control, the jog pad centre is decoration.
        Assert.Equal(1, System.Text.RegularExpressions.Regex.Matches(device, @"Jog.HomeCommand").Count);
        Assert.Contains("Text=\"Najet domů\"", device, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ generic scan

    /// <summary>
    /// Buttons that bind the same command inside one XAML file are a duplicate unless they sit in
    /// mutually exclusive states. Every exception is listed with the reason it is not visible at once;
    /// a new duplicate fails here and has to be justified or removed. Menu items, key bindings and
    /// context menus are not buttons and are ignored (a closed menu is not a second button).
    /// </summary>
    private static readonly Dictionary<string, string> AllowedRepeats = new(StringComparer.Ordinal)
    {
        ["MainWindow.xaml|GCode.PauseResumeCommand"] = "Pozastavit and Pokračovat swap through job-state styles, never both visible",
        ["Views/Kamil/KamilAssistantHost.xaml|MinimizeCommand"] = "two headers: expanded chat and quick ask, one at a time",
        ["Views/Kamil/KamilAssistantHost.xaml|DataContext.UseQuickPromptCommand"] = "suggestion chips in two layouts, one at a time",
        ["Views/DeviceSetup/DeviceWizardOverlay.xaml|ScanCommand"] = "wizard steps are mutually exclusive layers",
        ["Views/DeviceSetup/DeviceWizardOverlay.xaml|AutoConnectCommand"] = "wizard steps are mutually exclusive layers",
        ["Views/MachinePanelView.xaml|Connection.ConnectCommand"] = "header chip vs Připojení tab; panel is only reachable in machine-control mode, which nothing enters today",
        ["SettingsWindow.xaml|Account.RedeemLicenseCommand"] = "licence states are mutually exclusive",
        ["MaterialsWindow.xaml|AddCommand"] = "empty list vs list footer, mutually exclusive",
    };

    [Fact]
    public void NoXamlFileBindsTheSameCommandToTwoButtonsWithoutAReason()
    {
        var app = Path.Combine(Root, "Lasero.App");
        var offenders = new List<string>();
        foreach (var file in Directory.EnumerateFiles(app, "*.xaml", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(app, file).Replace('\\', '/');
            if (relative.StartsWith("obj/", StringComparison.Ordinal) || relative.StartsWith("bin/", StringComparison.Ordinal)
                || relative.StartsWith("Theme/", StringComparison.Ordinal)) continue;

            var document = XDocument.Load(file);
            var bindings = document.Descendants()
                .Where(e => e.Name.LocalName is "Button" or "RadioButton" or "ToggleButton")
                .Select(e => (string?)e.Attribute("Command"))
                .Where(c => c is not null && c.StartsWith("{Binding ", StringComparison.Ordinal))
                .Select(c => c![9..].Split(',', '}')[0].Trim())
                .GroupBy(c => c)
                .Where(g => g.Count() > 1);

            foreach (var group in bindings)
                if (!AllowedRepeats.ContainsKey($"{relative}|{group.Key}"))
                    offenders.Add($"{relative}: {group.Key} x{group.Count()}");
        }

        Assert.True(offenders.Count == 0,
            "Same command on several buttons in one view. Remove the repeat or list it with a reason: "
            + string.Join("; ", offenders));
    }

    [Fact]
    public void TheDocumentListsEveryDecision()
    {
        var doc = File.ReadAllText(Path.Combine(Root, "docs", "screen-controls-matrix.md"));
        Assert.Contains("## Redundant entry points", doc, StringComparison.Ordinal);
        foreach (var key in new[] { "Importovat grafiku", "Připojit", "Odpojit", "Owner decision" })
            Assert.Contains(key, doc, StringComparison.Ordinal);
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
