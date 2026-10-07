using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Lasero.App.ViewModels;
using Lasero.Core.Grbl;
using Lasero.Core.Jobs;
using Lasero.Core.Scene;
using Xunit;

namespace Lasero.Tests;

/// <summary>
/// Layout audit renders. Set LASERO_AUDIT=1 and LASERO_RENDER_OUT to write the screen matrix of
/// docs/layout-audit-2026-10.md; without LASERO_AUDIT these are skipped so the normal suite stays fast.
/// </summary>
[Collection("WpfUi")]
public sealed class LayoutAuditRenderTests
{
    private static bool Enabled => Environment.GetEnvironmentVariable("LASERO_AUDIT") == "1";

    public static string WritePng(string name, Action<DrawingContext, int, int> draw)
    {
        const int size = 160;
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen()) draw(dc, size, size);
        var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var path = Path.Combine(Path.GetTempPath(), name);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (var stream = File.Create(path)) encoder.Save(stream);
        return path;
    }

    private static string SamplePhoto() => WritePng($"audit-photo-{Guid.NewGuid():N}.png", (dc, w, h) =>
    {
        dc.DrawRectangle(new LinearGradientBrush(Colors.White, Colors.DimGray, 45), null, new Rect(0, 0, w, h));
        dc.DrawEllipse(Brushes.Black, null, new Point(w / 2, h / 2), 50, 50);
    });

    [Theory]
    [InlineData(1366, 768)]
    [InlineData(1080, 640)]
    [InlineData(1920, 1080)]
    public void ShellScreens(int width, int height)
    {
        if (!Enabled) return;
        InlineTextEditorRenderTests.Ui.Invoke(() =>
        {
            using var shell = new ShellHarness(width, height);
            var vm = shell.ViewModel;
            var size = $"{width}x{height}";
            void Shot(string name) => shell.Render($"{name}-{size}");

            vm.CurrentScreen = AppScreen.Home; Shot("home-new");
            vm.CurrentScreen = AppScreen.Chat; Shot("chat-empty");

            // Device: disconnected, connecting, error, connected, running.
            vm.CurrentScreen = AppScreen.Device; Shot("device-disconnected");
            vm.Connection.IsConnecting = true; Shot("device-connecting");
            vm.Connection.IsConnecting = false;
            vm.Connection.ConnectionError = "Spojení s gravírkou se přerušilo. Zkontrolujte kabel USB.";
            Shot("device-error");
            vm.Connection.ConnectionError = null;

            // Designer
            vm.CurrentScreen = AppScreen.Designer; Shot("designer-empty");
            vm.Scene.DrawPrimitive(DesignerTool.Rectangle, new Position(60, 60, 0), new Position(160, 140, 0));
            Shot("designer-shape-selected");
            vm.Scene.AddText("LASERO", new Position(180, 200, 0), 24);
            Shot("designer-text-selected");
            vm.Scene.ImportRasterFile(SamplePhoto(), new Lasero.Core.Import.RasterImportOptions { TargetWidthMm = 80 });
            Shot("designer-image-selected");
            vm.Scene.SelectedObjects.Clear();
            Shot("designer-design-no-selection");

            // Connected (simulator) with a design: strip shows the next step.
            shell.Machine.Connect(VirtualGrblTransport.PortName);
            ShellHarness.Pump();
            Shot("designer-connected-design");
            vm.CurrentScreen = AppScreen.Device; Shot("device-connected");
            vm.GCode.JobState = JobRunState.Running; Shot("device-running");
            vm.CurrentScreen = AppScreen.Designer; Shot("designer-running");
            vm.GCode.JobState = JobRunState.Idle;
            vm.CurrentScreen = AppScreen.Home; Shot("home-connected");
        });
    }
}
