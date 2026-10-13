using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Reactive;
using System.Reactive.Linq;
using Lasero.Avalonia.Services;
using Lasero.Core.Grbl;
using Lasero.Core.Machines;
using ReactiveUI;
using Serilog;

namespace Lasero.Avalonia.ViewModels;

public enum DeviceWizardStep
{
    Intro,
    Scanning,
    Results,
    Setup,
    Done,
}

/// <summary>
/// Port of Lasero.App/ViewModels/DeviceWizardViewModel.cs. Walks Intro → Scanning → Results → Setup →
/// Done using the same real DeviceScanner/ILaserMachineFactory (probes ports with a $$ query only,
/// never motion) and the same ConnectionViewModel the Devices screen already uses — no second
/// connection model. A fresh instance is created per wizard run (see ShellViewModel.OpenDeviceWizard),
/// matching WPF's own "reopening always starts over" design.
/// </summary>
public sealed class DeviceWizardViewModel : ReactiveObject
{
    private readonly DeviceScanner scanner;
    private readonly ILaserMachine machine;
    private readonly AppSettingsStore settingsStore;
    private CancellationTokenSource? scanCancellation;

    public ConnectionViewModel Connection { get; }
    public ObservableCollection<DiscoveredMachine> FoundMachines { get; } = new();

    private DeviceWizardStep step = DeviceWizardStep.Intro;
    // Public setter — matches WPF's [ObservableProperty]-generated Step (CommunityToolkit generates
    // a public setter by default), which the WPF test suite relies on to jump straight to a step
    // without re-running Scan; mirrored here for the same reason (see DeviceWizardViewModelTests).
    public DeviceWizardStep Step
    {
        get => step;
        set
        {
            this.RaiseAndSetIfChanged(ref step, value);
            this.RaisePropertyChanged(nameof(IsIntro));
            this.RaisePropertyChanged(nameof(IsScanning));
            this.RaisePropertyChanged(nameof(IsResults));
            this.RaisePropertyChanged(nameof(IsSetup));
            this.RaisePropertyChanged(nameof(IsDone));
            this.RaisePropertyChanged(nameof(FoundNothing));
            this.RaisePropertyChanged(nameof(FoundSomething));
            this.RaisePropertyChanged(nameof(CanFinish));
            RaiseStateChanged();
        }
    }

    private DiscoveredMachine? selectedMachine;
    public DiscoveredMachine? SelectedMachine
    {
        get => selectedMachine;
        set
        {
            this.RaiseAndSetIfChanged(ref selectedMachine, value);
            if (value is not null) MachineName = value.DisplayName;
            ConnectFailed = false;
            this.RaisePropertyChanged(nameof(LaserModeNeedsAttention));
            RaiseStateChanged();
        }
    }

    private string scanStatus = string.Empty;
    public string ScanStatus { get => scanStatus; private set => this.RaiseAndSetIfChanged(ref scanStatus, value); }

    private string? setupMessage;
    public string? SetupMessage { get => setupMessage; private set => this.RaiseAndSetIfChanged(ref setupMessage, value); }

    private string machineName = "Laserové zařízení";
    public string MachineName { get => machineName; set => this.RaiseAndSetIfChanged(ref machineName, value); }

    private bool isEnablingLaserMode;
    public bool IsEnablingLaserMode { get => isEnablingLaserMode; private set { this.RaiseAndSetIfChanged(ref isEnablingLaserMode, value); RaiseStateChanged(); } }

    private bool isConnecting;
    public bool IsConnecting { get => isConnecting; private set => this.RaiseAndSetIfChanged(ref isConnecting, value); }

    /// <summary>True once a connection attempt against SelectedMachine has run to completion without
    /// the controller actually ending up connected. The wizard stays on Results so the same card can
    /// show a calm retry rather than silently advancing to a Setup screen with nothing to configure.</summary>
    private bool connectFailed;
    public bool ConnectFailed { get => connectFailed; private set => this.RaiseAndSetIfChanged(ref connectFailed, value); }

    /// <summary>How long UseSelectedMachine waits for the controller to identify itself before
    /// treating the attempt as failed. Settable so tests can shrink it.</summary>
    public TimeSpan IdentificationTimeout { get; set; } = TimeSpan.FromSeconds(8);

    public bool IsIntro => Step == DeviceWizardStep.Intro;
    public bool IsScanning => Step == DeviceWizardStep.Scanning;
    public bool IsResults => Step == DeviceWizardStep.Results;
    public bool IsSetup => Step == DeviceWizardStep.Setup;
    public bool IsDone => Step == DeviceWizardStep.Done;
    public bool FoundNothing => Step == DeviceWizardStep.Results && FoundMachines.Count == 0;
    public bool FoundSomething => Step == DeviceWizardStep.Results && FoundMachines.Count > 0;
    public bool LaserModeNeedsAttention => SelectedMachine?.LaserModeIsOff == true;

    public bool CanFinish => Step == DeviceWizardStep.Setup &&
        Connection.IsConnected &&
        double.IsFinite(Connection.WorkAreaWidthMm) && Connection.WorkAreaWidthMm > 0 &&
        double.IsFinite(Connection.WorkAreaHeightMm) && Connection.WorkAreaHeightMm > 0;

    public ReactiveCommand<Unit, Unit> Scan { get; }
    public ReactiveCommand<Unit, Unit> CancelScan { get; }
    public ReactiveCommand<Unit, Unit> Back { get; }
    public ReactiveCommand<Unit, Unit> UseSelectedMachine { get; }
    public ReactiveCommand<Unit, Unit> EnableLaserMode { get; }
    public ReactiveCommand<Unit, Unit> Finish { get; }

    private readonly System.Reactive.Subjects.Subject<Unit> stateChanged = new();
    private void RaiseStateChanged() => stateChanged.OnNext(Unit.Default);

    public DeviceWizardViewModel(DeviceScanner scanner, ConnectionViewModel connection, ILaserMachine machine, AppSettingsStore settingsStore)
    {
        this.scanner = scanner;
        Connection = connection;
        this.machine = machine;
        this.settingsStore = settingsStore;

        var canUseSelectedMachine = stateChanged.StartWith(Unit.Default)
            .Select(_ => Step == DeviceWizardStep.Results && SelectedMachine is not null);
        var canEnableLaserMode = stateChanged.StartWith(Unit.Default)
            .Select(_ => LaserModeNeedsAttention && Connection.IsConnected && !IsEnablingLaserMode);
        var canFinishObservable = stateChanged.StartWith(Unit.Default).Select(_ => CanFinish);

        Scan = ReactiveCommand.CreateFromTask(DoScanAsync);
        CancelScan = ReactiveCommand.Create(DoCancelScan);
        Back = ReactiveCommand.Create(DoBack);
        UseSelectedMachine = ReactiveCommand.CreateFromTask(DoUseSelectedMachineAsync, canUseSelectedMachine);
        EnableLaserMode = ReactiveCommand.CreateFromTask(DoEnableLaserModeAsync, canEnableLaserMode);
        Finish = ReactiveCommand.Create(DoFinish, canFinishObservable);
    }

    private async Task DoScanAsync()
    {
        scanCancellation?.Cancel();
        scanCancellation = new CancellationTokenSource();
        var cancellationToken = scanCancellation.Token;

        FoundMachines.Clear();
        SelectedMachine = null;
        ConnectFailed = false;
        Step = DeviceWizardStep.Scanning;
        ScanStatus = "Hledám připojená zařízení…";

        var progress = new Progress<DeviceScanProgress>(report => ScanStatus = report.PortCount == 0
            ? "Hledám připojená zařízení…"
            : $"Zkouším {report.PortName} rychlostí {report.BaudRate} Bd ({report.PortIndex + 1} z {report.PortCount})…");

        try
        {
            var machines = await scanner.ScanAsync(progress, cancellationToken).ConfigureAwait(true);
            if (cancellationToken.IsCancellationRequested) return;
            foreach (var found in machines) FoundMachines.Add(found);
            SelectedMachine = FoundMachines.FirstOrDefault(m => !m.IsSimulator) ?? FoundMachines.FirstOrDefault();
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception exception)
        {
            Log.Warning(exception, "Device scan failed");
            ScanStatus = "Hledání se nezdařilo. Zkuste to prosím znovu.";
        }

        Step = DeviceWizardStep.Results;
        this.RaisePropertyChanged(nameof(FoundNothing));
        this.RaisePropertyChanged(nameof(FoundSomething));
    }

    private void DoCancelScan()
    {
        scanCancellation?.Cancel();
        Step = DeviceWizardStep.Intro;
    }

    private void DoBack() => Step = Step switch
    {
        DeviceWizardStep.Results => DeviceWizardStep.Intro,
        DeviceWizardStep.Setup => DeviceWizardStep.Results,
        _ => DeviceWizardStep.Intro,
    };

    private async Task DoUseSelectedMachineAsync()
    {
        var selected = SelectedMachine!;
        ConnectFailed = false;
        IsConnecting = true;
        try
        {
            if (Connection.IsConnected) await Connection.Disconnect.Execute();

            Connection.RefreshPorts.Execute().Subscribe();
            Connection.SelectedPort = selected.PortName;
            Connection.BaudRate = selected.BaudRate;
            await Connection.Connect.Execute();

            // The Devices screen re-reads $$ on every connect and writes the bed size it finds.
            // Waiting for that before showing the Setup fields keeps it from overwriting a size the
            // operator typed here.
            var identified = await WaitForIdentificationAsync(IdentificationTimeout).ConfigureAwait(true);

            // A port that never actually connected (wrong port, cable unplugged, held by another
            // program) is not "connected but slow to identify" — advance to Setup only when it did.
            if (!Connection.IsConnected)
            {
                ConnectFailed = true;
                return;
            }

            SetupMessage = identified
                ? Connection.IdentificationMessage
                : "Zařízení je připojené, ale parametry se nepodařilo načíst. Rozměry zadejte ručně.";
            Step = DeviceWizardStep.Setup;
            this.RaisePropertyChanged(nameof(CanFinish));
        }
        finally
        {
            IsConnecting = false;
        }
    }

    private Task<bool> WaitForIdentificationAsync(TimeSpan timeout)
    {
        if (Connection.DetectedDevice is not null) return Task.FromResult(true);

        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnConnectionChanged(object? sender, PropertyChangedEventArgs args)
        {
            if (args.PropertyName == nameof(ConnectionViewModel.DetectedDevice) && Connection.DetectedDevice is not null)
                completion.TrySetResult(true);
            else if (args.PropertyName == nameof(ConnectionViewModel.IsConnected) && !Connection.IsConnected)
                completion.TrySetResult(false);
        }

        Connection.PropertyChanged += OnConnectionChanged;
        return completion.Task
            .WaitAsync(timeout)
            .ContinueWith(
                task =>
                {
                    Connection.PropertyChanged -= OnConnectionChanged;
                    return task.Status == TaskStatus.RanToCompletion && task.Result;
                },
                TaskScheduler.Default);
    }

    /// <summary>Writes $32=1. The one thing the wizard changes on the controller itself, so it is
    /// never automatic — the operator has to ask for it, and is told what it does first (see the
    /// warning card bound to LaserModeNeedsAttention in DeviceWizardOverlay).</summary>
    private async Task DoEnableLaserModeAsync()
    {
        IsEnablingLaserMode = true;
        try
        {
            var result = await machine.SendCommandAsync("$32=1").ConfigureAwait(true);
            if (!result.IsOk)
            {
                SetupMessage = $"Laserový režim se nepodařilo zapnout: {result.Message}";
                return;
            }

            var settings = await machine.QuerySettingsAsync().ConfigureAwait(true);
            var profile = GrblDeviceProfileParser.Parse(settings, machine.FirmwareBanner);
            SelectedMachine = SelectedMachine! with { Profile = profile };
            SetupMessage = profile.LaserModeEnabled == true
                ? "Laserový režim $32 je zapnutý."
                : "Zařízení hlásí, že laserový režim stále není zapnutý.";
        }
        catch (Exception exception)
        {
            Log.Warning(exception, "Enabling GRBL laser mode failed");
            SetupMessage = "Laserový režim se nepodařilo zapnout.";
        }
        finally
        {
            IsEnablingLaserMode = false;
        }
    }

    private void DoFinish()
    {
        Connection.ActiveMachineName = MachineName;
        Connection.SaveWorkspaceAsDefault.Execute().Subscribe();
        settingsStore.Current.Device.Port = Connection.SelectedPort;
        settingsStore.Current.Device.BaudRate = Connection.BaudRate;
        settingsStore.Save();
        Step = DeviceWizardStep.Done;
    }

    /// <summary>Re-evaluates the finish gate after the operator edits a bed size on the Setup step —
    /// the Devices screen's fields own those two values, this only reads them back.</summary>
    public void NotifyWorkAreaEdited() => RaiseStateChanged();
}
