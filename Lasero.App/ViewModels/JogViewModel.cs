using System.Windows;
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

    public double[] StepSizePresets { get; } = [0.1, 1, 5, 10, 50, 100];

    [ObservableProperty] private double _stepSizeMm = 10;
    [ObservableProperty] private double _jogFeedRate = 2000;
    [ObservableProperty] private double _zStepSizeMm = 1;
    [ObservableProperty] private double _zJogFeedRate = 600;
    [ObservableProperty] private bool _enableZAxis = true;
    [ObservableProperty] private bool _invertZAxis;
    [ObservableProperty] private string? _lastActionMessage;

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
    private bool CanHome() => IsConnected() && HasFreshStatus()
        && _connection.LastStatus?.Mode is GrblMachineMode.Idle or GrblMachineMode.Alarm;
    private bool CanUnlock() => IsConnected()
        && (_connection.LastStatus?.Mode == GrblMachineMode.Alarm
            || _connection.ActiveAlert?.Kind == MachineAlertKind.Alarm);
    private bool CanJogXY() => CanManualMotion() && IsPositiveFinite(StepSizeMm) && IsPositiveFinite(JogFeedRate);
    private bool CanJogZ() => EnableZAxis && CanManualMotion() && IsPositiveFinite(ZStepSizeMm) && IsPositiveFinite(ZJogFeedRate);

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
        if (!CanManualMotion()) return;
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

    [RelayCommand(CanExecute = nameof(CanManualMotion))]
    private async Task SetOriginHere()
    {
        var result = await _connection.SetWorkOriginAsync(1, Position.Zero);
        LastActionMessage = result.IsOk ? "Pracovní nula byla nastavena na aktuální pozici (G54)." : result.Message;
    }

    [RelayCommand(CanExecute = nameof(CanManualMotion))]
    private async Task GoToWorkZero()
    {
        var result = await _connection.SendCommandAsync("G90 G0 X0 Y0");
        LastActionMessage = result.IsOk ? "Přesun na pracovní nulu byl dokončen." : result.Message;
    }

    private void NotifyMachineStateChanged()
    {
        NotifyXyJogCommands();
        NotifyZJogCommands();
        CancelJogCommand.NotifyCanExecuteChanged();
        HomeCommand.NotifyCanExecuteChanged();
        UnlockCommand.NotifyCanExecuteChanged();
        SoftResetCommand.NotifyCanExecuteChanged();
        SetOriginHereCommand.NotifyCanExecuteChanged();
        GoToWorkZeroCommand.NotifyCanExecuteChanged();
        CanJogToPoint = CanManualMotion();
    }

    partial void OnStepSizeMmChanged(double value) => NotifyXyJogCommands();
    partial void OnJogFeedRateChanged(double value) => NotifyXyJogCommands();
    partial void OnZStepSizeMmChanged(double value) => NotifyZJogCommands();
    partial void OnZJogFeedRateChanged(double value) => NotifyZJogCommands();
    partial void OnEnableZAxisChanged(bool value) => NotifyZJogCommands();
    partial void OnInvertZAxisChanged(bool value) => NotifyZJogCommands();

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
