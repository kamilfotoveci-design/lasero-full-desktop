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
    private DateTime _positioningLaserStartedUtc;
    private const double FixedPositioningLaserPowerPercent = 1;

    /// <summary>Asks the operator to confirm a risky machine action. Defaults to the shared Lasero
    /// dialog; tests replace it so no window is shown.</summary>
    public Func<LaseroDialogOptions, bool> ConfirmAction { get; set; } = options =>
        LaseroDialogWindow.Show(Application.Current?.MainWindow, options) == LaseroDialogChoice.Primary;

    /// <summary>How often a lit positioning laser re-checks that the machine is still connected, idle and reporting fresh status.</summary>
    public TimeSpan PositioningLaserWatchdogInterval { get; set; } = TimeSpan.FromMilliseconds(500);
    public TimeSpan PositioningLaserMaximumDuration { get; set; } = TimeSpan.FromSeconds(30);

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
    [ObservableProperty] private bool _isPositioningLaserStarting;
    [ObservableProperty] private bool _isPositioningLaserStopping;

    public bool IsPositioningLaserActiveOrStarting => IsPositioningLaserOn || IsPositioningLaserStarting;
    public bool IsPositioningLaserBusy => IsPositioningLaserActiveOrStarting || IsPositioningLaserStopping;
    public string PositioningLaserButtonLabel => IsPositioningLaserStopping ? "Vypínám paprsek…"
        : IsPositioningLaserActiveOrStarting ? "Vypnout paprsek" : "Zapnout paprsek (1 %)";
    public bool CanTogglePositioningLaser => !IsPositioningLaserStopping &&
        (IsPositioningLaserActiveOrStarting || CanUsePositioningLaser);

    public bool CanUsePositioningLaser =>
        _positioningLaserModeEnabled &&
        _positioningLaserMaximumSValue is > 0 && double.IsFinite(_positioningLaserMaximumSValue.Value) &&
        !JobActive() && CanManualMotion() &&
        PositioningLaserPowerPercent == FixedPositioningLaserPowerPercent;

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
    private bool CanManualMotionWithLaserOff() => !JobActive() && CanManualMotion() && !IsPositioningLaserBusy;
    /// <summary>Supplied by the shell: true while a job is Preparing, Framing, Running or Paused. Homing sends the head to the
    /// machine origin, so it must never start under a job, whatever the controller happens to report between two moves.</summary>
    public Func<bool>? IsJobActive { get; set; }

    private bool JobActive() => IsJobActive?.Invoke() == true;

    private bool CanHome() => !JobActive() && IsConnected() && HasFreshStatus() && !IsPositioningLaserActiveOrStarting && !IsPositioningLaserStopping
        && _connection.LastStatus?.Mode is GrblMachineMode.Idle or GrblMachineMode.Alarm;

    /// <summary>Why Najet domů is unavailable, or null when it is available. A disabled machine action says why.</summary>
    public string? HomeBlockedReason =>
        JobActive() ? "Při probíhající úloze nelze najíždět do výchozí polohy."
        : !IsConnected() ? "Do výchozí polohy lze najet po připojení zařízení."
        : !HasFreshStatus() ? "Čeká se na aktuální stav zařízení."
        : IsPositioningLaserActiveOrStarting || IsPositioningLaserStopping ? "Do výchozí polohy lze najet po vypnutí polohovacího laseru."
        : (_connection.LastStatus?.Mode is GrblMachineMode.Idle or GrblMachineMode.Alarm) ? null
        : "Zařízení se právě pohybuje nebo není v klidu.";

    /// <summary>The tooltip of the one Najet domů control.</summary>
    public string HomeTooltip => HomeBlockedReason ?? "Hlava se pohne do výchozí polohy. Pracovní prostor musí být volný.";

    /// <summary>Re-evaluates the job guard after the job state changed.</summary>
    public void RefreshJobGuard() => RunOnUiThread(NotifyMachineStateChanged);
    private bool CanUnlock() => IsConnected()
        && (_connection.LastStatus?.Mode == GrblMachineMode.Alarm
            || _connection.ActiveAlert?.Kind == MachineAlertKind.Alarm);
    private bool CanJogXY() => CanManualMotionWithLaserOff() && IsPositiveFinite(StepSizeMm) && IsPositiveFinite(JogFeedRate);
    private bool CanJogZ() => EnableZAxis && CanManualMotionWithLaserOff() && IsPositiveFinite(ZStepSizeMm) && IsPositiveFinite(ZJogFeedRate);

    public void ConfigurePositioningLaser(double? maximumSValue, bool? laserModeEnabled)
    {
        var previousMaximum = _positioningLaserMaximumSValue;
        var previousLaserMode = _positioningLaserModeEnabled;
        _positioningLaserMaximumSValue = maximumSValue is > 0 && double.IsFinite(maximumSValue.Value)
            ? maximumSValue.Value
            : null;
        _positioningLaserModeEnabled = laserModeEnabled == true;
        if (IsPositioningLaserActiveOrStarting &&
            (previousMaximum != _positioningLaserMaximumSValue ||
             previousLaserMode != _positioningLaserModeEnabled))
            _ = StopPositioningLaserAsync();
        OnPropertyChanged(nameof(CanUsePositioningLaser));
        OnPropertyChanged(nameof(CanTogglePositioningLaser));
    }

    public Task TogglePositioningLaserAsync() =>
        IsPositioningLaserActiveOrStarting ? StopPositioningLaserAsync() : StartPositioningLaserAsync();

    /// <summary>Starts a bounded 1% pointer session against the controller's confirmed $30 maximum.</summary>
    public async Task StartPositioningLaserAsync()
    {
        if (!CanUsePositioningLaser || IsPositioningLaserActiveOrStarting || IsPositioningLaserStopping) return;

        var request = Interlocked.Increment(ref _positioningLaserRequest);
        _positioningLaserStartedUtc = DateTime.UtcNow;
        IsPositioningLaserStarting = true;
        try
        {
            var sValue = _positioningLaserMaximumSValue!.Value * FixedPositioningLaserPowerPercent / 100.0;
            var result = await _connection.SendCommandAsync(
                $"M3 S{sValue.ToString("0.###", CultureInfo.InvariantCulture)}");

            // A second click can queue M5 while M3 awaits acknowledgement. Its completion must not
            // turn the UI back on after that stop request.
            if (request != Volatile.Read(ref _positioningLaserRequest)) return;
            if (!result.IsOk)
            {
                // The command may have reached the controller even if its acknowledgement was lost.
                if (IsConnected()) _connection.SoftReset();
                LastActionMessage = result.Message ?? "Zapnutí paprsku nebylo potvrzeno; zařízení bylo resetováno.";
                return;
            }

            IsPositioningLaserOn = true;
            LastActionMessage = "Polohovací paprsek: 1 %. Kliknutím vypnete.";
            if (DateTime.UtcNow - _positioningLaserStartedUtc >= PositioningLaserMaximumDuration ||
                !CanManualMotion() || JobActive())
                await StopPositioningLaserAsync();
        }
        catch (Exception ex)
        {
            if (request == Volatile.Read(ref _positioningLaserRequest))
            {
                IsPositioningLaserOn = false;
                if (IsConnected()) _connection.SoftReset();
                LastActionMessage = $"Zapnutí paprsku selhalo; zařízení bylo resetováno: {ex.Message}";
            }
        }
        finally
        {
            IsPositioningLaserStarting = false;
        }
    }

    public async Task StopPositioningLaserAsync()
    {
        if (IsPositioningLaserStopping) return;
        Interlocked.Increment(ref _positioningLaserRequest);
        IsPositioningLaserStopping = true;
        IsPositioningLaserStarting = false;
        IsPositioningLaserOn = false;
        try
        {
            if (!IsConnected()) return;

            var offCommand = _connection.SendCommandAsync("M5");
            if (await Task.WhenAny(offCommand, Task.Delay(TimeSpan.FromSeconds(1))) != offCommand)
            {
                // M5 is serialized behind other lines. Realtime reset can still reach the controller.
                _connection.SoftReset();
                LastActionMessage = "Vypnutí paprsku nebylo potvrzeno včas; zařízení bylo resetováno.";
                return;
            }
            var result = await offCommand;
            if (!result.IsOk)
            {
                // M5 can be rejected or lost; realtime reset bypasses the command queue.
                _connection.SoftReset();
                LastActionMessage = "Vypnutí paprsku se nepodařilo potvrdit, zařízení bylo resetováno.";
                return;
            }
            LastActionMessage = "Polohovací paprsek je vypnutý.";
        }
        catch (Exception ex)
        {
            if (IsConnected()) _connection.SoftReset();
            LastActionMessage = $"Vypnutí paprsku selhalo; zařízení bylo resetováno: {ex.Message}";
        }
        finally
        {
            IsPositioningLaserStopping = false;
        }
    }

    // A click starts a bounded session. A stale status, non-idle state, job or deadline stops it.
    private void UpdatePositioningLaserWatchdog()
    {
        var previous = Interlocked.Exchange(ref _positioningLaserWatchdog, null);
        previous?.Dispose();
        if (!IsPositioningLaserActiveOrStarting || IsPositioningLaserStopping) return;
        var interval = PositioningLaserWatchdogInterval > TimeSpan.Zero
            ? PositioningLaserWatchdogInterval
            : TimeSpan.FromMilliseconds(500);
        var request = Volatile.Read(ref _positioningLaserRequest);
        _positioningLaserWatchdog = new Timer(_ =>
        {
            // Deadline must not wait for a busy WPF dispatcher. Claim this session before sending
            // the realtime reset so a disposed timer cannot reset a later session.
            if (DateTime.UtcNow - _positioningLaserStartedUtc >= PositioningLaserMaximumDuration &&
                Interlocked.CompareExchange(ref _positioningLaserRequest, request + 1, request) == request)
            {
                _connection.SoftReset();
                RunOnUiThread(() =>
                {
                    IsPositioningLaserStarting = false;
                    IsPositioningLaserOn = false;
                    LastActionMessage = "Časový limit 30 s: polohovací paprsek byl vypnut resetem zařízení.";
                });
                return;
            }
            RunOnUiThread(CheckLitPositioningLaser);
        }, null, interval, interval);
    }

    private void CheckLitPositioningLaser()
    {
        if (!IsPositioningLaserActiveOrStarting || IsPositioningLaserStopping) return;
        if (CanManualMotion() && !JobActive() &&
            DateTime.UtcNow - _positioningLaserStartedUtc < PositioningLaserMaximumDuration) return;
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
                "Hlava se přesune na pracovní nulu XY rychloposuvem (G0) plnou rychlostí. Před pokračováním je potřeba ověřit, že je dráha volná.",
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
        if (!IsConnected() && IsPositioningLaserActiveOrStarting)
        {
            Interlocked.Increment(ref _positioningLaserRequest);
            IsPositioningLaserStarting = false;
            IsPositioningLaserOn = false;
        }
        else if (IsPositioningLaserActiveOrStarting && !IsPositioningLaserStopping &&
                 (!CanManualMotion() || JobActive()))
        {
            _ = StopPositioningLaserAsync();
        }
        NotifyXyJogCommands();
        NotifyZJogCommands();
        CancelJogCommand.NotifyCanExecuteChanged();
        HomeCommand.NotifyCanExecuteChanged();
        UnlockCommand.NotifyCanExecuteChanged();
        SoftResetCommand.NotifyCanExecuteChanged();
        SetOriginHereCommand.NotifyCanExecuteChanged();
        GoToWorkZeroCommand.NotifyCanExecuteChanged();
        CanJogToPoint = CanManualMotionWithLaserOff();
        OnPropertyChanged(nameof(CanUsePositioningLaser));
        OnPropertyChanged(nameof(CanTogglePositioningLaser));
        OnPropertyChanged(nameof(HomeBlockedReason));
        OnPropertyChanged(nameof(HomeTooltip));
    }

    partial void OnStepSizeMmChanged(double value) => NotifyXyJogCommands();
    partial void OnJogFeedRateChanged(double value) => NotifyXyJogCommands();
    partial void OnZStepSizeMmChanged(double value) => NotifyZJogCommands();
    partial void OnZJogFeedRateChanged(double value) => NotifyZJogCommands();
    partial void OnEnableZAxisChanged(bool value) => NotifyZJogCommands();
    partial void OnInvertZAxisChanged(bool value) => NotifyZJogCommands();
    partial void OnPositioningLaserPowerPercentChanged(double value) =>
        NotifyMachineStateChanged();

    partial void OnIsPositioningLaserStartingChanged(bool value)
    {
        OnPropertyChanged(nameof(IsPositioningLaserActiveOrStarting));
        OnPropertyChanged(nameof(IsPositioningLaserBusy));
        OnPropertyChanged(nameof(PositioningLaserButtonLabel));
        UpdatePositioningLaserWatchdog();
        NotifyMachineStateChanged();
    }

    partial void OnIsPositioningLaserStoppingChanged(bool value)
    {
        OnPropertyChanged(nameof(IsPositioningLaserBusy));
        OnPropertyChanged(nameof(PositioningLaserButtonLabel));
        NotifyMachineStateChanged();
    }

    partial void OnIsPositioningLaserOnChanged(bool value)
    {
        OnPropertyChanged(nameof(IsPositioningLaserActiveOrStarting));
        OnPropertyChanged(nameof(IsPositioningLaserBusy));
        OnPropertyChanged(nameof(PositioningLaserButtonLabel));
        UpdatePositioningLaserWatchdog();
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
    private static void RunOnUiThread(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess()) action();
        else dispatcher.BeginInvoke(action);
    }
}
