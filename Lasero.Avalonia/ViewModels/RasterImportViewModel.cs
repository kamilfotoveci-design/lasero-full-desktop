using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Runtime.Versioning;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Lasero.Avalonia.Services;
using Lasero.Core.GCode;
using Lasero.Core.Grbl;
using Lasero.Core.Import;
using Lasero.Core.Jobs;
using Lasero.Core.Machines;
using Lasero.Core.Raster;
using ReactiveUI;

namespace Lasero.Avalonia.ViewModels;

public sealed record RasterDitherChoice(string Label, string Hint, DitheringAlgorithm? Algorithm)
{
    public override string ToString() => Label;
}

/// <summary>
/// Port of Lasero.App/ViewModels/RasterImportViewModel.cs. Configures a single raster import before
/// it's placed on the scene: physical size, engraving parameters, a live processed-image preview, and
/// a live job-bounds readout with an independent "frame this" action — all computed off the UI thread
/// from the same Lasero.Core.Raster pipeline that ultimately generates the real G-code, so what's
/// previewed/framed here is what gets burned. Only the MVVM plumbing changed (ReactiveUI instead of
/// CommunityToolkit.Mvvm, Avalonia's WriteableBitmap instead of WPF's BitmapSource, Dispatcher.UIThread
/// instead of Application.Current.Dispatcher) — every field, guard and the debounced recompute pipeline
/// are unchanged.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class RasterImportViewModel : ReactiveObject, IDisposable
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
    private readonly Subject<bool> _canFrameChanged = new();

    public string FilePath { get; }
    public string FileName { get; }

    private double targetWidthMm;
    public double TargetWidthMm { get => targetWidthMm; set { this.RaiseAndSetIfChanged(ref targetWidthMm, value); ScheduleRecompute(); } }

    private bool keepAspectRatio = true;
    public bool KeepAspectRatio { get => keepAspectRatio; set { this.RaiseAndSetIfChanged(ref keepAspectRatio, value); ScheduleRecompute(); } }

    private double targetHeightMm;
    public double TargetHeightMm { get => targetHeightMm; set { this.RaiseAndSetIfChanged(ref targetHeightMm, value); ScheduleRecompute(); } }

    private double feedRatePerMinute;
    public double FeedRatePerMinute { get => feedRatePerMinute; set { this.RaiseAndSetIfChanged(ref feedRatePerMinute, value); ScheduleRecompute(); } }

    private double minPower;
    public double MinPower { get => minPower; set { this.RaiseAndSetIfChanged(ref minPower, value); ScheduleRecompute(); } }

    private double maxPower;
    public double MaxPower { get => maxPower; set { this.RaiseAndSetIfChanged(ref maxPower, value); ScheduleRecompute(); } }

    private double dpi;
    public double Dpi
    {
        get => dpi;
        set
        {
            this.RaiseAndSetIfChanged(ref dpi, value);
            this.RaisePropertyChanged(nameof(LineIntervalMm));
            ScheduleRecompute();
        }
    }

    private int passes = 1;
    public int Passes { get => passes; set { this.RaiseAndSetIfChanged(ref passes, value); ScheduleRecompute(); } }

    private bool invert;
    public bool Invert
    {
        get => invert;
        set
        {
            this.RaiseAndSetIfChanged(ref invert, value);
            _hasImmediatePreview = TryShowImmediateInversionPreview();
            ScheduleRecompute();
        }
    }

    private double gamma = 1;
    public double Gamma { get => gamma; set { this.RaiseAndSetIfChanged(ref gamma, value); ScheduleRecompute(); } }

    private double exposure;
    public double Exposure { get => exposure; set { this.RaiseAndSetIfChanged(ref exposure, value); ScheduleRecompute(); } }

    private double brightness;
    public double Brightness { get => brightness; set { this.RaiseAndSetIfChanged(ref brightness, value); ScheduleRecompute(); } }

    private double contrast;
    public double Contrast { get => contrast; set { this.RaiseAndSetIfChanged(ref contrast, value); ScheduleRecompute(); } }

    private double highlights;
    public double Highlights { get => highlights; set { this.RaiseAndSetIfChanged(ref highlights, value); ScheduleRecompute(); } }

    private double shadows;
    public double Shadows { get => shadows; set { this.RaiseAndSetIfChanged(ref shadows, value); ScheduleRecompute(); } }

    private double blackPoint;
    public double BlackPoint { get => blackPoint; set { this.RaiseAndSetIfChanged(ref blackPoint, value); ScheduleRecompute(); } }

    private double whitePoint = 255;
    public double WhitePoint { get => whitePoint; set { this.RaiseAndSetIfChanged(ref whitePoint, value); ScheduleRecompute(); } }

    private double noiseReduction;
    public double NoiseReduction { get => noiseReduction; set { this.RaiseAndSetIfChanged(ref noiseReduction, value); ScheduleRecompute(); } }

    private double sharpen;
    public double Sharpen { get => sharpen; set { this.RaiseAndSetIfChanged(ref sharpen, value); ScheduleRecompute(); } }

    private double edgeEnhance;
    public double EdgeEnhance { get => edgeEnhance; set { this.RaiseAndSetIfChanged(ref edgeEnhance, value); ScheduleRecompute(); } }

    private RasterDitherChoice selectedDithering = DitheringChoices[0];
    public RasterDitherChoice SelectedDithering { get => selectedDithering; set { this.RaiseAndSetIfChanged(ref selectedDithering, value); ScheduleRecompute(); } }

    private WriteableBitmap? previewImageSource;
    public WriteableBitmap? PreviewImageSource { get => previewImageSource; private set => this.RaiseAndSetIfChanged(ref previewImageSource, value); }

    private LaserJob? plannedJob;
    public LaserJob? PlannedJob
    {
        get => plannedJob;
        private set
        {
            this.RaiseAndSetIfChanged(ref plannedJob, value);
            this.RaisePropertyChanged(nameof(HasJob));
            _canFrameChanged.OnNext(CanFrame());
        }
    }

    private bool isComputing;
    public bool IsComputing { get => isComputing; private set { this.RaiseAndSetIfChanged(ref isComputing, value); this.RaisePropertyChanged(nameof(HasJob)); } }

    private bool isPreviewComputing;
    public bool IsPreviewComputing { get => isPreviewComputing; private set => this.RaiseAndSetIfChanged(ref isPreviewComputing, value); }

    private string? statusMessage;
    public string? StatusMessage { get => statusMessage; set => this.RaiseAndSetIfChanged(ref statusMessage, value); }

    private bool isFraming;
    public bool IsFraming
    {
        get => isFraming;
        private set { this.RaiseAndSetIfChanged(ref isFraming, value); _canFrameChanged.OnNext(CanFrame()); }
    }

    public bool HasJob => !IsComputing && PlannedJob is { Moves.Count: > 0 };

    public double LineIntervalMm
    {
        get => Dpi <= 0 ? 0 : 25.4 / Dpi;
        set
        {
            if (!double.IsFinite(value) || value <= 0) return;
            Dpi = 25.4 / value;
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
        FileName = Path.GetFileName(filePath);
        this.targetWidthMm = targetWidthMm;
        this.feedRatePerMinute = feedRatePerMinute;
        this.maxPower = maxPower;
        this.dpi = dpi;
        var initialOptions = new RasterImportOptions { TargetWidthMm = targetWidthMm, Dpi = dpi };
        _sourceImage = RasterImporter.LoadProcessingSource(filePath, initialOptions);
        _sourceTargetWidthMm = targetWidthMm;
        _sourceTargetHeightMm = null;
        _sourceDpi = dpi;

        FrameThisCommand = ReactiveCommand.CreateFromTask(FrameThis, _canFrameChanged.StartWith(false));

        _suppressRecompute = true;
        ApplyRecommendation(ImageAutoAdjuster.Recommend(_sourceImage));

        this.targetHeightMm = _sourceImage.Height * (targetWidthMm / _sourceImage.Width);

        _suppressRecompute = false;
        ScheduleRecompute();
    }

    public ReactiveCommand<Unit, Unit> FrameThisCommand { get; }

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
                var bitmap = ProcessedImagePreviewRenderer.Render(processed);
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
        if (Dispatcher.UIThread.CheckAccess()) _canFrameChanged.OnNext(CanFrame());
        else Dispatcher.UIThread.Post(() => _canFrameChanged.OnNext(CanFrame()));
    }

    public void Dispose()
    {
        _recomputeCancellation?.Cancel();
        _recomputeCancellation?.Dispose();
        _machine.ConnectionStateChanged -= OnMachineStateChanged;
        _machine.StatusUpdated -= OnMachineStatusUpdated;
        _machine.AlertChanged -= OnMachineAlertChanged;
        _canFrameChanged.OnCompleted();
        _canFrameChanged.Dispose();
    }
}
