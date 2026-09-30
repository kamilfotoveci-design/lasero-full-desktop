using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Lasero.App;
using Lasero.App.ViewModels;
using Lasero.App.Views.DeviceSetup;
using Lasero.Core.Grbl;
using Lasero.Core.Machines;

namespace Lasero.Tests;

public sealed class ZzScanShotTemp
{
    [Fact]
    public void RenderScanning()
    {
        var outDir = Environment.GetEnvironmentVariable("SCAN_OUT")!;
        Exception? failure = null;
        var t = new Thread(() =>
        {
            try
            {
                var app = new Lasero.App.App();
                app.InitializeComponent();
                var dir = Path.Combine(Path.GetTempPath(), "zzscan" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(dir);
                var transport = new VirtualGrblTransport { ResponseDelay = TimeSpan.Zero };
                var machine = new GrblConnection(transport);
                var settings = new AppSettingsStore(Path.Combine(dir, "s.json"));
                var connection = new ConnectionViewModel(machine, settings);
                var scanner = new DeviceScanner(new NullMachineFactory(), () => Array.Empty<string>());
                var vm = new DeviceWizardViewModel(scanner, connection, machine, settings);
                vm.Step = DeviceWizardStep.Scanning;
                vm.ScanStatus = "Hledám GRBL na portech COM… Zkouším COM3 rychlostí 115200 Bd (2 z 2)";
                var overlay = new DeviceWizardOverlay();
                var host = new System.Windows.Controls.Grid { Background = Brushes.White };
                host.Children.Add(new System.Windows.Controls.TextBlock { Text = "Domů (pozadí)", Margin = new Thickness(200, 100, 0, 0), FontSize = 32 });
                host.Children.Add(overlay);
                var win = new Window { Width = 1366, Height = 768, Left = -32000, Top = -32000, Content = host, ShowActivated = false, WindowStyle = WindowStyle.None };
                win.Show();
                overlay.Show(vm);
                void Pump(int ms)
                {
                    var frame = new DispatcherFrame();
                    var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ms) };
                    timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
                    timer.Start();
                    Dispatcher.PushFrame(frame);
                }
                for (var i = 0; i < 3; i++)
                {
                    Pump(i == 0 ? 900 : 350);
                    var rtb = new RenderTargetBitmap(1366, 768, 96, 96, PixelFormats.Pbgra32);
                    rtb.Render(host);
                    var enc = new PngBitmapEncoder();
                    enc.Frames.Add(BitmapFrame.Create(rtb));
                    using var fs = File.Create(Path.Combine(outDir, $"wiz-scanning-{i}.png"));
                    enc.Save(fs);
                }
            }
            catch (Exception ex) { failure = ex; }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        if (failure is not null) throw new Xunit.Sdk.XunitException(failure.ToString());
    }

    private sealed class NullMachineFactory : ILaserMachineFactory
    {
        public ILaserMachine Create() => throw new InvalidOperationException();
    }
}
