using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lasero.Core.Grbl;
using Lasero.Core.Machines;

namespace Lasero.App.ViewModels;

public partial class ConsoleViewModel : ObservableObject
{
    private const int MaxLines = 1000;
    private static readonly Regex PhysicalCommandPattern = new(
        @"(^|\s)(?:G0?0|G0?1|G0?2|G0?3|G28|G30|G53|G92|M0?3|M0?4|\$H|\$J(?:=|\s)|\$X)(?=\s|[XYZFIJPRSQ]|$)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private readonly ILaserMachine _connection;

    public ObservableCollection<string> Lines { get; } = new();

    [ObservableProperty] private string _commandInput = string.Empty;

    public ConsoleViewModel(ILaserMachine connection)
    {
        _connection = connection;
        _connection.RawLineReceived += line => RunOnUiThread(() => Append($"< {line}"));
        _connection.Connected += banner => RunOnUiThread(() =>
        {
            Append($"[připojeno] {banner}");
            SendCommand.NotifyCanExecuteChanged();
        });
        _connection.Disconnected += ex => RunOnUiThread(() =>
        {
            Append($"[odpojeno] {ex?.Message}");
            SendCommand.NotifyCanExecuteChanged();
        });
        _connection.StatusUpdated += _ => RunOnUiThread(() => SendCommand.NotifyCanExecuteChanged());
        _connection.AlertChanged += _ => RunOnUiThread(() => SendCommand.NotifyCanExecuteChanged());
    }

    private bool HasFreshIdleStatus() => _connection.LastStatusReceivedUtc is { } timestamp
        && DateTime.UtcNow - timestamp <= TimeSpan.FromSeconds(2)
        && _connection.LastStatus?.Mode == GrblMachineMode.Idle
        && _connection.ActiveAlert is null;

    private bool CanSend() => _connection.State == GrblConnectionState.Connected
        && HasFreshIdleStatus()
        && !string.IsNullOrWhiteSpace(CommandInput);

    [RelayCommand(CanExecute = nameof(CanSend))]
    private async Task Send()
    {
        var text = CommandInput.Trim();
        if (!HasFreshIdleStatus())
        {
            Append("! Příkaz nebyl odeslán. Konzola je dostupná pouze tehdy, když je stroj bezpečně v klidu.");
            SendCommand.NotifyCanExecuteChanged();
            return;
        }

        if (PhysicalCommandPattern.IsMatch(text))
        {
            var app = Application.Current;
            if (app is null || LaseroDialogWindow.Show(app.MainWindow, new LaseroDialogOptions(
                    "Potvrdit přímý příkaz stroji",
                    $"Příkaz „{text}“ může pohnout strojem, změnit pracovní nulu nebo zapnout laser. Odesílejte jej pouze tehdy, když rozumíte jeho účinku.",
                    "Odeslat příkaz",
                    CancelText: "Zrušit",
                    Tone: LaseroDialogTone.Danger,
                    DestructivePrimary: true)) != LaseroDialogChoice.Primary)
            {
                Append("[zrušeno] Rizikový příkaz nebyl odeslán.");
                return;
            }
        }

        CommandInput = string.Empty;
        Append($"> {text}");
        var result = await _connection.SendCommandAsync(text);
        if (!result.IsOk)
            Append($"! {result.Message}");
    }

    partial void OnCommandInputChanged(string value) => SendCommand.NotifyCanExecuteChanged();

    private void Append(string line)
    {
        Lines.Add($"[{DateTime.Now:HH:mm:ss.fff}] {line}");
        while (Lines.Count > MaxLines)
            Lines.RemoveAt(0);
    }

    private static void RunOnUiThread(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess()) action();
        else dispatcher.BeginInvoke(action);
    }
}
