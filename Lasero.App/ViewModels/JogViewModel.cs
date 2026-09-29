using System.Windows;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lasero.Core.Grbl;
using Lasero.Core.Machines;
using Serilog;

namespace Lasero.App.ViewModels;

public partial class JogViewModel : ObservableObject
{
    private readonly ILaserMachine _connection;
    private readonly AppSettingsStore _settingsStore;
    private long _positioningLaserRequest;
    private double? _positioningLaserMaximumSValue;
    private bool _positioningLaserModeEnabled;
    private Timer? _positioningLaserWatchdog;

    /// <summary>Asks the operator to confirm a risky machine action. Defaults to the shared Lasero
    /// dialog; tests replace it so no window is shown.</summary>
    public Func<LaseroDialogOptions, bool> ConfirmAction { get; set; } = options =>
        LaseroDialogWindow.Show(Application.Current?.MainWindow, options) == LaseroDialogChoice.Primary;

    /// <summary>How often a lit positioning laser re-checks that the machine is still connected, idle and reporting fresh status.</summary>
    public TimeSpan PositioningLaserWatchdogInterval { get; set; } = TimeSpan.FromMilliseconds(500);

    public double[] StepSizePresets { get; } = [0.1, 1, 5, 10, 50, 100];

    [ObservableProperty] private double _stepSizeMm = 10;
    [ObservableProperty] private double _jogFeedRate = 2000;
    [ObservableProperty] private double _zStepSizeMm = 1;
    [ObservableProperty] private double _zJogFeedRate = 600;
    [ObservableProperty] private bool _enableZAxis = true;
    [ObservableProperty] private bool _invertZAxis;
    [ObservableProperty] private string? _lastActionMessage;
    [ObservableProperty] private double _positioningLaserPowerPercent = 1;
    [ObservableProperty] private bool _isPositioningLaserOn;

    public bool CanUsePositioningLaser =>
        _positioningLaserModeEnabled &&
        _positioningLaserMaximumSValue is > 0 && double.IsFinite(_positioningLaserMaximumSValue.Value) &&
        CanManualMotion() &&
        IsValidPositioningPower(PositioningLaserPowerPercent);

    /// <summary>Whether a click-to-jog request would currently be accepted — bindable so the
    /// workspace preview canvas can show a crosshair cursor only when it would actually do something.</summary>
    [ObservableProperty] private bool _canJogToPoint;

    public JogViewModel(ILaserMachine connection, AppSettingsStore settingsStore)
    {
        _connection = connection;
        _settingsStore = settingsStore;
        _connection.ConnectionStateChanged += _ => RunOnUiThread(NotifyMachineStateChanged);
        _connection.StatusUpdated += _ => RunOnUiThread(NotifyMachineStateChanged);
        _connection.AlertChanged += _ => RunOnUiThread(NotifyMachineStateChanged);
    }

    private bool IsConnected() => _connection.State == GrblConnectionState.Connected;
    private bool HasFreshStatus() => _connection.LastStatusReceivedUtc is { } timestamp
        && DateTime.UtcNow - timestamp <= TimeSpan.FromSeconds(2);
    private bool CanManualMotion() => IsConnected() && HasFreshStatus() && _connection.LastStatus?.Mode == GrblMachineMode.Idle;
    // The positioning laser reports Idle to GRBL — CanManualMotion alone would happily let Jog/Home/
    // GoToWorkZero run while the beam is physically lit. Every motion command must also check this.
    private bool CanManualMotionWithLaserOff() => CanManualMotion() && !IsPositioningLaserOn;
    private bool CanHome() => IsConnected() && HasFreshStatus() && !IsPositioningLaserOn
        && _connection.LastStatus?.Mode is GrblMachineMode.Idle or GrblMachineMode.Alarm;
    private bool CanUnlock() => IsConnected()
        && (_connection.LastStatus?.Mode == GrblMachineMode.Alarm
            || _connection.ActiveAlert?.Kind == MachineAlertKind.Alarm);
    private bool CanJogXY() => CanManualMotionWithLaserOff() && IsPositiveFinite(StepSizeMm) && IsPositiveFinite(JogFeedRate);
    private bool CanJogZ() => EnableZAxis && CanManualMotionWithLaserOff() && IsPositiveFinite(ZStepSizeMm) && IsPositiveFinite(ZJogFeedRate);

    public void ConfigurePositioningLaser(double? maximumSValue, bool? laserModeEnabled)
    {
        _positioningLaserMaximumSValue = maximumSValue is > 0 && double.IsFinite(maximumSValue.Value)
            ? maximumSValue.Value
            : null;
        _positioningLaserModeEnabled = laserModeEnabled == true;
        OnPropertyChanged(nameof(CanUsePositioningLaser));
    }

    /// <summary>
    /// Energizes the laser at a deliberately low percentage of the controller's $30 maximum.
    /// The view invokes this on pointer/key down and always pairs it with StopPositioningLaserAsync
    /// on release; this is not a toggle that can be accidentally left enabled.
    /// </summary>
    public async Task StartPositioningLaserAsync()
    {
        if (!CanUsePositioningLaser || IsPositioningLaserOn) return;

        var request = Interlocked.Increment(ref _positioningLaserRequest);
        var sValue = _positioningLaserMaximumSValue!.Value * PositioningLaserPowerPercent / 100.0;
        var result = await _connection.SendCommandAsync(
            $"M3 S{sValue.ToString("0.###", CultureInfo.InvariantCulture)}");

        // A release can queue M5 while M3 is awaiting its controller acknowledgement. Never let the
        // older completion turn the UI back on after that release.
        if (request != Volatile.Read(ref _positioningLaserRequest)) return;
        IsPositioningLaserOn = result.IsOk;
        LastActionMessage = result.IsOk
            ? $"Polohovací paprsek: {PositioningLaserPowerPercent:0.#} %."
            : result.Message ?? "Polohovací paprsek se nepodařilo zapnout.";
    }

    public async Task StopPositioningLaserAsync()
    {
        Interlocked.Increment(ref _positioningLaserRequest);
        IsPositioningLaserOn = false;
        if (!IsConnected()) return;

        var result = await _connection.SendCommandAsync("M5");
        if (!result.IsOk)
        {
            // M5 is queued behind other line commands and can be rejected or lost; the realtime soft
            // reset bypasses the queue and always stops the spindle/laser, so never leave it at that.
            _connection.SoftReset();
            LastActionMessage = "Vypnutí paprsku se nepodařilo potvrdit, zařízení bylo resetováno.";
            return;
        }
        LastActionMessage = "Polohovací paprsek je vypnutý.";
    }

    // Dead-man's switch for a lit positioning laser. The view releases the beam on every input event
    // it can observe, but a stalled status stream or a non-idle controller is only visible here.
    private void UpdatePositioningLaserWatchdog(bool lit)
    {
        var previous = Interlocked.Exchange(ref _positioningLaserWatchdog, null);
        previous?.Dispose();
        if (!lit) return;
        var interval = PositioningLaserWatchdogInterval > TimeSpan.Zero
            ? PositioningLaserWatchdogInterval
            : TimeSpan.FromMilliseconds(500);
        _positioningLaserWatchdog = new Timer(_ => RunOnUiThread(CheckLitPositioningLaser), null, interval, interval);
    }

    private void CheckLitPositioningLaser()
    {
        if (!IsPositioningLaserOn || CanManualMotion()) return;
        _ = StopPositioningLaserAsync();
    }

    [RelayCommand(CanExecute = nameof(CanJogXY))]
    private Task JogXPos() => JogAsync(StepSizeMm, 0, 0);

    [RelayCommand(CanExecute = nameof(CanJogXY))]
    private Task JogXNeg() => JogAsync(-StepSizeMm, 0, 0);

    [RelayCommand(CanExecute = nameof(CanJogXY))]
    private Task JogYPos() => JogAsync(0, StepSizeMm, 0);

    [RelayCommand(CanExecute = nameof(CanJogXY))]
    private Task JogYNeg() => JogAsync(0, -StepSizeMm, 0);

    [RelayCommand(CanExecute = nameof(CanJogXY))]
    private Task JogXNegYPos() => JogAsync(-StepSizeMm, StepSizeMm, 0);

    [RelayCommand(CanExecute = nameof(CanJogXY))]
    private Task JogXPosYPos() => JogAsync(StepSizeMm, StepSizeMm, 0);

    [RelayCommand(CanExecute = nameof(CanJogXY))]
    private Task JogXNegYNeg() => JogAsync(-StepSizeMm, -StepSizeMm, 0);

    [RelayCommand(CanExecute = nameof(CanJogXY))]
    private Task JogXPosYNeg() => JogAsync(StepSizeMm, -StepSizeMm, 0);

    [RelayCommand(CanExecute = nameof(CanJogZ))]
    private Task JogZPos() => JogAsync(0, 0, InvertZAxis ? -ZStepSizeMm : ZStepSizeMm, ZJogFeedRate);

    [RelayCommand(CanExecute = nameof(CanJogZ))]
    private Task JogZNeg() => JogAsync(0, 0, InvertZAxis ? ZStepSizeMm : -ZStepSizeMm, ZJogFeedRate);

    private async Task JogAsync(double x, double y, double z, double? feedRate = null)
    {
        var result = await _connection.JogAsync(x, y, z, feedRate ?? JogFeedRate);
        LastActionMessage = result.IsOk
            ? $"Posun: X {x:+0.###;-0.###;0} · Y {y:+0.###;-0.###;0} · Z {z:+0.###;-0.###;0} mm"
            : result.Message ?? "Pohyb se nepodařilo provést.";
    }

    [RelayCommand(CanExecute = nameof(IsConnected))]
    private void CancelJog() => _connection.CancelJog();

    /// <summary>Absolute jog to a clicked point on the workspace preview — XY only, Z stays wherever
    /// the machine currently is (never a plunge triggered by a click).</summary>
    public async Task JogToPointAsync(double worldX, double worldY)
    {
        if (!CanManualMotionWithLaserOff()) return;
        var z = _connection.LastStatus?.WorkPosition.Z ?? 0;
        var result = await _connection.JogAsync(worldX, worldY, z, JogFeedRate, relative: false);
        LastActionMessage = result.IsOk ? $"Přesun na {worldX:0.#}, {worldY:0.#}." : result.Message;
    }

    [RelayCommand(CanExecute = nameof(CanHome))]
    private async Task Home()
    {
        LastActionMessage = "Probíhá najíždění do výchozí polohy…";
        var result = await _connection.HomeAsync();
        LastActionMessage = result.IsOk ? "Výchozí poloha byla nalezena." : result.Message;
    }

    [RelayCommand(CanExecute = nameof(CanUnlock))]
    private async Task Unlock()
    {
        var result = await _connection.UnlockAsync();
        LastActionMessage = result.IsOk ? "Zařízení bylo odemknuto." : result.Message;
    }

    [RelayCommand(CanExecute = nameof(IsConnected))]
    private void SoftReset()
    {
        var proceed = !_settingsStore.Current.Safety.ConfirmSoftReset || LaseroDialogWindow.Show(
            Application.Current.MainWindow,
            new LaseroDialogOptions(
                "Nouzový reset zařízení",
                "Reset okamžitě zastaví pohyb. Stroj poté nemusí znát přesnou polohu a před další prací může vyžadovat homing.",
                "Provést reset",
                CancelText: "Zrušit",
                Tone: LaseroDialogTone.Danger,
                DestructivePrimary: true)) == LaseroDialogChoice.Primary;

        if (!proceed) return;

        _connection.SoftReset();
        Log.Information("User-triggered GRBL soft reset");
        LastActionMessage = "Příkaz resetu byl odeslán.";
    }

    [RelayCommand(CanExecute = nameof(CanManualMotionWithLaserOff))]
    private async Task SetOriginHere()
    {
        if (!ConfirmAction(new LaseroDialogOptions(
                "Nastavit pracovní nulu",
                "Aktuální pracovní nula (G54) bude přepsána polohou, ve které se nyní nachází laserová hlava.",
                "Nastavit nulu",
                CancelText: "Zrušit",
                Tone: LaseroDialogTone.Warning))) return;
        // Machine state may have changed while the dialog was open.
        if (!CanManualMotionWithLaserOff()) return;

        var result = await _connection.SetWorkOriginAsync(1, Position.Zero);
        LastActionMessage = result.IsOk ? "Pracovní nula byla nastavena na aktuální pozici (G54)." : result.Message;
    }

    [RelayCommand(CanExecute = nameof(CanManualMotionWithLaserOff))]
    private async Task GoToWorkZero()
    {
        if (!ConfirmAction(new LaseroDialogOptions(
                "Přesun na pracovní nulu",
                "Hlava se přesune na pracovní nulu XY rychloposuvem (G0) plnou rychlostí. Před pokračováním zkontrolujte, že je dráha volná.",
                "Přesunout",
                CancelText: "Zrušit",
                Tone: LaseroDialogTone.Warning))) return;
        // Machine state may have changed while the dialog was open.
        if (!CanManualMotionWithLaserOff()) return;

        var result = await _connection.SendCommandAsync("G90 G0 X0 Y0");
        LastActionMessage = result.IsOk ? "Přesun na pracovní nulu byl dokončen." : result.Message;
    }

    private void NotifyMachineStateChanged()
    {
        if (!IsConnected()) IsPositioningLaserOn = false;
        NotifyXyJogCommands();
        NotifyZJogCommands();
        CancelJogCommand.NotifyCanExecuteChanged();
        HomeCommand.NotifyCanExecuteChanged();
        UnlockCommand.NotifyCanExecuteChanged();
        SoftResetCommand.NotifyCanExecuteChanged();
        SetOriginHereCommand.NotifyCanExecuteChanged();
        GoToWorkZeroCommand.NotifyCanExecuteChanged();
        CanJogToPoint = CanManualMotion();
        OnPropertyChanged(nameof(CanUsePositioningLaser));
    }

    partial void OnStepSizeMmChanged(double value) => NotifyXyJogCommands();
    partial void OnJogFeedRateChanged(double value) => NotifyXyJogCommands();
    partial void OnZStepSizeMmChanged(double value) => NotifyZJogCommands();
    partial void OnZJogFeedRateChanged(double value) => NotifyZJogCommands();
    partial void OnEnableZAxisChanged(bool value) => NotifyZJogCommands();
    partial void OnInvertZAxisChanged(bool value) => NotifyZJogCommands();
    partial void OnPositioningLaserPowerPercentChanged(double value) =>
        OnPropertyChanged(nameof(CanUsePositioningLaser));

    // The laser being lit must immediately re-gate every motion command (Jog/Home/SetOriginHere/
    // GoToWorkZero) — see CanManualMotionWithLaserOff. NotifyMachineStateChanged already resets
    // IsPositioningLaserOn to false on disconnect, which safely re-enters this once and settles.
    partial void OnIsPositioningLaserOnChanged(bool value)
    {
        UpdatePositioningLaserWatchdog(value);
        NotifyMachineStateChanged();
    }

    private void NotifyXyJogCommands()
    {
        JogXPosCommand.NotifyCanExecuteChanged();
        JogXNegCommand.NotifyCanExecuteChanged();
        JogYPosCommand.NotifyCanExecuteChanged();
        JogYNegCommand.NotifyCanExecuteChanged();
        JogXNegYPosCommand.NotifyCanExecuteChanged();
        JogXPosYPosCommand.NotifyCanExecuteChanged();
        JogXNegYNegCommand.NotifyCanExecuteChanged();
        JogXPosYNegCommand.NotifyCanExecuteChanged();
    }

    private void NotifyZJogCommands()
    {
        JogZPosCommand.NotifyCanExecuteChanged();
        JogZNegCommand.NotifyCanExecuteChanged();
    }

    private static bool IsPositiveFinite(double value) => double.IsFinite(value) && value > 0;
    private static bool IsValidPositioningPower(double value) =>
        double.IsFinite(value) && value is >= 0.1 and <= 5;

    private static void RunOnUiThread(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess()) action();
        else dispatcher.BeginInvoke(action);
    }
}
