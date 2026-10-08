using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lasero.Core.Grbl;
using Lasero.Core.Machines;
using Serilog;

namespace Lasero.App.ViewModels;

public enum DeviceWizardStep
{
    Intro,
    Scanning,
    Results,
    Setup,
    Done,
}

/// <summary>
/// Walks a first-time user from "I have a laser on a USB cable" to "the app is connected and knows
/// how big the bed is". Everything it does is something the Device panel can already do by hand;
/// the wizard's job is to do it in order and explain each step, not to add a second way to drive
/// the machine.
/// </summary>
public partial class DeviceWizardViewModel : ObservableObject
{
    private readonly DeviceScanner _scanner;
    private readonly ConnectionViewModel _connection;
    private readonly ILaserMachine _machine;
    private readonly AppSettingsStore _settingsStore;
    private CancellationTokenSource? _scanCancellation;

    public ConnectionViewModel Connection => _connection;
    public ObservableCollection<DiscoveredMachine> FoundMachines { get; } = new();

    [ObservableProperty] private DeviceWizardStep _step = DeviceWizardStep.Intro;
    [ObservableProperty] private DiscoveredMachine? _selectedMachine;
    [ObservableProperty] private string _scanStatus = string.Empty;
    [ObservableProperty] private string? _setupMessage;
    [ObservableProperty] private string _machineName = "Laserové zařízení";
    [ObservableProperty] private bool _isEnablingLaserMode;

    /// <summary>True once a connection attempt against <see cref="SelectedMachine"/> has run to
    /// completion without the controller actually ending up connected — wrong/busy port, cable
    /// unplugged mid-attempt, etc. The wizard stays on Results so the same card can show a calm
    /// retry rather than silently advancing to a Setup screen that has nothing to configure.</summary>
    [ObservableProperty] private bool _connectFailed;

    /// <summary>Set by the shell when the status-strip Připojit could not decide alone (several
    /// controllers or none) and hands over to the wizard; the overlay then starts the automatic
    /// search immediately instead of waiting for a second click.</summary>
    public bool AutoStartRequested { get; set; }

    /// <summary>A search the shell already ran (the status-strip Připojit). When set, the automatic
    /// connect uses it instead of opening every port again.</summary>
    public GrblPortScanResult? PreScanResult { get; set; }

    /// <summary>One line per serial port the last automatic search examined, with the reason it was not
    /// used. Shown when nothing was found so the operator can see what was tried.</summary>
    [ObservableProperty] private string? _scanReport;

    /// <summary>The headline sentence for the "nothing found" screen.</summary>
    [ObservableProperty] private string _nothingFoundSummary = "Je potřeba zkontrolovat USB kabel a zapnutí gravírky, potom lze hledání zopakovat.";

    /// <summary>How long <see cref="UseSelectedMachine"/> waits for the controller to identify
    /// itself before treating the attempt as failed. Internal so tests can shrink it instead of
    /// sleeping for the real 8 seconds slower hardware may legitimately need.</summary>
    internal TimeSpan IdentificationTimeout { get; set; } = TimeSpan.FromSeconds(8);

    public DeviceWizardViewModel(
        DeviceScanner scanner,
        ConnectionViewModel connection,
        ILaserMachine machine,
        AppSettingsStore settingsStore)
    {
        _scanner = scanner;
        _connection = connection;
        _machine = machine;
        _settingsStore = settingsStore;
    }

    public bool IsIntro => Step == DeviceWizardStep.Intro;
    public bool IsScanning => Step == DeviceWizardStep.Scanning;
    public bool IsResults => Step == DeviceWizardStep.Results;
    public bool IsSetup => Step == DeviceWizardStep.Setup;
    public bool IsDone => Step == DeviceWizardStep.Done;
    public bool FoundNothing => Step == DeviceWizardStep.Results && FoundMachines.Count == 0;
    public bool FoundSomething => Step == DeviceWizardStep.Results && FoundMachines.Count > 0;
    public bool LaserModeNeedsAttention => SelectedMachine?.LaserModeIsOff == true;

    /// <summary>True once the wizard has done everything it can and the operator may close it.</summary>
    public bool CanFinish => Step == DeviceWizardStep.Setup &&
        _connection.IsConnected &&
        double.IsFinite(_connection.WorkAreaWidthMm) && _connection.WorkAreaWidthMm > 0 &&
        double.IsFinite(_connection.WorkAreaHeightMm) && _connection.WorkAreaHeightMm > 0;

    partial void OnStepChanged(DeviceWizardStep value)
    {
        OnPropertyChanged(nameof(IsIntro));
        OnPropertyChanged(nameof(IsScanning));
        OnPropertyChanged(nameof(IsResults));
        OnPropertyChanged(nameof(IsSetup));
        OnPropertyChanged(nameof(IsDone));
        OnPropertyChanged(nameof(FoundNothing));
        OnPropertyChanged(nameof(FoundSomething));
        OnPropertyChanged(nameof(CanFinish));
        UseSelectedMachineCommand.NotifyCanExecuteChanged();
        AutoConnectCommand.NotifyCanExecuteChanged();
        FinishCommand.NotifyCanExecuteChanged();
    }

    private bool CanAutoConnect() => Step != DeviceWizardStep.Scanning && !_connection.IsConnecting && !_connection.IsDetecting;

    /// <summary>The primary action: find a GRBL controller on the serial ports and connect to it without
    /// asking for a model, port or baud rate. Runs only from this click. One controller is connected
    /// straight away; several are listed for a choice; none gives an actionable explanation.</summary>
    [RelayCommand(CanExecute = nameof(CanAutoConnect))]
    private async Task AutoConnect()
    {
        _scanCancellation?.Cancel();
        _scanCancellation = new CancellationTokenSource();
        var cancellationToken = _scanCancellation.Token;

        FoundMachines.Clear();
        SelectedMachine = null;
        ConnectFailed = false;
        ScanReport = null;
        if (_connection.IsConnected) _connection.DisconnectCommand.Execute(null);
        Step = DeviceWizardStep.Scanning;
        ScanStatus = "Hledám GRBL na portech COM…";

        var progress = new Progress<GrblPortScanProgress>(report =>
            ScanStatus = $"Hledám GRBL na portech COM… Zkouším {report.PortName} rychlostí {report.BaudRate} Bd ({report.PortIndex + 1} z {report.PortCount})");

        GrblPortScanResult? result = PreScanResult;
        PreScanResult = null;
        try
        {
            result ??= await _connection.DetectAsync(progress, cancellationToken).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception exception)
        {
            Log.Warning(exception, "Automatic GRBL detection failed");
        }

        if (cancellationToken.IsCancellationRequested) return;

        foreach (var found in result?.Grbl ?? [])
        {
            FoundMachines.Add(new DiscoveredMachine(found.PortName, found.BaudRate ?? 115200, found.FirmwareBanner,
                new GrblDeviceProfile { FirmwareBanner = found.FirmwareBanner })
            {
                IsAutoDetected = true,
                StateLabel = found.StateLabel,
            });
        }
        var (report, summary) = DescribeNothingFound(result);
        ScanReport = string.IsNullOrEmpty(report) ? null : report;
        NothingFoundSummary = summary;

        if (FoundMachines.Count == 1)
        {
            SelectedMachine = FoundMachines[0];
            ScanStatus = $"Nalezeno na {SelectedMachine.PortName}, připojuji…";
            var connected = await ConnectMachineAsync(SelectedMachine, cancellationToken).ConfigureAwait(true);
            if (cancellationToken.IsCancellationRequested)
            {
                if (_connection.IsConnected) _connection.DisconnectCommand.Execute(null);
                return;
            }
            if (connected) return;
        }

        Step = DeviceWizardStep.Results;
        OnPropertyChanged(nameof(FoundNothing));
        OnPropertyChanged(nameof(FoundSomething));
    }

    private static (string Report, string Summary) DescribeNothingFound(GrblPortScanResult? result)
    {
        if (result is null)
            return (string.Empty, "Automatické hledání teď nelze spustit. Zařízení lze připojit ručně v části Upřesnit.");
        if (result.PortsExamined == 0)
            return (string.Empty, "Windows nehlásí žádný port COM. Je potřeba ověřit, že je laser zapnutý a připojený datovým USB kabelem, případně nainstalovat ovladač CH340 nebo CP210x.");

        var lines = result.Others.Select(other => $"{other.PortName} - " + other.Outcome switch
        {
            GrblProbeOutcome.Busy => "port drží jiný program",
            GrblProbeOutcome.OpenFailed => "port se nepodařilo otevřít",
            GrblProbeOutcome.NotGrbl => "zařízení odpovídá, ale nejde o GRBL",
            GrblProbeOutcome.Skipped => "přeskočeno (Bluetooth)",
            _ => "bez odpovědi",
        });
        var examined = result.PortsExamined;
        return (string.Join(Environment.NewLine, lines),
            $"Prohledáno portů COM: {examined}. Žádný neodpověděl jako GRBL. Je potřeba zkontrolovat kabel, ovladač a zda port nedrží jiný program.");
    }

    partial void OnSelectedMachineChanged(DiscoveredMachine? value)
    {
        if (value is not null) MachineName = value.DisplayName;
        ConnectFailed = false;
        OnPropertyChanged(nameof(LaserModeNeedsAttention));
        UseSelectedMachineCommand.NotifyCanExecuteChanged();
        EnableLaserModeCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private async Task Scan()
    {
        _scanCancellation?.Cancel();
        _scanCancellation = new CancellationTokenSource();
        var cancellationToken = _scanCancellation.Token;

        FoundMachines.Clear();
        SelectedMachine = null;
        ConnectFailed = false;
        Step = DeviceWizardStep.Scanning;
        ScanStatus = "Hledání připojených zařízení…";

        var progress = new Progress<DeviceScanProgress>(report => ScanStatus = report.PortCount == 0
            ? "Hledání připojených zařízení…"
            : $"Zkoušení {report.PortName} rychlostí {report.BaudRate} Bd ({report.PortIndex + 1} z {report.PortCount})…");

        try
        {
            var machines = await _scanner.ScanAsync(progress, cancellationToken).ConfigureAwait(true);
            if (cancellationToken.IsCancellationRequested) return;
            foreach (var machine in machines) FoundMachines.Add(machine);
            SelectedMachine = FoundMachines.FirstOrDefault(machine => !machine.IsSimulator)
                ?? FoundMachines.FirstOrDefault();
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception exception)
        {
            Log.Warning(exception, "Device scan failed");
            ScanStatus = "Hledání se nezdařilo. Je potřeba zkontrolovat USB kabel, potom lze hledání zopakovat.";
        }

        Step = DeviceWizardStep.Results;
        OnPropertyChanged(nameof(FoundNothing));
        OnPropertyChanged(nameof(FoundSomething));
    }

    [RelayCommand]
    private void CancelScan()
    {
        _scanCancellation?.Cancel();
        _connection.CancelDetection();
        Step = DeviceWizardStep.Intro;
    }

    [RelayCommand]
    private void Back() => Step = Step switch
    {
        DeviceWizardStep.Results => DeviceWizardStep.Intro,
        DeviceWizardStep.Setup => DeviceWizardStep.Results,
        _ => DeviceWizardStep.Intro,
    };

    private bool CanUseSelectedMachine() => Step == DeviceWizardStep.Results && SelectedMachine is not null;

    [RelayCommand(CanExecute = nameof(CanUseSelectedMachine))]
    private async Task UseSelectedMachine() => await ConnectMachineAsync(SelectedMachine!).ConfigureAwait(true);

    /// <summary>Connects to one found controller and moves on to Setup. Shared by the manual pick from
    /// the result list and the automatic single-controller case. Returns whether it ended connected.</summary>
    private async Task<bool> ConnectMachineAsync(DiscoveredMachine machine, CancellationToken cancellationToken = default)
    {
        ConnectFailed = false;
        if (_connection.IsConnected) _connection.DisconnectCommand.Execute(null);

        // The simulator and any automatically found controller are generic GRBL: they must not inherit
        // a blocked hardware model left selected in the advanced section.
        if (machine.IsSimulator || machine.IsAutoDetected)
            _connection.SelectedCompatibility = MachineCompatibilityCatalog.Get(MachineCompatibilityCatalog.ExistingGrblId);
        _connection.RefreshPortsCommand.Execute(null);
        _connection.SelectedPort = machine.PortName;
        _connection.BaudRate = machine.BaudRate;
        // The scan just closed this port; give Windows a moment to release it before reopening.
        if (machine.IsAutoDetected && _connection.ConnectSettleDelay > TimeSpan.Zero)
            await Task.Delay(_connection.ConnectSettleDelay, cancellationToken).ConfigureAwait(true);
        _connection.ConnectCommand.Execute(null);

        // The Device panel re-reads $$ on every connect and writes the bed size it finds. Waiting for
        // that before showing the fields keeps it from overwriting a size the operator typed here.
        var identified = await WaitForIdentificationAsync(IdentificationTimeout).ConfigureAwait(true);

        // A port that never actually connected (wrong port, cable unplugged, held by another
        // program) is not "connected but slow to identify" — advancing to Setup there used to show
        // a configuration screen for a machine that was never actually there. Stay on Results and
        // let the card offer a calm retry instead.
        if (!_connection.IsConnected)
        {
            ConnectFailed = true;
            return false;
        }

        if (machine.IsAutoDetected && identified) MachineName = _connection.ActiveMachineName;
        SetupMessage = identified
            ? _connection.IdentificationMessage
            : "Zařízení je připojené, ale jeho nastavení se nepodařilo načíst. Rozměry pracovní plochy je potřeba zadat ručně.";
        Step = DeviceWizardStep.Setup;
        OnPropertyChanged(nameof(CanFinish));
        return true;
    }

    private Task<bool> WaitForIdentificationAsync(TimeSpan timeout)
    {
        // DetectedDevice is populated by the transport as soon as $$ is parsed; the public
        // work-area fields are applied just after that. Wait for the completed identification
        // message so Setup never reads the still-stale dimensions from before the query.
        if (!string.IsNullOrWhiteSpace(_connection.IdentificationMessage)) return Task.FromResult(true);

        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnConnectionChanged(object? sender, PropertyChangedEventArgs args)
        {
            if (args.PropertyName == nameof(ConnectionViewModel.IdentificationMessage)
                && !string.IsNullOrWhiteSpace(_connection.IdentificationMessage))
                completion.TrySetResult(true);
            else if (args.PropertyName == nameof(ConnectionViewModel.IsConnected) && !_connection.IsConnected)
                completion.TrySetResult(false);
        }

        _connection.PropertyChanged += OnConnectionChanged;
        return completion.Task
            .WaitAsync(timeout)
            .ContinueWith(
                task =>
                {
                    _connection.PropertyChanged -= OnConnectionChanged;
                    return task.Status == TaskStatus.RanToCompletion && task.Result;
                },
                TaskScheduler.Default);
    }

    private bool CanEnableLaserMode() => LaserModeNeedsAttention && _connection.IsConnected && !IsEnablingLaserMode;

    /// <summary>Writes $32=1. This is the one thing the wizard changes on the controller itself, so it
    /// is never automatic — the operator has to ask for it, and they are told what it does first.</summary>
    [RelayCommand(CanExecute = nameof(CanEnableLaserMode))]
    private async Task EnableLaserMode()
    {
        IsEnablingLaserMode = true;
        try
        {
            var result = await _machine.SendCommandAsync("$32=1").ConfigureAwait(true);
            if (!result.IsOk)
            {
                SetupMessage = $"Laserový režim se nepodařilo zapnout. Zpráva zařízení: {result.Message}";
                return;
            }

            var settings = await _machine.QuerySettingsAsync().ConfigureAwait(true);
            var profile = GrblDeviceProfileParser.Parse(settings, _machine.FirmwareBanner);
            SelectedMachine = SelectedMachine! with { Profile = profile };
            SetupMessage = profile.LaserModeEnabled == true
                ? "Laserový režim $32 je zapnutý."
                : "Zařízení hlásí, že laserový režim stále není zapnutý.";
        }
        catch (Exception exception)
        {
            Log.Warning(exception, "Enabling GRBL laser mode failed");
            SetupMessage = "Laserový režim se nepodařilo zapnout. Je potřeba ověřit, zda je zařízení v klidu, potom lze akci zopakovat.";
        }
        finally
        {
            IsEnablingLaserMode = false;
        }
    }

    partial void OnIsEnablingLaserModeChanged(bool value) => EnableLaserModeCommand.NotifyCanExecuteChanged();

    [RelayCommand(CanExecute = nameof(CanFinishCommand))]
    private void Finish()
    {
        _connection.ActiveMachineName = MachineName;
        _connection.SaveWorkspaceAsDefaultCommand.Execute(null);
        _settingsStore.Current.Device.Port = _connection.SelectedPort;
        _settingsStore.Current.Device.BaudRate = _connection.BaudRate;
        _settingsStore.Save();
        Step = DeviceWizardStep.Done;
    }

    private bool CanFinishCommand() => CanFinish;

    /// <summary>Re-evaluates the finish gate after the operator edits a bed size. The Device panel owns
    /// those two values; the wizard only reads them back.</summary>
    public void NotifyWorkAreaEdited()
    {
        OnPropertyChanged(nameof(CanFinish));
        FinishCommand.NotifyCanExecuteChanged();
    }
}
