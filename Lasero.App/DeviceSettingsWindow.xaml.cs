using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using Lasero.App.ViewModels;
using Lasero.Core.Materials;

namespace Lasero.App;

public partial class DeviceSettingsWindow : Window
{
    public MainViewModel ViewModel { get; }
    public DeviceSettingsDraft Draft { get; }

    public DeviceSettingsWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        ViewModel = viewModel;
        Draft = DeviceSettingsDraft.From(viewModel);
        DataContext = this;
    }

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        if (!IsPositive(Draft.WorkAreaWidthMm) || !IsPositive(Draft.WorkAreaHeightMm))
        {
            SettingsErrorText.Text = "Rozměry pracovní plochy musí být kladná čísla v milimetrech.";
            return;
        }

        if (!IsPositive(Draft.JogStepMm) || !IsPositive(Draft.JogFeedRateMmPerMinute) ||
            !IsPositive(Draft.ZJogStepMm) || !IsPositive(Draft.ZJogFeedRateMmPerMinute))
        {
            SettingsErrorText.Text = "Krok a rychlost pohybu musí být kladná čísla.";
            return;
        }

        SettingsErrorText.Text = string.Empty;
        ViewModel.Connection.SelectedPort = Draft.Port;
        ViewModel.Connection.BaudRate = Draft.BaudRate;
        ViewModel.Connection.WorkAreaWidthMm = Draft.WorkAreaWidthMm;
        ViewModel.Connection.WorkAreaHeightMm = Draft.WorkAreaHeightMm;

        ViewModel.Jog.StepSizeMm = Draft.JogStepMm;
        ViewModel.Jog.JogFeedRate = Draft.JogFeedRateMmPerMinute;
        ViewModel.Jog.EnableZAxis = Draft.EnableZAxis;
        ViewModel.Jog.InvertZAxis = Draft.InvertZAxis;
        ViewModel.Jog.ZStepSizeMm = Draft.ZJogStepMm;
        ViewModel.Jog.ZJogFeedRate = Draft.ZJogFeedRateMmPerMinute;

        ViewModel.Settings.Safety.RequireFramingBeforeStart = Draft.RequireFramingBeforeStart;
        ViewModel.Settings.Safety.ConfirmSoftReset = Draft.ConfirmSoftReset;
        ViewModel.Settings.Safety.ShowMachineStatusAfterConnect = Draft.ShowMachineStatusAfterConnect;
        ViewModel.Materials.SelectedTechnology = Draft.LaserTechnology;
        ViewModel.Materials.SelectedPowerWatts = Draft.LaserPowerWatts;
        ViewModel.SaveSettings();

        DialogResult = true;
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => DialogResult = false;

    private static bool IsPositive(double value) => double.IsFinite(value) && value > 0;
}

public sealed partial class DeviceSettingsDraft : ObservableObject
{
    [ObservableProperty] private string? _port;
    [ObservableProperty] private int _baudRate = 115200;
    [ObservableProperty] private double _workAreaWidthMm = 500;
    [ObservableProperty] private double _workAreaHeightMm = 400;
    [ObservableProperty] private LaserTechnology _laserTechnology = LaserTechnology.Diode;
    [ObservableProperty] private int _laserPowerWatts = 20;
    [ObservableProperty] private double _jogStepMm = 10;
    [ObservableProperty] private double _jogFeedRateMmPerMinute = 2000;
    [ObservableProperty] private bool _enableZAxis = true;
    [ObservableProperty] private bool _invertZAxis;
    [ObservableProperty] private double _zJogStepMm = 1;
    [ObservableProperty] private double _zJogFeedRateMmPerMinute = 600;
    [ObservableProperty] private bool _requireFramingBeforeStart = true;
    [ObservableProperty] private bool _confirmSoftReset = true;
    [ObservableProperty] private bool _showMachineStatusAfterConnect;

    public static DeviceSettingsDraft From(MainViewModel viewModel) => new()
    {
        Port = viewModel.Connection.SelectedPort,
        BaudRate = viewModel.Connection.BaudRate,
        WorkAreaWidthMm = viewModel.Connection.WorkAreaWidthMm,
        WorkAreaHeightMm = viewModel.Connection.WorkAreaHeightMm,
        LaserTechnology = viewModel.Materials.SelectedTechnology,
        LaserPowerWatts = viewModel.Materials.SelectedPowerWatts,
        JogStepMm = viewModel.Jog.StepSizeMm,
        JogFeedRateMmPerMinute = viewModel.Jog.JogFeedRate,
        EnableZAxis = viewModel.Jog.EnableZAxis,
        InvertZAxis = viewModel.Jog.InvertZAxis,
        ZJogStepMm = viewModel.Jog.ZStepSizeMm,
        ZJogFeedRateMmPerMinute = viewModel.Jog.ZJogFeedRate,
        RequireFramingBeforeStart = viewModel.Settings.Safety.RequireFramingBeforeStart,
        ConfirmSoftReset = viewModel.Settings.Safety.ConfirmSoftReset,
        ShowMachineStatusAfterConnect = viewModel.Settings.Safety.ShowMachineStatusAfterConnect,
    };
}
