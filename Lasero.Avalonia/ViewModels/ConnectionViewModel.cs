using System.Collections.ObjectModel;
using System.Reactive;
using System.Reactive.Linq;
using Avalonia.Threading;
using Lasero.Avalonia.Services;
using Lasero.Core.Grbl;
using Lasero.Core.Machines;
using ReactiveUI;
using Serilog;

namespace Lasero.Avalonia.ViewModels;

/// <summary>
/// Port of Lasero.App/ViewModels/ConnectionViewModel.cs. Real GRBL connection state — no fabricated
/// "connected" state, no commands that run without the underlying ILaserMachine actually being asked.
/// Only the MVVM plumbing changed: CommunityToolkit's [ObservableProperty]/[RelayCommand] became
/// ReactiveUI's RaiseAndSetIfChanged/ReactiveCommand (this codebase's own convention — see
/// EditorViewModel/HomeViewModel), and WPF's Application.Current.Dispatcher became Avalonia's
/// Dispatcher.UIThread. The connection/business logic itself — every guard, every field — is unchanged.
/// Constructing this class does not touch the connection: RefreshPorts only enumerates OS port names,
/// it never opens one, so opening the Devices screen or app startup cannot move anything.
/// </summary>
public sealed class ConnectionViewModel : ReactiveObject
{
    private readonly ILaserMachine connection;
    private readonly AppSettingsStore settingsStore;
    private CancellationTokenSource? identificationCancellation;

    public ObservableCollection<string> AvailablePorts { get; } = new();
    public int[] CommonBaudRates { get; } = [9600, 19200, 38400, 57600, 115200, 230400];

    private string? selectedPort;
    public string? SelectedPort
    {
        get => selectedPort;
        set
        {
            this.RaiseAndSetIfChanged(ref selectedPort, value);
            IsVirtualMachineSelected = VirtualGrblTransport.IsVirtualPort(value);
            RaiseConnectionStateChanged();
        }
    }

    private int baudRate = 115200;
    public int BaudRate { get => baudRate; set => this.RaiseAndSetIfChanged(ref baudRate, value); }

    private bool isConnected;
    public bool IsConnected { get => isConnected; private set { this.RaiseAndSetIfChanged(ref isConnected, value); RaiseConnectionStateChanged(); } }

    private bool isConnecting;
    public bool IsConnecting { get => isConnecting; private set { this.RaiseAndSetIfChanged(ref isConnecting, value); RaiseConnectionStateChanged(); } }

    private string statusText = "Nepřipojeno";
    public string StatusText { get => statusText; private set => this.RaiseAndSetIfChanged(ref statusText, value); }

    private string? firmwareBanner;
    public string? FirmwareBanner { get => firmwareBanner; private set => this.RaiseAndSetIfChanged(ref firmwareBanner, value); }

    private GrblDeviceProfile? detectedDevice;
    public GrblDeviceProfile? DetectedDevice { get => detectedDevice; private set => this.RaiseAndSetIfChanged(ref detectedDevice, value); }

    private string? identificationMessage;
    public string? IdentificationMessage { get => identificationMessage; private set => this.RaiseAndSetIfChanged(ref identificationMessage, value); }

    private string? activeMachineProfileId;
    public string? ActiveMachineProfileId { get => activeMachineProfileId; private set => this.RaiseAndSetIfChanged(ref activeMachineProfileId, value); }

    private string activeMachineName = "Laserové zařízení";
    // Public setter: the Device Wizard's Finish step writes the operator-editable name here too
    // (matches Lasero.App's DeviceWizardViewModel.Finish assigning Connection.ActiveMachineName
    // directly) — everywhere else still only assigns it from identification.
    public string ActiveMachineName { get => activeMachineName; set => this.RaiseAndSetIfChanged(ref activeMachineName, value); }

    private string? workspaceMessage;
    public string? WorkspaceMessage { get => workspaceMessage; private set => this.RaiseAndSetIfChanged(ref workspaceMessage, value); }

    private bool isVirtualMachineSelected;
    public bool IsVirtualMachineSelected { get => isVirtualMachineSelected; private set => this.RaiseAndSetIfChanged(ref isVirtualMachineSelected, value); }

    private string? connectionError;
    public string? ConnectionError { get => connectionError; private set => this.RaiseAndSetIfChanged(ref connectionError, value); }

    private double workAreaWidthMm;
    public double WorkAreaWidthMm
    {
        get => workAreaWidthMm;
        set
        {
            this.RaiseAndSetIfChanged(ref workAreaWidthMm, value);
            if (double.IsFinite(value) && value > 0) settingsStore.Current.Machine.WorkAreaWidthMm = value;
            RaiseConnectionStateChanged();
        }
    }

    private double workAreaHeightMm;
    public double WorkAreaHeightMm
    {
        get => workAreaHeightMm;
        set
        {
            this.RaiseAndSetIfChanged(ref workAreaHeightMm, value);
            if (double.IsFinite(value) && value > 0) settingsStore.Current.Machine.WorkAreaHeightMm = value;
            RaiseConnectionStateChanged();
        }
    }

    public bool HasAvailablePorts => AvailablePorts.Count > 0;

    public ReactiveCommand<Unit, Unit> RefreshPorts { get; }
    public ReactiveCommand<Unit, Unit> Connect { get; }
    public ReactiveCommand<Unit, Unit> Disconnect { get; }
    public ReactiveCommand<Unit, Unit> SaveWorkspaceAsDefault { get; }

    public ConnectionViewModel(ILaserMachine connection, AppSettingsStore settingsStore)
    {
        this.connection = connection;
        this.settingsStore = settingsStore;
        workAreaWidthMm = settingsStore.Current.Machine.WorkAreaWidthMm;
        workAreaHeightMm = settingsStore.Current.Machine.WorkAreaHeightMm;

        var canRefreshPorts = connectionStateChanged.StartWith(Unit.Default).Select(_ => !IsConnected && !IsConnecting);
        var canConnect = connectionStateChanged.StartWith(Unit.Default).Select(_ => !IsConnected && !IsConnecting && !string.IsNullOrEmpty(SelectedPort));
        var canDisconnect = connectionStateChanged.StartWith(Unit.Default).Select(_ => IsConnected);
        var canSaveWorkspace = connectionStateChanged.StartWith(Unit.Default).Select(_ =>
            !string.IsNullOrWhiteSpace(ActiveMachineProfileId) &&
            double.IsFinite(WorkAreaWidthMm) && WorkAreaWidthMm > 0 &&
            double.IsFinite(WorkAreaHeightMm) && WorkAreaHeightMm > 0);

        RefreshPorts = ReactiveCommand.Create(DoRefreshPorts, canRefreshPorts);
        Connect = ReactiveCommand.Create(DoConnect, canConnect);
        Disconnect = ReactiveCommand.Create(DoDisconnect, canDisconnect);
        SaveWorkspaceAsDefault = ReactiveCommand.Create(DoSaveWorkspaceAsDefault, canSaveWorkspace);

        connection.ConnectionStateChanged += state => RunOnUiThread(() =>
        {
            IsConnected = state == GrblConnectionState.Connected;
            IsConnecting = state == GrblConnectionState.Connecting;
            StatusText = state switch
            {
                GrblConnectionState.Connecting => "Připojuji zařízení…",
                GrblConnectionState.Connected => "Připojeno — čekám na identifikaci zařízení",
                _ => "Nepřipojeno",
            };
            if (state == GrblConnectionState.Connected)
            {
                identificationCancellation?.Cancel();
                identificationCancellation = new CancellationTokenSource();
                _ = IdentifyDeviceAsync(identificationCancellation.Token);
            }
            else if (state == GrblConnectionState.Disconnected)
            {
                identificationCancellation?.Cancel();
                DetectedDevice = null;
                FirmwareBanner = null;
                IdentificationMessage = null;
            }
        });
        connection.Connected += banner => RunOnUiThread(() =>
        {
            FirmwareBanner = banner;
            IsConnected = true;
            StatusText = $"Připojeno — {banner}";
        });
        connection.Disconnected += error => RunOnUiThread(() =>
        {
            IsConnected = false;
            IsConnecting = false;
            ConnectionError = error?.Message;
            StatusText = error is null ? "Nepřipojeno" : $"Spojení bylo přerušeno: {error.Message}";
        });

        DoRefreshPorts();
    }

    // Pushed whenever any state a CanExecute predicate reads changes — the ReactiveUI equivalent of
    // WPF's *Command.NotifyCanExecuteChanged() calls, just centralized instead of one call site per
    // command. Connect/Disconnect/RefreshPorts/SaveWorkspaceAsDefault all key off this.
    private readonly System.Reactive.Subjects.Subject<Unit> connectionStateChanged = new();
    private void RaiseConnectionStateChanged() => connectionStateChanged.OnNext(Unit.Default);

    private async Task IdentifyDeviceAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(500, cancellationToken).ConfigureAwait(false);
            var settings = await connection.QuerySettingsAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
            var profile = GrblDeviceProfileParser.Parse(settings, connection.FirmwareBanner);
            RunOnUiThread(() =>
            {
                DetectedDevice = profile;
                var knownMachine = KnownMachineProfiles.Match(profile);
                ActiveMachineName = knownMachine?.DisplayName ??
                    (string.IsNullOrWhiteSpace(profile.FirmwareBanner)
                        ? SelectedPort ?? "Laserové zařízení"
                        : profile.FirmwareBanner);
                ActiveMachineProfileId = BuildMachineProfileId(profile, SelectedPort);
                settingsStore.Current.Machine.ActiveProfileId = ActiveMachineProfileId;

                if (settingsStore.Current.Machine.Profiles.TryGetValue(ActiveMachineProfileId, out var saved))
                {
                    WorkAreaWidthMm = saved.WorkAreaWidthMm;
                    WorkAreaHeightMm = saved.WorkAreaHeightMm;
                    WorkspaceMessage = "Použita uložená pracovní plocha pro toto zařízení.";
                }
                else
                {
                    WorkAreaWidthMm = profile.MaxTravelXmm ?? knownMachine?.WorkAreaWidthMm ?? WorkAreaWidthMm;
                    WorkAreaHeightMm = profile.MaxTravelYmm ?? knownMachine?.WorkAreaHeightMm ?? WorkAreaHeightMm;
                    WorkspaceMessage = "Rozměry byly načteny ze zařízení. Uložte je jako výchozí, pokud jsou správné.";
                }

                IdentificationMessage = profile.LaserModeEnabled == false
                    ? "Zařízení bylo nalezeno, ale GRBL laserový režim $32 není zapnutý."
                    : $"Zařízení nalezeno — pracovní plocha {settingsStore.Current.Machine.WorkAreaWidthMm:0.#} × {settingsStore.Current.Machine.WorkAreaHeightMm:0.#} mm.";
            });
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "GRBL device identification failed");
            RunOnUiThread(() =>
                IdentificationMessage = "Připojeno, ale parametry zařízení se nepodařilo automaticky načíst.");
        }
    }

    private static string BuildMachineProfileId(GrblDeviceProfile profile, string? port)
    {
        var banner = string.IsNullOrWhiteSpace(profile.FirmwareBanner) ? "GRBL" : profile.FirmwareBanner.Trim();
        var dimensions = $"{profile.MaxTravelXmm:0.###}x{profile.MaxTravelYmm:0.###}x{profile.MaxTravelZmm:0.###}";
        var fallbackPort = string.IsNullOrWhiteSpace(profile.FirmwareBanner) &&
                           profile.MaxTravelXmm is null && profile.MaxTravelYmm is null
            ? port ?? "unknown"
            : string.Empty;
        return $"{banner}|{dimensions}|{fallbackPort}";
    }

    private void DoRefreshPorts()
    {
        var current = SelectedPort;
        AvailablePorts.Clear();
        var physicalPorts = GrblConnection.GetAvailablePortNames().OrderBy(n => n).ToArray();
        foreach (var name in physicalPorts)
            AvailablePorts.Add(name);
        AvailablePorts.Add(VirtualGrblTransport.PortName);

        SelectedPort = AvailablePorts.Contains(current!) ? current : physicalPorts.FirstOrDefault() ?? VirtualGrblTransport.PortName;
        this.RaisePropertyChanged(nameof(HasAvailablePorts));
    }

    private void DoConnect()
    {
        try
        {
            IsConnecting = true;
            ConnectionError = null;
            StatusText = $"Připojuji se k {SelectedPort}…";
            connection.Connect(SelectedPort!, BaudRate);
            connection.StartStatusPolling(TimeSpan.FromMilliseconds(200));
        }
        catch (Exception ex)
        {
            IsConnecting = false;
            Log.Warning(ex, "Failed to open GRBL connection on {Port}", SelectedPort);
            ConnectionError = ex.Message;
            StatusText = $"Připojení selhalo: {ex.Message}";
        }
    }

    private void DoDisconnect()
    {
        connection.Disconnect();
        IsConnected = false;
        IsConnecting = false;
        ConnectionError = null;
        StatusText = "Nepřipojeno";
    }

    private void DoSaveWorkspaceAsDefault()
    {
        var profileId = ActiveMachineProfileId!;
        if (!settingsStore.Current.Machine.Profiles.TryGetValue(profileId, out var profile))
            profile = new MachineProfilePreferences();
        profile.DisplayName = ActiveMachineName;
        profile.WorkAreaWidthMm = WorkAreaWidthMm;
        profile.WorkAreaHeightMm = WorkAreaHeightMm;
        settingsStore.Current.Machine.Profiles[profileId] = profile;
        settingsStore.Current.Machine.ActiveProfileId = profileId;
        settingsStore.Save();
        WorkspaceMessage = $"Výchozí pracovní plocha {WorkAreaWidthMm:0.#} × {WorkAreaHeightMm:0.#} mm byla uložena.";
    }

    private static void RunOnUiThread(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess()) action();
        else Dispatcher.UIThread.Post(action);
    }
}
