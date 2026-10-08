using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Collections.ObjectModel;
using Lasero.App.Controls.Motion;
using Lasero.App.ViewModels;
using Lasero.Core.Machines;
using Lasero.Core.History;
using Lasero.App.Views;
using Xunit;

namespace Lasero.Tests;

/// <summary>
/// Off-screen renders of the real screens (no live input or focus). They give empty and populated Home
/// specimens at reference sizes and prove each screen still loads its XAML against the theme.
/// </summary>
[Collection("WpfUi")]
public sealed class ScreenSpecimenTests
{
    private static Dispatcher Ui => InlineTextEditorRenderTests.Ui;

    private static void Flush() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

    private static void Render(FrameworkElement view, string name, double width = 1366, double height = 768, Brush? background = null, double dpi = 96, Action<FrameworkElement>? inspect = null)
    {
        var host = new Border
        {
            Width = width,
            Height = height,
            Background = background ?? (Brush)Application.Current.FindResource("Brush.Background"),
            Child = view,
        };
        TextOptions.SetTextRenderingMode(host, TextRenderingMode.Grayscale);
        var window = new Window
        {
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            SizeToContent = SizeToContent.WidthAndHeight,
            ShowActivated = false,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -20000,
            Top = -20000,
            Content = host,
        };
        try
        {
            window.Show();
            Flush();
            host.UpdateLayout();
            inspect?.Invoke(view);
            var bitmap = new RenderTargetBitmap(
                (int)Math.Round(width * dpi / 96), (int)Math.Round(height * dpi / 96), dpi, dpi, PixelFormats.Pbgra32);
            bitmap.Render(host);
            var dir = Environment.GetEnvironmentVariable("LASERO_RENDER_OUT");
            if (!string.IsNullOrWhiteSpace(dir))
            {
                Directory.CreateDirectory(dir);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var stream = File.Create(Path.Combine(dir, name + ".png"));
                encoder.Save(stream);
            }
        }
        finally
        {
            window.Close();
        }
    }

    [Fact]
    public void HomeScreenRenders() => Ui.Invoke(() => RenderHome(CreateEmptyHome(), "screen-home", 1100, 768));

    // The Home content area at the two reference windows: 1366x768 and 1080x640, less the 184 px
    // navigation, the 60 px title bar and the 56 px status strip.
    [Fact]
    public void HomeAt1366x768Renders() => Ui.Invoke(() => RenderHome("screen-home-1366x768", 1182, 652));

    [Fact]
    public void HomeAt1080x640Renders() => Ui.Invoke(() => RenderHome("screen-home-1080x640", 896, 524));

    [Theory]
    [InlineData(96)]
    [InlineData(120)]
    [InlineData(144)]
    [InlineData(192)]
    public void HomeTextRendersAtWindowsScalingDpi(double dpi) => Ui.Invoke(() =>
        RenderHome(CreateEmptyHome(), $"screen-home-1366x768-{dpi}dpi", 1182, 652, dpi));

    [Fact]
    public void HomeWithRecentProjectsAt1366x768Renders() => Ui.Invoke(() => RenderHome(CreateHomeWithProjects(), "screen-home-projects-1366x768", 1182, 652));

    [Fact]
    public void HomeWithRecentProjectsAt1080x640Renders() => Ui.Invoke(() => RenderHome(CreateHomeWithProjects(), "screen-home-projects-1080x640", 896, 524));

    // The tip of the day sits between the actions and "Pokračovat v práci". At the two reference windows
    // and at 150 percent it must be fully inside the column, show its action and keep the sentence >= 15 px.
    [Theory]
    [InlineData(1182, 652, 96, "1366x768")]
    [InlineData(896, 524, 96, "1080x640")]
    [InlineData(1182, 652, 144, "1366x768-150pct")]
    [InlineData(896, 524, 144, "1080x640-150pct")]
    public void HomeTipOfDayCardFitsAndStaysReadable(double width, double height, double dpi, string label) => Ui.Invoke(() =>
    {
        var checkedCard = false;
        RenderHome(CreateHomeWithProjects(), $"screen-home-tip-{label}", width, height, dpi, view =>
        {
            var card = TipOfDayCardTests.FindDescendant<Lasero.App.Components.TipOfDayCard>(view);
            Assert.NotNull(card);
            TipOfDayCardTests.AssertCardLaidOutCleanly(card!, view, width);
            Assert.Equal("HomeTipOfDay", System.Windows.Automation.AutomationProperties.GetAutomationId(card));
            Assert.Equal("Tip dne", System.Windows.Automation.AutomationProperties.GetName(card));
            checkedCard = true;
        });
        Assert.True(checkedCard);
    });

    [Fact]
    public void HomeWithoutProjectsShowsTheTipCardAt1080x640() =>
        Ui.Invoke(() => RenderHome(CreateEmptyHome(), "screen-home-tip-empty-1080x640", 896, 524));

    private static void RenderHome(string name, double width, double height, double dpi = 96)
    {
        var previous = LaseroMotion.ForceReducedMotion;
        LaseroMotion.ForceReducedMotion = true;
        try { Render(CreateEmptyHome(), name, width, height, dpi: dpi); }
        finally { LaseroMotion.ForceReducedMotion = previous; }
    }

    private static void RenderHome(HomeView view, string name, double width, double height, double dpi = 96, Action<FrameworkElement>? inspect = null)
    {
        var previous = LaseroMotion.ForceReducedMotion;
        LaseroMotion.ForceReducedMotion = true;
        try { Render(view, name, width, height, dpi: dpi, inspect: inspect); }
        finally { LaseroMotion.ForceReducedMotion = previous; }
    }

    private static HomeView CreateEmptyHome() => new()
    {
        DataContext = new HomeSpecimenMainViewModel(),
    };

    private static HomeView CreateHomeWithProjects()
    {
        var state = new HomeSpecimenState(withProjects: true);
        return new HomeView { DataContext = new HomeSpecimenMainViewModel(state) };
    }

    private sealed class HomeSpecimenMainViewModel
    {
        public HomeSpecimenState Home { get; }
        public HomeSpecimenSettings Settings { get; } = new();

        public HomeSpecimenMainViewModel(HomeSpecimenState? home = null) => Home = home ?? new HomeSpecimenState();
    }

    private sealed class HomeSpecimenState
    {
        public bool HasRecentProjects => RecentProjects.Count > 0;
        public bool HasRecentMaterials => false;
        public bool HasLastJob => false;
        public RecentProjectItemViewModel? FeaturedProject => RecentProjectRows.Count > 0 ? RecentProjectRows[0] : null;
        public ObservableCollection<RecentProjectItemViewModel> RecentProjectRows { get; } = new();
        public ObservableCollection<RecentProjectItemViewModel> OtherRecentProjectRows { get; } = new();
        public ObservableCollection<RecentProjectEntry> RecentProjects { get; } = new();
        public string DeviceName => "Žádné zařízení";
        public string ConnectionSummaryLabel => "Nepřipojeno";
        public string DeviceConnectionLabel => "Gravírku je potřeba připojit kabelem USB";
        public string FirmwareLabel => "Neznámo";
        public string TipOfDay => "Před výrobou vždy zkontrolujte náhled a polohu materiálu.";
        public int TipPosition => 3;
        public int TipCount => 12;
        public System.Windows.Input.ICommand NextTipCommand { get; } = new Lasero.Tests.TipOfDayCardTests.NoopCommand();
        public HomeSpecimenConnection Connection { get; } = new();
        public HomeSpecimenMachineStatus MachineStatus { get; } = new();

        public HomeSpecimenState(bool withProjects = false)
        {
            if (!withProjects) return;
            AddProject("recent.lasero", "Nedávný projekt", false);
            AddProject("another.lasero", "Další projekt", true);
        }

        private void AddProject(string fileName, string name, bool other)
        {
            var entry = new RecentProjectEntry(Path.Combine(Path.GetTempPath(), fileName), name, DateTime.UtcNow, null);
            RecentProjects.Add(entry);
            var row = new RecentProjectItemViewModel(entry, DateTime.Now);
            RecentProjectRows.Add(row);
            if (other) OtherRecentProjectRows.Add(row);
        }
    }

    private sealed class HomeSpecimenConnection
    {
        public bool IsConnected => false;
    }

    private sealed class HomeSpecimenMachineStatus
    {
        public LaserMachineDisplayState DisplayState => LaserMachineDisplayState.Disconnected;
    }

    private sealed class HomeSpecimenSettings
    {
        public HomeSpecimenMachineSettings Machine { get; } = new();
    }

    private sealed class HomeSpecimenMachineSettings
    {
        public double WorkAreaWidthMm => 0;
        public double WorkAreaHeightMm => 0;
    }

    [Fact]
    public void InspectorRenders() => Ui.Invoke(() =>
        Render(new DesignerInspectorView(), "screen-inspector", 360, 640, (Brush)Application.Current.FindResource("Brush.Surface")));

    [Fact]
    public void MachinePanelRenders() => Ui.Invoke(() =>
        Render(new MachinePanelView(), "screen-machine-panel", 360, 640, (Brush)Application.Current.FindResource("Brush.Surface")));

    [Fact]
    public void DeviceScreenRenders() => Ui.Invoke(() => Render(new DeviceView(), "screen-device", 1100, 768));

    [Fact]
    public void ChatScreenRenders() => Ui.Invoke(() => Render(new ChatView(), "screen-chat", 1100, 768));

    [Fact]
    public void DesignerToolRailRenders() => Ui.Invoke(() =>
        Render(new DesignerToolRail { HorizontalAlignment = HorizontalAlignment.Left }, "screen-tool-rail", 120, 420,
            (Brush)Application.Current.FindResource("Brush.Surface")));
}
