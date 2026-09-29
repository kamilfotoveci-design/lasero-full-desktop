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
        FinishCommand.NotifyCanExecuteChanged();
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
            ScanStatus = "Hledání se nezdařilo. Zkontrolujte USB kabel a zkuste to znovu.";
        }

        Step = DeviceWizardStep.Results;
        OnPropertyChanged(nameof(FoundNothing));
        OnPropertyChanged(nameof(FoundSomething));
    }

    [RelayCommand]
    private void CancelScan()
    {
        _scanCancellation?.Cancel();
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
    private async Task UseSelectedMachine()
    {
        var machine = SelectedMachine!;
        ConnectFailed = false;
        if (_connection.IsConnected) _connection.DisconnectCommand.Execute(null);

        // Choosing the explicitly labelled simulator must not inherit a blocked hardware profile.
        if (machine.IsSimulator)
            _connection.SelectedCompatibility = MachineCompatibilityCatalog.Get(MachineCompatibilityCatalog.ExistingGrblId);
        _connection.RefreshPortsCommand.Execute(null);
        _connection.SelectedPort = machine.PortName;
        _connection.BaudRate = machine.BaudRate;
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
            return;
        }

        SetupMessage = identified
            ? _connection.IdentificationMessage
            : "Zařízení je připojené, ale jeho nastavení se nepodařilo načíst. Rozměry pracovní plochy zadejte ručně.";
        Step = DeviceWizardStep.Setup;
        OnPropertyChanged(nameof(CanFinish));
    }

    private Task<bool> WaitForIdentificationAsync(TimeSpan timeout)
    {
        if (_connection.DetectedDevice is not null) return Task.FromResult(true);

        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnConnectionChanged(object? sender, PropertyChangedEventArgs args)
        {
            if (args.PropertyName == nameof(ConnectionViewModel.DetectedDevice) && _connection.DetectedDevice is not null)
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
            SetupMessage = "Laserový režim se nepodařilo zapnout. Zkontrolujte, zda je zařízení v klidu, a zkuste to znovu.";
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
