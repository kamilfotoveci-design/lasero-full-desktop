using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Lasero.App.Views;
using Xunit;

namespace Lasero.Tests;

/// <summary>
/// Off-screen renders of the real screens (no live window, no input, no focus). They exist to give
/// before/after specimens at 1366x768 (set LASERO_RENDER_OUT) and to prove each screen still loads
/// its XAML against the theme. The views get no DataContext, so lists are empty, which is also how the
/// app looks on first run.
/// </summary>
[Collection("WpfUi")]
public sealed class ScreenSpecimenTests
{
    private static Dispatcher Ui => InlineTextEditorRenderTests.Ui;

    private static void Flush() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

    private static void Render(FrameworkElement view, string name, double width = 1366, double height = 768, Brush? background = null)
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
            var bitmap = new RenderTargetBitmap((int)width, (int)height, 96, 96, PixelFormats.Pbgra32);
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
    public void HomeScreenRenders() => Ui.Invoke(() => Render(new HomeView(), "screen-home", 1100, 768));

    // The Home content area at the two reference windows: 1366x768 and 1080x640, less the 184 px
    // navigation, the 60 px title bar and the 56 px status strip.
    [Fact]
    public void HomeAt1366x768Renders() => Ui.Invoke(() => Render(new HomeView(), "screen-home-1366x768", 1182, 652));

    [Fact]
    public void HomeAt1080x640Renders() => Ui.Invoke(() => Render(new HomeView(), "screen-home-1080x640", 896, 524));

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
