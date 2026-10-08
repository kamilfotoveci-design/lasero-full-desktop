using System.Windows;
using System.Windows.Media.Imaging;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lasero.Core.GCode;
using Lasero.Core.Grbl;
using Lasero.Core.Import;
using Lasero.Core.Jobs;
using Lasero.Core.Machines;
using Lasero.Core.Raster;

namespace Lasero.App.ViewModels;

public sealed record RasterDitherChoice(string Label, string Hint, DitheringAlgorithm? Algorithm,
    bool UseThreshold = false, bool UseDithering = true)
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
    private const int DebounceMs = 120;

    public static IReadOnlyList<RasterDitherChoice> DitheringChoices { get; } =
    [
        new("Práh", "Rozdělí obraz na černou a bílou podle nastavené hranice.", null, UseThreshold: true, UseDithering: false),
        new("Floyd-Steinberg", "Rychlé černobílé podání s jemným rozptylem bodů.", DitheringAlgorithm.FloydSteinberg),
        new("Stucki", "Vyvážený detail a plynulé tóny. Doporučeno pro fotografie.", DitheringAlgorithm.Stucki),
        new("Jarvis", "Jemnější rozptyl detailů pro větší fotografie.", DitheringAlgorithm.Jarvis),
        new("Atkinson", "Lehčí výsledek s výraznými hranami. Doporučeno pro loga.", DitheringAlgorithm.Atkinson),
        new("Stupně šedi", "Zachová plynulé tóny pomocí proměnného výkonu laseru.", null, UseDithering: false),
    ];

    private readonly ILaserMachine _machine;
    private readonly AppSettingsStore _settingsStore;
    public RasterImagePresetStore PresetStore { get; }
    public ObservableCollection<RasterImagePreset> UserPresets { get; } = [];
    public IReadOnlyList<RasterDitherChoice> AvailableDitheringChoices => DitheringChoices;
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
    [ObservableProperty] private int _sharpenRadius = 1;
    [ObservableProperty] private int _thresholdValue = 128;
    [ObservableProperty] private double _edgeEnhance;
    [ObservableProperty] private RasterDitherChoice _selectedDithering = DitheringChoices[0];

    [ObservableProperty] private BitmapSource? _previewImageSource;
    [ObservableProperty] private BitmapSource? _originalImageSource;
    [ObservableProperty] private LaserJob? _plannedJob;
    [ObservableProperty] private bool _isComputing;
    [ObservableProperty] private bool _isPreviewComputing;
    [ObservableProperty] private string? _statusMessage;
    [ObservableProperty] private bool _isFraming;
    [ObservableProperty] private double _previewZoom = 1;
    [ObservableProperty] private double _previewPanX;
    [ObservableProperty] private double _previewPanY;

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

    public double PreviewPixelsToDipScale => 96 / Dpi;
    public double PreviewImageWidthDip => TargetWidthMm * 96 / 25.4;
    public double PreviewImageHeightDip => (KeepAspectRatio ? _sourceImage.Height * TargetWidthMm / _sourceImage.Width : TargetHeightMm) * 96 / 25.4;
    public string OriginalDimensionsLabel => $"{_sourceImage.Width} × {_sourceImage.Height} px · {TargetWidthMm:0.#} × {PreviewImageHeightDip * 25.4 / 96:0.#} mm";
    public string ProcessedDimensionsLabel => PreviewImageSource is { } image
        ? $"{image.PixelWidth} × {image.PixelHeight} px · {TargetWidthMm:0.#} × {PreviewImageHeightDip * 25.4 / 96:0.#} mm"
        : "Výsledek se připravuje";

    public RasterImportViewModel(ILaserMachine machine, AppSettingsStore settingsStore, string filePath,
        double targetWidthMm, double feedRatePerMinute, double maxPower, double dpi,
        RasterImportOptions? initialOptions = null, RasterImagePresetStore? presetStore = null)
    {
        _machine = machine;
        _settingsStore = settingsStore;
        PresetStore = presetStore ?? RasterImagePresetStore.CreateDefault();
        ReloadPresets();
        _machine.ConnectionStateChanged += OnMachineStateChanged;
        _machine.StatusUpdated += OnMachineStatusUpdated;
        _machine.AlertChanged += OnMachineAlertChanged;
        FilePath = filePath;
        FileName = System.IO.Path.GetFileName(filePath);
        _targetWidthMm = targetWidthMm;
        _feedRatePerMinute = feedRatePerMinute;
        _maxPower = maxPower;
        _dpi = dpi;
        var hasInitialOptions = initialOptions is not null;
        initialOptions ??= new RasterImportOptions
        {
            TargetWidthMm = targetWidthMm,
            Dpi = dpi,
            FeedRatePerMinute = feedRatePerMinute,
            MaxPower = maxPower,
        };
        _sourceImage = RasterImporter.LoadProcessingSource(filePath, initialOptions);
        _keepAspectRatio = initialOptions.TargetHeightMm is null;
        _sourceTargetWidthMm = targetWidthMm;
        _sourceTargetHeightMm = initialOptions.TargetHeightMm;
        _sourceDpi = dpi;
        _suppressRecompute = true;
        if (hasInitialOptions) ApplyOptions(initialOptions);
        else ApplyRecommendation(ImageAutoAdjuster.Recommend(_sourceImage));

        _targetHeightMm = initialOptions.TargetHeightMm ?? _sourceImage.Height * (targetWidthMm / _sourceImage.Width);

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
            UseThreshold = SelectedDithering.UseThreshold,
            ThresholdValue = (byte)ThresholdValue,
            UseDithering = SelectedDithering.UseDithering,
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
            SharpenRadius = SharpenRadius,
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
        if (ThresholdValue is < 0 or > 255)
            return "Práh musí být v rozsahu 0 až 255.";
        if (SharpenRadius is < 1 or > 4)
            return "Poloměr doostření musí být v rozsahu 1 až 4 body.";
        if (Passes < 1)
            return "Počet průchodů musí být alespoň 1.";
        return null;
    }

    partial void OnTargetWidthMmChanged(double value)
    {
        OnPropertyChanged(nameof(PreviewImageWidthDip));
        OnPropertyChanged(nameof(PreviewImageHeightDip));
        OnPropertyChanged(nameof(OriginalDimensionsLabel));
        OnPropertyChanged(nameof(ProcessedDimensionsLabel));
        ScheduleRecompute();
    }
    partial void OnTargetHeightMmChanged(double value)
    {
        OnPropertyChanged(nameof(PreviewImageHeightDip));
        OnPropertyChanged(nameof(OriginalDimensionsLabel));
        OnPropertyChanged(nameof(ProcessedDimensionsLabel));
        ScheduleRecompute();
    }
    partial void OnFeedRatePerMinuteChanged(double value) => ScheduleRecompute();
    partial void OnMinPowerChanged(double value) => ScheduleRecompute();
    partial void OnMaxPowerChanged(double value) => ScheduleRecompute();
    partial void OnDpiChanged(double value)
    {
        OnPropertyChanged(nameof(LineIntervalMm));
        OnPropertyChanged(nameof(PreviewPixelsToDipScale));
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
    partial void OnSharpenRadiusChanged(int value) => ScheduleRecompute();
    partial void OnThresholdValueChanged(int value) => ScheduleRecompute();
    partial void OnSelectedDitheringChanged(RasterDitherChoice value) => ScheduleRecompute();

    partial void OnPreviewImageSourceChanged(BitmapSource? value) => OnPropertyChanged(nameof(ProcessedDimensionsLabel));

    partial void OnKeepAspectRatioChanged(bool value)
    {
        OnPropertyChanged(nameof(PreviewImageHeightDip));
        OnPropertyChanged(nameof(OriginalDimensionsLabel));
        OnPropertyChanged(nameof(ProcessedDimensionsLabel));
        ScheduleRecompute();
    }

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
        SelectedDithering = DitheringChoices.First(choice => choice.Algorithm == DitheringAlgorithm.Stucki);
        ThresholdValue = 128;
        SharpenRadius = 1;
    }

    private void ApplyOptions(RasterImportOptions options)
    {
        Gamma = options.Gamma;
        Exposure = options.Exposure;
        Brightness = options.Brightness;
        Contrast = options.Contrast;
        Highlights = options.Highlights;
        Shadows = options.Shadows;
        BlackPoint = options.BlackPoint;
        WhitePoint = options.WhitePoint;
        Invert = options.Invert;
        NoiseReduction = options.NoiseReduction;
        Sharpen = options.Sharpen;
        SharpenRadius = options.SharpenRadius;
        EdgeEnhance = options.EdgeEnhance;
        ThresholdValue = options.ThresholdValue;
        SelectedDithering = DitheringChoices.FirstOrDefault(choice =>
            options.UseThreshold ? choice.UseThreshold
            : options.UseDithering ? choice.Algorithm == options.DitheringAlgorithm
            : !choice.UseDithering && !choice.UseThreshold) ?? DitheringChoices[0];
    }

    public void ApplyBuiltInPreset(string name)
    {
        var options = name switch
        {
            "Fotografie" => new ImageProcessingOptions { Gamma = 1.1, Contrast = 8, Highlights = -12, Shadows = 8, NoiseReduction = 8, Sharpen = 18, UseDithering = true, DitheringAlgorithm = DitheringAlgorithm.Stucki },
            "Dřevo" => new ImageProcessingOptions { Gamma = 0.92, Contrast = 12, BlackPoint = 5, WhitePoint = 248, Sharpen = 16, UseDithering = true, DitheringAlgorithm = DitheringAlgorithm.Atkinson },
            "Kůže" => new ImageProcessingOptions { Gamma = 1.08, Contrast = 6, Highlights = -10, Shadows = 10, NoiseReduction = 10, UseDithering = true, DitheringAlgorithm = DitheringAlgorithm.FloydSteinberg },
            "Logo" => new ImageProcessingOptions { Contrast = 20, UseThreshold = true, ThresholdValue = 150, UseDithering = false },
            "Jemné tóny" => new ImageProcessingOptions { Gamma = 1, UseDithering = false },
            _ => null,
        };
        if (options is not null) ApplyProcessingOptions(options);
    }

    public void ApplyUserPreset(RasterImagePreset preset) => ApplyProcessingOptions(preset.ToOptions());

    private void ApplyProcessingOptions(ImageProcessingOptions options)
    {
        _suppressRecompute = true;
        Gamma = options.Gamma;
        Exposure = options.Exposure;
        Brightness = options.Brightness;
        Contrast = options.Contrast;
        Highlights = options.Highlights;
        Shadows = options.Shadows;
        BlackPoint = options.BlackPoint;
        WhitePoint = options.WhitePoint;
        Invert = options.Invert;
        NoiseReduction = options.NoiseReduction;
        Sharpen = options.Sharpen;
        SharpenRadius = options.SharpenRadius;
        EdgeEnhance = options.EdgeEnhance;
        ThresholdValue = options.ThresholdValue;
        SelectedDithering = DitheringChoices.FirstOrDefault(choice =>
            options.UseThreshold ? choice.UseThreshold
            : options.UseDithering ? choice.Algorithm == options.DitheringAlgorithm
            : !choice.UseDithering && !choice.UseThreshold) ?? DitheringChoices[0];
        _suppressRecompute = false;
        ScheduleRecompute();
    }

    public ImageProcessingOptions CurrentProcessingOptions => new()
    {
        Gamma = Gamma, Exposure = Exposure, Brightness = Brightness, Contrast = Contrast,
        Highlights = Highlights, Shadows = Shadows, BlackPoint = BlackPoint, WhitePoint = WhitePoint,
        Invert = Invert, NoiseReduction = NoiseReduction, Sharpen = Sharpen, SharpenRadius = SharpenRadius,
        EdgeEnhance = EdgeEnhance, UseDithering = SelectedDithering.UseDithering,
        DitheringAlgorithm = SelectedDithering.Algorithm ?? DitheringAlgorithm.Stucki,
        UseThreshold = SelectedDithering.UseThreshold, ThresholdValue = (byte)ThresholdValue,
    };

    public RasterImagePreset SavePreset(string name) => PresetStore.Save(name, CurrentProcessingOptions);
    public void ReloadPresets()
    {
        UserPresets.Clear();
        foreach (var preset in PresetStore.List()) UserPresets.Add(preset);
    }
    public void DeletePreset(Guid id)
    {
        PresetStore.Delete(id);
        ReloadPresets();
    }

    [RelayCommand]
    private void ResetAll()
    {
        _suppressRecompute = true;
        ApplyRecommendation(ImageAutoAdjuster.Recommend(_sourceImage));
        Invert = false;
        PreviewZoom = 1;
        PreviewPanX = 0;
        PreviewPanY = 0;
        _suppressRecompute = false;
        ScheduleRecompute();
    }

    public void SetZoom(double zoom)
    {
        PreviewZoom = Math.Clamp(zoom, 0.08, 8);
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
            var source = await Task.Run(() => GetProcessingSource(options), cancellationToken);
            if (cancellationToken.IsCancellationRequested) return;
            OriginalImageSource = await Task.Run(() => ProcessedImagePreviewRenderer.RenderSource(source), cancellationToken);

            // Show a quick, bounded preview first. It is strictly visual: job generation always uses
            // the full-resolution processing below, never this reduced image.
            var immediate = await Task.Run(() =>
            {
                var previewSource = RasterImportPreviewScaler.Downsample(source, 800, cancellationToken);
                var image = RasterImporter.Process(previewSource, options);
                return ProcessedImagePreviewRenderer.Render(image);
            }, cancellationToken);
            if (cancellationToken.IsCancellationRequested) return;
            PreviewImageSource = immediate;
            IsPreviewComputing = false;

            var processed = await Task.Run(() => RasterImporter.Process(source, options), cancellationToken);
            if (cancellationToken.IsCancellationRequested) return;
            var preview = await Task.Run(() => ProcessedImagePreviewRenderer.Render(processed), cancellationToken);
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
