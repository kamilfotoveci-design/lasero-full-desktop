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
    private CancellationTokenSource? _identificationCancellation;

    public ObservableCollection<string> AvailablePorts { get; } = new();
    public int[] CommonBaudRates { get; } = [9600, 19200, 38400, 57600, 115200, 230400];

    [ObservableProperty] private string? _selectedPort;
    [ObservableProperty] private int _baudRate = 115200;
    [ObservableProperty] private bool _isConnected;
    [ObservableProperty] private bool _isConnecting;
    [ObservableProperty] private string _statusText = "Nepřipojeno";
    [ObservableProperty] private string? _firmwareBanner;
    [ObservableProperty] private GrblDeviceProfile? _detectedDevice;
    [ObservableProperty] private string? _identificationMessage;
    [ObservableProperty] private string? _activeMachineProfileId;
    [ObservableProperty] private string _activeMachineName = "Laserové zařízení";
    [ObservableProperty] private string? _workspaceMessage;
    [ObservableProperty] private bool _isVirtualMachineSelected;

    // Mirrors settingsStore.Current.Machine.WorkAreaWidthMm/HeightMm as observable properties — that
    // settings object is a plain JSON-serializable POCO with no change notification, so a canvas bound
    // directly to it would only ever see whatever was on disk at startup and never the real bed size
    // once IdentifyDeviceAsync below overwrites it from the connected GRBL controller's $130/$131.
    [ObservableProperty] private double _workAreaWidthMm;
    [ObservableProperty] private double _workAreaHeightMm;

    public ConnectionViewModel(ILaserMachine connection, AppSettingsStore settingsStore)
    {
        _connection = connection;
        _settingsStore = settingsStore;
        _workAreaWidthMm = settingsStore.Current.Machine.WorkAreaWidthMm;
        _workAreaHeightMm = settingsStore.Current.Machine.WorkAreaHeightMm;
        _connection.ConnectionStateChanged += state => RunOnUiThread(() =>
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
            StatusText = $"Připojeno — {banner}";
        });
        _connection.Disconnected += error => RunOnUiThread(() =>
        {
            IsConnected = false;
            IsConnecting = false;
            StatusText = error is null ? "Nepřipojeno" : $"Spojení bylo přerušeno: {error.Message}";
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
                DetectedDevice = profile;
                ActiveMachineName = string.IsNullOrWhiteSpace(profile.FirmwareBanner)
                    ? SelectedPort ?? "Laserové zařízení"
                    : profile.FirmwareBanner;
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
                    if (profile.MaxTravelXmm is { } width) WorkAreaWidthMm = width;
                    if (profile.MaxTravelYmm is { } height) WorkAreaHeightMm = height;
                    WorkspaceMessage = "Rozměry byly načteny ze zařízení. Uložte je jako výchozí, pokud jsou správné.";
                }

                IdentificationMessage = profile.LaserModeEnabled == false
                    ? "Zařízení bylo nalezeno, ale GRBL laserový režim $32 není zapnutý."
                    : $"Zařízení nalezeno — pracovní plocha {_settingsStore.Current.Machine.WorkAreaWidthMm:0.#} × {_settingsStore.Current.Machine.WorkAreaHeightMm:0.#} mm.";
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

    public bool HasAvailablePorts => AvailablePorts.Count > 0;

    private bool CanRefreshPorts() => !IsConnected && !IsConnecting;

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

    private bool CanConnect() => !IsConnected && !IsConnecting && !string.IsNullOrEmpty(SelectedPort);

    [RelayCommand(CanExecute = nameof(CanConnect))]
    private void Connect()
    {
        try
        {
            IsConnecting = true;
            StatusText = $"Připojuji se k {SelectedPort}…";
            _connection.Connect(SelectedPort!, BaudRate);
            _connection.StartStatusPolling(TimeSpan.FromMilliseconds(200));
        }
        catch (Exception ex)
        {
            IsConnecting = false;
            Log.Warning(ex, "Failed to open GRBL connection on {Port}", SelectedPort);
            StatusText = $"Připojení selhalo: {ex.Message}";
        }
    }

    private bool CanDisconnect() => IsConnected;

    [RelayCommand(CanExecute = nameof(CanDisconnect))]
    private void Disconnect()
    {
        _connection.Disconnect();
        IsConnected = false;
        IsConnecting = false;
        StatusText = "Nepřipojeno";
    }

    partial void OnIsConnectedChanged(bool value)
    {
        ConnectCommand.NotifyCanExecuteChanged();
        DisconnectCommand.NotifyCanExecuteChanged();
        RefreshPortsCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsConnectingChanged(bool value)
    {
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
