using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lasero.Core.Grbl;
using Lasero.Core.Machines;
using Serilog;

namespace Lasero.App.ViewModels;

public partial class ConnectionViewModel : ObservableObject
{
    private readonly ILaserMachine _connection;
    private readonly AppSettingsStore _settingsStore;
    private readonly IGrblPortScanner? _portScanner;
    private CancellationTokenSource? _identificationCancellation;
    private CancellationTokenSource? _detectionCancellation;

    public IReadOnlyList<MachineCompatibility> CompatibilityOptions => MachineCompatibilityCatalog.All;
    public MachineCompatibility SelectedCompatibility
    {
        get => MachineCompatibilityCatalog.Get((_connection as GrblConnection)?.CompatibilityId ?? MachineCompatibilityCatalog.ExistingGrblId);
        set
        {
            if (value is null || IsConnected || IsConnecting) return;
            if (_connection is GrblConnection grbl) grbl.CompatibilityId = value.Id;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CompatibilityMessage));
            ConnectCommand.NotifyCanExecuteChanged();
        }
    }
    public string CompatibilityMessage => SelectedCompatibility.Limitation;
    public ObservableCollection<string> AvailablePorts { get; } = new();
    public int[] CommonBaudRates { get; } = [9600, 19200, 38400, 57600, 115200, 230400];

    [ObservableProperty] private string? _selectedPort;
    [ObservableProperty] private int _baudRate = 115200;
    [ObservableProperty] private bool _isConnected;
    [ObservableProperty] private bool _isConnecting;
    [ObservableProperty] private string _statusText = "Nepřipojeno";

    /// <summary>True while the automatic connect is probing serial ports. Only ever set by an explicit
    /// operator action (the connect buttons), never at start-up.</summary>
    [ObservableProperty] private bool _isDetecting;

    /// <summary>The short progress line shown while <see cref="IsDetecting"/>.</summary>
    [ObservableProperty] private string? _detectionStatus;

    /// <summary>Raised when the automatic connect could not decide alone (several controllers or none)
    /// and the guided wizard has to take over from there.</summary>
    public event Action<GrblPortScanResult?>? AutoConnectNeedsWizard;

    /// <summary>How long the OS gets to release a port the probe just closed before it is reopened for
    /// the real connection. Internal so tests do not sleep.</summary>
    internal TimeSpan ConnectSettleDelay { get; set; } = TimeSpan.FromMilliseconds(300);

    /// <summary>The full sentence behind <see cref="StatusText"/>: what went wrong and what to try, or
    /// the status itself when nothing went wrong. StatusText stays short because it sits in a badge.</summary>
    public string StatusDetail => ConnectionError ?? StatusText;

    partial void OnStatusTextChanged(string value) => OnPropertyChanged(nameof(StatusDetail));
    partial void OnConnectionErrorChanged(string? value) => OnPropertyChanged(nameof(StatusDetail));
    [ObservableProperty] private string? _firmwareBanner;
    [ObservableProperty] private GrblDeviceProfile? _detectedDevice;
    [ObservableProperty] private string? _identificationMessage;
    [ObservableProperty] private string? _activeMachineProfileId;
    [ObservableProperty] private string _activeMachineName = "Laserové zařízení";
    [ObservableProperty] private string? _workspaceMessage;
    [ObservableProperty] private bool _isVirtualMachineSelected;

    /// <summary>Why the last connection attempt failed, or null if the last one did not. StatusText
    /// carries the same sentence, but it is also used for every ordinary state, so it cannot be read
    /// as "something went wrong" — a status surface needs to know the difference.</summary>
    [ObservableProperty] private string? _connectionError;

    // Mirrors settingsStore.Current.Machine.WorkAreaWidthMm/HeightMm as observable properties — that
    // settings object is a plain JSON-serializable POCO with no change notification, so a canvas bound
    // directly to it would only ever see whatever was on disk at startup and never the real bed size
    // once IdentifyDeviceAsync below overwrites it from the connected GRBL controller's $130/$131.
    [ObservableProperty] private double _workAreaWidthMm;
    [ObservableProperty] private double _workAreaHeightMm;

    public ConnectionViewModel(ILaserMachine connection, AppSettingsStore settingsStore, IGrblPortScanner? portScanner = null)
    {
        _connection = connection;
        _settingsStore = settingsStore;
        _portScanner = portScanner;
        if (_connection is IGrblDeviceProfileSource profileSource)
            profileSource.DeviceProfileChanged += profile => RunOnUiThread(() => DetectedDevice = profile);
        _workAreaWidthMm = settingsStore.Current.Machine.WorkAreaWidthMm;
        _workAreaHeightMm = settingsStore.Current.Machine.WorkAreaHeightMm;
        _connection.ConnectionStateChanged += state => RunOnUiThread(() =>
        {
            IsConnected = state == GrblConnectionState.Connected;
            IsConnecting = state == GrblConnectionState.Connecting;
            StatusText = state switch
            {
                GrblConnectionState.Connecting => "Připojování zařízení…",
                GrblConnectionState.Connected => "Připojeno - zjišťuje se typ zařízení",
                _ => "Nepřipojeno",
            };
            if (state == GrblConnectionState.Connected)
            {
                RememberConnectedPort();
                _identificationCancellation?.Cancel();
                _identificationCancellation = new CancellationTokenSource();
                _ = IdentifyDeviceAsync(_identificationCancellation.Token);
            }
            else if (state == GrblConnectionState.Disconnected)
            {
                _identificationCancellation?.Cancel();
                DetectedDevice = null;
                FirmwareBanner = null;
                IdentificationMessage = null;
            }
        });
        _connection.Connected += banner => RunOnUiThread(() =>
        {
            FirmwareBanner = banner;
            IsConnected = true;
            StatusText = $"Připojeno - {banner}";
        });
        _connection.Disconnected += error => RunOnUiThread(() =>
        {
            IsConnected = false;
            IsConnecting = false;
            ConnectionError = error is null ? null : UserFacingErrors.ConnectionLost(error);
            StatusText = error is null ? "Nepřipojeno" : "Spojení přerušeno";
        });

        RefreshPorts();
    }

    private async Task IdentifyDeviceAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(500, cancellationToken).ConfigureAwait(false);
            var settings = await _connection.QuerySettingsAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
            var profile = GrblDeviceProfileParser.Parse(settings, _connection.FirmwareBanner);
            RunOnUiThread(() =>
            {
                if (cancellationToken.IsCancellationRequested || !IsConnected) return;
                if (profile.NumericSettings.Count < 4)
                {
                    IdentificationMessage = "Zařízení neodpovědělo jako řadič GRBL. Zkontrolujte, zda je vybrán správný model a port, a zda zařízení používá firmware GRBL.";
                    return;
                }
                DetectedDevice = profile;
                var knownMachine = KnownMachineProfiles.Match(profile);
                ActiveMachineName = knownMachine?.DisplayName ??
                    (string.IsNullOrWhiteSpace(profile.FirmwareBanner)
                        ? SelectedPort ?? "Laserové zařízení"
                        : profile.FirmwareBanner);
                ActiveMachineProfileId = BuildMachineProfileId(profile, SelectedPort);
                _settingsStore.Current.Machine.ActiveProfileId = ActiveMachineProfileId;

                if (_settingsStore.Current.Machine.Profiles.TryGetValue(ActiveMachineProfileId, out var saved))
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

                IdentificationMessage = DescribeIdentification(profile);
                SaveWorkspaceAsDefaultCommand.NotifyCanExecuteChanged();
            });
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "GRBL device identification failed");
            RunOnUiThread(() =>
                IdentificationMessage = "Laser je připojen, ale jeho nastavení se nepodařilo načíst. Odpojte jej, zkontrolujte USB kabel a připojte se znovu.");
        }
    }

    /// <summary>Connecting never fails because a setting is missing: the controller is usable as a generic
    /// GRBL machine, and what is missing is reported here and enforced by JobPreflight before any job.</summary>
    private string DescribeIdentification(GrblDeviceProfile profile)
    {
        if (profile.LaserModeEnabled == false)
            return "Zařízení bylo nalezeno, ale laserový režim GRBL ($32) není zapnutý. Bez něj nelze úlohu spustit. Zapnout jej lze v průvodci zařízením.";

        var message = $"Zařízení nalezeno - pracovní plocha {_settingsStore.Current.Machine.WorkAreaWidthMm:0.#} × {_settingsStore.Current.Machine.WorkAreaHeightMm:0.#} mm.";
        if (profile.MaxTravelXmm is null || profile.MaxTravelYmm is null)
            message += " Řadič nehlásí rozměry ($130, $131), ověřte pracovní plochu ručně.";
        if (profile.MaxSpindleSpeed is null)
            message += " Řadič nehlásí maximální hodnotu S ($30), před spuštěním úlohy ji bude nutné nastavit.";
        if (profile.LaserModeEnabled is null)
            message += " Řadič nehlásí laserový režim ($32), před spuštěním úlohy jej bude nutné ověřit.";
        return message;
    }

    private void RememberConnectedPort()
    {
        // Only a real, physical connection counts as "a port the operator has used before". The
        // simulator must not turn the status-strip Připojit into a silent simulator connect later.
        if (string.IsNullOrWhiteSpace(SelectedPort) || VirtualGrblTransport.IsVirtualPort(SelectedPort)) return;
        _settingsStore.Current.Device.LastConnectedPort = SelectedPort;
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

    public bool HasAvailablePorts => AvailablePorts.Count > 0;

    private bool CanRefreshPorts() => !IsConnected && !IsConnecting && !IsDetecting;

    [RelayCommand(CanExecute = nameof(CanRefreshPorts))]
    private void RefreshPorts()
    {
        var current = SelectedPort;
        AvailablePorts.Clear();
        var physicalPorts = GrblConnection.GetAvailablePortNames().OrderBy(n => n).ToArray();
        foreach (var name in physicalPorts)
            AvailablePorts.Add(name);
        AvailablePorts.Add(VirtualGrblTransport.PortName);

        SelectedPort = AvailablePorts.Contains(current!) ? current : physicalPorts.FirstOrDefault() ?? VirtualGrblTransport.PortName;
        OnPropertyChanged(nameof(HasAvailablePorts));
    }

    private bool CanConnect() => SelectedCompatibility.AllowsDirectConnection && !IsConnected && !IsConnecting && !IsDetecting && !string.IsNullOrEmpty(SelectedPort);

    public bool CanAutoDetect => _portScanner is not null && !IsConnected && !IsConnecting && !IsDetecting;

    /// <summary>Probes the serial ports read-only for GRBL controllers. Returns null when this build has
    /// no scanner. Runs only when the operator asked for it, and never while a session is open.</summary>
    public async Task<GrblPortScanResult?> DetectAsync(IProgress<GrblPortScanProgress>? extraProgress = null, CancellationToken cancellationToken = default)
    {
        if (_portScanner is null || IsConnected || IsConnecting || IsDetecting) return null;
        _detectionCancellation?.Cancel();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _detectionCancellation = linked;
        IsDetecting = true;
        ConnectionError = null;
        DetectionStatus = "Hledám GRBL na portech COM…";
        StatusText = DetectionStatus;
        var progress = new Progress<GrblPortScanProgress>(report =>
        {
            DetectionStatus = $"Hledám GRBL na portech COM… {report.PortName} ({report.PortIndex + 1} z {report.PortCount})";
            extraProgress?.Report(report);
        });
        try
        {
            return await Task.Run(() => _portScanner.ScanAsync(progress, linked.Token), linked.Token).ConfigureAwait(true);
        }
        finally
        {
            IsDetecting = false;
            DetectionStatus = null;
            if (!IsConnected && !IsConnecting) StatusText = "Nepřipojeno";
            if (ReferenceEquals(_detectionCancellation, linked)) _detectionCancellation = null;
        }
    }

    public void CancelDetection() => _detectionCancellation?.Cancel();

    /// <summary>Connects to a controller the scan found. The auto-detected machine is always a generic
    /// GRBL profile: its work area and S range come from the controller itself, so no model is needed.</summary>
    public async Task<bool> ConnectDetectedAsync(GrblProbeResult found, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(found);
        if (IsConnected) Disconnect();
        SelectedCompatibility = MachineCompatibilityCatalog.Get(MachineCompatibilityCatalog.ExistingGrblId);
        if (!AvailablePorts.Contains(found.PortName)) AvailablePorts.Add(found.PortName);
        SelectedPort = found.PortName;
        BaudRate = found.BaudRate ?? 115200;
        if (ConnectSettleDelay > TimeSpan.Zero) await Task.Delay(ConnectSettleDelay, cancellationToken).ConfigureAwait(true);
        Connect();
        return IsConnected;
    }

    private bool HasPreviouslyChosenPort() =>
        !string.IsNullOrWhiteSpace(_settingsStore.Current.Device.LastConnectedPort)
        && AvailablePorts.Contains(_settingsStore.Current.Device.LastConnectedPort!);

    /// <summary>The status strip's Připojit. A port the operator connected to before is reused as it was;
    /// with no earlier choice the ports are probed instead of connecting to whatever the combo box
    /// happens to show. One controller found: connect. Several or none: the wizard takes over.</summary>
    [RelayCommand(CanExecute = nameof(CanSmartConnect))]
    private async Task SmartConnect()
    {
        if (HasPreviouslyChosenPort())
        {
            SelectedPort = _settingsStore.Current.Device.LastConnectedPort;
            if (CanConnect()) Connect();
            return;
        }

        GrblPortScanResult? result;
        try { result = await DetectAsync().ConfigureAwait(true); }
        catch (OperationCanceledException) { return; }
        catch (Exception ex)
        {
            Log.Warning(ex, "Automatic GRBL detection failed");
            AutoConnectNeedsWizard?.Invoke(null);
            return;
        }

        if (result is { Grbl.Count: 1 })
            await ConnectDetectedAsync(result.Grbl[0]).ConfigureAwait(true);
        else
            AutoConnectNeedsWizard?.Invoke(result);
    }

    private bool CanSmartConnect() => !IsConnected && !IsConnecting && !IsDetecting;

    [RelayCommand(CanExecute = nameof(CanConnect))]
    private void Connect()
    {
        try
        {
            IsConnecting = true;
            ConnectionError = null;
            StatusText = $"Připojování k {SelectedPort}…";
            _connection.Connect(SelectedPort!, BaudRate);
            _connection.StartStatusPolling(TimeSpan.FromMilliseconds(200));
        }
        catch (Exception ex)
        {
            IsConnecting = false;
            Log.Warning(ex, "Failed to open GRBL connection on {Port}", SelectedPort);
            ConnectionError = UserFacingErrors.ConnectionFailed(ex, SelectedPort);
            StatusText = "Připojení selhalo";
        }
    }

    private bool CanDisconnect() => IsConnected;

    [RelayCommand(CanExecute = nameof(CanDisconnect))]
    private void Disconnect()
    {
        _connection.Disconnect();
        IsConnected = false;
        IsConnecting = false;
        ConnectionError = null;
        StatusText = "Nepřipojeno";
    }

    partial void OnIsDetectingChanged(bool value)
    {
        ConnectCommand.NotifyCanExecuteChanged();
        SmartConnectCommand.NotifyCanExecuteChanged();
        RefreshPortsCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanAutoDetect));
    }

    partial void OnIsConnectedChanged(bool value)
    {
        SmartConnectCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanAutoDetect));
        ConnectCommand.NotifyCanExecuteChanged();
        DisconnectCommand.NotifyCanExecuteChanged();
        RefreshPortsCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsConnectingChanged(bool value)
    {
        SmartConnectCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanAutoDetect));
        ConnectCommand.NotifyCanExecuteChanged();
        RefreshPortsCommand.NotifyCanExecuteChanged();
    }

    partial void OnSelectedPortChanged(string? value)
    {
        IsVirtualMachineSelected = VirtualGrblTransport.IsVirtualPort(value);
        ConnectCommand.NotifyCanExecuteChanged();
    }

    private bool CanSaveWorkspaceAsDefault() =>
        !string.IsNullOrWhiteSpace(ActiveMachineProfileId) &&
        double.IsFinite(WorkAreaWidthMm) && WorkAreaWidthMm > 0 &&
        double.IsFinite(WorkAreaHeightMm) && WorkAreaHeightMm > 0;

    [RelayCommand(CanExecute = nameof(CanSaveWorkspaceAsDefault))]
    private void SaveWorkspaceAsDefault()
    {
        var profileId = ActiveMachineProfileId!;
        if (!_settingsStore.Current.Machine.Profiles.TryGetValue(profileId, out var profile))
            profile = new MachineProfilePreferences();
        profile.DisplayName = ActiveMachineName;
        profile.WorkAreaWidthMm = WorkAreaWidthMm;
        profile.WorkAreaHeightMm = WorkAreaHeightMm;
        _settingsStore.Current.Machine.Profiles[profileId] = profile;
        _settingsStore.Current.Machine.ActiveProfileId = profileId;
        _settingsStore.Save();
        WorkspaceMessage = $"Výchozí pracovní plocha {WorkAreaWidthMm:0.#} × {WorkAreaHeightMm:0.#} mm byla uložena.";
    }

    partial void OnWorkAreaWidthMmChanged(double value)
    {
        if (double.IsFinite(value) && value > 0) _settingsStore.Current.Machine.WorkAreaWidthMm = value;
        SaveWorkspaceAsDefaultCommand.NotifyCanExecuteChanged();
    }

    partial void OnWorkAreaHeightMmChanged(double value)
    {
        if (double.IsFinite(value) && value > 0) _settingsStore.Current.Machine.WorkAreaHeightMm = value;
        SaveWorkspaceAsDefaultCommand.NotifyCanExecuteChanged();
    }

    private static void RunOnUiThread(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess()) action();
        else dispatcher.Invoke(action);
    }
}
