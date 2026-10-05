using System.IO;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Lasero.App.ViewModels;
using Lasero.Core.LaseroApi;
using Xunit;

namespace Lasero.Tests;

/// <summary>
/// Crispness of the shared window rendering and of the sign-in window. The root DPI is forced with
/// <c>VisualTreeHelper.SetRootDpi</c> so layout rounding snaps to the real device grid of a 125, 150 or
/// 200 percent monitor - rendering at 96 dpi and scaling afterwards would hide exactly the blur these
/// tests exist to catch. Set LASERO_LOGIN_SHOTS to a folder to also get PNGs of the sign-in window.
/// </summary>
public sealed class LoginWindowRenderTests
{
    private static Dispatcher Ui => InlineTextEditorRenderTests.Ui;

    public static IEnumerable<object[]> Scales => [[1.0], [1.25], [1.5], [2.0]];

    private static BitmapSource Render(FrameworkElement root, double width, double height, double scale)
    {
        VisualTreeHelper.SetRootDpi(root, new DpiScale(scale, scale));
        root.Measure(new Size(width, height));
        root.Arrange(new Rect(0, 0, width, height));
        root.UpdateLayout();
        var bitmap = new RenderTargetBitmap(
            (int)Math.Round(width * scale), (int)Math.Round(height * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bitmap.Render(root);
        bitmap.Freeze();
        return bitmap;
    }

    private static byte[] Pixels(BitmapSource bitmap)
    {
        var data = new byte[bitmap.PixelWidth * 4 * bitmap.PixelHeight];
        bitmap.CopyPixels(data, bitmap.PixelWidth * 4, 0);
        return data;
    }

    private static void Save(BitmapSource bitmap, string name)
    {
        var folder = Environment.GetEnvironmentVariable("LASERO_LOGIN_SHOTS");
        if (string.IsNullOrWhiteSpace(folder)) return;
        Directory.CreateDirectory(folder);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(Path.Combine(folder, name));
        encoder.Save(stream);
    }

    /// <summary>Distinct colours on one pixel row (a background colour and a line colour count as two).
    /// More than two means some pixel is a blend of the two: the anti-aliased fringe of a soft edge.</summary>
    private static int DistinctColours(byte[] pixels, int stride, int row, int fromX, int toX)
    {
        var seen = new HashSet<int>();
        for (var x = fromX; x < toX; x++)
        {
            var i = row * stride + x * 4;
            // Quantise to 8 levels so 1-2 step gamma noise is not mistaken for a blend.
            seen.Add(((pixels[i] >> 3) << 16) | ((pixels[i + 1] >> 3) << 8) | (pixels[i + 2] >> 3));
        }
        return seen.Count;
    }

    private static int BorderBlendPixels(double scale, bool layoutRounding)
    {
        return Ui.Invoke(() =>
        {
            // Fractional margin and size put the 1px border off the device grid unless layout
            // rounding snaps it - the situation every window is in at 125 and 150 percent.
            var grid = new Grid { Background = Brushes.White, UseLayoutRounding = layoutRounding, SnapsToDevicePixels = layoutRounding };
            grid.Children.Add(new Border
            {
                Margin = new Thickness(10.3, 10.3, 0, 0), Width = 100.4, Height = 40.2,
                HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
                BorderBrush = Brushes.Black, BorderThickness = new Thickness(1),
            });
            var bitmap = Render(grid, 200, 100, scale);
            var row = (int)Math.Round(30 * scale);
            return DistinctColours(Pixels(bitmap), bitmap.PixelWidth * 4, row, 0, bitmap.PixelWidth) - 2;
        });
    }

    [Theory]
    [MemberData(nameof(Scales))]
    public void OnePixelBorderStaysOnePixelWideWithLayoutRounding(double scale)
    {
        Assert.Equal(0, BorderBlendPixels(scale, layoutRounding: true));
    }

    [Theory]
    [InlineData(1.25)]
    [InlineData(1.5)]
    public void TheSameBorderBlursWithoutLayoutRounding(double scale)
    {
        // Guards the metric itself: if this stops finding blended pixels, the test above proves nothing.
        Assert.True(BorderBlendPixels(scale, layoutRounding: false) > 0);
    }

    private static AccountViewModel Account() =>
        new(new LaseroAuthClient(new HttpClient()),
            new LaseroAccountClient(new HttpClient()),
            new SessionStore(Path.Combine(Path.GetTempPath(), $"lasero-login-render-{Guid.NewGuid():N}.dat")),
            new DeviceIdStore(Path.Combine(Path.GetTempPath(), $"lasero-login-render-{Guid.NewGuid():N}.txt")),
            new DeviceActivationClient(new HttpClient()));

    [Theory]
    [MemberData(nameof(Scales))]
    public void SignInWindowEdgesAndDividerAreSingleCrispLines(double scale)
    {
        var (window, columnDivider, topEdge, bottomEdge, titleRule) = Ui.Invoke(() =>
        {
            var w = new Lasero.App.LoginWindow(Account());
            try
            {
            var root = (FrameworkElement)w.Content;
            // The window is never shown, so its template (and the inherited properties it would pass
            // down) is not in the tree. Hand the root exactly what the real window computes for itself.
            root.SetValue(FrameworkElement.UseLayoutRoundingProperty, w.UseLayoutRounding);
            root.SetValue(UIElement.SnapsToDevicePixelsProperty, w.SnapsToDevicePixels);
            root.SetValue(System.Windows.Media.TextOptions.TextFormattingModeProperty, System.Windows.Media.TextOptions.GetTextFormattingMode(w));
            var bitmap = Render(root, w.Width, w.Height, scale);
            Save(bitmap, $"login-{(int)Math.Round(scale * 100)}.png");
            var data = Pixels(bitmap);
            var stride = bitmap.PixelWidth * 4;
            var midRow = (int)Math.Round(w.Height * scale * 0.6);
            var x0 = (int)Math.Floor(284 * scale);
            var x1 = (int)Math.Ceiling(292 * scale);
            // Outer 1px border: one flat row along the top and bottom, away from the rounded corners.
            var from = (int)Math.Round(40 * scale);
            var to = (int)Math.Round(700 * scale);
            return (bitmap,
                DistinctColours(data, stride, midRow, x0, x1),
                DistinctColours(data, stride, 0, from, to),
                DistinctColours(data, stride, bitmap.PixelHeight - 1, from, to),
                // Title bar bottom rule sits at 44 DIP; scan a column well inside the right pane.
                DistinctColoursColumn(data, stride, (int)Math.Round(400 * scale), (int)Math.Floor(40 * scale), (int)Math.Ceiling(48 * scale)));
            }
            // A constructed Window registers with Application.Windows even when never shown; leaving
            // it there would let it become Application.MainWindow for every other test on this thread.
            finally { w.Close(); }
        });

        Assert.Equal((int)Math.Round(780 * scale), window.PixelWidth);
        Assert.True(columnDivider <= 2, $"column divider blends at {scale}: {columnDivider} colours");
        Assert.Equal(1, topEdge);
        Assert.Equal(1, bottomEdge);
        Assert.True(titleRule <= 3, $"title bar rule blends at {scale}: {titleRule} colours (title fill, rule, pane)");
    }

    private static int DistinctColoursColumn(byte[] pixels, int stride, int x, int fromRow, int toRow)
    {
        var seen = new HashSet<int>();
        for (var y = fromRow; y < toRow; y++)
        {
            var i = y * stride + x * 4;
            seen.Add(((pixels[i] >> 3) << 16) | ((pixels[i + 1] >> 3) << 8) | (pixels[i + 2] >> 3));
        }
        return seen.Count;
    }

    [Fact]
    public void SignInWindowSizeIsAWholeNumberOfDevicePixelsAtEveryCommonScale()
    {
        // 125 and 175 percent are the scales that expose half pixels: 570 x 1.25 = 712.5.
        foreach (var dip in new[] { 780.0, 572.0, 288.0, 44.0 })
            foreach (var scale in new[] { 1.0, 1.25, 1.5, 1.75, 2.0 })
                Assert.Equal(0, dip * scale % 1, 6);
    }

    // ---- source-level guards on the shared rendering settings ----

    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "Lasero.App"))) dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("Repository root not found.");
    }

    private static string Read(params string[] relative) =>
        File.ReadAllText(Path.Combine(new[] { Root(), "Lasero.App" }.Concat(relative).ToArray()));

    [Fact]
    public void BaseWindowStyleTurnsOnLayoutRoundingAndPixelSnapping()
    {
        var theme = Read("Theme", "LaseroTheme.xaml");
        var style = Regex.Match(theme, "<Style TargetType=\"Window\">.*?</Style>", RegexOptions.Singleline).Value;
        Assert.Matches("UseLayoutRounding\"\\s+Value=\"True\"", style);
        Assert.Matches("SnapsToDevicePixels\"\\s+Value=\"True\"", style);
        Assert.Matches("TextFormattingMode\"\\s+Value=\"Display\"", style);
    }

    [Fact]
    public void AppOverridesWindowMetadataSoWindowsWithoutTheStyleStillRoundToDevicePixels()
    {
        var app = Read("App.xaml.cs");
        Assert.Contains("UseLayoutRoundingProperty.OverrideMetadata", app);
        Assert.Contains("SnapsToDevicePixelsProperty.OverrideMetadata", app);
        Assert.Contains("TextFormattingMode.Display", app);
    }

    [Fact]
    public void BitmapsAreResampledWithTheHighQualityFilterByDefault()
    {
        var theme = Read("Theme", "LaseroTheme.xaml");
        Assert.Matches("<Style TargetType=\"Image\">\\s*<Setter Property=\"RenderOptions.BitmapScalingMode\" Value=\"HighQuality\"", theme);
    }

    [Fact]
    public void NoWindowIsTransparent()
    {
        // AllowsTransparency=True moves a window onto the layered, software-composited path: text drops
        // to grayscale, the DWM shadow and rounded corners are lost. Popups are exempt (they are not
        // windows); only the root Window element of each *Window.xaml is checked.
        var offenders = Directory.EnumerateFiles(Path.Combine(Root(), "Lasero.App"), "*.xaml", SearchOption.AllDirectories)
            .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            .Where(p => Regex.IsMatch(File.ReadAllText(p), "^\\s*<Window\\b", RegexOptions.Multiline))
            .Where(p => Regex.Match(File.ReadAllText(p), "<Window\\b[^>]*>", RegexOptions.Singleline).Value.Contains("AllowsTransparency"))
            .Select(Path.GetFileName)
            .ToList();
        Assert.Empty(offenders);
    }

    [Fact]
    public void SignInFieldFocusIsNotTheErrorColour()
    {
        var xaml = Read("LoginWindow.xaml");
        // Focus border is graphite, red is reserved for the error state, and the border never thickens
        // (a 2px focus border moved the text by a pixel).
        Assert.Matches("IsKeyboardFocused\"\\s+Value=\"True\">\\s*<Setter TargetName=\"Bg\" Property=\"BorderBrush\" Value=\"\\{StaticResource Brush.PrimaryAction\\}\"", xaml);
        Assert.DoesNotContain("Property=\"BorderThickness\" Value=\"2\"", xaml);
        Assert.Matches("Trigger Property=\"Tag\" Value=\"error\">\\s*<Setter TargetName=\"Bg\" Property=\"BorderBrush\" Value=\"\\{StaticResource Brush.Danger\\}\"", xaml);
    }

    [Fact]
    public void SignInCopyUsesNeitherTykaniNorVykaniNorQuestionOrExclamationMarks()
    {
        var text = Read("LoginWindow.xaml") + Read("ViewModels", "AccountViewModel.cs");
        var strings = Regex.Matches(text, "(?:Text|Content)=\"([^\"{]+)\"|StatusMessage = \"([^\"]+)\"|(?:Email|Password)Error = \"([^\"]+)\"")
            .SelectMany(m => m.Groups.Cast<Group>().Skip(1).Where(g => g.Success).Select(g => g.Value))
            .ToList();
        Assert.NotEmpty(strings);
        foreach (var s in strings)
        {
            Assert.DoesNotContain("?", s);
            Assert.DoesNotContain("!", s);
            Assert.DoesNotMatch("\\b(Přihlaste|Zadejte|Zkontrolujte|Zkuste|Vítejte|Tvořte|vás|váš|můžete|prosím)\\b", s);
        }
    }

    // ---- the pure corner-preference decision ----

    [Theory]
    [InlineData(19045)] // Windows 10 22H2
    [InlineData(21996)]
    [InlineData(0)]
    public void OlderWindowsRequestNoCornerPreferenceSoTheWindowStaysSquareWithoutArtifacts(int build)
        => Assert.Null(Lasero.App.WindowFrameHook.ChooseCornerPreference(build));

    [Theory]
    [InlineData(22000)] // Windows 11 21H2
    [InlineData(22631)]
    [InlineData(26100)]
    public void Windows11RequestsRoundCorners(int build)
        => Assert.Equal(2, Lasero.App.WindowFrameHook.ChooseCornerPreference(build));
}
