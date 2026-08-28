using System.Windows;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lasero.Core.GCode;
using Lasero.Core.Grbl;
using Lasero.Core.Import;
using Lasero.Core.Jobs;
using Lasero.Core.Machines;
using Lasero.Core.Raster;

namespace Lasero.App.ViewModels;

public enum RasterToneMode { Grayscale, Threshold, Dither }

/// <summary>
/// Configures a single raster import before it's placed on the scene: physical size, engraving
/// parameters, a live processed-image preview, and a live job-bounds readout with an independent
/// "frame this" action — all computed off the UI thread from the same Lasero.Core.Raster pipeline
/// that ultimately generates the real G-code, so what's previewed/framed here is what gets burned.
/// </summary>
public partial class RasterImportViewModel : ObservableObject, IDisposable
{
    private const int DebounceMs = 150;

    private readonly ILaserMachine _machine;
    private readonly AppSettingsStore _settingsStore;
    private CancellationTokenSource? _recomputeCancellation;

    public string FilePath { get; }
    public string FileName { get; }

    [ObservableProperty] private double _targetWidthMm;
    [ObservableProperty] private bool _keepAspectRatio = true;
    [ObservableProperty] private double _targetHeightMm;
    [ObservableProperty] private double _feedRatePerMinute;
    [ObservableProperty] private double _minPower;
    [ObservableProperty] private double _maxPower;
    [ObservableProperty] private double _dpi;
    [ObservableProperty] private int _passes = 1;
    [ObservableProperty] private RasterToneMode _toneMode = RasterToneMode.Dither;
    [ObservableProperty] private double _thresholdValue = 128;
    [ObservableProperty] private bool _invert;
    [ObservableProperty] private double _brightness;
    [ObservableProperty] private double _contrast;

    [ObservableProperty] private BitmapSource? _previewImageSource;
    [ObservableProperty] private LaserJob? _plannedJob;
    [ObservableProperty] private bool _isComputing;
    [ObservableProperty] private string? _statusMessage;
    [ObservableProperty] private bool _isFraming;

    public bool HasJob => PlannedJob is { Moves.Count: > 0 };
    public double LineIntervalMm
    {
        get => Dpi <= 0 ? 0 : 25.4 / Dpi;
        set
        {
            if (!double.IsFinite(value) || value <= 0) return;
            Dpi = 25.4 / value;
            OnPropertyChanged();
        }
    }

    public RasterImportViewModel(ILaserMachine machine, AppSettingsStore settingsStore, string filePath,
        double targetWidthMm, double feedRatePerMinute, double maxPower, double dpi, bool useThreshold, double thresholdValue)
    {
        _machine = machine;
        _settingsStore = settingsStore;
        _machine.ConnectionStateChanged += OnMachineStateChanged;
        _machine.StatusUpdated += OnMachineStatusUpdated;
        _machine.AlertChanged += OnMachineAlertChanged;
        FilePath = filePath;
        FileName = System.IO.Path.GetFileName(filePath);
        _targetWidthMm = targetWidthMm;
        _feedRatePerMinute = feedRatePerMinute;
        _maxPower = maxPower;
        _dpi = dpi;
        _toneMode = useThreshold ? RasterToneMode.Threshold : RasterToneMode.Dither;
        _thresholdValue = thresholdValue;

        var (_, heightMm) = RasterImporter.GetPlacedSizeMm(filePath, targetWidthMm);
        _targetHeightMm = heightMm;

        ScheduleRecompute();
    }

    public RasterImportOptions BuildOptions() => new()
    {
        TargetWidthMm = TargetWidthMm,
        TargetHeightMm = KeepAspectRatio ? null : TargetHeightMm,
        Dpi = Dpi,
        FeedRatePerMinute = FeedRatePerMinute,
        MinPower = MinPower,
        MaxPower = MaxPower,
        Passes = Passes,
        UseThreshold = ToneMode == RasterToneMode.Threshold,
        ThresholdValue = (byte)Math.Clamp(ThresholdValue, 0, 255),
        UseDithering = ToneMode == RasterToneMode.Dither,
        DitheringAlgorithm = DitheringAlgorithm.Stucki,
        Brightness = Brightness,
        Contrast = Contrast,
        Invert = Invert,
    };

    public bool TryValidate(out string? message)
    {
        message = GetValidationMessage();
        return message is null;
    }

    private string? GetValidationMessage()
    {
        if (!double.IsFinite(TargetWidthMm) || TargetWidthMm <= 0)
            return "Šířka musí být větší než 0 mm.";
        if (!KeepAspectRatio && (!double.IsFinite(TargetHeightMm) || TargetHeightMm <= 0))
            return "Výška musí být větší než 0 mm.";
        if (!double.IsFinite(FeedRatePerMinute) || FeedRatePerMinute <= 0)
            return "Rychlost musí být větší než 0 mm/min.";
        if (!double.IsFinite(MinPower) || MinPower is < 0 or > 100)
            return "Minimální výkon musí být v rozsahu 0 až 100 %.";
        if (!double.IsFinite(MaxPower) || MaxPower is < 0 or > 100)
            return "Maximální výkon musí být v rozsahu 0 až 100 %.";
        if (MinPower > MaxPower)
            return "Minimální výkon nesmí být vyšší než maximální výkon.";
        if (!double.IsFinite(Dpi) || Dpi is < 25 or > 1200)
            return "Rozlišení musí být v rozsahu 25 až 1200 DPI.";
        if (Passes < 1)
            return "Počet průchodů musí být alespoň 1.";
        return null;
    }

    partial void OnTargetWidthMmChanged(double value) => ScheduleRecompute();
    partial void OnKeepAspectRatioChanged(bool value) => ScheduleRecompute();
    partial void OnTargetHeightMmChanged(double value) => ScheduleRecompute();
    partial void OnFeedRatePerMinuteChanged(double value) => ScheduleRecompute();
    partial void OnMinPowerChanged(double value) => ScheduleRecompute();
    partial void OnMaxPowerChanged(double value) => ScheduleRecompute();
    partial void OnDpiChanged(double value)
    {
        OnPropertyChanged(nameof(LineIntervalMm));
        ScheduleRecompute();
    }
    partial void OnPassesChanged(int value) => ScheduleRecompute();
    partial void OnToneModeChanged(RasterToneMode value) => ScheduleRecompute();
    partial void OnThresholdValueChanged(double value) => ScheduleRecompute();
    partial void OnInvertChanged(bool value) => ScheduleRecompute();
    partial void OnBrightnessChanged(double value) => ScheduleRecompute();
    partial void OnContrastChanged(double value) => ScheduleRecompute();

    partial void OnPlannedJobChanged(LaserJob? value)
    {
        OnPropertyChanged(nameof(HasJob));
        FrameThisCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsFramingChanged(bool value) => FrameThisCommand.NotifyCanExecuteChanged();

    private void ScheduleRecompute()
    {
        _recomputeCancellation?.Cancel();
        var cts = new CancellationTokenSource();
        _recomputeCancellation = cts;
        _ = RecomputeAsync(cts.Token);
    }

    private async Task RecomputeAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(DebounceMs, cancellationToken);
        }
        catch (TaskCanceledException)
        {
            return;
        }

        IsComputing = true;
        StatusMessage = null;
        if (!TryValidate(out var validationMessage))
        {
            PreviewImageSource = null;
            PlannedJob = null;
            StatusMessage = validationMessage;
            IsComputing = false;
            return;
        }

        var options = BuildOptions();
        var path = FilePath;

        try
        {
            var (preview, job) = await Task.Run(() =>
            {
                var processed = RasterImporter.LoadProcessedPreview(path, options);
                var planned = RasterImporter.BuildLaserJob(path, options);
                var bitmap = ProcessedImagePreviewRenderer.Render(processed); // frozen — safe to hand across threads
                return (bitmap, planned);
            }, cancellationToken);

            if (cancellationToken.IsCancellationRequested) return;

            PreviewImageSource = preview;
            PlannedJob = job;
            IsComputing = false;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            StatusMessage = $"Náhled se nepodařilo vypočítat: {ex.Message}";
            PlannedJob = null;
            IsComputing = false;
        }
    }

    private bool CanFrame() => !IsFraming
        && PlannedJob is { Moves.Count: > 0 }
        && _machine.State == GrblConnectionState.Connected
        && _machine.LastStatusReceivedUtc is { } timestamp
        && DateTime.UtcNow - timestamp <= TimeSpan.FromSeconds(2)
        && _machine.LastStatus?.Mode == GrblMachineMode.Idle
        && _machine.ActiveAlert is null;

    [RelayCommand(CanExecute = nameof(CanFrame))]
    private async Task FrameThis()
    {
        if (PlannedJob is not { } job) return;

        IsFraming = true;
        StatusMessage = null;
        try
        {
            var frameLines = FramingService.BuildFrameGCode(job.Bounds, new FramingOptions
            {
                FeedRatePerMinute = 3000,
                Mode = FramingMode.FullOutline,
                LaserPower = 0,
            });

            var document = GCodeParser.Parse(frameLines);
            var preflight = JobPreflight.Evaluate(new JobPreflightContext
            {
                IsConnected = _machine.State == GrblConnectionState.Connected,
                Document = document,
                MachineStatus = _machine.LastStatus,
                MachineStatusAge = _machine.LastStatusReceivedUtc is { } ts ? DateTime.UtcNow - ts : null,
                RequireFraming = false,
                HasFramedCurrentDocument = false,
                WorkAreaWidthMm = _settingsStore.Current.Machine.WorkAreaWidthMm,
                WorkAreaHeightMm = _settingsStore.Current.Machine.WorkAreaHeightMm,
            });

            if (!preflight.CanStart)
            {
                StatusMessage = preflight.FirstBlockingIssue?.Message ?? "Rámování nelze spustit.";
                return;
            }

            var runner = new GCodeJobRunner(_machine);
            await runner.RunAsync(document.RawLines, abortOnError: true);
            StatusMessage = runner.State == JobRunState.Completed
                ? "Rámování dokončeno."
                : "Rámování nebylo dokončeno.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Rámování se nepodařilo spustit: {ex.Message}";
        }
        finally
        {
            IsFraming = false;
        }
    }

    private void OnMachineStateChanged(GrblConnectionState _) => NotifyFrameAvailability();
    private void OnMachineStatusUpdated(MachineStatus _) => NotifyFrameAvailability();
    private void OnMachineAlertChanged(MachineAlert? _) => NotifyFrameAvailability();

    private void NotifyFrameAvailability()
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess()) FrameThisCommand.NotifyCanExecuteChanged();
        else dispatcher.BeginInvoke(FrameThisCommand.NotifyCanExecuteChanged);
    }

    public void Dispose()
    {
        _recomputeCancellation?.Cancel();
        _recomputeCancellation?.Dispose();
        _machine.ConnectionStateChanged -= OnMachineStateChanged;
        _machine.StatusUpdated -= OnMachineStatusUpdated;
        _machine.AlertChanged -= OnMachineAlertChanged;
        GC.SuppressFinalize(this);
    }
}
