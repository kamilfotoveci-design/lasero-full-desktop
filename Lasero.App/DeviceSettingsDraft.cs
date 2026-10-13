using CommunityToolkit.Mvvm.ComponentModel;
using Lasero.App.ViewModels;
using Lasero.Core.Jobs;
using Lasero.Core.Materials;

namespace Lasero.App;

/// <summary>Controller values for the read-only rows. Anything the controller has not reported says so
/// instead of showing a guess.</summary>
public sealed record DeviceReadout(string MaxSpindle, string LaserMode, string Travel)
{
    private const string NotReadYet = "Zjistí se po připojení";

    public static DeviceReadout From(Lasero.Core.Grbl.GrblDeviceProfile? profile) => new(
        profile?.MaxSpindleSpeed is { } maxS ? maxS.ToString("0.###", System.Globalization.CultureInfo.CurrentCulture) : NotReadYet,
        profile?.LaserModeEnabled switch
        {
            true => "Zapnutý ($32=1)",
            false => "Vypnutý ($32=0)",
            _ => NotReadYet,
        },
        profile is { MaxTravelXmm: { } x, MaxTravelYmm: { } y }
            ? string.Create(System.Globalization.CultureInfo.CurrentCulture, $"{x:0.###} × {y:0.###} mm")
            : NotReadYet);
}

/// <summary>
/// What the device settings page edits: a copy of the saved values that nothing reads until Uložit writes it back.
/// The serial port and baud rate are not in it on purpose: they are chosen and applied live on the Připojení a ovládání
/// page, and a second copy here would overwrite that choice with a stale one on save.
/// </summary>
public sealed partial class DeviceSettingsDraft : ObservableObject
{
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
    [ObservableProperty] private double _framingPowerPercent = FramingOptions.VisiblePowerPercent;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StartLinesPreview), nameof(EndLinesPreview))]
    private AirAssistMode _airAssist = AirAssistMode.Off;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EndLinesPreview))]
    private bool _returnToStartAfterJob;

    /// <summary>The lines that open every streamed job: air assist (when on), then the fixed preamble the
    /// toolpath generator writes: absolute coordinates, millimetres, laser off.</summary>
    public string StartLinesPreview => string.Join('\n', JobProgram.Prologue(Options).Append("G90").Append("G21").Append("M5"));

    /// <summary>The lines that close every streamed job. The runner always ends with M5; the rest is
    /// what the two switches above add.</summary>
    public string EndLinesPreview => string.Join('\n', JobProgram.Epilogue(Options).Append("M5"));

    private JobProgramOptions Options => new()
    {
        AirAssist = AirAssist,
        // The preview shows the work zero; a job started from the current position returns to its
        // reference point instead, which only exists once the job is placed.
        ReturnTo = ReturnToStartAfterJob ? (0d, 0d) : null,
    };

    /// <summary>True when every value equals the other draft's. The page compares the draft with the snapshot it was filled
    /// from to know whether there is anything unsaved to protect.</summary>
    public bool ValuesEqual(DeviceSettingsDraft other) =>
        WorkAreaWidthMm.Equals(other.WorkAreaWidthMm)
        && WorkAreaHeightMm.Equals(other.WorkAreaHeightMm)
        && LaserTechnology == other.LaserTechnology
        && LaserPowerWatts == other.LaserPowerWatts
        && JogStepMm.Equals(other.JogStepMm)
        && JogFeedRateMmPerMinute.Equals(other.JogFeedRateMmPerMinute)
        && EnableZAxis == other.EnableZAxis
        && InvertZAxis == other.InvertZAxis
        && ZJogStepMm.Equals(other.ZJogStepMm)
        && ZJogFeedRateMmPerMinute.Equals(other.ZJogFeedRateMmPerMinute)
        && RequireFramingBeforeStart == other.RequireFramingBeforeStart
        && ConfirmSoftReset == other.ConfirmSoftReset
        && ShowMachineStatusAfterConnect == other.ShowMachineStatusAfterConnect
        && FramingPowerPercent.Equals(other.FramingPowerPercent)
        && AirAssist == other.AirAssist
        && ReturnToStartAfterJob == other.ReturnToStartAfterJob;

    public static DeviceSettingsDraft From(MainViewModel viewModel) => new()
    {
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
        FramingPowerPercent = viewModel.Settings.Machine.GetFramingPowerPercent(),
        AirAssist = viewModel.Settings.Machine.AirAssist,
        ReturnToStartAfterJob = viewModel.Settings.Machine.ReturnToStartAfterJob,
    };
}
