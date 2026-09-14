using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using Lasero.Core.GCode;
using Lasero.Core.Geometry;
using Lasero.Core.Grbl;
using Lasero.Core.Scene;

namespace Lasero.App.ViewModels;

/// <summary>Drives OffsetPathWindow's live preview. Mirrors BitmapTraceViewModel's shape closely:
/// debounced recompute via CancellationTokenSource + a short delay, IsComputing/StatusMessage/
/// PreviewGeometry as the UI-facing state, Dispose cancels on close. The sources are captured once at
/// construction — the dialog never re-reads live selection while it is open.</summary>
public partial class OffsetPathViewModel : ObservableObject, IDisposable
{
    /// <summary>Margin added around the selection's own bounds so a large outward offset is not
    /// clipped by the preview canvas before the operator even sees it.</summary>
    private const double PreviewMarginMm = 20;

    private readonly IReadOnlyList<SceneObject> _sources;
    private readonly IVectorOffsetService _service;
    private readonly BoundingBox2D _sourceBounds;
    private CancellationTokenSource? _recomputeCancellation;
    private bool _initialized;
    private Dictionary<SceneObject, VectorPath> _resultsBySource = new();

    [ObservableProperty] private double _distanceMm = 2.0;
    [ObservableProperty] private VectorJoinType _joinType = VectorJoinType.Round;
    [ObservableProperty] private double _miterLimit = 2.0;
    [ObservableProperty] private bool _isComputing;
    [ObservableProperty] private string? _statusMessage;

    /// <summary>Every selected source's own outline, faint/thin in the window — kept as a separate
    /// Geometry from PreviewGeometry so the two can be styled differently (source faint, result
    /// prominent) in OffsetPathWindow.xaml while sharing one world-mm coordinate frame, so they
    /// overlay correctly regardless of the chosen distance.</summary>
    [ObservableProperty] private Geometry? _sourceOutlineGeometry;

    /// <summary>The offset result, the prominent stroke in the window.</summary>
    [ObservableProperty] private Geometry? _previewGeometry;

    /// <summary>Fixed preview canvas size in mm — the selection's own combined bounds plus a margin,
    /// computed once at construction so the Viewbox does not rescale (and the preview does not appear
    /// to "zoom") every time the operator drags the distance slider.</summary>
    public double PreviewWidthMm { get; }
    public double PreviewHeightMm { get; }

    /// <summary>True once at least one source produced a non-empty offset result at the current
    /// settings — gates the dialog's OK button, matching BitmapTraceViewModel.HasResult.</summary>
    public bool HasResult => _resultsBySource.Count > 0 && !IsComputing;

    public sealed record JoinTypeOption(VectorJoinType Value, string Label);

    /// <summary>Backs OffsetPathWindow's join-style ComboBox — DisplayMemberPath="Label",
    /// SelectedValuePath="Value", SelectedValue bound straight to JoinType.</summary>
    public IReadOnlyList<JoinTypeOption> JoinTypeOptions { get; } =
    [
        new(VectorJoinType.Round, "Zaoblený"),
        new(VectorJoinType.Miter, "Ostrý"),
        new(VectorJoinType.Bevel, "Zkosený"),
    ];

    public OffsetPathViewModel(IReadOnlyList<SceneObject> sources, IVectorOffsetService? service = null)
    {
        ArgumentNullException.ThrowIfNull(sources);
        if (sources.Count == 0) throw new ArgumentException("Offset needs at least one selected object.", nameof(sources));

        _sources = sources.ToList();
        _service = service ?? Clipper2VectorOffsetService.Default;

        var bounds = BoundingBox2D.Empty;
        foreach (var source in _sources) bounds = Union(bounds, source.WorldBounds());
        if (bounds.IsEmpty) bounds = new BoundingBox2D(0, 0, 1, 1);
        _sourceBounds = new BoundingBox2D(
            bounds.MinX - PreviewMarginMm, bounds.MinY - PreviewMarginMm,
            bounds.MaxX + PreviewMarginMm, bounds.MaxY + PreviewMarginMm);
        PreviewWidthMm = _sourceBounds.Width;
        PreviewHeightMm = _sourceBounds.Height;

        SourceOutlineGeometry = BuildGeometry(
            _sources.SelectMany(source => source.GetWorldShapes()
                .Select(shape => (Points: shape.Points, shape.IsClosed))), _sourceBounds);

        _initialized = true;
        QueueRecompute(immediate: true);
    }

    /// <summary>The final computed offset, keyed by source — handed to SceneViewModel.ApplyOffset
    /// when the window's OK button is pressed. Only sources whose offset survived at the current
    /// distance are present.</summary>
    public IReadOnlyDictionary<SceneObject, VectorPath> ResultsBySource => _resultsBySource;

    partial void OnDistanceMmChanged(double value) => QueueRecompute();
    partial void OnJoinTypeChanged(VectorJoinType value) => QueueRecompute();
    partial void OnMiterLimitChanged(double value) => QueueRecompute();
    partial void OnIsComputingChanged(bool value) => OnPropertyChanged(nameof(HasResult));

    private void QueueRecompute(bool immediate = false)
    {
        if (!_initialized) return;
        _recomputeCancellation?.Cancel();
        _recomputeCancellation?.Dispose();
        _recomputeCancellation = new CancellationTokenSource();
        _resultsBySource = new Dictionary<SceneObject, VectorPath>();
        PreviewGeometry = null;
        OnPropertyChanged(nameof(HasResult));
        _ = RecomputeAsync(_recomputeCancellation.Token, immediate);
    }

    private async Task RecomputeAsync(CancellationToken cancellationToken, bool immediate)
    {
        try
        {
            IsComputing = true;
            StatusMessage = "Počítám offset…";
            if (!immediate) await Task.Delay(180, cancellationToken);

            var distance = DistanceMm;
            var joinType = JoinType;
            var miterLimit = Math.Max(1.0, MiterLimit);

            var results = await Task.Run(() =>
            {
                var map = new Dictionary<SceneObject, VectorPath>();
                foreach (var source in _sources)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var path = VectorOffsetPlanner.ComputeOffset(source, _service, distance, joinType, miterLimit);
                    if (path is not null) map[source] = path;
                }
                return map;
            }, cancellationToken);

            cancellationToken.ThrowIfCancellationRequested();
            _resultsBySource = results;
            PreviewGeometry = BuildGeometry(
                results.Values.SelectMany(path => path.Subpaths
                    .Select(subpath => (Points: (IReadOnlyList<Position>)subpath.Nodes.Select(node => node.Anchor).ToList(), subpath.IsClosed))),
                _sourceBounds);
            StatusMessage = results.Count == 0
                ? "Offset o tuto vzdálenost odstraní všechny vybrané tvary."
                : results.Count < _sources.Count
                    ? "Offset o tuto vzdálenost odstraní některé z vybraných tvarů."
                    : "Náhled je připravený.";
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            _resultsBySource = new Dictionary<SceneObject, VectorPath>();
            PreviewGeometry = null;
            StatusMessage = $"Výpočet offsetu se nezdařil: {ex.Message}";
        }
        finally
        {
            if (!cancellationToken.IsCancellationRequested) IsComputing = false;
            OnPropertyChanged(nameof(HasResult));
        }
    }

    /// <summary>Builds one PathGeometry from a set of (Points, IsClosed) polylines, all in the same
    /// world-mm frame. Y is flipped the same way BitmapTraceViewModel.BuildPreviewGeometry flips it
    /// (canvasY = top - worldY) and X is shifted so the canvas origin sits at bounds.MinX — matching
    /// SceneCanvas's own world-Y-grows-up / screen-Y-grows-down convention.</summary>
    private static Geometry BuildGeometry(
        IEnumerable<(IReadOnlyList<Position> Points, bool IsClosed)> shapes, BoundingBox2D bounds)
    {
        var geometry = new PathGeometry();
        var left = bounds.MinX;
        var top = bounds.MaxY;

        foreach (var (points, isClosed) in shapes)
        {
            if (points.Count < 2) continue;
            var figure = new PathFigure
            {
                StartPoint = new System.Windows.Point(points[0].X - left, top - points[0].Y),
                IsClosed = isClosed,
                IsFilled = false,
            };
            var segment = new PolyLineSegment();
            foreach (var point in points.Skip(1))
                segment.Points.Add(new System.Windows.Point(point.X - left, top - point.Y));
            figure.Segments.Add(segment);
            geometry.Figures.Add(figure);
        }

        geometry.Freeze();
        return geometry;
    }

    private static BoundingBox2D Union(BoundingBox2D a, BoundingBox2D b)
    {
        if (a.IsEmpty) return b;
        if (b.IsEmpty) return a;
        return new BoundingBox2D(
            Math.Min(a.MinX, b.MinX), Math.Min(a.MinY, b.MinY),
            Math.Max(a.MaxX, b.MaxX), Math.Max(a.MaxY, b.MaxY));
    }

    public void Dispose()
    {
        _recomputeCancellation?.Cancel();
        _recomputeCancellation?.Dispose();
        _recomputeCancellation = null;
    }
}
