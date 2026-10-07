using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Lasero.App;
using Lasero.App.Components;
using Lasero.App.ViewModels;
using Lasero.App.Views;
using Lasero.Core.Grbl;
using Lasero.Core.Jobs;
using Lasero.Core.Scene;
using Xunit;

namespace Lasero.Tests;

/// <summary>
/// The layout system, pinned two ways: source tests for what markup must use (one header, one footer, one primary), and
/// render tests on the real shell (alignment, equal heights, no clipped command names) at the two reference windows.
/// </summary>
[Collection("WpfUi")]
public sealed class LayoutSystemRenderTests
{
    private static System.Windows.Threading.Dispatcher Ui => InlineTextEditorRenderTests.Ui;

    private static T View<T>(ShellHarness shell) where T : FrameworkElement =>
        LayoutAssertions.Descendants<T>(shell.Window).First();

    private static TextBlock TitleOf(PageHeader header) =>
        LayoutAssertions.Descendants<TextBlock>(header).First(text => text.Text == header.Title);

    [Theory]
    [InlineData(1366, 768)]
    [InlineData(1080, 640)]
    public void HomeZarizeniAndChatShareOneHeaderPaddingAndTitleStyle(int width, int height) => Ui.Invoke(() =>
    {
        using var shell = new ShellHarness(width, height);
        var vm = shell.ViewModel;
        var expected = (double)Application.Current.FindResource("Size.Text.Title");
        foreach (var (screen, type) in new[] { (AppScreen.Home, typeof(HomeView)), (AppScreen.Device, typeof(DeviceView)), (AppScreen.Chat, typeof(ChatView)) })
        {
            vm.CurrentScreen = screen;
            shell.Settle();
            var view = LayoutAssertions.Descendants<FrameworkElement>(shell.Window).First(element => element.GetType() == type);
            var header = LayoutAssertions.Descendants<PageHeader>(view).First();
            var bounds = LayoutAssertions.BoundsIn(header, view);
            var scaffold = LayoutAssertions.Ancestor<PageScaffold>(header)!;
            var padding = scaffold.ActualWidth < 720 ? 24 : 32; // the compact rule: Home at 1080 has a 536 px column
            Assert.True(Math.Abs(bounds.Left - padding) <= 1, $"{screen}: header starts {bounds.Left:0.#} px from the screen edge, expected {padding} at {width}x{height}");
            Assert.True(Math.Abs(bounds.Top - padding) <= 1, $"{screen}: header starts {bounds.Top:0.#} px from the top, expected {padding}");
            var title = TitleOf(header);
            Assert.Equal(expected, title.FontSize);
            Assert.Equal(FontWeights.Bold, title.FontWeight);
            Assert.False(string.IsNullOrWhiteSpace(header.Subtitle), $"{screen} has no subtitle");
            Assert.False(LayoutAssertions.IsClipped(title), $"{screen}: the title is clipped");
        }
    });

    [Theory]
    [InlineData(1366, 768)]
    [InlineData(1080, 640)]
    public void HomeContentAlignsToTheHeaderEdgeAndTheRailIsThePanelWidth(int width, int height) => Ui.Invoke(() =>
    {
        using var shell = new ShellHarness(width, height);
        shell.ViewModel.CurrentScreen = AppScreen.Home;
        shell.Settle();
        var home = View<HomeView>(shell);
        var header = LayoutAssertions.Descendants<PageHeader>(home).First();
        var actions = LayoutAssertions.Descendants<WrapPanel>(home).First(panel =>
            System.Windows.Automation.AutomationProperties.GetAutomationId(panel) == "HomeActions");
        var headerLeft = LayoutAssertions.BoundsIn(header, home).Left;
        var actionsLeft = LayoutAssertions.BoundsIn(actions, home).Left;
        Assert.True(Math.Abs(headerLeft - actionsLeft) <= 1, $"the action row starts at {actionsLeft:0.#}, the header at {headerLeft:0.#}");

        var panelWidth = (double)Application.Current.FindResource("Layout.PanelWidth");
        var rail = LayoutAssertions.Descendants<SectionHeader>(home).First(s => s.Title == "Zařízení");
        var railBorder = LayoutAssertions.Ancestor<Border>(rail)!;
        Assert.True(Math.Abs(home.ActualWidth - LayoutAssertions.BoundsIn(railBorder, home).Left - panelWidth) <= 1,
            "the Home device panel is not the shared 360 px panel width");
    });

    [Theory]
    [InlineData(1366, 768)]
    [InlineData(1080, 640)]
    public void ZarizeniHeaderActionSitsOnTheRightPageMarginAndCentredOnTheTitleBlock(int width, int height) => Ui.Invoke(() =>
    {
        using var shell = new ShellHarness(width, height);
        shell.ViewModel.CurrentScreen = AppScreen.Device;
        shell.Settle();
        var device = View<DeviceView>(shell);
        var header = LayoutAssertions.Descendants<PageHeader>(device).First();
        var button = LayoutAssertions.Descendants<Button>(header).First();
        var buttonBounds = LayoutAssertions.BoundsIn(button, device);
        Assert.True(Math.Abs((device.ActualWidth - 32) - buttonBounds.Right) <= 1.5 || header.ActualWidth >= 1119,
            $"the header action ends {device.ActualWidth - buttonBounds.Right:0.#} px from the right edge, expected 32");
        var title = LayoutAssertions.BoundsIn(TitleOf(header), device);
        var headerBounds = LayoutAssertions.BoundsIn(header, device);
        // The action cluster is vertically centred on the header block, the same rule on every screen.
        var centreOfHeader = headerBounds.Top + headerBounds.Height / 2;
        Assert.True(Math.Abs((buttonBounds.Top + buttonBounds.Height / 2) - centreOfHeader) <= 2, "the header action is not centred on the header");
        Assert.True(title.Bottom <= buttonBounds.Bottom + 80);
    });

    [Theory]
    [InlineData(1366, 768)]
    [InlineData(1080, 640)]
    public void NoCommandNameIsClippedOnAnyFullScreen(int width, int height) => Ui.Invoke(() =>
    {
        using var shell = new ShellHarness(width, height);
        var vm = shell.ViewModel;
        var clipped = new List<string>();
        void Scan(string state)
        {
            shell.Settle();
            clipped.AddRange(LayoutAssertions.ClippedButtonLabels(shell.Window).Select(label => $"{state}: {label}"));
        }

        vm.CurrentScreen = AppScreen.Home; Scan("home");
        vm.CurrentScreen = AppScreen.Chat; Scan("chat");
        vm.CurrentScreen = AppScreen.Device; Scan("device disconnected");
        shell.Machine.Connect(VirtualGrblTransport.PortName);
        LayoutAuditRenderTests.WaitFor(() => vm.Connection.IsConnected);
        Scan("device connected");
        vm.CurrentScreen = AppScreen.Designer; Scan("designer");
        Assert.True(clipped.Count == 0, "Clipped button labels:\n" + string.Join('\n', clipped.Distinct()));
    });

    [Fact]
    public void TheTwoPositionBoxesOnZarizeniAreEquallyHigh() => Ui.Invoke(() =>
    {
        using var shell = new ShellHarness(1366, 768);
        var vm = shell.ViewModel;
        vm.CurrentScreen = AppScreen.Device;
        shell.Machine.Connect(VirtualGrblTransport.PortName);
        LayoutAuditRenderTests.WaitFor(() => vm.Connection.IsConnected);
        shell.Settle();
        var device = View<DeviceView>(shell);
        Border Box(string caption) => LayoutAssertions.Ancestor<Border>(
            LayoutAssertions.Descendants<TextBlock>(device).First(text => text.Text == caption))!;
        Assert.Equal(Box("Poloha stroje").ActualHeight, Box("Pracovní poloha").ActualHeight, 0.5);
    });

    [Theory]
    [InlineData(1366, 768)]
    [InlineData(1080, 640)]
    public void TheNextStepOnTheStripIsOneShortLineThatFits(int width, int height) => Ui.Invoke(() =>
    {
        using var shell = new ShellHarness(width, height);
        var vm = shell.ViewModel;
        vm.CurrentScreen = AppScreen.Designer;
        vm.Scene.DrawPrimitive(DesignerTool.Rectangle, new Position(10, 10, 0), new Position(60, 50, 0));
        shell.Settle();
        var message = LayoutAssertions.Descendants<TextBlock>(shell.Window).First(text => text.Text == GuidanceText.ConnectNextStep);
        Assert.True(LayoutAssertions.IsShown(message), "the next step is not shown on the strip");
        Assert.False(LayoutAssertions.IsClipped(message), $"the next step is cut off at {width}x{height}");
        Assert.True(message.FontSize >= 14);
    });

    [Fact]
    public void TheCompactStripHidesTheIdleJobBadgeOnlyWhenTheWindowIsNarrow() => Ui.Invoke(() =>
    {
        foreach (var (width, expectBadge) in new[] { (1080, false), (1366, true) })
        {
            using var shell = new ShellHarness(width, 768);
            shell.ViewModel.CurrentScreen = AppScreen.Designer;
            shell.ViewModel.Scene.DrawPrimitive(DesignerTool.Rectangle, new Position(10, 10, 0), new Position(60, 50, 0));
            shell.Settle();
            var badge = LayoutAssertions.Descendants<StatusBadge>(shell.Window)
                .FirstOrDefault(b => b.StatusText == shell.ViewModel.GCode.JobBadgeLabel);
            Assert.Equal(expectBadge, badge is not null && badge.IsVisible);
        }
    });

    [Fact]
    public void PageScaffoldUsesTheCompactPaddingOnlyOnANarrowScreen() => Ui.Invoke(() =>
    {
        foreach (var (width, expected) in new[] { (600.0, 24.0), (1000.0, 32.0) })
        {
            var scaffold = new PageScaffold { Header = new TextBlock { Text = "Hlavička" }, Body = new TextBlock { Text = "Obsah" } };
            var host = new Border { Width = width, Height = 400, Child = scaffold };
            var window = new Window
            {
                WindowStyle = WindowStyle.None, ShowActivated = false, ShowInTaskbar = false, Left = -20000, Top = -20000,
                SizeToContent = SizeToContent.WidthAndHeight, Content = host,
            };
            try { window.Show(); ShellHarness.Pump(); host.UpdateLayout(); Assert.Equal(expected, scaffold.PagePadding); }
            finally { window.Close(); }
        }
    });

    [Fact]
    public void DialogFooterPlacesCancelLeftOfThePrimaryEightApartInsideTwentyFourAndKeepsAMinimumBand() => Ui.Invoke(() =>
    {
        var cancel = new Button { Content = "Zrušit", Width = 90, Height = 44 };
        var middle = new Button { Content = "Neukládat", Width = 100, Height = 44, Visibility = Visibility.Collapsed };
        var primary = new Button { Content = "Uložit", Width = 120, Height = 44 };
        var hint = new TextBlock { Text = "Nápověda", TextWrapping = TextWrapping.Wrap };
        DialogFooter.SetIsLeading(hint, true);
        var footer = new DialogFooter { Width = 600 };
        foreach (var child in new UIElement[] { hint, cancel, middle, primary }) footer.Children.Add(child);
        footer.Measure(new Size(600, double.PositiveInfinity));
        footer.Arrange(new Rect(0, 0, 600, footer.DesiredSize.Height));

        Assert.True(footer.DesiredSize.Height >= 72);
        Assert.Equal(600 - 24, primary.TranslatePoint(new Point(primary.ActualWidth, 0), footer).X, 0.5);
        Assert.Equal(8, primary.TranslatePoint(new Point(0, 0), footer).X - (cancel.TranslatePoint(new Point(cancel.ActualWidth, 0), footer).X), 0.5);
        Assert.True(cancel.TranslatePoint(new Point(0, 0), footer).X < primary.TranslatePoint(new Point(0, 0), footer).X);
        Assert.Equal(24, hint.TranslatePoint(new Point(0, 0), footer).X, 0.5);
        Assert.Equal(0, middle.ActualWidth); // collapsed: no space, no gap
    });
}

/// <summary>Source-level rules for the layout system (what markup has to use).</summary>
public sealed class LayoutSystemSourceTests
{
    private static string Root()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "Lasero.App")))
            directory = directory.Parent;
        return directory!.FullName;
    }

    private static string App(params string[] parts) => File.ReadAllText(Path.Combine([Root(), "Lasero.App", .. parts]));

    [Theory]
    [InlineData("Views", "HomeView.xaml")]
    [InlineData("Views", "DeviceView.xaml")]
    [InlineData("Views", "ChatView.xaml")]
    public void EveryFullScreenIsAPageScaffoldWithAPageHeader(string folder, string file)
    {
        var xaml = App(folder, file);
        Assert.Contains("<components:PageScaffold", xaml);
        Assert.Equal(1, Regex.Matches(xaml, "<components:PageHeader ").Count);
        Assert.DoesNotContain("Style=\"{StaticResource PageTitle}\"", xaml);
    }

    [Theory]
    [InlineData("SettingsWindow.xaml")]
    [InlineData("DeviceSettingsWindow.xaml")]
    [InlineData("MaterialsWindow.xaml")]
    [InlineData("RasterImportWindow.xaml")]
    [InlineData("BitmapTraceWindow.xaml")]
    [InlineData("OffsetPathWindow.xaml")]
    [InlineData("KeyboardShortcutsWindow.xaml")]
    [InlineData("LaseroDialogWindow.xaml")]
    public void EveryDialogAndToolWindowEndsInTheSharedFooterWithThePrimaryLast(string file)
    {
        var xaml = Regex.Replace(App(file), "<!--.*?-->", "", RegexOptions.Singleline);
        Assert.Contains("<components:DialogFooter", xaml);
        var footer = Regex.Match(xaml, "<components:DialogFooter.*?</components:DialogFooter>", RegexOptions.Singleline).Value;
        var buttons = Regex.Matches(footer, "<Button\\b[^>]*>").Select(m => m.Value).ToList();
        Assert.NotEmpty(buttons);
        var last = buttons[^1];
        Assert.True(last.Contains("Button.Primary") || last.Contains("IsDefault=\"True\"") || last.Contains("IsDefault=\"True\""),
            $"{file}: the last footer button is not the primary: {last}");
        // Cancel is always before the primary, never after it.
        var cancelIndex = buttons.FindIndex(b => b.Contains("Zrušit") || b.Contains("OnCancelClick") || b.Contains("CancelButton"));
        if (cancelIndex >= 0) Assert.True(cancelIndex < buttons.Count - 1, $"{file}: Cancel must come before the primary");
        Assert.DoesNotMatch("Padding=\"2[08],1[26]\"", xaml);
    }

    [Fact]
    public void TheDestructiveDialogPrimaryIsRedAndTheOrdinaryOneGraphite()
    {
        var code = App("LaseroDialogWindow.xaml.cs");
        Assert.Contains("options.DestructivePrimary ? \"Button.Danger\" : \"Button.Primary\"", code);
    }

    [Theory]
    [InlineData("Views", "HomeView.xaml")]
    [InlineData("Views", "DeviceView.xaml")]
    [InlineData("Views", "ChatView.xaml")]
    public void NoScreenCarriesTwoCompetingPrimaryButtons(string folder, string file)
    {
        var xaml = Regex.Replace(App(folder, file), "<!--.*?-->", "", RegexOptions.Singleline);
        var graphite = Regex.Matches(xaml, "Style=\"\\{StaticResource (Button\\.Primary|Button\\.Large|PrimaryAction|PrimaryBlue)\\}\"").Count;
        // Zařízení's one primary is the ProcessStatusCard primary (a component, so it does not appear as a literal style here).
        Assert.True(graphite <= 1, $"{file} has {graphite} graphite primary buttons");
        Assert.DoesNotContain("Button.DangerSolid", xaml);
    }

    [Fact]
    public void HomeShowsExactlyOneTipOfTheDayAndOneNextStepHook()
    {
        var home = App("Views", "HomeView.xaml");
        Assert.Equal(1, Regex.Matches(home, "AutomationId=\"HomeTipOfDay\"").Count);
        Assert.Contains("NextStep=\"{Binding ScreenNextStep}\"", home);
        Assert.Contains("NextStep=\"{Binding ScreenNextStep}\"", App("Views", "DeviceView.xaml"));
    }

    [Fact]
    public void TheRightPanelsUseTheSharedWidthToken()
    {
        Assert.Contains("Layout.PanelColumn", App("Views", "HomeView.xaml"));
        Assert.Contains("Layout.PanelColumn", App("Views", "DeviceView.xaml"));
        var main = App("MainWindow.xaml");
        Assert.Contains("x:Name=\"InspectorColumn\" Width=\"360\"", main);
        Assert.Contains("x:Key=\"Layout.PanelWidth\">360<", App("Theme", "LaseroTheme.xaml"));
    }

    [Theory]
    [InlineData("Card", "Layout.CardPadding")]
    [InlineData("Card.Compact", "Layout.CardPaddingCompact")]
    public void CardLevelsUseTheirPaddingToken(string style, string token)
    {
        var styles = App("Theme", "SharedUiStyles.xaml");
        var start = styles.IndexOf($"x:Key=\"{style}\"", StringComparison.Ordinal);
        var block = styles.Substring(start, styles.IndexOf("</Style>", start, StringComparison.Ordinal) - start);
        Assert.Contains(token, block);
    }

    [Fact]
    public void ScreensDoNotRebuildTheEmptyStateAsABox()
    {
        foreach (var file in new[] { "HomeView.xaml", "DeviceView.xaml", "ChatView.xaml" })
            Assert.DoesNotContain("Card.Empty", App("Views", file));
        Assert.Contains("Text.EmptyLine", App("Views", "HomeView.xaml"));
    }

    [Fact]
    public void NextStepTextFollowsTheBrandRules()
    {
        var texts = new[]
        {
            GuidanceText.ConnectNextStep, GuidanceText.FramingNextStep, GuidanceText.StartNextStep,
        }.Concat(Enum.GetValues<AppScreen>().SelectMany(screen => new[] { false, true }.SelectMany(connected => new[] { false, true }.SelectMany(design =>
            new[] { false, true }.Select(project => GuidanceText.ScreenNextStep(new ScreenHintContext(screen, false, connected, false, design, project, project)))))))
            .Where(text => text is not null).Cast<string>().Distinct().ToList();
        Assert.NotEmpty(texts);
        foreach (var text in texts)
        {
            Assert.StartsWith("Další krok: ", text);
            Assert.DoesNotContain('?', text);
            Assert.DoesNotContain('!', text);
            Assert.DoesNotContain('–', text);
            Assert.DoesNotContain('—', text);
        }
    }

    [Fact]
    public void ScreenNextStepIsQuietWhileAJobRunsOrAConnectionIsInProgress()
    {
        var running = new ScreenHintContext(AppScreen.Device, true, true, false, true, true, true);
        Assert.Null(GuidanceText.ScreenNextStep(running));
        Assert.Null(GuidanceText.ScreenNextStep(running with { JobActive = false, IsConnected = false, IsConnecting = true }));
        Assert.Equal("Další krok: Připojit laser", GuidanceText.ScreenNextStep(running with { JobActive = false, IsConnected = false }));
        Assert.Equal("Další krok: Nový projekt", GuidanceText.ScreenNextStep(new ScreenHintContext(AppScreen.Home, false, false, false, false, false, false)));
        Assert.Null(GuidanceText.ScreenNextStep(new ScreenHintContext(AppScreen.Chat, false, true, false, true, true, true)));
        Assert.Null(GuidanceText.ScreenNextStep(new ScreenHintContext(AppScreen.Designer, false, true, false, true, true, true)));
    }

    [Theory]
    [InlineData(AppScreen.Designer, JobRunState.Idle, true, false)]
    [InlineData(AppScreen.Designer, JobRunState.Idle, false, true)]
    [InlineData(AppScreen.Designer, JobRunState.Running, true, true)]
    [InlineData(AppScreen.Home, JobRunState.Idle, false, false)]
    [InlineData(AppScreen.Home, JobRunState.Completed, true, true)]
    public void TheJobBadgeFollowsTheScreenStateAndWindowWidth(AppScreen screen, JobRunState state, bool compact, bool shown) =>
        Assert.Equal(shown, ScreenChrome.ShowJobBadge(screen, state, compact));
}
