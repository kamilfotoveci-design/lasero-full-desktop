using System.Windows;
using System.Windows.Controls;
using Lasero.App.ViewModels;

namespace Lasero.App;

public partial class SettingsWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly Func<Window, bool> _prepareSignOut;
    private readonly EditorPreferencesForm _editorForm;

    // The editor fields that currently hold a value the editor refuses, with the reason each one gives.
    private readonly Dictionary<object, string> _fieldErrors = new();

    public bool SignOutRequested { get; private set; }

    /// <summary>Set when "Znovu zobrazit úvod" closed the dialog; MainWindow then opens the welcome.</summary>
    public bool ReplayIntroRequested { get; private set; }

    public SettingsWindow(MainViewModel viewModel, Func<Window, bool> prepareSignOut)
    {
        InitializeComponent();
        _viewModel = viewModel;
        _prepareSignOut = prepareSignOut;
        DataContext = viewModel;

        // The editor pages edit a copy; only "Uložit a zavřít" makes it the live preferences.
        _editorForm = new EditorPreferencesForm(viewModel.Settings.Editor.Clone());
        EditorPage.DataContext = _editorForm;
        GridPage.DataContext = _editorForm;
        DisplayPage.DataContext = _editorForm;

        RequireFramingToggle.IsChecked = viewModel.Settings.Safety.RequireFramingBeforeStart;
        ConfirmResetToggle.IsChecked = viewModel.Settings.Safety.ConfirmSoftReset;
        ShowMachineStatusToggle.IsChecked = viewModel.Settings.Safety.ShowMachineStatusAfterConnect;
        StartupAnimationToggle.IsChecked = viewModel.Settings.Startup.ShowIntroAnimation;
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => TrySaveAndClose();

    private void OnReplayIntroClick(object sender, RoutedEventArgs e)
    {
        // Same save path as "Uložit a zavřít", so nothing typed here is lost when the intro opens.
        if (TrySaveAndClose()) ReplayIntroRequested = true;
    }

    private void OnResetTipsClick(object sender, RoutedEventArgs e)
    {
        _viewModel.Guidance.ResetTips();
        TipsResetText.Text = "Tipy se zobrazí znovu při příští příležitosti.";
    }

    /// <summary>Keeps the reason a field is refused in the footer for as long as the field holds the bad
    /// value; entering a valid one (or Esc) clears it.</summary>
    private void OnFieldValidationError(object sender, ValidationErrorEventArgs e)
    {
        var source = e.OriginalSource ?? sender;
        if (e.Action == ValidationErrorEventAction.Added)
            _fieldErrors[source] = e.Error.Exception?.GetBaseException().Message
                ?? e.Error.ErrorContent?.ToString() ?? "Hodnota není platná.";
        else
            _fieldErrors.Remove(source);

        SettingsErrorText.Text = _fieldErrors.Count > 0 ? _fieldErrors.Values.First() : string.Empty;
    }

    private bool TrySaveAndClose()
    {
        if (_fieldErrors.Count > 0)
        {
            SettingsErrorText.Text = _fieldErrors.Values.First();
            return false;
        }

        if (Validation.GetHasError(WorkAreaWidthInput) || Validation.GetHasError(WorkAreaHeightInput) ||
            !double.IsFinite(_viewModel.Settings.Machine.WorkAreaWidthMm) || _viewModel.Settings.Machine.WorkAreaWidthMm <= 0 ||
            !double.IsFinite(_viewModel.Settings.Machine.WorkAreaHeightMm) || _viewModel.Settings.Machine.WorkAreaHeightMm <= 0)
        {
            SettingsErrorText.Text = "Rozměry pracovní plochy musí být kladná čísla v milimetrech.";
            return false;
        }

        SettingsErrorText.Text = string.Empty;
        _viewModel.Settings.Safety.RequireFramingBeforeStart = RequireFramingToggle.IsChecked == true;
        _viewModel.Settings.Safety.ConfirmSoftReset = ConfirmResetToggle.IsChecked == true;
        _viewModel.Settings.Safety.ShowMachineStatusAfterConnect = ShowMachineStatusToggle.IsChecked == true;
        _viewModel.Settings.Startup.ShowIntroAnimation = StartupAnimationToggle.IsChecked == true;
        _editorForm.Draft.Normalize();
        _viewModel.Settings.Editor = _editorForm.Draft;
        _viewModel.SaveSettings();
        Close();
        return true;
    }

    private void OnSignOutClick(object sender, RoutedEventArgs e)
    {
        var confirmed = LaseroDialogWindow.Show(this, new LaseroDialogOptions(
            "Odhlásit se",
            "Uložené přihlášení bude z tohoto počítače odstraněno. Místní projekty zůstanou zachované.",
            "Odhlásit se",
            CancelText: "Zůstat přihlášený",
            Tone: LaseroDialogTone.Warning)) == LaseroDialogChoice.Primary;
        if (!confirmed) return;
        if (!_prepareSignOut(this)) return;

        _viewModel.Account.SignOutCommand.Execute(null);
        SignOutRequested = true;
        Close();
    }
}
