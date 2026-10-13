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
/// construction — the dialog never re-reads live selection while it is open.
///
/// The whole selection is offset as ONE region (see VectorOffsetPlanner.ComputeMergedOffset): the
/// result is one merged outline per side, not one ring around every selected contour. Nothing here
/// touches the scene; the window's OK hands Results to SceneViewModel.ApplyOffsetResults, which is
/// the single undo step.</summary>
public partial class OffsetPathViewModel : ObservableObject, IDisposable
{
    /// <summary>Margin added around the selection's own bounds so a large outward offset is not
    /// clipped by the preview canvas before the operator even sees it.</summary>
    private const double PreviewMarginMm = 20;

    /// <summary>Upper bound of the typed distance. Anything larger is clamped, so a stray extra digit
    /// cannot ask the geometry engine for a ring the size of the building.</summary>
    public const double MaxDistanceMm = 1000;

    private readonly IReadOnlyList<SceneObject> _sources;
    private readonly IVectorOffsetService _service;
    private readonly IVectorBooleanService _booleans;
    private readonly BoundingBox2D _sourceBounds;
    private CancellationTokenSource? _recomputeCancellation;
    private bool _initialized;
    private IReadOnlyList<VectorPath> _results = [];
    private IReadOnlyList<VectorOffsetResultKind> _resultKinds = [];

    /// <summary>Offset distance in mm. The side comes from Direction, so only the magnitude matters.</summary>
    [ObservableProperty] private double _distanceMm = VectorOffsetOptions.DefaultDistanceMm;

    /// <summary>Ven (default), Dovnitř or Oba.</summary>
    [ObservableProperty] private VectorOffsetDirection _direction = VectorOffsetDirection.Outward;

    /// <summary>Oblý (default), Kosý or Roh.</summary>
    [ObservableProperty] private VectorJoinType _joinType = VectorJoinType.Round;

    /// <summary>Pouze vnější tvary: only the outer boundaries are offset; counters and shapes inside
    /// them are dropped. Default on.</summary>
    [ObservableProperty] private bool _onlyOuterShapes = true;

    /// <summary>Vybrat výsledné objekty: select the new objects after OK. Default off.</summary>
    [ObservableProperty] private bool _selectResults;

    /// <summary>Odstranit původní objekty: remove the source objects after OK. Default off (the
    /// inverse of the stored OffsetKeepOriginal preference).</summary>
    [ObservableProperty] private bool _deleteOriginals;

    /// <summary>Keep the result derived from its original source objects after OK.</summary>
    [ObservableProperty] private bool _linkedOffset;

    /// <summary>Optimalizovat výsledky: merge repeated and near-collinear points. Default on.</summary>
    [ObservableProperty] private bool _optimizeResults = true;

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
    /// to "zoom") every time the distance changes.</summary>
    public double PreviewWidthMm { get; }
    public double PreviewHeightMm { get; }

    /// <summary>True once the current settings produced at least one object — gates the dialog's OK
    /// button, matching BitmapTraceViewModel.HasResult.</summary>
    public bool HasResult => _results.Count > 0 && !IsComputing;

    /// <summary>The objects OK will create (one per side, plus one for buffered open paths). Handed
    /// to SceneViewModel.ApplyOffsetResults.</summary>
    public IReadOnlyList<VectorPath> Results => _results;
    public IReadOnlyList<VectorOffsetResultKind> ResultKinds => _resultKinds;

    /// <param name="keepOriginal">The stored OffsetKeepOriginal preference; Odstranit původní objekty
    /// starts as its inverse, so an install that never changed it starts with the originals kept.</param>
    public OffsetPathViewModel(
        IReadOnlyList<SceneObject> sources,
        IVectorOffsetService? service = null,
        bool keepOriginal = true,
        IVectorBooleanService? booleans = null,
        VectorOffsetDirection? initialDirection = null,
        VectorOffsetOptions? initialOptions = null,
        bool initiallyLinked = false)
    {
        ArgumentNullException.ThrowIfNull(sources);
        if (sources.Count == 0) throw new ArgumentException("Offset needs at least one selected object.", nameof(sources));

        _deleteOriginals = initiallyLinked ? false : !keepOriginal;
        _linkedOffset = initiallyLinked;
        _selectResults = initiallyLinked;
        _distanceMm = initialOptions?.DistanceMm ?? VectorOffsetOptions.DefaultDistanceMm;
        _direction = initialDirection ?? initialOptions?.Direction ?? VectorOffsetDirection.Outward;
        _joinType = initialOptions?.JoinType ?? VectorJoinType.Round;
        _onlyOuterShapes = initialOptions?.OnlyOuterShapes ?? true;
        _optimizeResults = initialOptions?.Optimize ?? true;
        _sources = sources.ToList();
        _service = service ?? Clipper2VectorOffsetService.Default;
        _booleans = booleans ?? Clipper2VectorBooleanService.Default;

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

    partial void OnDistanceMmChanged(double value) => QueueRecompute();
    partial void OnDirectionChanged(VectorOffsetDirection value) => QueueRecompute();
    partial void OnJoinTypeChanged(VectorJoinType value) => QueueRecompute();
    partial void OnOnlyOuterShapesChanged(bool value) => QueueRecompute();
    partial void OnOptimizeResultsChanged(bool value) => QueueRecompute();
    partial void OnLinkedOffsetChanged(bool value)
    {
        if (value) DeleteOriginals = false;
    }
    partial void OnDeleteOriginalsChanged(bool value)
    {
        if (value) LinkedOffset = false;
    }
    partial void OnIsComputingChanged(bool value) => OnPropertyChanged(nameof(HasResult));

    /// <summary>The settings as the geometry engine takes them, distance clamped to a usable range.</summary>
    public VectorOffsetOptions CurrentOptions => new()
    {
        DistanceMm = double.IsFinite(DistanceMm) ? Math.Min(Math.Abs(DistanceMm), MaxDistanceMm) : 0,
        Direction = Direction,
        JoinType = JoinType,
        OnlyOuterShapes = OnlyOuterShapes,
        Optimize = OptimizeResults,
    };

    private void QueueRecompute(bool immediate = false)
    {
        if (!_initialized) return;
        _recomputeCancellation?.Cancel();
        _recomputeCancellation?.Dispose();
        _recomputeCancellation = new CancellationTokenSource();
        _results = [];
        _resultKinds = [];
        PreviewGeometry = null;
        OnPropertyChanged(nameof(HasResult));
        _ = RecomputeAsync(_recomputeCancellation.Token, immediate);
    }

    private async Task RecomputeAsync(CancellationToken cancellationToken, bool immediate)
    {
        try
        {
            var options = CurrentOptions;
            if (options.DistanceMm < 0.001)
            {
                StatusMessage = "Vzdálenost musí být větší než nula.";
                return;
            }

            IsComputing = true;
            StatusMessage = "Počítám offset…";
            if (!immediate) await Task.Delay(180, cancellationToken);

            var parts = await Task.Run(
                () => VectorOffsetPlanner.ComputeMergedOffsetParts(_sources, _booleans, _service, options, cancellationToken),
                cancellationToken);

            cancellationToken.ThrowIfCancellationRequested();
            _results = parts.Select(part => part.Path).ToList();
            _resultKinds = parts.Select(part => part.Kind).ToList();
            PreviewGeometry = BuildGeometry(
                _results.SelectMany(path => path.Subpaths
                    .Select(subpath => (Points: (IReadOnlyList<Position>)subpath.Nodes.Select(node => node.Anchor).ToList(), subpath.IsClosed))),
                _sourceBounds);
            var nodeCount = _results.Sum(path => path.Subpaths.Sum(subpath => subpath.Nodes.Count));
            StatusMessage = _results.Count == 0
                ? "Posunutí o tuto vzdálenost nevytvoří žádný tvar."
                : $"Náhled je připravený. Uzlů ve výsledku: {nodeCount}.";
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            _results = [];
            _resultKinds = [];
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
