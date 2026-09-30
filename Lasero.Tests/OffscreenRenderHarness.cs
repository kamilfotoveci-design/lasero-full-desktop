using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Lasero.App;
using Lasero.App.Components;
using Lasero.App.ViewModels;
using Lasero.App.Views.DeviceSetup;
using Lasero.Core.Grbl;
using Lasero.Core.Machines;

namespace Lasero.Tests;

/// <summary>
/// A screenshot tool that happens to live in the test project. It does nothing unless the environment
/// variable LASERO_RENDER_DIR names a folder, so an ordinary test run is unaffected; with it set, the
/// device-setup surfaces are hosted in a window parked far off screen and written out as PNGs at 100%
/// and 150% (144 dpi). Nothing here touches a serial port: the wizard is driven by setting its Step,
/// exactly as DeviceWizardViewModelTests does.
/// LASERO_RENDER_TAG prefixes the file names so before and after runs can sit side by side.
/// </summary>
public sealed class OffscreenRenderHarness
{
    [Fact]
    public void RenderDeviceSurfaces()
    {
        var dir = Environment.GetEnvironmentVariable("LASERO_RENDER_DIR");
        if (string.IsNullOrWhiteSpace(dir)) return;
        var tag = Environment.GetEnvironmentVariable("LASERO_RENDER_TAG") ?? "out";
        Directory.CreateDirectory(dir);

        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { Run(dir, tag); }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(120)), "offscreen render timed out");
        if (failure is not null) throw new InvalidOperationException("offscreen render failed", failure);
    }

    private static void Run(string dir, string tag)
    {
        var app = new Lasero.App.App();
        app.InitializeComponent();

        var wizardDir = Path.Combine(Path.GetTempPath(), "lasero-render-harness", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(wizardDir);
        var transport = new VirtualGrblTransport { ResponseDelay = TimeSpan.Zero };
        var machine = new GrblConnection(transport);
        var settings = new AppSettingsStore(Path.Combine(wizardDir, "settings.json"));
        var connection = new ConnectionViewModel(machine, settings);
        var scanner = new DeviceScanner(new NullMachineFactory(), () => Array.Empty<string>());
        var wizard = new DeviceWizardViewModel(scanner, connection, machine, settings);

        var overlay = new DeviceWizardOverlay();
        var window = new Window
        {
            Width = 760, Height = 780, WindowStyle = WindowStyle.None, ShowInTaskbar = false,
            ShowActivated = false, Left = -20000, Top = 0, Content = overlay,
            Background = new SolidColorBrush(Color.FromRgb(0xF7, 0xF7, 0xF5)),
        };
        window.Show();
        overlay.Show(wizard);
        Pump(500);

        var sheet = (FrameworkElement)overlay.FindName("Sheet");

        void Shot(string name)
        {
            Pump(450);
            foreach (var scale in new[] { 1.0, 1.5 })
                Save(sheet, Path.Combine(dir, $"{tag}-{name}{(scale > 1 ? "-144dpi" : "")}.png"), scale);
        }

        Shot("wizard-1-intro");

        ((ToggleButton)Find(overlay, "ManualToggle")).IsChecked = true;
        Shot("wizard-2-upresnit");
        ((ToggleButton)Find(overlay, "ManualToggle")).IsChecked = false;

        wizard.Step = DeviceWizardStep.Scanning;
        wizard.ScanStatus = "Zkouším porty COM3, COM4";
        Shot("wizard-3-scanning");

        wizard.Step = DeviceWizardStep.Results;
        Shot("wizard-4-results-nothing");

        wizard.Step = DeviceWizardStep.Done;
        Shot("wizard-5-done");

        window.Close();

        // Glyph crispness strip: the machine glyph at every icon size the app uses, plus the large art.
        var strip = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(16), Background = Brushes.White };
        foreach (var key in new[] { "Size.Icon.Sm", "Size.Icon.Md", "Size.Icon.Lg", "Size.Icon.Xl" })
        {
            strip.Children.Add(new IconGlyph
            {
                IconData = (Geometry)app.FindResource("Glyph.Device"),
                Width = (double)app.FindResource(key), Height = (double)app.FindResource(key),
                Foreground = (Brush)app.FindResource("Brush.TextPrimary"), Margin = new Thickness(0, 0, 20, 0),
                VerticalAlignment = VerticalAlignment.Center,
            });
        }
        strip.Children.Add(new DeviceArt { Margin = new Thickness(12, 0, 0, 0) });
        strip.Children.Add(new DeviceArt { IsActive = true, Margin = new Thickness(12, 0, 0, 0) });
        var stripWindow = new Window
        {
            SizeToContent = SizeToContent.WidthAndHeight, WindowStyle = WindowStyle.None, ShowInTaskbar = false,
            ShowActivated = false, Left = -20000, Top = 0, Content = strip,
        };
        stripWindow.Show();
        Pump(300);
        foreach (var scale in new[] { 1.0, 1.5, 4.0 })
            Save(strip, Path.Combine(dir, $"{tag}-glyphs{(scale > 1 ? $"-{scale * 96:0}dpi" : "")}.png"), scale);
        stripWindow.Close();
    }

    private static FrameworkElement Find(FrameworkElement root, string name) =>
        (FrameworkElement)root.FindName(name) ?? throw new InvalidOperationException(name);

    private static void Pump(int milliseconds)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(milliseconds) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }

    private static void Save(FrameworkElement element, string path, double scale)
    {
        element.UpdateLayout();
        var width = (int)Math.Ceiling(element.ActualWidth * scale);
        var height = (int)Math.Ceiling(element.ActualHeight * scale);
        var bitmap = new RenderTargetBitmap(width, height, 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bitmap.Render(element);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }
}
