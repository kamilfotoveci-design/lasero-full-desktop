using System.IO;
using System.Text.RegularExpressions;
using Lasero.App.Tour;

namespace Lasero.Tests;

/// <summary>
/// Source-level guards for the guided tour: every step points at a name that really exists in the XAML
/// (a renamed control would otherwise silently turn a coach mark into a floating card), the copy follows
/// the brand rules, the tour cannot reach hardware, and the window wiring stays in place.
/// </summary>
public sealed class TourSourceTests
{
    private static readonly string Root = FindRepositoryRoot();
    private static string App(params string[] parts) => Path.Combine([Root, "Lasero.App", .. parts]);
    private static string Read(params string[] parts) => File.ReadAllText(App(parts));

    private static readonly Regex DeclaredName = new(
        @"(?:x:Name|(?:\w+:)?AutomationProperties\.AutomationId)=""([^""]+)""", RegexOptions.Compiled);

    private static HashSet<string> DeclaredNames()
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(App(), "*.xaml", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") ||
                file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")) continue;
            foreach (Match m in DeclaredName.Matches(File.ReadAllText(file))) names.Add(m.Groups[1].Value);
        }
        return names;
    }

    [Fact]
    public void EveryStepTargetNameIsDeclaredInTheXaml()
    {
        var declared = DeclaredNames();
        foreach (var step in TourSteps.All)
        foreach (var id in step.TargetIds)
            Assert.True(declared.Contains(id), $"step '{step.Id}' points at '{id}', which no x:Name or AutomationId declares");
    }

    [Fact]
    public void StepTargetsAreDeclaredWhereTheyAreExpected()
    {
        Assert.Contains("AutomationProperties.AutomationId=\"HomeActions\"", Read("Views", "HomeView.xaml"));
        Assert.Contains("x:Name=\"DesignerRail\"", Read("MainWindow.xaml"));
        Assert.Contains("x:Name=\"DesignerInspector\"", Read("MainWindow.xaml"));
        Assert.Contains("AutomationProperties.AutomationId=\"RailMaterialsButton\"", Read("Views", "DesignerToolRail.xaml"));
        Assert.Contains("AutomationProperties.AutomationId=\"StripMachineZone\"", Read("MainWindow.xaml"));
        Assert.Contains("AutomationProperties.AutomationId=\"StripJobActions\"", Read("MainWindow.xaml"));
        Assert.Contains("x:Name=\"PersistentAvatarLayer\"", Read("Views", "Kamil", "KamilAssistantHost.xaml"));
    }

    [Fact]
    public void EveryStepIconExistsInTheIconSet()
    {
        var icons = Read("Theme", "Icons.xaml");
        foreach (var step in TourSteps.All)
            Assert.Contains($"x:Key=\"{step.IconKey}\"", icons);
        Assert.Contains("x:Key=\"Glyph.Check\"", icons);
    }

    private static IEnumerable<(string Where, string Text)> AllCopy()
    {
        foreach (var step in TourSteps.All)
        {
            yield return ($"{step.Id}.title", step.Title);
            yield return ($"{step.Id}.body", step.Body);
            if (step.SafetyNote is not null) yield return ($"{step.Id}.safety", step.SafetyNote);
            foreach (var p in step.Points ?? []) yield return ($"{step.Id}.point.{p.Term}", $"{p.Term} {p.Meaning}");
        }
        yield return ("finish.title", TourSteps.FinishTitle);
        yield return ("finish.body", TourSteps.FinishBody);
        yield return ("finish.replay", TourSteps.FinishReplay);
        foreach (var tip in TipCatalog.All) yield return ($"tip.{tip.Id}", tip.Text);
        foreach (var tip in TipOfDay.All) yield return ("tip-of-day", tip);

        var overlay = Read("Tour", "TourOverlay.xaml");
        foreach (Match m in Regex.Matches(overlay, @"(?:Text|Content)=""([^""{]+)"""))
            yield return ("overlay xaml", m.Groups[1].Value);
    }

    [Fact]
    public void CopyFollowsTheBrandRulesNoQuestionOrExclamationMarksNoLongDashesNeutralForm()
    {
        foreach (var (where, text) in AllCopy())
        {
            Assert.DoesNotContain('?', text);
            Assert.DoesNotContain('!', text);
            Assert.DoesNotContain('—', text); // em dash
            Assert.DoesNotContain('–', text); // en dash
            Assert.DoesNotContain("...", text);
            Assert.False(text != text.Trim(), $"{where}: stray whitespace");
        }
    }

    [Fact]
    public void CopyAvoidsTykaniAndVykaniVerbForms()
    {
        // Imperative plural (vykani) and second person singular (tykani) endings the manual bans.
        var banned = new Regex(@"\b(?:uvidíte|uvidíš|zadejte|zadej|klikněte|klikni|vyberte|vyber|otevřete|otevři|zkuste|zkus|zeptejte|zeptej|můžete|můžeš|upravíte|upravíš|stiskněte|stiskni)\b",
            RegexOptions.IgnoreCase);
        foreach (var (where, text) in AllCopy())
            Assert.False(banned.IsMatch(text), $"{where}: '{text}' addresses the reader directly");
    }

    [Fact]
    public void EachStepBodyIsOneOrTwoShortSentences()
    {
        foreach (var step in TourSteps.All)
        {
            var sentences = step.Body.Split(". ", StringSplitOptions.RemoveEmptyEntries).Length;
            Assert.InRange(sentences, 1, 2);
            Assert.EndsWith(".", step.Body);
            Assert.True(step.Body.Length <= 230, $"{step.Id}: body is {step.Body.Length} characters");
            Assert.True(step.Title.Length <= 32, $"{step.Id}: title is {step.Title.Length} characters");
        }

        foreach (var tip in TipCatalog.All)
            Assert.True(tip.Text.Length <= 110, $"{tip.Id}: tip is {tip.Text.Length} characters");
    }

    [Fact]
    public void ReadableTextOnlyNothingInTheTourIsSmallerThanSixteenPixels()
    {
        // Sizes come from tokens; the tokens used for tour text must be the 16 and 24 steps.
        foreach (var file in new[] { "TourOverlay.xaml", "TipChip.xaml" })
        {
            var xaml = Read("Tour", file);
            Assert.DoesNotContain("Size.Text.Meta", xaml);
            Assert.DoesNotContain("Size.Text.Body", xaml);
            Assert.DoesNotContain("Text.Muted", xaml);
            Assert.False(Regex.IsMatch(xaml, @"FontSize=""[0-9.]+"""), $"{file} carries a literal FontSize");
        }

        var theme = Read("Theme", "LaseroTheme.xaml");
        // Tour text is read, not scanned: the card-title and page-title steps are both at least 16px.
        Assert.Matches(@"x:Key=""Size.Text.Section"">(1[6-9]|2d)<", theme);
        Assert.Matches(@"x:Key=""Size.Text.Title"">(2\d|3\d)<", theme);
    }

    [Fact]
    public void TourCodeNeverTouchesTheMachineTheJobOrTheDesign()
    {
        foreach (var file in new[] { "TourOverlay.xaml.cs", "TourModel.cs", "TourLayout.cs", "TourTargetResolver.cs", "GuidanceService.cs", "TipChip.xaml.cs" })
        {
            var code = Read("Tour", file);
            foreach (var forbidden in new[] { "Command", "Connection", "GCode", "Scene", "SerialPort", "Grbl", "RunJob", "RunFraming" })
                Assert.False(code.Contains(forbidden, StringComparison.Ordinal), $"{file} mentions '{forbidden}'");
        }
    }

    [Fact]
    public void OverlayHasNoPerFrameOrPerLayoutSubscriptionSoItCannotSlowTheCanvas()
    {
        var code = Read("Tour", "TourOverlay.xaml.cs") + Read("Tour", "TipChip.xaml.cs");
        Assert.DoesNotContain("LayoutUpdated", code);
        Assert.DoesNotContain("CompositionTarget", code);
        Assert.DoesNotContain("DispatcherTimer", Read("Tour", "TourOverlay.xaml.cs"));
        // Collapsed means out of the layout and render passes.
        Assert.Contains("Visibility=\"Collapsed\"", Read("Tour", "TourOverlay.xaml"));
        Assert.Contains("Visibility=\"Collapsed\"", Read("Tour", "TipChip.xaml"));
    }

    [Fact]
    public void MainWindowHostsTheOverlayAboveTheWizardAndTheTipChipBelowEveryOverlay()
    {
        var xaml = Read("MainWindow.xaml");
        Assert.Contains("<tour:TourOverlay x:Name=\"TourHost\"", xaml);
        Assert.Contains("<tour:TipChip x:Name=\"TipChipHost\"", xaml);

        int Z(string name)
        {
            var m = Regex.Match(xaml, name + @"[^>]*?Panel\.ZIndex=""(\d+)""", RegexOptions.Singleline);
            Assert.True(m.Success, name);
            return int.Parse(m.Groups[1].Value);
        }

        Assert.True(Z("TourHost") > Z("DeviceWizardOverlayHost"));
        Assert.True(Z("TipChipHost") < Z("DeviceWizardOverlayHost"));
    }

    [Fact]
    public void MainWindowKeepsCanvasShortcutsOutOfTheTourAndRestoresFocusAfterwards()
    {
        var code = Read("MainWindow.xaml.cs");
        var preview = code[code.IndexOf("private void OnPreviewKeyDown", StringComparison.Ordinal)..];
        Assert.Contains("if (TourHost.IsOpen) return;", preview[..preview.IndexOf("var isTextInput", StringComparison.Ordinal)]);
        Assert.Contains("TourHost.FocusFallback = FocusAfterTour;", code);
        Assert.Contains("DesignerCanvas.Focus();", code[code.IndexOf("private void FocusAfterTour", StringComparison.Ordinal)..]);
    }

    [Fact]
    public void MainWindowRefusesTheTourWhileAJobIsActiveAndClosesItIfOneStarts()
    {
        var code = Read("MainWindow.xaml.cs");
        Assert.Contains("_viewModel.GCode.IsJobActive || DeviceWizardOverlayHost.IsVisible", code);
        Assert.Contains("TourHost.Cancel();", code);
    }

    [Fact]
    public void MainWindowDecidesNewOrExistingBeforeTheRecoveryPromptAndOffersTheWelcomeAfterIt()
    {
        var code = Read("MainWindow.xaml.cs");
        var loaded = code[code.IndexOf("private void OnLoaded", StringComparison.Ordinal)..];
        var evaluate = loaded.IndexOf("EvaluateGuidance();", StringComparison.Ordinal);
        var prompt = loaded.IndexOf("PromptRecoverySnapshot();", StringComparison.Ordinal);
        var welcome = loaded.IndexOf("ShowWelcomeIfOwed();", StringComparison.Ordinal);
        Assert.True(evaluate > 0 && evaluate < prompt && prompt < welcome);
        Assert.DoesNotContain("new OnboardingWindow", code);
    }

    [Fact]
    public void EveryTipHasARealEventBehindIt()
    {
        var triggers = Read("Tour", "GuidanceTriggers.cs");
        foreach (var id in new[] { "Import", "Selection", "NodeEdit", "Connect", "Frame", "Start", "Kamil" })
            Assert.Contains($"TipCatalog.{id}", triggers);
        Assert.Contains("Scene.FileImported +=", triggers);
        Assert.Contains("Scene.SelectedObjects.CollectionChanged", triggers);
        Assert.Contains("nameof(ConnectionViewModel.IsConnected)", triggers);
        Assert.Contains("JobRunState.Framing", triggers);
        Assert.Contains("JobRunState.Running", triggers);
        Assert.Contains("SceneCanvas.IsNodeEditActiveProperty", triggers);
        Assert.Contains("nameof(KamilAssistantViewModel.State)", triggers);
    }

    [Fact]
    public void ReplayAndHelpEntryPointsExist()
    {
        var home = Read("Views", "HomeView.xaml");
        Assert.Contains("Command=\"{Binding ReplayTourCommand}\"", home);
        Assert.Contains("AutomationProperties.Name=\"Prohlídka aplikace\"", home);
        Assert.Contains("Text=\"TIP DNE\"", home);
        Assert.Contains("Command=\"{Binding Home.NextTipCommand}\"", home);

        var settings = Read("SettingsWindow.xaml");
        Assert.Contains("AutomationProperties.Name=\"Znovu zobrazit úvod\"", settings);
        Assert.Contains("AutomationProperties.Name=\"Obnovit tipy\"", settings);
    }

    [Fact]
    public void TipsUseOneQuietVisualLanguageNoPillNoDoubleBorderNoStockFocusOutline()
    {
        var chip = Read("Tour", "TipChip.xaml");
        Assert.Single(Regex.Matches(chip, "<Border[ >]"));          // one border, no nested frames
        Assert.DoesNotContain("Radius.Pill", chip);
        Assert.DoesNotContain("SelectedSurface", chip);
        Assert.Contains("CornerRadius=\"{StaticResource Radius.Md}\"", chip);
        Assert.Contains("UseLayoutRounding=\"True\"", chip);
        Assert.Contains("Button.Quiet", chip);
        Assert.DoesNotContain("GhostIcon", chip);

        var home = Read("Views", "HomeView.xaml");
        var tip = home[home.IndexOf("HomeTipOfDay", StringComparison.Ordinal)..];
        tip = tip[..tip.IndexOf("Pokračovat v práci", StringComparison.Ordinal)];
        Assert.DoesNotContain("Style=\"{StaticResource Card}\"", tip);
        Assert.DoesNotContain("Radius.Pill", tip);
        Assert.DoesNotContain("SelectedSurface", tip);
        Assert.DoesNotContain("Button.Link", tip);
        Assert.Contains("Button.Quiet", tip);
        Assert.Contains("Glyph.ChevronRight", tip);
        Assert.Contains("Brush.Brand", tip);

        var shared = Read("Theme", "SharedUiStyles.xaml");
        var quiet = shared[shared.IndexOf("x:Key=\"Button.Quiet\"", StringComparison.Ordinal)..];
        quiet = quiet[..quiet.IndexOf("</Style>", StringComparison.Ordinal)];
        Assert.Contains("Property=\"FocusVisualStyle\" Value=\"{x:Null}\"", quiet);
        Assert.Contains("components:FocusVisual.IsVisible", quiet);
        Assert.DoesNotContain("IsKeyboardFocused", quiet);
        Assert.Contains("Brush.TintText", quiet);                   // the tint at rest, a deeper tint on hover
        Assert.Contains("Brush.TintText.Hover", quiet);
        Assert.DoesNotContain("Underline", quiet);                  // hover darkens, it never underlines
        Assert.DoesNotMatch("CornerRadius=\"[0-9]", quiet);
    }

    [Fact]
    public void TourStateIsPersistedOnlyThroughTheGuidanceSettingsSection()
    {
        var store = Read("AppSettingsStore.cs");
        Assert.Contains("public Lasero.App.Tour.GuidancePreferences Guidance", store);
        var main = Read("MainWindow.xaml.cs");
        Assert.Contains("TourHost.WelcomeAnswered += guidance.RecordWelcome;", main);
        Assert.Contains("TourHost.TourEnded += guidance.RecordTour;", main);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "LaseroDesktop.sln")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root not found.");
    }
}
