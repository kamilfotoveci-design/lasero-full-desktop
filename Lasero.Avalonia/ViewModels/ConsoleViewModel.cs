using System.Collections.ObjectModel;
using System.Reactive;
using System.Reactive.Linq;
using System.Text.RegularExpressions;
using Avalonia.Threading;
using Lasero.Core.Grbl;
using Lasero.Core.Machines;
using ReactiveUI;

namespace Lasero.Avalonia.ViewModels;

/// <summary>Port of Lasero.App/ViewModels/ConsoleViewModel.cs — the raw GRBL console. Same safety
/// gate: any command matching a physical-motion pattern (G0/G1/G28/$H/$J/etc.) requires confirmation
/// before it is sent, same as JogViewModel.SoftReset, via the view-supplied ConfirmDangerousAction
/// delegate. Sending is also refused unless the machine is connected, idle, and alert-free.</summary>
public sealed class ConsoleViewModel : ReactiveObject
{
    private const int MaxLines = 1000;
    private static readonly Regex PhysicalCommandPattern = new(
        @"(^|\s)(?:G0?0|G0?1|G0?2|G0?3|G28|G30|G53|G92|M0?3|M0?4|\$H|\$J(?:=|\s)|\$X)(?=\s|[XYZFIJPRSQ]|$)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex PowerProfileCommandPattern = new(
        @"(^|\s)\$(?:30|32)\s*=", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private readonly ILaserMachine connection;

    public ObservableCollection<string> Lines { get; } = new();

    /// <summary>See JogViewModel.ConfirmDangerousAction — same delegate, wired by the same view.</summary>
    public Func<string, string, string, bool, Task<bool>>? ConfirmDangerousAction { get; set; }

    private string commandInput = string.Empty;
    public string CommandInput { get => commandInput; set { this.RaiseAndSetIfChanged(ref commandInput, value); RaiseSendStateChanged(); } }

    public ReactiveCommand<Unit, Unit> Send { get; }

    public ConsoleViewModel(ILaserMachine connection)
    {
        this.connection = connection;

        var canSend = sendStateChanged.StartWith(Unit.Default).Select(_ => CanSend());
        Send = ReactiveCommand.CreateFromTask(DoSendAsync, canSend);

        connection.RawLineReceived += line => RunOnUiThread(() => Append($"< {line}"));
        connection.Connected += banner => RunOnUiThread(() =>
        {
            Append($"[připojeno] {banner}");
            RaiseSendStateChanged();
        });
        connection.Disconnected += ex => RunOnUiThread(() =>
        {
            Append($"[odpojeno] {ex?.Message}");
            RaiseSendStateChanged();
        });
        connection.StatusUpdated += _ => RunOnUiThread(RaiseSendStateChanged);
        connection.AlertChanged += _ => RunOnUiThread(RaiseSendStateChanged);
    }

    private bool HasFreshIdleStatus() => connection.LastStatusReceivedUtc is { } timestamp
        && DateTime.UtcNow - timestamp <= TimeSpan.FromSeconds(2)
        && connection.LastStatus?.Mode == GrblMachineMode.Idle
        && connection.ActiveAlert is null;

    private bool CanSend() => connection.State == GrblConnectionState.Connected
        && HasFreshIdleStatus()
        && !string.IsNullOrWhiteSpace(CommandInput);

    private async Task DoSendAsync()
    {
        var text = CommandInput.Trim();
        if (!HasFreshIdleStatus())
        {
            Append("! Příkaz nebyl odeslán. Konzola je dostupná pouze tehdy, když je stroj bezpečně v klidu.");
            RaiseSendStateChanged();
            return;
        }

        if (PhysicalCommandPattern.IsMatch(text) || PowerProfileCommandPattern.IsMatch(text))
        {
            if (ConfirmDangerousAction is null) return; // no owner wired up yet — do nothing, not skip the gate
            var confirmed = await ConfirmDangerousAction(
                "Potvrdit přímý příkaz stroji",
                $"Příkaz „{text}“ může pohnout strojem, změnit pracovní nulu nebo zapnout laser. Odesílejte jej pouze tehdy, když rozumíte jeho účinku.",
                "Odeslat příkaz",
                true);
            if (!confirmed)
            {
                Append("[zrušeno] Rizikový příkaz nebyl odeslán.");
                return;
            }
        }

        CommandInput = string.Empty;
        Append($"> {text}");
        var result = await connection.SendCommandAsync(text);
        if (!result.IsOk)
            Append($"! {result.Message}");
    }

    private readonly System.Reactive.Subjects.Subject<Unit> sendStateChanged = new();
    private void RaiseSendStateChanged() => sendStateChanged.OnNext(Unit.Default);

    private void Append(string line)
    {
        Lines.Add($"[{DateTime.Now:HH:mm:ss.fff}] {line}");
        while (Lines.Count > MaxLines)
            Lines.RemoveAt(0);
    }

    private static void RunOnUiThread(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess()) action();
        else Dispatcher.UIThread.Post(action);
    }
}
