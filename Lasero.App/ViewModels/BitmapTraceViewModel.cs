using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using Lasero.Core.Trace;

namespace Lasero.App.ViewModels;

public partial class BitmapTraceViewModel : ObservableObject, IDisposable
{
    private readonly string _filePath;
    private CancellationTokenSource? _traceCancellation;
    private bool _initialized;

    [ObservableProperty] private double _threshold = 128;
    [ObservableProperty] private double _simplificationPixels = 1.2;
    [ObservableProperty] private int _minimumFeaturePixels = 8;
    [ObservableProperty] private bool _invert;
    [ObservableProperty] private bool _isComputing;
    [ObservableProperty] private string? _statusMessage;
    [ObservableProperty] private Geometry? _previewGeometry;
    [ObservableProperty] private BitmapTraceResult? _result;

    public string FileName { get; }
    public BitmapSource SourceImage { get; }
    public double PreviewWidthMm { get; }
    public double PreviewHeightMm { get; }
    public bool HasResult => Result is { ContourCount: > 0 } && !IsComputing;
    public string ResultSummary => Result is null
        ? "Čekám na náhled"
        : $"{Result.ContourCount} obrysů · {Result.PointCount:N0} bodů";

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
        Result = null;
        PreviewGeometry = null;
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
                MinimumFeaturePixels = Math.Max(1, MinimumFeaturePixels),
                SimplificationPixels = Math.Max(0, SimplificationPixels),
                TargetWidthMm = PreviewWidthMm,
                Invert = Invert,
            };

            var result = await Task.Run(() => BitmapTracer.Trace(_filePath, options, cancellationToken), cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            Result = result;
            PreviewGeometry = BuildPreviewGeometry(result);
            StatusMessage = result.ContourCount == 0
                ? "Nebyly nalezeny žádné obrysy. Zkuste upravit práh nebo zapnout invertování."
                : "Náhled je připravený. Modrá čára ukazuje výsledný vektor.";
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            Result = null;
            PreviewGeometry = null;
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

    private static Geometry BuildPreviewGeometry(BitmapTraceResult result)
    {
        var geometry = new PathGeometry { FillRule = FillRule.EvenOdd };
        foreach (var shape in result.Document.Shapes)
        {
            if (shape.Points.Count < 2) continue;
            var figure = new PathFigure
            {
                StartPoint = new System.Windows.Point(shape.Points[0].X, result.HeightMm - shape.Points[0].Y),
                IsClosed = shape.IsClosed,
                IsFilled = false,
            };
            var segment = new PolyLineSegment();
            foreach (var point in shape.Points.Skip(1))
                segment.Points.Add(new System.Windows.Point(point.X, result.HeightMm - point.Y));
            figure.Segments.Add(segment);
            geometry.Figures.Add(figure);
        }
        geometry.Freeze();
        return geometry;
    }

    public void Dispose()
    {
        _traceCancellation?.Cancel();
        _traceCancellation?.Dispose();
        _traceCancellation = null;
    }
}
