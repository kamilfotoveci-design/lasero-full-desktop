using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using Lasero.Core.Grbl;
using Lasero.Core.Scene;
using Lasero.Core.Trace;

namespace Lasero.App.ViewModels;

public partial class BitmapTraceViewModel : ObservableObject, IDisposable
{
    private readonly string _filePath;
    private CancellationTokenSource? _traceCancellation;
    private bool _initialized;

    [ObservableProperty] private double _threshold = 128;
    [ObservableProperty] private TraceMode _mode = TraceMode.FilledShapes;
    [ObservableProperty] private ThresholdMode _thresholdMode = ThresholdMode.Manual;
    [ObservableProperty] private TraceQuality _quality = TraceQuality.Balanced;
    [ObservableProperty] private double _detail = 0.5;
    [ObservableProperty] private double _noiseRemoval;
    [ObservableProperty] private double _contrast;
    // A modest cubic fit preserves small letter details without reproducing pixel stair steps.
    [ObservableProperty] private double _simplificationPixels = 0.4;
    [ObservableProperty] private int _minimumFeaturePixels = 2;
    [ObservableProperty] private bool _invert;
    [ObservableProperty] private bool _isComputing;
    [ObservableProperty] private string? _statusMessage;
    [ObservableProperty] private Geometry? _previewGeometry;
    [ObservableProperty] private IReadOnlyList<ColoredTracePreviewPath> _coloredPreviewPaths = [];
    [ObservableProperty] private double _previewStrokeThicknessMm = 0.12;
    [ObservableProperty] private BitmapTraceResult? _result;

    public string FileName { get; }
    public BitmapSource SourceImage { get; }
    // Public setters are a defensive WPF binding boundary: sizing/layout updates must never
    // turn a stale/default TwoWay binding into an unhandled Dispatcher exception.
    public double PreviewWidthMm { get; set; }
    public double PreviewHeightMm { get; set; }
    public bool HasResult => Result is { ContourCount: > 0 } && !IsComputing;
    public bool IsFilledMode => Mode == TraceMode.FilledShapes;
    public bool IsColorMode => Mode == TraceMode.Color;
    public bool IsOutlineMode => Mode == TraceMode.Outline;
    public bool IsManualThreshold => IsFilledMode && ThresholdMode == ThresholdMode.Manual;
    public string ModeDescription => Mode switch
    {
        TraceMode.FilledShapes => "Uzavřené tvary a otvory pro loga, text a černobílou grafiku.",
        TraceMode.Outline => "Hrany z fotografie nebo linkové kresby; může zachytit i vnitřní detaily.",
        TraceMode.Color => "Oddělené barevné plochy pro barevná loga a ilustrace.",
        _ => string.Empty,
    };
    public string ResultSummary => Result is null
        ? "Čekám na náhled"
        : $"{Result.ContourCount} obrysů · {Result.NodeCount:N0} uzlů";

    public BitmapTraceViewModel(string filePath, double targetWidthMm)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        if (!File.Exists(filePath)) throw new FileNotFoundException("Zdrojová bitmapa nebyla nalezena.", filePath);
        if (!double.IsFinite(targetWidthMm) || targetWidthMm <= 0)
            throw new ArgumentOutOfRangeException(nameof(targetWidthMm));

        _filePath = filePath;
        FileName = Path.GetFileName(filePath);
        SourceImage = LoadBitmap(filePath);
        PreviewWidthMm = targetWidthMm;
        PreviewHeightMm = targetWidthMm * SourceImage.PixelHeight / SourceImage.PixelWidth;
        _initialized = true;
        QueueTrace(immediate: true);
    }

    partial void OnThresholdChanged(double value) => QueueTrace();
    partial void OnModeChanged(TraceMode value)
    {
        OnPropertyChanged(nameof(IsFilledMode));
        OnPropertyChanged(nameof(IsColorMode));
        OnPropertyChanged(nameof(IsOutlineMode));
        OnPropertyChanged(nameof(IsManualThreshold));
        OnPropertyChanged(nameof(ModeDescription));
        QueueTrace();
    }
    partial void OnThresholdModeChanged(ThresholdMode value)
    {
        OnPropertyChanged(nameof(IsManualThreshold));
        QueueTrace();
    }
    partial void OnQualityChanged(TraceQuality value) => QueueTrace();
    partial void OnDetailChanged(double value) => QueueTrace();
    partial void OnNoiseRemovalChanged(double value) => QueueTrace();
    partial void OnContrastChanged(double value) => QueueTrace();
    partial void OnSimplificationPixelsChanged(double value) => QueueTrace();
    partial void OnMinimumFeaturePixelsChanged(int value) => QueueTrace();
    partial void OnInvertChanged(bool value) => QueueTrace();
    partial void OnIsComputingChanged(bool value) => OnPropertyChanged(nameof(HasResult));
    partial void OnResultChanged(BitmapTraceResult? value)
    {
        OnPropertyChanged(nameof(HasResult));
        OnPropertyChanged(nameof(ResultSummary));
    }

    private void QueueTrace(bool immediate = false)
    {
        if (!_initialized) return;
        _traceCancellation?.Cancel();
        _traceCancellation?.Dispose();
        _traceCancellation = new CancellationTokenSource();
        _ = RecomputeAsync(_traceCancellation.Token, immediate);
    }

    private async Task RecomputeAsync(CancellationToken cancellationToken, bool immediate)
    {
        try
        {
            IsComputing = true;
            StatusMessage = "Připravuji vektorový náhled…";
            if (!immediate) await Task.Delay(180, cancellationToken);

            var options = new BitmapTraceOptions
            {
                Threshold = (byte)Math.Clamp(Math.Round(Threshold), 0, 255),
                Mode = Mode,
                ThresholdMode = ThresholdMode,
                Quality = Quality,
                Detail = Math.Clamp(Detail, 0, 1),
                NoiseRemoval = Math.Clamp(NoiseRemoval, 0, 1),
                Contrast = Math.Clamp(Contrast, -1, 1),
                MinimumFeaturePixels = Math.Max(1, MinimumFeaturePixels),
                SimplificationPixels = Math.Max(0, SimplificationPixels),
                TargetWidthMm = PreviewWidthMm,
                Invert = Invert,
            };

            // Trace and construct frozen WPF geometry off the UI thread. The previous preview stays
            // visible while a slider is moving, then the latest result replaces it in one UI turn.
            var preview = await Task.Run(() =>
            {
                var traced = BitmapTracer.Trace(_filePath, options, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                if (options.Mode == TraceMode.Color)
                {
                    var colored = traced.VectorPaths.Select(item =>
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var brush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(
                            item.Color.R, item.Color.G, item.Color.B));
                        brush.Freeze();
                        return new ColoredTracePreviewPath(
                            BuildPreviewGeometry([item], traced.HeightMm, fill: true), brush);
                    }).ToArray();
                    return (Result: traced, Geometry: (Geometry?)null,
                        ColoredPaths: (IReadOnlyList<ColoredTracePreviewPath>)colored);
                }
                return (Result: traced, Geometry: (Geometry?)BuildPreviewGeometry(traced),
                    ColoredPaths: (IReadOnlyList<ColoredTracePreviewPath>)[]);
            }, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            Result = preview.Result;
            PreviewGeometry = preview.Geometry;
            ColoredPreviewPaths = preview.ColoredPaths;
            StatusMessage = preview.Result.ContourCount == 0
                ? options.Mode == TraceMode.FilledShapes
                    ? "Nebyly nalezeny žádné obrysy. Lze upravit práh nebo zapnout invertování."
                    : "Nebyly nalezeny žádné obrysy. Lze zvolit jiný režim nebo upravit rozpoznání."
                : options.Mode == TraceMode.Color
                    ? "Náhled je připravený. Barevné plochy ukazují výsledné vektorové vrstvy."
                    : "Náhled je připravený. Zvýrazněná křivka ukazuje výsledný Bézierův vektor.";
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            Result = null;
            PreviewGeometry = null;
            ColoredPreviewPaths = [];
            StatusMessage = $"Trasování se nezdařilo: {ex.Message}";
        }
        finally
        {
            if (!cancellationToken.IsCancellationRequested) IsComputing = false;
        }
    }

    private static BitmapSource LoadBitmap(string filePath)
    {
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.UriSource = new Uri(filePath, UriKind.Absolute);
        image.EndInit();
        image.Freeze();
        return image;
    }

    private static Geometry BuildPreviewGeometry(BitmapTraceResult result) =>
        BuildPreviewGeometry(result.VectorPaths, result.HeightMm);

    private static Geometry BuildPreviewGeometry(IReadOnlyList<TracedVectorObject> vectorPaths, double heightMm, bool fill = false)
    {
        var geometry = new PathGeometry { FillRule = FillRule.Nonzero }; // fill contract: NonZero per compound group (docs/fill-winding-contract.md)
        foreach (var traced in vectorPaths)
        {
            foreach (var subpath in traced.Path.Subpaths)
            {
                if (subpath.Nodes.Count < 2) continue;
                var figure = new PathFigure
                {
                    StartPoint = ToPreviewPoint(subpath.Nodes[0].Anchor, heightMm),
                    IsClosed = subpath.IsClosed,
                    IsFilled = fill,
                };

                for (var i = 0; i < subpath.SegmentCount; i++)
                {
                    var (a, b) = subpath.Segment(i);
                    if (VectorSubpath.IsStraightSegment(a, b))
                    {
                        figure.Segments.Add(new LineSegment(ToPreviewPoint(b.Anchor, heightMm), isStroked: true));
                        continue;
                    }

                    var control1 = a.HandleOut ?? a.Anchor;
                    var control2 = b.HandleIn ?? b.Anchor;
                    figure.Segments.Add(new BezierSegment(
                        ToPreviewPoint(control1, heightMm),
                        ToPreviewPoint(control2, heightMm),
                        ToPreviewPoint(b.Anchor, heightMm),
                        isStroked: true));
                }

                geometry.Figures.Add(figure);
            }
        }
        geometry.Freeze();
        return geometry;
    }

    private static System.Windows.Point ToPreviewPoint(Position point, double heightMm) =>
        new(point.X, heightMm - point.Y);

    public void Dispose()
    {
        _traceCancellation?.Cancel();
        _traceCancellation?.Dispose();
        _traceCancellation = null;
    }
}

public sealed record ColoredTracePreviewPath(Geometry Geometry, Brush Stroke);
