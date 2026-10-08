using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Lasero.App;
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

    internal static void WaitFor(Func<bool> condition, int milliseconds = 8000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(milliseconds);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            ShellHarness.Pump();
            Thread.Sleep(25);
        }
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
            vm.Connection.ConnectionError = "Spojení s gravírkou se přerušilo. Je potřeba zkontrolovat kabel USB.";
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
            WaitFor(() => vm.Connection.IsConnected && vm.MachineStatus.DisplayState != Lasero.Core.Machines.LaserMachineDisplayState.Connecting);
            Shot("designer-connected-design");
            vm.CurrentScreen = AppScreen.Device; Shot("device-connected");
            vm.GCode.JobState = JobRunState.Running; Shot("device-running");
            vm.CurrentScreen = AppScreen.Designer; Shot("designer-running");
            vm.GCode.JobState = JobRunState.Idle;
            vm.CurrentScreen = AppScreen.Home; Shot("home-connected");
            if (width == 1366)
            {
                foreach (var dpi in new[] { 120.0, 144.0, 192.0 })
                {
                    vm.CurrentScreen = AppScreen.Home; shell.Render($"home-connected-{size}-{dpi}dpi", dpi);
                    vm.CurrentScreen = AppScreen.Device; shell.Render($"device-connected-{size}-{dpi}dpi", dpi);
                }
            }
        });
    }

    // ----------------------------------------------------------------------------- windows and overlays

    internal static void ShowRender(Window window, string name, double width, double height, double dpi = 96, Action? prepare = null)
    {
        window.Width = width;
        window.Height = height;
        window.ShowActivated = false;
        window.ShowInTaskbar = false;
        window.Topmost = false;
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = -20000;
        window.Top = -20000;
        try
        {
            window.Show();
            ShellHarness.Pump();
            prepare?.Invoke();
            window.UpdateLayout();
            ShellHarness.Pump();
            var bitmap = new RenderTargetBitmap(
                (int)Math.Round(window.ActualWidth * dpi / 96), (int)Math.Round(window.ActualHeight * dpi / 96), dpi, dpi, PixelFormats.Pbgra32);
            bitmap.Render(window);
            ShellHarness.Save(bitmap, name);
        }
        finally { window.Close(); }
    }

    [Fact]
    public void Windows()
    {
        if (!Enabled) return;
        InlineTextEditorRenderTests.Ui.Invoke(() =>
        {
            using var shell = new ShellHarness(1366, 768);
            var vm = shell.ViewModel;
            vm.Scene.DrawPrimitive(DesignerTool.Rectangle, new Position(10, 10, 0), new Position(90, 70, 0));

            ShowRender(new SettingsWindow(vm, _ => true), "win-settings-980x1500", 980, 1500);
            ShowRender(new SettingsWindow(vm, _ => true), "win-settings-980x728", 980, 728);
            ShowRender(new MaterialsWindow(vm), "win-materials-1180x720", 1180, 720);
            ShowRender(new MaterialsWindow(vm), "win-materials-1080x600", 1080, 600);
            ShowRender(new DeviceSettingsWindow(vm), "win-device-settings", 900, 760);
            ShowRender(new KeyboardShortcutsWindow(), "win-shortcuts", 760, 720);
            ShowRender(new PreviewWindow(vm), "win-job-preview", 1120, 760);
            ShowRender(new LoginWindow(vm.Account), "win-login", 780, 572);
            ShowRender(new OffsetPathWindow(new OffsetPathViewModel(vm.Scene.SelectedObjects.ToList())), "win-offset", 960, 660);
            var photo = SamplePhoto();
            ShowRender(new BitmapTraceWindow(new BitmapTraceViewModel(photo, 80)), "win-trace", 1080, 720);
            ShowRender(new RasterImportWindow(new RasterImportViewModel(shell.Machine, vm.SettingsStore, photo, 80, 3000, 100, 254)), "win-raster-import", 1080, 720);

            ShowRender(LaseroDialogWindow.Create(new LaseroDialogOptions("Neuložené změny",
                "Projekt obsahuje změny, které ještě nejsou uložené. Uložením se práce zachová.",
                "Uložit projekt", SecondaryText: "Neukládat", CancelText: "Zrušit", Tone: LaseroDialogTone.Warning)), "dlg-save", 420, 300);
            ShowRender(LaseroDialogWindow.Create(new LaseroDialogOptions("Nalezena záloha projektu",
                "Aplikace byla ukončena bez uložení. Lze obnovit poslední automaticky uloženou verzi.",
                "Obnovit", SecondaryText: null, CancelText: "Zahodit", Tone: LaseroDialogTone.Information)), "dlg-recovery", 420, 300);
            ShowRender(LaseroDialogWindow.Create(new LaseroDialogOptions("Probíhající úloha",
                "Laser právě zpracovává úlohu. Před ukončením aplikace je nutné úlohu bezpečně zastavit.",
                "Zastavit a ukončit", CancelText: "Zůstat v aplikaci", Tone: LaseroDialogTone.Danger, DestructivePrimary: true)), "dlg-confirm-danger", 420, 300);
            ShowRender(LaseroDialogWindow.Create(new LaseroDialogOptions("Importovat soubor",
                "Soubor je větší než pracovní plocha a bude zmenšen.", "Rozumím", CancelText: null)), "dlg-info", 420, 300);
        });
    }

    [Theory]
    [InlineData(1366, 768)]
    [InlineData(1080, 640)]
    public void Overlays(int width, int height)
    {
        if (!Enabled) return;
        var size = $"{width}x{height}";
        InlineTextEditorRenderTests.Ui.Invoke(() =>
        {
            using var shell = new ShellHarness(width, height, userId: "audit-user");
            var vm = shell.ViewModel;
            var tour = (Lasero.App.Tour.TourOverlay)shell.Window.FindName("TourHost");
            var chip = (Lasero.App.Tour.TipChip)shell.Window.FindName("TipChipHost");
            tour.ForceStatic = true;
            chip.ForceStatic = true;

            // On the developer machine the legacy onboarding marker makes the account an existing user, so ask for the
            // welcome and the tips explicitly.
            vm.Guidance.ResetIntro();
            tour.ShowWelcome();
            shell.Render($"welcome-{size}");
            tour.Cancel();
            ShellHarness.Pump();
            vm.Guidance.ResetTips();

            vm.CurrentScreen = AppScreen.Designer;
            vm.Guidance.TryOfferTip(Lasero.App.Tour.TipCatalog.Connect);
            shell.Render($"tip-chip-{size}");
            vm.Guidance.DismissTip();

            vm.CurrentScreen = AppScreen.Home;
            vm.ReplayTourCommand.Execute(null);
            shell.Render($"tour-step1-{size}");
            for (var i = 2; i <= 4; i++)
            {
                ((System.Windows.Controls.Button)tour.FindName("NextButton")).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                shell.Render($"tour-step{i}-{size}");
            }
            tour.Cancel();
        });

        InlineTextEditorRenderTests.Ui.Invoke(() =>
        {
            using var shell = new ShellHarness(width, height);
            var overlay = (Lasero.App.Views.DeviceSetup.DeviceWizardOverlay)shell.Window.FindName("DeviceWizardOverlayHost");
            var wizard = shell.ViewModel.CreateDeviceWizard();
            overlay.Show(wizard);
            foreach (var step in new[] { DeviceWizardStep.Intro, DeviceWizardStep.Results, DeviceWizardStep.Setup, DeviceWizardStep.Done })
            {
                wizard.Step = step;
                shell.Render($"wizard-{step.ToString().ToLowerInvariant()}-{size}");
            }
        });
    }
}
