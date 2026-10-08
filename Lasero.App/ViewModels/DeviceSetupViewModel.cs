using System.Collections.Generic;
using System.ComponentModel;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lasero.App.Components;
using Lasero.Core.Grbl;

namespace Lasero.App.ViewModels;

/// <summary>One row of the status card's detail block.</summary>
public sealed record DeviceDetail(string Label, string Value);

/// <summary>
/// Turns the connection's raw state into the one sentence and the one action the operator needs at
/// that moment, for the Zařízení screen's status card.
/// <para>
/// It decides nothing: it reads IsConnecting / IsConnected / ConnectionError / DetectedDevice and
/// reports what it finds. The manual port and baud controls underneath stay exactly as they were, so
/// this is the guided path rather than the only path.
/// </para>
/// </summary>
public partial class DeviceSetupViewModel : ObservableObject
{
    private readonly ConnectionViewModel _connection;
    private readonly Action _openWizard;

    public DeviceSetupViewModel(ConnectionViewModel connection, Action openWizard)
    {
        _connection = connection;
        _openWizard = openWizard;
        _connection.PropertyChanged += OnConnectionChanged;
    }

    private void OnConnectionChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is not (nameof(ConnectionViewModel.IsConnected)
            or nameof(ConnectionViewModel.IsConnecting)
            or nameof(ConnectionViewModel.IsDetecting)
            or nameof(ConnectionViewModel.DetectionStatus)
            or nameof(ConnectionViewModel.ConnectionError)
            or nameof(ConnectionViewModel.DetectedDevice)
            or nameof(ConnectionViewModel.IdentificationMessage)
            or nameof(ConnectionViewModel.SelectedPort)
            or nameof(ConnectionViewModel.BaudRate)
            or nameof(ConnectionViewModel.WorkAreaWidthMm)
            or nameof(ConnectionViewModel.WorkAreaHeightMm)
            or nameof(ConnectionViewModel.ActiveMachineName)))
        {
            return;
        }

        OnPropertyChanged(nameof(Status));
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(Description));
        OnPropertyChanged(nameof(ShowsProgress));
        OnPropertyChanged(nameof(PrimaryLabel));
        OnPropertyChanged(nameof(PrimaryCommand));
        OnPropertyChanged(nameof(SecondaryLabel));
        OnPropertyChanged(nameof(SecondaryCommand));
        OnPropertyChanged(nameof(Details));
    }

    public ProcessStatus Status
    {
        get
        {
            if (_connection.IsConnecting || _connection.IsDetecting) return ProcessStatus.Progress;
            if (_connection.IsConnected)
                return _connection.DetectedDevice is null ? ProcessStatus.Progress : ProcessStatus.Success;
            return _connection.ConnectionError is null ? ProcessStatus.Waiting : ProcessStatus.Error;
        }
    }

    public string Title => Status switch
    {
        ProcessStatus.Progress when !_connection.IsConnected => "Hledání gravírky",
        ProcessStatus.Progress => "Nastavení gravírky",
        ProcessStatus.Success => "Gravírka je připravena",
        ProcessStatus.Error => "Gravírku se nepodařilo připojit",
        _ => "Připojení gravírky",
    };

    public string Description => Status switch
    {
        ProcessStatus.Progress when _connection.IsDetecting =>
            _connection.DetectionStatus ?? "Hledám GRBL na portech COM…",
        ProcessStatus.Progress when !_connection.IsConnected =>
            $"Otevírání portu {_connection.SelectedPort} rychlostí {_connection.BaudRate} Bd.",
        ProcessStatus.Progress =>
            "Načítání nastavení stroje a rozměrů pracovní plochy z GRBL ($130 a $131).",
        ProcessStatus.Success =>
            _connection.IdentificationMessage ?? "Lasero rozpoznalo zařízení a načetlo jeho parametry.",
        ProcessStatus.Error =>
            _connection.ConnectionError ?? "LASERO se nepodařilo připojit ke stroji. Je potřeba zkontrolovat USB kabel, potom lze připojení zkusit znovu.",
        _ => "Laser je potřeba zapnout a připojit datovým USB kabelem. Po klepnutí na Připojit LASERO použije zvolený port, ve výchozím stavu Automaticky samo najde řadič GRBL na portech COM, model ani port není třeba vybírat. Porty se nezkoušejí bez klepnutí. Návrh lze upravovat i bez laseru.",
    };

    /// <summary>A bar only while something is genuinely running, and indeterminate throughout: GRBL
    /// does not report how far through a settings read it is, and a made-up percentage would be worse
    /// than admitting the app cannot see it.</summary>
    public bool ShowsProgress => Status == ProcessStatus.Progress;

    public double? Progress => null;

    /// <summary>
    /// No primary action once the machine is ready. There is nothing left to do on this screen at
    /// that point, and a blue button with nowhere to go is worse than none — disconnecting is the
    /// only remaining option and it belongs in the quiet slot.
    /// </summary>
    public string? PrimaryLabel => Status switch
    {
        ProcessStatus.Waiting => "Připojit",
        ProcessStatus.Error => "Připojit znovu",
        _ => null,
    };

    public ICommand? PrimaryCommand => Status switch
    {
        ProcessStatus.Waiting => _connection.ConnectSelectedCommand,
        ProcessStatus.Error => _connection.ConnectSelectedCommand,
        _ => null,
    };

    public string? SecondaryLabel => Status switch
    {
        ProcessStatus.Progress => "Zrušit",
        ProcessStatus.Success => "Odpojit",
        ProcessStatus.Error => "Průvodce zařízením",
        _ => null,
    };

    public ICommand? SecondaryCommand => Status switch
    {
        ProcessStatus.Progress when _connection.IsDetecting => CancelDetectionCommand,
        ProcessStatus.Progress or ProcessStatus.Success => _connection.DisconnectCommand,
        ProcessStatus.Error => OpenWizardCommand,
        _ => null,
    };

    /// <summary>Only shown once there is something real to show. Every value comes off the connected
    /// controller, so the block is empty rather than plausible when nothing is attached.</summary>
    public IReadOnlyList<DeviceDetail> Details
    {
        get
        {
            if (Status != ProcessStatus.Success) return [];
            return
            [
                new DeviceDetail("Zařízení", _connection.ActiveMachineName),
                new DeviceDetail("Port", PortLabel),
                new DeviceDetail("Pracovní plocha", WorkAreaLabel),
                new DeviceDetail("Firmware", FirmwareLabel),
            ];
        }
    }

    [RelayCommand]
    private void OpenWizard() => _openWizard();

    [RelayCommand]
    private void CancelDetection() => _connection.CancelDetection();

    private string PortLabel => VirtualGrblTransport.IsVirtualPort(_connection.SelectedPort)
        ? "Simulátor — bez hardwaru"
        : $"{_connection.SelectedPort} · {_connection.BaudRate} Bd";

    private string WorkAreaLabel =>
        $"{_connection.WorkAreaWidthMm:0.#} × {_connection.WorkAreaHeightMm:0.#} mm";

    private string FirmwareLabel
    {
        get
        {
            var banner = _connection.DetectedDevice?.FirmwareBanner;
            if (string.IsNullOrWhiteSpace(banner)) return "Neznámo";
            var bracket = banner.IndexOf('[');
            return (bracket > 0 ? banner[..bracket] : banner).Trim();
        }
    }
}
