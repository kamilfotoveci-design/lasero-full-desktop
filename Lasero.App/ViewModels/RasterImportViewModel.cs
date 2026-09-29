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

public sealed record RasterDitherChoice(string Label, string Hint, DitheringAlgorithm? Algorithm)
{
    public override string ToString() => Label;
}

/// <summary>
/// Configures a single raster import before it's placed on the scene: physical size, engraving
/// parameters, a live processed-image preview, and a live job-bounds readout with an independent
/// "frame this" action — all computed off the UI thread from the same Lasero.Core.Raster pipeline
/// that ultimately generates the real G-code, so what's previewed/framed here is what gets burned.
/// </summary>
public partial class RasterImportViewModel : ObservableObject, IDisposable
{
    private const int DebounceMs = 40;

    public static IReadOnlyList<RasterDitherChoice> DitheringChoices { get; } =
    [
        new("Stucki", "Stucki — nejlepší pro fotky, hladké přechody, doporučeno pro dřevo", DitheringAlgorithm.Stucki),
        new("Floyd-Steinberg", "Floyd-Steinberg — klasický, dobrý kontrast, rychlý výpočet", DitheringAlgorithm.FloydSteinberg),
        new("Jarvis", "Jarvis — detail podobný Stucki, o něco ostřejší šum", DitheringAlgorithm.Jarvis),
        new("Atkinson", "Atkinson — světlé tóny, vhodný pro loga a jednodušší grafiku", DitheringAlgorithm.Atkinson),
        new("Sierra", "Sierra — vyvážené šedotóny, kompromis Stucki a Floyd", DitheringAlgorithm.Sierra),
        new("Ordered (Bayer)", "Ordered (Bayer) — vzorový dither, rytmická textura na plochách", DitheringAlgorithm.Ordered),
        new("Žádný", "Bez ditheringu — prahové binarizování, jen čistá černá a bílá", null),
    ];

    private readonly ILaserMachine _machine;
    private readonly AppSettingsStore _settingsStore;
    private readonly object _sourceLock = new();
    private GrayscaleImage _sourceImage;
    private double _sourceTargetWidthMm;
    private double? _sourceTargetHeightMm;
    private double _sourceDpi;
    private ProcessedImage? _lastProcessedImage;
    private bool _hasImmediatePreview;
    private CancellationTokenSource? _recomputeCancellation;
    private bool _suppressRecompute;

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
    [ObservableProperty] private bool _invert;
    [ObservableProperty] private double _gamma = 1;
    [ObservableProperty] private double _exposure;
    [ObservableProperty] private double _brightness;
    [ObservableProperty] private double _contrast;
    [ObservableProperty] private double _highlights;
    [ObservableProperty] private double _shadows;
    [ObservableProperty] private double _blackPoint;
    [ObservableProperty] private double _whitePoint = 255;
    [ObservableProperty] private double _noiseReduction;
    [ObservableProperty] private double _sharpen;
    [ObservableProperty] private double _edgeEnhance;
    [ObservableProperty] private RasterDitherChoice _selectedDithering = DitheringChoices[0];

    [ObservableProperty] private BitmapSource? _previewImageSource;
    [ObservableProperty] private LaserJob? _plannedJob;
    [ObservableProperty] private bool _isComputing;
    [ObservableProperty] private bool _isPreviewComputing;
    [ObservableProperty] private string? _statusMessage;
    [ObservableProperty] private bool _isFraming;

    public bool HasJob => !IsComputing && PlannedJob is { Moves.Count: > 0 };
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
        double targetWidthMm, double feedRatePerMinute, double maxPower, double dpi)
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
        var initialOptions = new RasterImportOptions { TargetWidthMm = targetWidthMm, Dpi = dpi };
        _sourceImage = RasterImporter.LoadProcessingSource(filePath, initialOptions);
        _sourceTargetWidthMm = targetWidthMm;
        _sourceTargetHeightMm = null;
        _sourceDpi = dpi;
        _suppressRecompute = true;
        ApplyRecommendation(ImageAutoAdjuster.Recommend(_sourceImage));

        _targetHeightMm = _sourceImage.Height * (targetWidthMm / _sourceImage.Width);

        _suppressRecompute = false;
        ScheduleRecompute();
    }

    public RasterImportOptions BuildOptions()
    {
        var ditherAlgorithm = SelectedDithering.Algorithm;
        return new RasterImportOptions
        {
            TargetWidthMm = TargetWidthMm,
            TargetHeightMm = KeepAspectRatio ? null : TargetHeightMm,
            Dpi = Dpi,
            FeedRatePerMinute = FeedRatePerMinute,
            MinPower = MinPower,
            MaxPower = MaxPower,
            Passes = Passes,
            UseThreshold = ditherAlgorithm is null,
            ThresholdValue = 128,
            UseDithering = ditherAlgorithm is not null,
            DitheringAlgorithm = ditherAlgorithm ?? DitheringAlgorithm.Stucki,
            Gamma = Gamma,
            Exposure = Exposure,
            Brightness = Brightness,
            Contrast = Contrast,
            Highlights = Highlights,
            Shadows = Shadows,
            BlackPoint = BlackPoint,
            WhitePoint = WhitePoint,
            Invert = Invert,
            NoiseReduction = NoiseReduction,
            Sharpen = Sharpen,
            EdgeEnhance = EdgeEnhance,
        };
    }

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
    partial void OnInvertChanged(bool value)
    {
        _hasImmediatePreview = TryShowImmediateInversionPreview();
        ScheduleRecompute();
    }
    partial void OnGammaChanged(double value) => ScheduleRecompute();
    partial void OnExposureChanged(double value) => ScheduleRecompute();
    partial void OnBrightnessChanged(double value) => ScheduleRecompute();
    partial void OnContrastChanged(double value) => ScheduleRecompute();
    partial void OnHighlightsChanged(double value) => ScheduleRecompute();
    partial void OnShadowsChanged(double value) => ScheduleRecompute();
    partial void OnBlackPointChanged(double value) => ScheduleRecompute();
    partial void OnWhitePointChanged(double value) => ScheduleRecompute();
    partial void OnNoiseReductionChanged(double value) => ScheduleRecompute();
    partial void OnSharpenChanged(double value) => ScheduleRecompute();
    partial void OnEdgeEnhanceChanged(double value) => ScheduleRecompute();
    partial void OnSelectedDitheringChanged(RasterDitherChoice value) => ScheduleRecompute();

    private void ApplyRecommendation(ImageAutoAdjustment recommendation)
    {
        Gamma = recommendation.Gamma;
        Exposure = 0;
        Brightness = recommendation.Brightness;
        Contrast = recommendation.Contrast;
        Highlights = recommendation.Highlights;
        Shadows = recommendation.Shadows;
        BlackPoint = recommendation.BlackPoint;
        WhitePoint = recommendation.WhitePoint;
        NoiseReduction = recommendation.NoiseReduction;
        Sharpen = recommendation.Sharpen;
        EdgeEnhance = 0;
        SelectedDithering = DitheringChoices[0];
    }

    private bool TryShowImmediateInversionPreview()
    {
        if (_lastProcessedImage is not { } current) return false;

        var invertedPower = new double[current.PowerFraction.Count];
        for (var index = 0; index < invertedPower.Length; index++)
            invertedPower[index] = 1 - current.PowerFraction[index];

        _lastProcessedImage = current with { PowerFraction = invertedPower };
        PreviewImageSource = ProcessedImagePreviewRenderer.Render(_lastProcessedImage);
        return true;
    }

    partial void OnPlannedJobChanged(LaserJob? value)
    {
        OnPropertyChanged(nameof(HasJob));
        FrameThisCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsFramingChanged(bool value) => FrameThisCommand.NotifyCanExecuteChanged();
    partial void OnIsComputingChanged(bool value) => OnPropertyChanged(nameof(HasJob));

    private void ScheduleRecompute()
    {
        if (_suppressRecompute) return;
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
        IsPreviewComputing = !_hasImmediatePreview;
        _hasImmediatePreview = false;
        StatusMessage = null;
        if (!TryValidate(out var validationMessage))
        {
            PreviewImageSource = null;
            PlannedJob = null;
            StatusMessage = validationMessage;
            IsComputing = false;
            IsPreviewComputing = false;
            return;
        }

        var options = BuildOptions();

        try
        {
            var (processed, preview) = await Task.Run(() =>
            {
                var source = GetProcessingSource(options);
                var processed = RasterImporter.Process(source, options);
                var bitmap = ProcessedImagePreviewRenderer.Render(processed); // frozen — safe to hand across threads
                return (processed, bitmap);
            }, cancellationToken);

            if (cancellationToken.IsCancellationRequested) return;

            PreviewImageSource = preview;
            _lastProcessedImage = processed;
            IsPreviewComputing = false;

            var job = await Task.Run(() => RasterImporter.BuildLaserJob(processed, options), cancellationToken);
            if (cancellationToken.IsCancellationRequested) return;

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
            IsPreviewComputing = false;
        }
    }

    private GrayscaleImage GetProcessingSource(RasterImportOptions options)
    {
        lock (_sourceLock)
        {
            if (NearlyEquals(_sourceTargetWidthMm, options.TargetWidthMm)
                && NullableNearlyEquals(_sourceTargetHeightMm, options.TargetHeightMm)
                && NearlyEquals(_sourceDpi, options.Dpi))
                return _sourceImage;

            _sourceImage = RasterImporter.LoadProcessingSource(FilePath, options);
            _sourceTargetWidthMm = options.TargetWidthMm;
            _sourceTargetHeightMm = options.TargetHeightMm;
            _sourceDpi = options.Dpi;
            return _sourceImage;
        }
    }

    private static bool NearlyEquals(double left, double right) => Math.Abs(left - right) < 1e-6;
    private static bool NullableNearlyEquals(double? left, double? right) =>
        left is null ? right is null : right is not null && NearlyEquals(left.Value, right.Value);

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
            var controllerMaximumS = (_machine as IGrblDeviceProfileSource)?.DeviceProfile?.MaxSpindleSpeed;
            var frameLines = FramingService.BuildFrameGCode(job.Bounds, new FramingOptions
            {
                FeedRatePerMinute = 3000,
                Mode = FramingMode.FullOutline,
                LaserPower = controllerMaximumS is > 0 ? FramingOptions.VisiblePowerPercent : 0,
            }, controllerMaximumS);

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
