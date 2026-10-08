using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Lasero.App;
using Lasero.App.Components;
using Lasero.App.ViewModels;
using Lasero.App.Views;
using Lasero.Core.Grbl;
using Xunit;

namespace Lasero.Tests;

/// <summary>
/// Zařízení has exactly one connect button when disconnected (Připojit, which runs the selected port) and exactly one
/// homing control (the labelled Najet domů, never the jog pad centre), and homing is unavailable under a job and says why.
/// </summary>
[Collection("WpfUi")]
public sealed class DeviceControlsTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "lasero-device-controls-tests", Guid.NewGuid().ToString("N"));

    public DeviceControlsTests() => Directory.CreateDirectory(_directory);

    public void Dispose() { try { Directory.Delete(_directory, true); } catch { } }

    private static System.Windows.Threading.Dispatcher Ui => InlineTextEditorRenderTests.Ui;

    private (JogViewModel Jog, ConnectionViewModel Connection, GrblConnection Machine) CreateJog()
    {
        var machine = new GrblConnection(new VirtualGrblTransport { ResponseDelay = TimeSpan.Zero });
        var settings = new AppSettingsStore(Path.Combine(_directory, Guid.NewGuid().ToString("N") + ".json"));
        return (new JogViewModel(machine, settings), new ConnectionViewModel(machine, settings), machine);
    }

    [Fact]
    public void ConnectIsOneButtonLabelledPripojitInEveryDisconnectedState()
    {
        var machine = new GrblConnection(new VirtualGrblTransport { ResponseDelay = TimeSpan.Zero });
        var settings = new AppSettingsStore(Path.Combine(_directory, "setup.json"));
        var connection = new ConnectionViewModel(machine, settings);
        var setup = new DeviceSetupViewModel(connection, () => { });

        Assert.Equal(ProcessStatus.Waiting, setup.Status);
        Assert.Equal("Připojit", setup.PrimaryLabel);
        Assert.Same(connection.ConnectSelectedCommand, setup.PrimaryCommand);
        Assert.Equal("Připojit", connection.ConnectButtonText);

        connection.ConnectionError = "Spojení s gravírkou se přerušilo.";
        Assert.Equal(ProcessStatus.Error, setup.Status);
        Assert.Equal("Připojit znovu", setup.PrimaryLabel);
        Assert.Same(connection.ConnectSelectedCommand, setup.PrimaryCommand);
    }

    [Fact]
    public void HomingIsRefusedUnderAJobAndSaysWhy()
    {
        var (jog, _, machine) = CreateJog();
        using (machine)
        {
            Assert.False(jog.HomeCommand.CanExecute(null));
            Assert.Contains("po připojení", jog.HomeBlockedReason, StringComparison.Ordinal);
            Assert.Contains("po připojení", jog.HomeTooltip, StringComparison.Ordinal);

            jog.IsJobActive = () => true;
            jog.RefreshJobGuard();
            Assert.False(jog.HomeCommand.CanExecute(null));
            Assert.Equal("Při probíhající úloze nelze najíždět do výchozí polohy.", jog.HomeBlockedReason);
            Assert.Equal(jog.HomeBlockedReason, jog.HomeTooltip);
        }
    }

    [Fact]
    public void HomingTooltipWarnsThatTheHeadMovesWhenHomingIsAvailable()
    {
        var (jog, _, machine) = CreateJog();
        using (machine)
        {
            machine.Connect(VirtualGrblTransport.PortName);
            machine.StartStatusPolling(TimeSpan.FromMilliseconds(50));
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (!jog.HomeCommand.CanExecute(null) && DateTime.UtcNow < deadline) Thread.Sleep(25);

            if (jog.HomeCommand.CanExecute(null))
            {
                Assert.Null(jog.HomeBlockedReason);
                Assert.Equal("Hlava se pohne do výchozí polohy. Pracovní prostor musí být volný.", jog.HomeTooltip);
                jog.IsJobActive = () => true;
                Assert.False(jog.HomeCommand.CanExecute(null), "a running job must disable homing at once");
            }
        }
    }

    [Theory]
    [InlineData(1366, 768)]
    [InlineData(1080, 640)]
    public void TheDeviceScreenShowsOneConnectButtonWhenDisconnectedAndOneHomingButtonWhenConnected(int width, int height) => Ui.Invoke(() =>
    {
        using var shell = new ShellHarness(width, height);
        var vm = shell.ViewModel;
        vm.CurrentScreen = AppScreen.Device;
        shell.Settle();
        var device = LayoutAssertions.Descendants<DeviceView>(shell.Window).First();

        var connectCommands = new ICommand[] { vm.Connection.ConnectSelectedCommand, vm.Connection.SmartConnectCommand, vm.Connection.ConnectCommand };
        var connectButtons = LayoutAssertions.Descendants<Button>(device)
            .Where(b => b.IsVisible && connectCommands.Any(c => ReferenceEquals(c, b.Command))).ToList();
        Assert.Single(connectButtons);
        Assert.Equal("Připojit", connectButtons[0].Content?.ToString());
        Assert.DoesNotContain(LayoutAssertions.Descendants<TextBlock>(device), t => t.Text.Contains("k vybranému portu") || t.Text == "Připojit automaticky");

        shell.Machine.Connect(VirtualGrblTransport.PortName);
        LayoutAuditRenderTests.WaitFor(() => vm.Connection.IsConnected);
        shell.Settle();
        var homing = LayoutAssertions.Descendants<Button>(device).Where(b => ReferenceEquals(b.Command, vm.Jog.HomeCommand)).ToList();
        Assert.Single(homing);
        Assert.Contains(LayoutAssertions.Descendants<TextBlock>(homing[0]), t => t.Text == "Najet domů");
        // The pad centre is decoration: nothing clickable sits in the middle cell of the 3x3 pad.
        var pad = LayoutAssertions.Descendants<Grid>(device).First(g => g.RowDefinitions.Count == 3 && g.ColumnDefinitions.Count == 3);
        Assert.DoesNotContain(pad.Children.OfType<Button>(), b => Grid.GetRow(b) == 1 && Grid.GetColumn(b) == 1);
    });
}
