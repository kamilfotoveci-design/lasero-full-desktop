using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Lasero.Core.Trace;
using ReactiveUI;

namespace Lasero.Avalonia.ViewModels;

/// <summary>
/// Port of Lasero.App/ViewModels/BitmapTraceViewModel.cs. Converts a black-and-white bitmap into
/// vector contours via the shared Lasero.Core.Trace.BitmapTracer pipeline — no change to the tracing
/// logic itself, only the MVVM plumbing (ReactiveUI instead of CommunityToolkit.Mvvm) and the preview
/// types (Avalonia Bitmap/StreamGeometry instead of WPF's BitmapImage/PathGeometry).
/// </summary>
public sealed class BitmapTraceViewModel : ReactiveObject, IDisposable
{
    private readonly string _filePath;
    private CancellationTokenSource? _traceCancellation;
    private readonly bool _initialized;

    private double threshold = 128;
    public double Threshold { get => threshold; set { this.RaiseAndSetIfChanged(ref threshold, value); QueueTrace(); } }

    private double simplificationPixels = 0.2;
    public double SimplificationPixels { get => simplificationPixels; set { this.RaiseAndSetIfChanged(ref simplificationPixels, value); QueueTrace(); } }

    private int minimumFeaturePixels = 8;
    public int MinimumFeaturePixels { get => minimumFeaturePixels; set { this.RaiseAndSetIfChanged(ref minimumFeaturePixels, value); QueueTrace(); } }

    private bool invert;
    public bool Invert { get => invert; set { this.RaiseAndSetIfChanged(ref invert, value); QueueTrace(); } }

    private bool isComputing;
    public bool IsComputing { get => isComputing; private set { this.RaiseAndSetIfChanged(ref isComputing, value); this.RaisePropertyChanged(nameof(HasResult)); } }

    private string? statusMessage;
    public string? StatusMessage { get => statusMessage; private set => this.RaiseAndSetIfChanged(ref statusMessage, value); }

    private Geometry? previewGeometry;
    public Geometry? PreviewGeometry { get => previewGeometry; private set => this.RaiseAndSetIfChanged(ref previewGeometry, value); }

    private BitmapTraceResult? result;
    public BitmapTraceResult? Result
    {
        get => result;
        private set
        {
            this.RaiseAndSetIfChanged(ref result, value);
            this.RaisePropertyChanged(nameof(HasResult));
            this.RaisePropertyChanged(nameof(ResultSummary));
        }
    }

    public string FileName { get; }
    public Bitmap SourceImage { get; }
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
        PreviewHeightMm = targetWidthMm * SourceImage.PixelSize.Height / SourceImage.PixelSize.Width;
        _initialized = true;
        QueueTrace(immediate: true);
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

    private static Bitmap LoadBitmap(string filePath)
    {
        using var stream = File.OpenRead(filePath);
        return new Bitmap(stream);
    }

    private static Geometry BuildPreviewGeometry(BitmapTraceResult result)
    {
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.SetFillRule(FillRule.EvenOdd);
            foreach (var shape in result.Document.Shapes)
            {
                if (shape.Points.Count < 2) continue;
                context.BeginFigure(
                    new Point(shape.Points[0].X, result.HeightMm - shape.Points[0].Y),
                    isFilled: false);
                foreach (var point in shape.Points.Skip(1))
                    context.LineTo(new Point(point.X, result.HeightMm - point.Y));
                context.EndFigure(shape.IsClosed);
            }
        }
        return geometry;
    }

    public void Dispose()
    {
        _traceCancellation?.Cancel();
        _traceCancellation?.Dispose();
        _traceCancellation = null;
    }
}
