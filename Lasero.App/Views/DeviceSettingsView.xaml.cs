using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using Lasero.App.ViewModels;
using Lasero.Core.Jobs;

namespace Lasero.App.Views;

/// <summary>
/// The four device settings pages (Rozměry / Jednotky, Základní nastavení, G-code, Další nastavení) and the sticky
/// Uložit / Zrušit bar under them. The tab strip belongs to the Zařízení screen; this control shows the page
/// <see cref="Tab"/> selects and is hidden by the host on the connection page.
///
/// All four pages edit one <see cref="Draft"/>. Nothing reaches the machine, the jog panel or the settings file until
/// Uložit; moving between tabs, or to the connection page, keeps the draft as it is. The host calls
/// <see cref="Refresh"/> when the screen is entered, Zrušit calls it too, and a draft that was never edited follows
/// the live values when the operator comes back from the connection page (the jog step is changed there).
/// </summary>
public partial class DeviceSettingsView : UserControl, INotifyPropertyChanged
{
    public static readonly DependencyProperty TabProperty = DependencyProperty.Register(
        nameof(Tab), typeof(DeviceScreenTab), typeof(DeviceSettingsView),
        new PropertyMetadata(DeviceScreenTab.Connection, OnTabChanged));

    private static readonly TimeSpan SavedMessageDuration = TimeSpan.FromSeconds(2);

    private readonly DispatcherTimer _savedTimer;
    private MainViewModel? _viewModel;
    private DeviceSettingsDraft _draft = new();
    private DeviceSettingsDraft _baseline = new();
    private DeviceReadout _readout = DeviceReadout.From(null);

    public DeviceSettingsView()
    {
        InitializeComponent();
        PageRoot.DataContext = this;
        _savedTimer = new DispatcherTimer { Interval = SavedMessageDuration };
        _savedTimer.Tick += (_, _) => HideSaved();
        _draft.PropertyChanged += OnDraftChanged;
        DataContextChanged += OnDataContextChanged;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Which page is showing. Bound to <see cref="MainViewModel.DeviceTab"/> by the host.</summary>
    public DeviceScreenTab Tab
    {
        get => (DeviceScreenTab)GetValue(TabProperty);
        set => SetValue(TabProperty, value);
    }

    public MainViewModel? ViewModel => _viewModel;

    public DeviceSettingsDraft Draft => _draft;

    /// <summary>What the controller reported on connection ($30, $32, $130, $131). Shown read-only: these
    /// are the machine's own values, and the app never overrides them with a typed number.</summary>
    public DeviceReadout Readout => _readout;

    /// <summary>True when the draft differs from the saved values it was filled from.</summary>
    public bool IsDirty => !_draft.ValuesEqual(_baseline);

    /// <summary>Fills the draft from the saved settings again and drops any message. Called on entering the screen, by
    /// Zrušit, and after a save so the page shows what was actually stored (the setters clamp).</summary>
    public void Refresh()
    {
        HideSaved();
        SettingsErrorText.Text = string.Empty;

        var fresh = _viewModel is null ? new DeviceSettingsDraft() : DeviceSettingsDraft.From(_viewModel);
        _baseline = _viewModel is null ? new DeviceSettingsDraft() : DeviceSettingsDraft.From(_viewModel);
        _draft.PropertyChanged -= OnDraftChanged;
        _draft = fresh;
        _draft.PropertyChanged += OnDraftChanged;
        Notify(nameof(Draft));
        Notify(nameof(IsDirty));
        RefreshReadout();
        ResetUnparsedNumbers();
    }

    private static void OnTabChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var view = (DeviceSettingsView)d;
        // Coming over from the connection page, an untouched draft picks up what was changed live over there. An edited
        // one is left alone: nothing the operator typed is lost by looking at another page.
        if ((DeviceScreenTab)e.OldValue == DeviceScreenTab.Connection
            && (DeviceScreenTab)e.NewValue != DeviceScreenTab.Connection
            && !view.IsDirty)
            view.Refresh();
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_viewModel is not null) _viewModel.Connection.PropertyChanged -= OnConnectionPropertyChanged;
        _viewModel = e.NewValue as MainViewModel;
        if (_viewModel is not null) _viewModel.Connection.PropertyChanged += OnConnectionPropertyChanged;
        Notify(nameof(ViewModel));
        Refresh();
    }

    private void OnConnectionPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (nameof(ConnectionViewModel.IsConnected)
            or nameof(ConnectionViewModel.DetectedDevice))) return;
        if (Dispatcher.CheckAccess()) RefreshReadout();
        else Dispatcher.BeginInvoke(new Action(RefreshReadout));
    }

    /// <summary>The controller values follow the connection; they are not part of the draft.</summary>
    private void RefreshReadout()
    {
        var connection = _viewModel?.Connection;
        _readout = DeviceReadout.From(connection is { IsConnected: true } ? connection.DetectedDevice : null);
        Notify(nameof(Readout));
    }

    private void OnDraftChanged(object? sender, PropertyChangedEventArgs e)
    {
        HideSaved();
        Notify(nameof(IsDirty));
    }

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        if (_viewModel is null) return;

        // Leaving a field commits it; the click on Uložit has already moved focus, so by now every typed
        // value that parses is in the draft. A field that does not parse keeps its error outline.
        if (HasInvalidNumber(this))
        {
            ShowError("Některá hodnota není platné číslo. Pole s chybou je označeno červeně.");
            return;
        }

        var problem = Validate(Draft);
        if (problem is not null)
        {
            ShowError(problem);
            return;
        }

        SettingsErrorText.Text = string.Empty;
        _viewModel.Connection.WorkAreaWidthMm = Draft.WorkAreaWidthMm;
        _viewModel.Connection.WorkAreaHeightMm = Draft.WorkAreaHeightMm;

        _viewModel.Jog.StepSizeMm = Draft.JogStepMm;
        _viewModel.Jog.JogFeedRate = Draft.JogFeedRateMmPerMinute;
        _viewModel.Jog.EnableZAxis = Draft.EnableZAxis;
        _viewModel.Jog.InvertZAxis = Draft.InvertZAxis;
        _viewModel.Jog.ZStepSizeMm = Draft.ZJogStepMm;
        _viewModel.Jog.ZJogFeedRate = Draft.ZJogFeedRateMmPerMinute;

        _viewModel.Settings.Safety.RequireFramingBeforeStart = Draft.RequireFramingBeforeStart;
        _viewModel.Settings.Safety.ConfirmSoftReset = Draft.ConfirmSoftReset;
        _viewModel.Settings.Safety.ShowMachineStatusAfterConnect = Draft.ShowMachineStatusAfterConnect;

        var machine = _viewModel.Settings.Machine;
        machine.FramingPowerPercent = Draft.FramingPowerPercent;
        machine.AirAssist = Draft.AirAssist;
        machine.ReturnToStartAfterJob = Draft.ReturnToStartAfterJob;

        _viewModel.Materials.SelectedTechnology = Draft.LaserTechnology;
        _viewModel.Materials.SelectedPowerWatts = Draft.LaserPowerWatts;
        _viewModel.SaveSettings();

        Refresh();
        ShowSaved();
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => Refresh();

    private void ShowError(string message)
    {
        HideSaved();
        SettingsErrorText.Text = message;
    }

    private void ShowSaved()
    {
        SavedText.Visibility = Visibility.Visible;
        _savedTimer.Stop();
        _savedTimer.Start();
    }

    private void HideSaved()
    {
        _savedTimer.Stop();
        SavedText.Visibility = Visibility.Collapsed;
    }

    /// <summary>The first problem in the draft, worded for the operator, or null when it can be saved.
    /// Each message names the tab that holds the field.</summary>
    public static string? Validate(DeviceSettingsDraft draft)
    {
        if (!IsPositive(draft.WorkAreaWidthMm) || !IsPositive(draft.WorkAreaHeightMm))
            return "Rozměry pracovní plochy (Rozměry / Jednotky) musí být kladná čísla v milimetrech.";
        if (!IsPositive(draft.ZJogStepMm) || !IsPositive(draft.ZJogFeedRateMmPerMinute))
            return "Krok a rychlost osy Z (Rozměry / Jednotky) musí být kladná čísla.";
        if (!double.IsFinite(draft.FramingPowerPercent) || draft.FramingPowerPercent < 0
            || draft.FramingPowerPercent > FramingOptions.MaxVisiblePowerPercent)
            return $"Výkon laseru při rámování (Základní nastavení) musí být od 0 do {FramingOptions.MaxVisiblePowerPercent:0} %.";
        if (!IsPositive(draft.JogStepMm) || !IsPositive(draft.JogFeedRateMmPerMinute))
            return "Krok a rychlost ručního pohybu (Další nastavení) musí být kladná čísla.";
        return null;
    }

    private static bool IsPositive(double value) => double.IsFinite(value) && value > 0;

    private void Notify(string property) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));

    private static bool HasInvalidNumber(DependencyObject root) => FindInvalidNumbers(root).Count > 0;

    /// <summary>Typed text that is not a number keeps its error outline and the previous value in the draft. After the draft
    /// is replaced the value often has not changed, so the field would keep showing the rejected text: this puts the draft's
    /// value back into it and clears the error.</summary>
    private void ResetUnparsedNumbers()
    {
        foreach (var box in FindInvalidNumbers(this))
        {
            if (BindingOperations.GetBindingExpression(box, TextBox.TextProperty) is not { } binding) continue;
            Validation.ClearInvalid(binding);
            binding.UpdateTarget();
        }
    }

    /// <summary>Every text box with a validation error under <paramref name="root"/>. Walks the logical and the visual
    /// tree, each element once: walking both trees without a visited set revisits every element through both paths and
    /// doubles the work at each level, which froze the app on Uložit.</summary>
    private static List<TextBox> FindInvalidNumbers(DependencyObject root)
    {
        var found = new List<TextBox>();
        Collect(root, new HashSet<DependencyObject>(), found);
        return found;
    }

    private static void Collect(DependencyObject node, HashSet<DependencyObject> visited, List<TextBox> found)
    {
        if (!visited.Add(node)) return;
        if (node is TextBox box && Validation.GetHasError(box)) found.Add(box);
        foreach (var child in LogicalTreeHelper.GetChildren(node).OfType<DependencyObject>())
            Collect(child, visited, found);
        if (node is Visual or System.Windows.Media.Media3D.Visual3D)
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
                Collect(VisualTreeHelper.GetChild(node, i), visited, found);
    }
}
