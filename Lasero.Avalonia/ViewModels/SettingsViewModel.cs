using System.Reactive;
using ReactiveUI;
using Lasero.Avalonia.Services;

namespace Lasero.Avalonia.ViewModels;

/// <summary>
/// Port of Lasero.App/SettingsWindow — same two persistence behaviors as WPF, not simplified:
/// the three safety checkboxes are deferred-commit (loaded once when the screen opens, only written
/// to AppSettingsStore.Safety when Save runs, exactly like SettingsWindow.xaml.cs's constructor/
/// OnCloseClick pair), while the work-area fields and the laser profile picker commit live to the
/// in-memory settings object as soon as they change (via ConnectionViewModel.WorkAreaWidthMm/HeightMm
/// and MaterialCatalogViewModel.SelectedTechnology/SelectedPowerWatts — the same real objects Machine
/// Control and Materials already use, not separate Settings-owned copies) and are only flushed to disk
/// on Save, same as WPF. Save validates the work area exactly like OnCloseClick does.
///
/// "Účet Lasero" (WPF's SettingsWindow.xaml lines 63-90) is now ported too — Login/Account
/// (AccountViewModel) exists as of this pass, so this exposes the same real sign-out and license
/// activation, reading the real Firebase/Netlify entitlement state instead of a placeholder.
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")] // AccountViewModel is Windows-only — see its own remarks.
public sealed class SettingsViewModel : ReactiveObject
{
    private readonly AppSettingsStore settingsStore;

    public ConnectionViewModel Connection { get; }
    public MaterialCatalogViewModel Materials { get; }
    public AccountViewModel Account { get; }

    private bool requireFramingBeforeStart;
    public bool RequireFramingBeforeStart { get => requireFramingBeforeStart; set => this.RaiseAndSetIfChanged(ref requireFramingBeforeStart, value); }

    private bool confirmSoftReset;
    public bool ConfirmSoftReset { get => confirmSoftReset; set => this.RaiseAndSetIfChanged(ref confirmSoftReset, value); }

    private bool showMachineStatusAfterConnect;
    public bool ShowMachineStatusAfterConnect { get => showMachineStatusAfterConnect; set => this.RaiseAndSetIfChanged(ref showMachineStatusAfterConnect, value); }

    private string errorMessage = string.Empty;
    public string ErrorMessage { get => errorMessage; private set => this.RaiseAndSetIfChanged(ref errorMessage, value); }

    public ReactiveCommand<Unit, Unit> Save { get; }

    /// <summary>Raised after a successful Save — matches WPF's OnCloseClick closing the window;
    /// the Avalonia shell is screen-based rather than window-based, so this navigates back to Home
    /// instead (see ShellViewModel).</summary>
    public event Action? Saved;

    public SettingsViewModel(AppSettingsStore settingsStore, ConnectionViewModel connection, MaterialCatalogViewModel materials, AccountViewModel account)
    {
        this.settingsStore = settingsStore;
        Connection = connection;
        Materials = materials;
        Account = account;

        LoadSafetyToggles();

        Save = ReactiveCommand.Create(DoSave);
    }

    /// <summary>Re-reads the deferred-commit safety toggles from the store — called when the screen
    /// is shown, matching WPF re-reading them every time a fresh SettingsWindow is constructed.</summary>
    public void LoadSafetyToggles()
    {
        RequireFramingBeforeStart = settingsStore.Current.Safety.RequireFramingBeforeStart;
        ConfirmSoftReset = settingsStore.Current.Safety.ConfirmSoftReset;
        ShowMachineStatusAfterConnect = settingsStore.Current.Safety.ShowMachineStatusAfterConnect;
        ErrorMessage = string.Empty;
    }

    private void DoSave()
    {
        var machine = settingsStore.Current.Machine;
        if (!double.IsFinite(machine.WorkAreaWidthMm) || machine.WorkAreaWidthMm <= 0 ||
            !double.IsFinite(machine.WorkAreaHeightMm) || machine.WorkAreaHeightMm <= 0)
        {
            ErrorMessage = "Rozměry pracovní plochy musí být kladná čísla v milimetrech.";
            return;
        }

        ErrorMessage = string.Empty;
        settingsStore.Current.Safety.RequireFramingBeforeStart = RequireFramingBeforeStart;
        settingsStore.Current.Safety.ConfirmSoftReset = ConfirmSoftReset;
        settingsStore.Current.Safety.ShowMachineStatusAfterConnect = ShowMachineStatusAfterConnect;
        settingsStore.Save();
        Saved?.Invoke();
    }
}
