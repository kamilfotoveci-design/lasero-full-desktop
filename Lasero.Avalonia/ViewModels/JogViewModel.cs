using System.Globalization;
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
/// Port of Lasero.App/ViewModels/JogViewModel.cs. Every motion command is gated behind the exact same
/// CanManualMotion/CanHome/CanUnlock/CanJogXY/CanJogZ predicates as WPF — connected, a fresh (&lt;2s
/// old) status report, GRBL Idle, and (for anything that moves) the positioning laser off. None of that
/// changed; only the MVVM plumbing (ReactiveCommand instead of RelayCommand, Dispatcher.UIThread
/// instead of WPF's Dispatcher) and the SoftReset confirmation, which now goes through a
/// view-supplied ConfirmDangerousAction delegate instead of a WPF dialog — see DeviceView, which wires
/// it to ConfirmationDialog.ShowAsync. If no owner window has wired it up yet, SoftReset does nothing
/// rather than skip the confirmation, matching "do not invent placeholder behavior" for a safety gate.
/// Constructing this class subscribes to connection events only; it sends no command to the machine.
/// </summary>
public sealed class JogViewModel : ReactiveObject
{
    private readonly ILaserMachine connection;
    private readonly AppSettingsStore settingsStore;
    private long positioningLaserRequest;
    private double positioningLaserMaximumSValue = 1000;
    private bool positioningLaserModeEnabled;

    public double[] StepSizePresets { get; } = [0.1, 1, 5, 10, 50, 100];

    /// <summary>View-supplied confirmation prompt (title, message, primary button text, destructive) ->
    /// true if the user confirmed. Wired by DeviceView to ConfirmationDialog.ShowAsync against its own
    /// Window. Left null, SoftReset intentionally does nothing rather than skip the safety gate.</summary>
    public Func<string, string, string, bool, Task<bool>>? ConfirmDangerousAction { get; set; }

    private double stepSizeMm = 10;
    public double StepSizeMm { get => stepSizeMm; set { this.RaiseAndSetIfChanged(ref stepSizeMm, value); RaiseJogStateChanged(); } }

    private double jogFeedRate = 2000;
    public double JogFeedRate { get => jogFeedRate; set { this.RaiseAndSetIfChanged(ref jogFeedRate, value); RaiseJogStateChanged(); } }

    private double zStepSizeMm = 1;
    public double ZStepSizeMm { get => zStepSizeMm; set { this.RaiseAndSetIfChanged(ref zStepSizeMm, value); RaiseJogStateChanged(); } }

    private double zJogFeedRate = 600;
    public double ZJogFeedRate { get => zJogFeedRate; set { this.RaiseAndSetIfChanged(ref zJogFeedRate, value); RaiseJogStateChanged(); } }

    private bool enableZAxis = true;
    public bool EnableZAxis { get => enableZAxis; set { this.RaiseAndSetIfChanged(ref enableZAxis, value); RaiseJogStateChanged(); } }

    private bool invertZAxis;
    public bool InvertZAxis { get => invertZAxis; set => this.RaiseAndSetIfChanged(ref invertZAxis, value); }

    private string? lastActionMessage;
    public string? LastActionMessage { get => lastActionMessage; private set => this.RaiseAndSetIfChanged(ref lastActionMessage, value); }

    private double positioningLaserPowerPercent = 1;
    public double PositioningLaserPowerPercent { get => positioningLaserPowerPercent; set { this.RaiseAndSetIfChanged(ref positioningLaserPowerPercent, value); RaiseJogStateChanged(); } }

    private bool isPositioningLaserOn;
    public bool IsPositioningLaserOn { get => isPositioningLaserOn; private set { this.RaiseAndSetIfChanged(ref isPositioningLaserOn, value); RaiseJogStateChanged(); } }

    private bool canJogToPoint;
    public bool CanJogToPoint { get => canJogToPoint; private set => this.RaiseAndSetIfChanged(ref canJogToPoint, value); }

    public bool CanUsePositioningLaser =>
        positioningLaserModeEnabled && CanManualMotion() && IsValidPositioningPower(PositioningLaserPowerPercent);

    public ReactiveCommand<Unit, Unit> JogXPos { get; }
    public ReactiveCommand<Unit, Unit> JogXNeg { get; }
    public ReactiveCommand<Unit, Unit> JogYPos { get; }
    public ReactiveCommand<Unit, Unit> JogYNeg { get; }
    public ReactiveCommand<Unit, Unit> JogXNegYPos { get; }
    public ReactiveCommand<Unit, Unit> JogXPosYPos { get; }
    public ReactiveCommand<Unit, Unit> JogXNegYNeg { get; }
    public ReactiveCommand<Unit, Unit> JogXPosYNeg { get; }
    public ReactiveCommand<Unit, Unit> JogZPos { get; }
    public ReactiveCommand<Unit, Unit> JogZNeg { get; }
    public ReactiveCommand<Unit, Unit> CancelJog { get; }
    public ReactiveCommand<Unit, Unit> Home { get; }
    public ReactiveCommand<Unit, Unit> Unlock { get; }
    public ReactiveCommand<Unit, Unit> SoftReset { get; }
    public ReactiveCommand<Unit, Unit> SetOriginHere { get; }
    public ReactiveCommand<Unit, Unit> GoToWorkZero { get; }

    public JogViewModel(ILaserMachine connection, AppSettingsStore settingsStore)
    {
        this.connection = connection;
        this.settingsStore = settingsStore;

        var canJogXY = jogStateChanged.StartWith(Unit.Default).Select(_ => CanJogXYNow());
        var canJogZ = jogStateChanged.StartWith(Unit.Default).Select(_ => CanJogZNow());
        var canManualMotionWithLaserOff = jogStateChanged.StartWith(Unit.Default).Select(_ => CanManualMotionWithLaserOff());
        var canHomeObservable = jogStateChanged.StartWith(Unit.Default).Select(_ => CanHome());
        var canUnlockObservable = jogStateChanged.StartWith(Unit.Default).Select(_ => CanUnlock());
        var isConnectedObservable = jogStateChanged.StartWith(Unit.Default).Select(_ => IsConnected());

        JogXPos = ReactiveCommand.CreateFromTask(() => JogAsync(StepSizeMm, 0, 0), canJogXY);
        JogXNeg = ReactiveCommand.CreateFromTask(() => JogAsync(-StepSizeMm, 0, 0), canJogXY);
        JogYPos = ReactiveCommand.CreateFromTask(() => JogAsync(0, StepSizeMm, 0), canJogXY);
        JogYNeg = ReactiveCommand.CreateFromTask(() => JogAsync(0, -StepSizeMm, 0), canJogXY);
        JogXNegYPos = ReactiveCommand.CreateFromTask(() => JogAsync(-StepSizeMm, StepSizeMm, 0), canJogXY);
        JogXPosYPos = ReactiveCommand.CreateFromTask(() => JogAsync(StepSizeMm, StepSizeMm, 0), canJogXY);
        JogXNegYNeg = ReactiveCommand.CreateFromTask(() => JogAsync(-StepSizeMm, -StepSizeMm, 0), canJogXY);
        JogXPosYNeg = ReactiveCommand.CreateFromTask(() => JogAsync(StepSizeMm, -StepSizeMm, 0), canJogXY);
        JogZPos = ReactiveCommand.CreateFromTask(() => JogAsync(0, 0, InvertZAxis ? -ZStepSizeMm : ZStepSizeMm, ZJogFeedRate), canJogZ);
        JogZNeg = ReactiveCommand.CreateFromTask(() => JogAsync(0, 0, InvertZAxis ? ZStepSizeMm : -ZStepSizeMm, ZJogFeedRate), canJogZ);
        CancelJog = ReactiveCommand.Create(() => connection.CancelJog(), isConnectedObservable);
        Home = ReactiveCommand.CreateFromTask(DoHomeAsync, canHomeObservable);
        Unlock = ReactiveCommand.CreateFromTask(DoUnlockAsync, canUnlockObservable);
        SoftReset = ReactiveCommand.CreateFromTask(DoSoftResetAsync, isConnectedObservable);
        SetOriginHere = ReactiveCommand.CreateFromTask(DoSetOriginHereAsync, canManualMotionWithLaserOff);
        GoToWorkZero = ReactiveCommand.CreateFromTask(DoGoToWorkZeroAsync, canManualMotionWithLaserOff);

        connection.ConnectionStateChanged += _ => RunOnUiThread(NotifyMachineStateChanged);
        connection.StatusUpdated += _ => RunOnUiThread(NotifyMachineStateChanged);
        connection.AlertChanged += _ => RunOnUiThread(NotifyMachineStateChanged);
    }

    private bool IsConnected() => connection.State == GrblConnectionState.Connected;
    private bool HasFreshStatus() => connection.LastStatusReceivedUtc is { } timestamp
        && DateTime.UtcNow - timestamp <= TimeSpan.FromSeconds(2);
    private bool CanManualMotion() => IsConnected() && HasFreshStatus() && connection.LastStatus?.Mode == GrblMachineMode.Idle;
    private bool CanManualMotionWithLaserOff() => CanManualMotion() && !IsPositioningLaserOn;
    private bool CanHome() => IsConnected() && HasFreshStatus() && !IsPositioningLaserOn
        && connection.LastStatus?.Mode is GrblMachineMode.Idle or GrblMachineMode.Alarm;
    private bool CanUnlock() => IsConnected()
        && (connection.LastStatus?.Mode == GrblMachineMode.Alarm
            || connection.ActiveAlert?.Kind == MachineAlertKind.Alarm);
    private bool CanJogXYNow() => CanManualMotionWithLaserOff() && IsPositiveFinite(StepSizeMm) && IsPositiveFinite(JogFeedRate);
    private bool CanJogZNow() => EnableZAxis && CanManualMotionWithLaserOff() && IsPositiveFinite(ZStepSizeMm) && IsPositiveFinite(ZJogFeedRate);

    public void ConfigurePositioningLaser(double? maximumSValue, bool? laserModeEnabled)
    {
        positioningLaserMaximumSValue = maximumSValue is > 0 && double.IsFinite(maximumSValue.Value)
            ? maximumSValue.Value
            : 1000;
        positioningLaserModeEnabled = laserModeEnabled == true;
        this.RaisePropertyChanged(nameof(CanUsePositioningLaser));
    }

    /// <summary>Energizes the laser at a deliberately low percentage of the controller's $30 maximum.
    /// The view calls this on pointer/key down and always pairs it with StopPositioningLaserAsync on
    /// release; this is not a toggle that can be accidentally left on.</summary>
    public async Task StartPositioningLaserAsync()
    {
        if (!CanUsePositioningLaser || IsPositioningLaserOn) return;

        var request = Interlocked.Increment(ref positioningLaserRequest);
        var sValue = positioningLaserMaximumSValue * PositioningLaserPowerPercent / 100.0;
        var result = await connection.SendCommandAsync($"M3 S{sValue.ToString("0.###", CultureInfo.InvariantCulture)}");

        if (request != Volatile.Read(ref positioningLaserRequest)) return;
        IsPositioningLaserOn = result.IsOk;
        LastActionMessage = result.IsOk
            ? $"Polohovací paprsek: {PositioningLaserPowerPercent:0.#} %."
            : result.Message ?? "Polohovací paprsek se nepodařilo zapnout.";
    }

    public async Task StopPositioningLaserAsync()
    {
        Interlocked.Increment(ref positioningLaserRequest);
        IsPositioningLaserOn = false;
        if (!IsConnected()) return;

        var result = await connection.SendCommandAsync("M5");
        LastActionMessage = result.IsOk
            ? "Polohovací paprsek je vypnutý."
            : result.Message ?? "Polohovací paprsek se nepodařilo vypnout.";
    }

    private async Task JogAsync(double x, double y, double z, double? feedRate = null)
    {
        var result = await connection.JogAsync(x, y, z, feedRate ?? JogFeedRate);
        LastActionMessage = result.IsOk
            ? $"Posun: X {x:+0.###;-0.###;0} · Y {y:+0.###;-0.###;0} · Z {z:+0.###;-0.###;0} mm"
            : result.Message ?? "Pohyb se nepodařilo provést.";
    }

    /// <summary>Absolute jog to a clicked point on the workspace preview — XY only, Z stays wherever
    /// the machine currently is (never a plunge triggered by a click).</summary>
    public async Task JogToPointAsync(double worldX, double worldY)
    {
        if (!CanManualMotionWithLaserOff()) return;
        var z = connection.LastStatus?.WorkPosition.Z ?? 0;
        var result = await connection.JogAsync(worldX, worldY, z, JogFeedRate, relative: false);
        LastActionMessage = result.IsOk ? $"Přesun na {worldX:0.#}, {worldY:0.#}." : result.Message;
    }

    private async Task DoHomeAsync()
    {
        LastActionMessage = "Probíhá najíždění do výchozí polohy…";
        var result = await connection.HomeAsync();
        LastActionMessage = result.IsOk ? "Výchozí poloha byla nalezena." : result.Message;
    }

    private async Task DoUnlockAsync()
    {
        var result = await connection.UnlockAsync();
        LastActionMessage = result.IsOk ? "Zařízení bylo odemknuto." : result.Message;
    }

    private async Task DoSoftResetAsync()
    {
        if (settingsStore.Current.Safety.ConfirmSoftReset)
        {
            if (ConfirmDangerousAction is null) return; // no owner wired up yet — do nothing, not skip the gate
            var confirmed = await ConfirmDangerousAction(
                "Nouzový reset zařízení",
                "Reset okamžitě zastaví pohyb. Stroj poté nemusí znát přesnou polohu a před další prací může vyžadovat homing.",
                "Provést reset",
                true);
            if (!confirmed) return;
        }

        connection.SoftReset();
        Log.Information("User-triggered GRBL soft reset");
        LastActionMessage = "Příkaz resetu byl odeslán.";
    }

    private async Task DoSetOriginHereAsync()
    {
        var result = await connection.SetWorkOriginAsync(1, Position.Zero);
        LastActionMessage = result.IsOk ? "Pracovní nula byla nastavena na aktuální pozici (G54)." : result.Message;
    }

    private async Task DoGoToWorkZeroAsync()
    {
        var result = await connection.SendCommandAsync("G90 G0 X0 Y0");
        LastActionMessage = result.IsOk ? "Přesun na pracovní nulu byl dokončen." : result.Message;
    }

    private readonly System.Reactive.Subjects.Subject<Unit> jogStateChanged = new();
    private void RaiseJogStateChanged() => NotifyMachineStateChanged();

    private void NotifyMachineStateChanged()
    {
        if (!IsConnected()) IsPositioningLaserOn = false;
        CanJogToPoint = CanManualMotion();
        this.RaisePropertyChanged(nameof(CanUsePositioningLaser));
        jogStateChanged.OnNext(Unit.Default);
    }

    private static bool IsPositiveFinite(double value) => double.IsFinite(value) && value > 0;
    private static bool IsValidPositioningPower(double value) => double.IsFinite(value) && value is >= 0.1 and <= 5;

    private static void RunOnUiThread(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess()) action();
        else Dispatcher.UIThread.Post(action);
    }
}
