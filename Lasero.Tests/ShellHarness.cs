using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Lasero.App;
using Lasero.App.ViewModels;
using Lasero.Core.Grbl;
using Lasero.Core.Machines;
using Lasero.Core.LaseroApi;

namespace Lasero.Tests;

/// <summary>
/// The whole shell (<see cref="MainWindow"/> with a real <see cref="MainViewModel"/> graph) built on a
/// scratch directory and the built-in simulator, shown far off-screen and rendered through
/// <see cref="RenderTargetBitmap"/>. No real COM port, no real profile folder and no input of any kind.
/// Used by the layout audit and by the render-level layout assertions.
/// </summary>
internal sealed class ShellHarness : IDisposable
{
    private readonly string _directory;

    public MainViewModel ViewModel { get; }
    public MainWindow Window { get; }
    public GrblConnection Machine { get; }

    private sealed class NoNetworkHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The layout harness never talks to the network.");
    }

    private sealed class NoPorts : IGrblPortScanner
    {
        public Task<GrblPortScanResult> ScanAsync(IProgress<GrblPortScanProgress>? progress = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new GrblPortScanResult([], []));
    }

    public ShellHarness(double width, double height, string? userId = null)
    {
        _directory = Path.Combine(Path.GetTempPath(), "lasero-shell-harness", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        string P(string name) => Path.Combine(_directory, name);

        var settings = new AppSettingsStore(P("settings.json"));
        Machine = new GrblConnection(new VirtualGrblTransport { ResponseDelay = TimeSpan.Zero });
        var connection = new ConnectionViewModel(Machine, settings, new NoPorts());
        var machineStatus = new MachineStatusViewModel(Machine);
        var jog = new JogViewModel(Machine, settings);
        var console = new ConsoleViewModel(Machine);
        var scene = new SceneViewModel();
        var gcode = new GCodeViewModel(Machine, scene, settings);
        var account = new AccountViewModel(
            new LaseroAuthClient(new HttpClient(new NoNetworkHandler())),
            new LaseroAccountClient(new HttpClient(new NoNetworkHandler())),
            new SessionStore(P("session.dat")),
            new DeviceIdStore(P("device-id.txt")),
            new DeviceActivationClient(new HttpClient(new NoNetworkHandler())));
        if (userId is not null) account.UserId = userId;
        var recent = new RecentProjectsStore(P("recent.json"));
        var jobs = new JobHistoryStore(P("jobs.json"));
        var materialsStore = new MaterialPresetStore(P("materials.json"));
        var home = new HomeViewModel(recent, jobs, connection, machineStatus, gcode, account);
        var materials = new MaterialsViewModel(materialsStore, settings, new MaterialSyncClient(new HttpClient(new NoNetworkHandler())), account);
        var chat = new ChatViewModel(new LaseroChatClient(new HttpClient(new NoNetworkHandler())), account, new ChatStore(P("chat")));
        var kamil = new KamilAssistantViewModel(chat, scene, connection);
        var recovery = new ProjectRecoveryStore(P("recovery"));
        ViewModel = new MainViewModel(
            connection, machineStatus, jog, console, scene, gcode, account, home, materials, chat, kamil,
            recovery, settings, recent, jobs,
            () => throw new InvalidOperationException("The wizard is built by the audit when needed."));

        var removal = new BackgroundRemovalCoordinator(scene, new NoBackgroundRemoval());
        Window = new MainWindow(ViewModel, removal, new BackgroundRemovalConsentStore(P("consent.json")))
        {
            Width = width,
            Height = height,
            ShowActivated = false,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -20000,
            Top = -20000,
        };
        Window.Show();
        Pump();
    }

    private sealed class NoBackgroundRemoval : Lasero.Core.BackgroundRemoval.IBackgroundRemovalService
    {
        public bool IsReady => false;
        public Task PrepareAsync(IProgress<double>? progress = null, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<string> RemoveBackgroundAsync(string sourceFilePath, string? destinationFilePath = null, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("The layout harness never removes backgrounds.");
    }

    public static void Pump()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

    public void Settle()
    {
        Pump();
        Window.UpdateLayout();
        Pump();
    }

    public void Render(string name, double dpi = 96)
    {
        Settle();
        var width = Window.ActualWidth;
        var height = Window.ActualHeight;
        var bitmap = new RenderTargetBitmap(
            (int)Math.Round(width * dpi / 96), (int)Math.Round(height * dpi / 96), dpi, dpi, PixelFormats.Pbgra32);
        bitmap.Render(Window);
        Save(bitmap, name);
    }

    public static void Save(BitmapSource bitmap, string name)
    {
        var dir = Environment.GetEnvironmentVariable("LASERO_RENDER_OUT");
        if (string.IsNullOrWhiteSpace(dir)) return;
        Directory.CreateDirectory(dir);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(Path.Combine(dir, name + ".png"));
        encoder.Save(stream);
    }

    public void Dispose()
    {
        try { Window.Close(); } catch { /* the harness is throwaway */ }
        try { Machine.Dispose(); } catch { }
        try { Directory.Delete(_directory, true); } catch { }
    }
}
