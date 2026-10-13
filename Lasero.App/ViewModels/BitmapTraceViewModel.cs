using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using Lasero.Core.Grbl;
using Lasero.Core.Raster;
using Lasero.Core.Scene;
using Lasero.Core.Trace;

namespace Lasero.App.ViewModels;

/// <summary>
/// The trace dialog: one grayscale threshold trace with the Cutoff / Threshold / Ignore less than /
/// Smoothness / Optimize controls and a live vector preview. Every value that changes the geometry
/// queues a debounced re-trace; fade, points, trace opacity and the delete-image switch only change how
/// the preview is drawn or what Apply does with the source bitmap.
/// </summary>
public partial class BitmapTraceViewModel : ObservableObject, IDisposable
{
    private const double FadedImageOpacity = 0.35;
    private const int PreviewLongSidePx = 800;
    private static readonly TimeSpan RefineDelay = TimeSpan.FromMilliseconds(250);

    private readonly string _filePath;
    private CancellationTokenSource? _traceCancellation;
    private bool _initialized;
    private readonly object _imageLock = new();
    private GrayscaleImage? _fullImage;
    private GrayscaleImage? _previewImage;

    [ObservableProperty] private double _cutoff;
    [ObservableProperty] private double _threshold = 128;
    [ObservableProperty] private double _ignoreLessThan = 2;
    [ObservableProperty] private double _smoothness = 1.0;
    [ObservableProperty] private double _optimize = 0.2;
    [ObservableProperty] private bool _deleteBoundary = true;

    [ObservableProperty] private bool _fadeImage = true;
    [ObservableProperty] private bool _showPoints;
    [ObservableProperty] private bool _showTrace = true;
    [ObservableProperty] private double _traceOpacityPercent = 100;
    [ObservableProperty] private bool _deleteImageAfterTracing = true;

    [ObservableProperty] private bool _isComputing;
    [ObservableProperty] private string? _statusMessage;
    [ObservableProperty] private Geometry? _previewGeometry;
    [ObservableProperty] private Geometry? _pointsGeometry;
    [ObservableProperty] private double _previewStrokeThicknessMm = 0.12;
    [ObservableProperty] private BitmapTraceResult? _result;

    public string FileName { get; }
    public BitmapSource SourceImage { get; }
    // Public setters are a defensive WPF binding boundary: sizing/layout updates must never
    // turn a stale/default TwoWay binding into an unhandled Dispatcher exception.
    public double PreviewWidthMm { get; set; }
    public double PreviewHeightMm { get; set; }
    /// <summary>The centred busy card only while there is nothing yet to look at; later re-traces update the preview in place.</summary>
    public bool ShowBusyOverlay => IsComputing && PreviewGeometry is null;
    public bool HasResult => Result is { ContourCount: > 0 } && !IsComputing;
    public double SourceImageOpacity => FadeImage ? FadedImageOpacity : 1;
    public double TraceOpacity => Math.Clamp(TraceOpacityPercent, 0, 100) / 100;

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

    partial void OnCutoffChanged(double value) => QueueTrace();
    partial void OnThresholdChanged(double value) => QueueTrace();
    partial void OnIgnoreLessThanChanged(double value) => QueueTrace();
    partial void OnSmoothnessChanged(double value) => QueueTrace();
    partial void OnOptimizeChanged(double value) => QueueTrace();
    partial void OnDeleteBoundaryChanged(bool value) => QueueTrace();
    partial void OnFadeImageChanged(bool value) => OnPropertyChanged(nameof(SourceImageOpacity));
    partial void OnTraceOpacityPercentChanged(double value) => OnPropertyChanged(nameof(TraceOpacity));
    partial void OnIsComputingChanged(bool value)
    {
        OnPropertyChanged(nameof(HasResult));
        OnPropertyChanged(nameof(ShowBusyOverlay));
    }
    partial void OnPreviewGeometryChanged(Geometry? value) => OnPropertyChanged(nameof(ShowBusyOverlay));
    partial void OnResultChanged(BitmapTraceResult? value) => OnPropertyChanged(nameof(HasResult));

    private void QueueTrace(bool immediate = false)
    {
        if (!_initialized) return;
        // Every change throws away the run in flight (fast or full) and starts again from the quick trace.
        _traceCancellation?.Cancel();
        _traceCancellation?.Dispose();
        _traceCancellation = new CancellationTokenSource();
        _ = RecomputeAsync(_traceCancellation.Token);
    }

    private BitmapTraceOptions BuildOptions() => new()
    {
        Threshold = (byte)Math.Clamp(Math.Round(double.IsFinite(Threshold) ? Threshold : 128), 0, 255),
        Cutoff = (byte)Math.Clamp(Math.Round(double.IsFinite(Cutoff) ? Cutoff : 0), 0, 255),
        IgnoreLessThanPixels = Math.Clamp(double.IsFinite(IgnoreLessThan) ? IgnoreLessThan : 2, 0, 100000),
        Smoothness = Math.Clamp(double.IsFinite(Smoothness) ? Smoothness : 1, 0, BitmapTraceOptions.MaxSmoothness),
        OptimizeTolerance = Math.Clamp(double.IsFinite(Optimize) ? Optimize : 0.2, 0, 10),
        DeleteBoundary = DeleteBoundary,
        TargetWidthMm = PreviewWidthMm,
    };

    private sealed record TraceOutput(BitmapTraceResult Result, Geometry Outline, Geometry Points);

    /// <summary>Progressive trace. A large image is first traced at reduced resolution so the preview follows
    /// a slider while it is dragged; after a short pause with no further change the full-quality trace runs
    /// and replaces it. Only the full-quality trace is ever stored as <see cref="Result"/>, so Apply always
    /// uses it, and Apply stays disabled until it has finished.</summary>
    private async Task RecomputeAsync(CancellationToken cancellationToken)
    {
        try
        {
            IsComputing = true;
            StatusMessage ??= "Počítám obrysy…";
            var options = BuildOptions();
            var longSide = Math.Max(SourceImage.PixelWidth, SourceImage.PixelHeight);

            if (longSide > PreviewLongSidePx)
            {
                var quick = await Task.Run(() => Compute(preview: true, options, cancellationToken), cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                Show(quick);
                await Task.Delay(RefineDelay, cancellationToken);
            }

            var full = await Task.Run(() => Compute(preview: false, options, cancellationToken), cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            Result = full.Result;
            Show(full);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            Result = null;
            PreviewGeometry = null;
            PointsGeometry = null;
            StatusMessage = $"Trasování se nezdařilo: {ex.Message}";
        }
        finally
        {
            if (!cancellationToken.IsCancellationRequested) IsComputing = false;
        }
    }

    private void Show(TraceOutput output)
    {
        PreviewGeometry = output.Outline;
        PointsGeometry = output.Points;
        StatusMessage = output.Result.ContourCount == 0
            ? "Nebyly nalezeny žádné obrysy. Lze upravit prahovou hodnotu nebo odříznutí."
            : $"{output.Result.ContourCount} obrysů · {output.Result.NodeCount:N0} uzlů";
    }

    /// <summary>Runs off the UI thread. Builds the frozen WPF geometry here too, so the UI thread only swaps it in.</summary>
    private TraceOutput Compute(bool preview, BitmapTraceOptions options, CancellationToken cancellationToken)
    {
        var image = GetImage(preview);
        var scaled = preview ? ScaleForPreview(options, image) : options;
        var traced = BitmapTracer.Trace(image, scaled, FileName, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return new TraceOutput(traced,
            BuildPreviewGeometry(traced.VectorPaths, traced.HeightMm),
            BuildPointsGeometry(traced.VectorPaths, traced.HeightMm));
    }

    /// <summary>Pixel-based settings follow the reduced resolution: lengths scale with it, areas with its square.</summary>
    private BitmapTraceOptions ScaleForPreview(BitmapTraceOptions options, GrayscaleImage previewImage)
    {
        var fullWidth = BitmapTracer.WorkingWidth(SourceImage.PixelWidth, SourceImage.PixelHeight);
        var factor = Math.Clamp(previewImage.Width / (double)Math.Max(1, fullWidth), 0.05, 1);
        return options with
        {
            IgnoreLessThanPixels = options.IgnoreLessThanPixels * factor * factor,
            Smoothness = options.Smoothness > 0 ? Math.Max(0.25, options.Smoothness * factor) : 0,
            OptimizeTolerance = options.OptimizeTolerance * factor,
        };
    }

    private GrayscaleImage GetImage(bool preview)
    {
        lock (_imageLock)
        {
            if (preview) return _previewImage ??= BitmapTracer.LoadWorkingImage(_filePath, PreviewLongSidePx);
            return _fullImage ??= BitmapTracer.LoadWorkingImage(_filePath);
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

    private static Geometry BuildPreviewGeometry(IReadOnlyList<TracedVectorObject> vectorPaths, double heightMm)
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
                    IsFilled = false,
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

    /// <summary>One node dot per anchor: a hair-length stroked segment, drawn with round caps so the
    /// window can size every dot in screen pixels through the stroke thickness.</summary>
    private static Geometry BuildPointsGeometry(IReadOnlyList<TracedVectorObject> vectorPaths, double heightMm)
    {
        var geometry = new PathGeometry();
        foreach (var traced in vectorPaths)
        {
            foreach (var subpath in traced.Path.Subpaths)
            {
                foreach (var node in subpath.Nodes)
                {
                    var point = ToPreviewPoint(node.Anchor, heightMm);
                    var figure = new PathFigure { StartPoint = point, IsClosed = false, IsFilled = false };
                    figure.Segments.Add(new LineSegment(new System.Windows.Point(point.X + 0.0001, point.Y), isStroked: true));
                    geometry.Figures.Add(figure);
                }
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
