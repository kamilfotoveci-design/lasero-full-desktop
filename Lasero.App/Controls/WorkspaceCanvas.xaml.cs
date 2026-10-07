using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Lasero.Core.GCode;

namespace Lasero.App.Controls;

/// <summary>
/// 2D preview of a loaded job's toolpath plus the live machine position
/// marker. Plain WPF Canvas/Shape rendering — this is inherently 2D (line
/// segments + one marker), so retained-mode vector drawing is simple, fast
/// enough for realistic job sizes, and needs no extra rendering dependency.
/// </summary>
public partial class WorkspaceCanvas : UserControl
{
    private const double MarginPx = 20;
    private const int CompletedChunkSize = 512;

    private double _scale = 1;
    private double _offsetXMm;
    private double _offsetYMm;
    private Ellipse? _marker;
    private readonly List<Path?> _completedChunkPaths = [];
    private int _redrawVersion;
    private int _documentLayerInsertIndex;
    private CancellationTokenSource? _layerBuildCancellation;
    private Path? _partialCompletedPath;
    private int _visibleCompletedChunks;
    private int _completedSegmentCount;
    private static readonly Brush CompletedStroke = CreateFrozenBrush(Color.FromRgb(0x1D, 0x1D, 0x1F));
    private static readonly Brush BurnStroke = CreateFrozenBrush(Color.FromArgb(0xB0, 0xE5, 0x30, 0x2B));
    private static readonly Brush TravelStroke = CreateFrozenBrush(Color.FromArgb(0x70, 0x8E, 0x8E, 0x93));

    public static readonly DependencyProperty DocumentProperty = DependencyProperty.Register(
        nameof(Document), typeof(GCodeDocument), typeof(WorkspaceCanvas),
        new PropertyMetadata(null, (d, _) => ((WorkspaceCanvas)d).Redraw()));

    public GCodeDocument? Document
    {
        get => (GCodeDocument?)GetValue(DocumentProperty);
        set => SetValue(DocumentProperty, value);
    }

    public static readonly DependencyProperty MachineXProperty = DependencyProperty.Register(
        nameof(MachineX), typeof(double), typeof(WorkspaceCanvas),
        new PropertyMetadata(0.0, (d, _) => ((WorkspaceCanvas)d).UpdateMarker()));

    public double MachineX
    {
        get => (double)GetValue(MachineXProperty);
        set => SetValue(MachineXProperty, value);
    }

    public static readonly DependencyProperty MachineYProperty = DependencyProperty.Register(
        nameof(MachineY), typeof(double), typeof(WorkspaceCanvas),
        new PropertyMetadata(0.0, (d, _) => ((WorkspaceCanvas)d).UpdateMarker()));

    public double MachineY
    {
        get => (double)GetValue(MachineYProperty);
        set => SetValue(MachineYProperty, value);
    }

    public static readonly DependencyProperty SimulationXProperty = DependencyProperty.Register(
        nameof(SimulationX), typeof(double), typeof(WorkspaceCanvas),
        new PropertyMetadata(0.0, (d, _) => ((WorkspaceCanvas)d).UpdateMarker()));

    public double SimulationX
    {
        get => (double)GetValue(SimulationXProperty);
        set => SetValue(SimulationXProperty, value);
    }

    public static readonly DependencyProperty SimulationYProperty = DependencyProperty.Register(
        nameof(SimulationY), typeof(double), typeof(WorkspaceCanvas),
        new PropertyMetadata(0.0, (d, _) => ((WorkspaceCanvas)d).UpdateMarker()));

    public double SimulationY
    {
        get => (double)GetValue(SimulationYProperty);
        set => SetValue(SimulationYProperty, value);
    }

    public static readonly DependencyProperty UseSimulationPositionProperty = DependencyProperty.Register(
        nameof(UseSimulationPosition), typeof(bool), typeof(WorkspaceCanvas),
        new PropertyMetadata(false, (d, _) => ((WorkspaceCanvas)d).UpdateMarker()));

    public bool UseSimulationPosition
    {
        get => (bool)GetValue(UseSimulationPositionProperty);
        set => SetValue(UseSimulationPositionProperty, value);
    }

    public static readonly DependencyProperty ProgressPercentProperty = DependencyProperty.Register(
        nameof(ProgressPercent), typeof(double), typeof(WorkspaceCanvas),
        new PropertyMetadata(0.0, (d, _) => ((WorkspaceCanvas)d).UpdateProgress()));

    public double ProgressPercent
    {
        get => (double)GetValue(ProgressPercentProperty);
        set => SetValue(ProgressPercentProperty, value);
    }

    // Machine bed size in mm — same source and same "0 means unknown, skip the bed rect" convention
    // as SceneCanvas.WorkAreaWidthMm/HeightMm; see that control for the full explanation.
    public static readonly DependencyProperty WorkAreaWidthMmProperty = DependencyProperty.Register(
        nameof(WorkAreaWidthMm), typeof(double), typeof(WorkspaceCanvas),
        new PropertyMetadata(0.0, (d, _) => ((WorkspaceCanvas)d).Redraw()));

    public double WorkAreaWidthMm
    {
        get => (double)GetValue(WorkAreaWidthMmProperty);
        set => SetValue(WorkAreaWidthMmProperty, value);
    }

    public static readonly DependencyProperty WorkAreaHeightMmProperty = DependencyProperty.Register(
        nameof(WorkAreaHeightMm), typeof(double), typeof(WorkspaceCanvas),
        new PropertyMetadata(0.0, (d, _) => ((WorkspaceCanvas)d).Redraw()));

    public double WorkAreaHeightMm
    {
        get => (double)GetValue(WorkAreaHeightMmProperty);
        set => SetValue(WorkAreaHeightMmProperty, value);
    }

    /// <summary>Whether a double-click would currently be accepted as a jog request — purely a cursor
    /// hint (crosshair vs arrow); JogViewModel.JogToPointAsync re-checks connection state itself.</summary>
    public static readonly DependencyProperty JogEnabledProperty = DependencyProperty.Register(
        nameof(JogEnabled), typeof(bool), typeof(WorkspaceCanvas),
        new PropertyMetadata(false, (d, e) => ((WorkspaceCanvas)d).DrawCanvas.Cursor = (bool)e.NewValue ? Cursors.Cross : Cursors.Arrow));

    public bool JogEnabled
    {
        get => (bool)GetValue(JogEnabledProperty);
        set => SetValue(JogEnabledProperty, value);
    }

    /// <summary>Raised on a double-click inside the bed area, with the clicked point already converted
    /// to work-coordinate mm and clamped into [0, WorkAreaWidthMm] x [0, WorkAreaHeightMm].</summary>
    public event Action<double, double>? JogToRequested;

    public WorkspaceCanvas()
    {
        InitializeComponent();
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e) => Redraw();

    private void OnDrawCanvasMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount != 2 || WorkAreaWidthMm <= 0 || WorkAreaHeightMm <= 0) return;

        var point = e.GetPosition(DrawCanvas);
        var worldX = Math.Clamp(ToWorldX(point.X), 0, WorkAreaWidthMm);
        var worldY = Math.Clamp(ToWorldY(point.Y), 0, WorkAreaHeightMm);
        JogToRequested?.Invoke(worldX, worldY);
    }

    private void Redraw()
    {
        _layerBuildCancellation?.Cancel();
        _layerBuildCancellation = null;
        _redrawVersion++;
        DrawCanvas.Children.Clear();
        TopRuler.Children.Clear();
        LeftRuler.Children.Clear();
        _marker = null;
        _completedChunkPaths.Clear();
        _partialCompletedPath = null;
        _visibleCompletedChunks = 0;
        _completedSegmentCount = 0;

        if (ActualWidth <= 0 || ActualHeight <= 0)
            return;

        var box = Document?.BoundingBox ?? BoundingBox2D.Empty;
        if (WorkAreaWidthMm > 0 && WorkAreaHeightMm > 0)
            box = box.IsEmpty ? new BoundingBox2D(0, 0, WorkAreaWidthMm, WorkAreaHeightMm) : box.Include(0, 0).Include(WorkAreaWidthMm, WorkAreaHeightMm);

        if (box.IsEmpty)
        {
            // Empty state: fixed pixel grid centered on the control, so it never
            // reads as a dead black rectangle before a job is loaded.
            _scale = 4; // 4px/mm — arbitrary but gives a sensible-looking grid
            _offsetXMm = -ActualWidth / 2 / _scale;
            _offsetYMm = -ActualHeight / 2 / _scale;
        }
        else
        {
            var availableW = Math.Max(1, ActualWidth - 2 * MarginPx);
            var availableH = Math.Max(1, ActualHeight - 2 * MarginPx);
            var scaleX = box.Width < 1e-6 ? availableW : availableW / box.Width;
            var scaleY = box.Height < 1e-6 ? availableH : availableH / box.Height;
            _scale = Math.Min(scaleX, scaleY);
            _offsetXMm = box.MinX - MarginPx / _scale;
            _offsetYMm = box.MinY - MarginPx / _scale;
        }

        DrawBed();
        DrawGrid();

        if (Document is not null)
        {
            // The three toolpath layers can hold hundreds of thousands of segments. They are built on a
            // worker thread and added when ready (see BeginBuildToolpathLayers); the completed-so-far
            // chunks are only built when the simulation reaches them.
            _documentLayerInsertIndex = DrawCanvas.Children.Count;
            BeginBuildToolpathLayers(Document);
            for (var start = 0; start < Document.Segments.Count; start += CompletedChunkSize)
                _completedChunkPaths.Add(null);

            _partialCompletedPath = new Path
            {
                Stroke = CompletedStroke,
                StrokeThickness = 1.8,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                IsHitTestVisible = false,
            };
            DrawCanvas.Children.Add(_partialCompletedPath);
            UpdateProgress();
        }

        AddMarker();
        UpdateMarker();
    }

    private void UpdateProgress()
    {
        if (Document is null || _partialCompletedPath is null) return;
        var target = (int)Math.Floor(Document.Segments.Count * Math.Clamp(ProgressPercent, 0, 100) / 100);
        target = Math.Clamp(target, 0, Document.Segments.Count);
        if (target == _completedSegmentCount) return;

        var fullChunks = target / CompletedChunkSize;
        for (var index = Math.Min(_visibleCompletedChunks, fullChunks); index < fullChunks && index < _completedChunkPaths.Count; index++)
            EnsureChunkPath(index).Visibility = Visibility.Visible;
        for (var index = fullChunks; index < _visibleCompletedChunks && index < _completedChunkPaths.Count; index++)
            if (_completedChunkPaths[index] is { } hidden) hidden.Visibility = Visibility.Collapsed;
        _visibleCompletedChunks = fullChunks;

        var partialCount = target - fullChunks * CompletedChunkSize;
        _partialCompletedPath.Data = partialCount > 0
            ? BuildSegmentGeometry(fullChunks * CompletedChunkSize, partialCount)
            : null;
        _completedSegmentCount = target;
    }

    private Path EnsureChunkPath(int index)
    {
        if (_completedChunkPaths[index] is { } existing) return existing;
        var geometry = BuildSegmentGeometry(index * CompletedChunkSize, Math.Min(CompletedChunkSize, Document!.Segments.Count - index * CompletedChunkSize));
        var path = new Path
        {
            Data = geometry,
            Stroke = CompletedStroke,
            StrokeThickness = 1.8,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            IsHitTestVisible = false,
            Visibility = Visibility.Collapsed,
        };
        // Keep the partial (in-progress) path on top of the finished chunks.
        var partialIndex = _partialCompletedPath is null ? -1 : DrawCanvas.Children.IndexOf(_partialCompletedPath);
        if (partialIndex >= 0) DrawCanvas.Children.Insert(partialIndex, path);
        else DrawCanvas.Children.Add(path);
        _completedChunkPaths[index] = path;
        return path;
    }

    /// <summary>
    /// Builds the burn, travel and rapid layers in one pass on a worker thread and adds them to the
    /// canvas when done. Consecutive segments that share an end point become one polyline, so a job of
    /// several hundred thousand tiny segments is a few thousand figures rather than several hundred
    /// thousand. A newer redraw cancels and supersedes a build still in flight.
    /// </summary>
    private void BeginBuildToolpathLayers(GCodeDocument document)
    {
        var version = _redrawVersion;
        var cancellation = _layerBuildCancellation = new CancellationTokenSource();
        var token = cancellation.Token;
        var scale = _scale;
        var offsetX = _offsetXMm;
        var offsetY = _offsetYMm;
        var height = ActualHeight;
        var dispatcher = Dispatcher;

        _ = Task.Run(() => BuildToolpathLayers(document.Segments, scale, offsetX, offsetY, height, token), token)
            .ContinueWith(task =>
            {
                if (!task.IsCompletedSuccessfully) return;
                dispatcher.BeginInvoke(() =>
                {
                    if (version != _redrawVersion) return;
                    var (burn, travel, rapid) = task.Result;
                    var index = Math.Min(_documentLayerInsertIndex, DrawCanvas.Children.Count);
                    foreach (var (geometry, stroke, thickness, dashes) in new[]
                             {
                                 (burn, BurnStroke, 1.6, (DoubleCollection?)null),
                                 (travel, TravelStroke, 0.75, null),
                                 (rapid, TravelStroke, 0.75, new DoubleCollection { 4, 2 }),
                             })
                    {
                        if (geometry is null) continue;
                        DrawCanvas.Children.Insert(index++, new Path
                        {
                            Data = geometry,
                            Stroke = stroke,
                            StrokeThickness = thickness,
                            StrokeDashArray = dashes,
                            StrokeStartLineCap = PenLineCap.Round,
                            StrokeEndLineCap = PenLineCap.Round,
                            StrokeLineJoin = PenLineJoin.Round,
                            IsHitTestVisible = false,
                        });
                    }
                });
            }, TaskScheduler.Default);
    }

    internal static (StreamGeometry? Burn, StreamGeometry? Travel, StreamGeometry? Rapid) BuildToolpathLayers(
        IReadOnlyList<GCodeSegment> segments, double scale, double offsetXMm, double offsetYMm, double height,
        CancellationToken cancellationToken)
    {
        var geometries = new[] { new StreamGeometry(), new StreamGeometry(), new StreamGeometry() };
        var contexts = geometries.Select(geometry => geometry.Open()).ToArray();
        var lastEnd = new Point?[3];
        var any = new bool[3];

        for (var index = 0; index < segments.Count; index++)
        {
            if ((index & 0xFFF) == 0) cancellationToken.ThrowIfCancellationRequested();
            var segment = segments[index];
            var layer = segment.LaserOn ? 0 : segment.IsRapid ? 2 : 1;
            var start = new Point(MarginPx + (segment.Start.X - offsetXMm) * scale, height - MarginPx - (segment.Start.Y - offsetYMm) * scale);
            var end = new Point(MarginPx + (segment.End.X - offsetXMm) * scale, height - MarginPx - (segment.End.Y - offsetYMm) * scale);

            // Rapids are dashed, and a dash pattern restarts per figure: keep each move its own figure.
            var continues = layer != 2 && lastEnd[layer] is { } previous && previous == start;
            if (!continues) contexts[layer].BeginFigure(start, isFilled: false, isClosed: false);
            contexts[layer].LineTo(end, isStroked: true, isSmoothJoin: false);
            lastEnd[layer] = end;
            any[layer] = true;
        }

        foreach (var context in contexts) context.Close();
        StreamGeometry? Finish(int layer)
        {
            if (!any[layer]) return null;
            geometries[layer].Freeze();
            return geometries[layer];
        }

        return (Finish(0), Finish(1), Finish(2));
    }

    private StreamGeometry? BuildSegmentGeometry(
        int start,
        int count,
        Func<GCodeSegment, bool>? predicate = null)
    {
        if (Document is null || count <= 0) return null;
        var geometry = new StreamGeometry();
        var hasSegments = false;
        using (var context = geometry.Open())
        {
            var end = Math.Min(start + count, Document.Segments.Count);
            for (var index = Math.Max(0, start); index < end; index++)
            {
                var segment = Document.Segments[index];
                if (predicate is not null && !predicate(segment)) continue;
                hasSegments = true;
                context.BeginFigure(new Point(ToCanvasX(segment.Start.X), ToCanvasY(segment.Start.Y)), false, false);
                context.LineTo(new Point(ToCanvasX(segment.End.X), ToCanvasY(segment.End.Y)), true, false);
            }
        }

        if (!hasSegments) return null;
        geometry.Freeze();
        return geometry;
    }

    private void DrawBed()
    {
        if (WorkAreaWidthMm <= 0 || WorkAreaHeightMm <= 0) return;

        var rect = new Rectangle
        {
            Width = Math.Max(0, WorkAreaWidthMm * _scale),
            Height = Math.Max(0, WorkAreaHeightMm * _scale),
            Fill = (Brush)FindResource("Brush.Panel"),
            Stroke = (Brush)FindResource("Brush.PanelBorder"),
            StrokeThickness = 1,
        };
        Canvas.SetLeft(rect, ToCanvasX(0));
        Canvas.SetTop(rect, ToCanvasY(WorkAreaHeightMm));
        DrawCanvas.Children.Add(rect);
    }

    private void DrawGrid()
    {
        var step = RulerMath.PickStep(_scale);

        var gridBrush = new SolidColorBrush(Color.FromArgb(0x14, 0x1D, 0x1D, 0x1F));
        var axisBrush = new SolidColorBrush(Color.FromArgb(0x40, 0x1D, 0x1D, 0x1F));

        var firstX = RulerMath.FirstTick(_offsetXMm, step);
        for (var x = firstX; x < _offsetXMm + ActualWidth / _scale; x += step)
        {
            var px = ToCanvasX(x);
            DrawCanvas.Children.Add(new Line
            {
                X1 = px, X2 = px, Y1 = 0, Y2 = ActualHeight,
                Stroke = Math.Abs(x) < 1e-6 ? axisBrush : gridBrush,
                StrokeThickness = Math.Abs(x) < 1e-6 ? 1.2 : 1,
            });
        }

        var firstY = RulerMath.FirstTick(_offsetYMm, step);
        for (var y = firstY; y < _offsetYMm + ActualHeight / _scale; y += step)
        {
            var py = ToCanvasY(y);
            DrawCanvas.Children.Add(new Line
            {
                X1 = 0, X2 = ActualWidth, Y1 = py, Y2 = py,
                Stroke = Math.Abs(y) < 1e-6 ? axisBrush : gridBrush,
                StrokeThickness = Math.Abs(y) < 1e-6 ? 1.2 : 1,
            });
        }

        DrawRulers(step, firstX, firstY);
    }

    private void DrawRulers(double step, double firstX, double firstY)
    {
        var tickBrush = (Brush)FindResource("Brush.PanelBorder");
        var labelBrush = (Brush)FindResource("Brush.TextSecondary");
        // FontFamily is inherited from the Window, but these labels aren't in the
        // visual tree yet when we Measure() them below — an unparented element has
        // no inheritance context, so Measure would silently fall back to the WPF
        // default font instead of Inter. That understates the real rendered width
        // enough that the right-edge fit check below passes for labels that then
        // get clipped by TopRuler's own ClipToBounds once actually added. Setting
        // the same font explicitly keeps the measurement honest.
        var labelFont = (FontFamily)FindResource("Font.Numeric");

        for (var x = firstX; x < _offsetXMm + ActualWidth / _scale; x += step)
        {
            var px = ToCanvasX(x);
            TopRuler.Children.Add(new Line { X1 = px, X2 = px, Y1 = TopRuler.ActualHeight - 6, Y2 = TopRuler.ActualHeight, Stroke = tickBrush, StrokeThickness = 1 });

            var label = new TextBlock { Text = FormatTickMm(x), FontSize = 9, FontFamily = labelFont, Foreground = labelBrush };
            label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            if (px + 3 + label.DesiredSize.Width <= TopRuler.ActualWidth)
            {
                Canvas.SetLeft(label, px + 3);
                Canvas.SetTop(label, 2);
                TopRuler.Children.Add(label);
            }
        }

        for (var y = firstY; y < _offsetYMm + ActualHeight / _scale; y += step)
        {
            var py = ToCanvasY(y);
            LeftRuler.Children.Add(new Line { X1 = LeftRuler.ActualWidth - 6, X2 = LeftRuler.ActualWidth, Y1 = py, Y2 = py, Stroke = tickBrush, StrokeThickness = 1 });

            var label = new TextBlock { Text = FormatTickMm(y), FontSize = 9, FontFamily = labelFont, Foreground = labelBrush };
            label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            if (!RulerLabelLayout.FitsVertically(py, label.DesiredSize.Height, LeftRuler.ActualHeight)) continue;
            Canvas.SetLeft(label, Math.Max(1, LeftRuler.ActualWidth - 8 - label.DesiredSize.Width));
            Canvas.SetTop(label, py - label.DesiredSize.Height / 2);
            LeftRuler.Children.Add(label);
        }
    }

    private static string FormatTickMm(double valueMm) =>
        Math.Abs(valueMm % 1) < 1e-6 ? ((int)Math.Round(valueMm)).ToString() : valueMm.ToString("0.#");

    private Ellipse? _markerGlow;

    private void AddMarker()
    {
        _markerGlow = new Ellipse
        {
            Width = 22,
            Height = 22,
            // A hairline ring, not a soft coloured glow.
            Stroke = new SolidColorBrush(Color.FromArgb(0x60, 0xE5, 0x30, 0x2B)),
            StrokeThickness = 1,
        };
        _marker = new Ellipse
        {
            Width = 10,
            Height = 10,
            // The laser position is a beam marker, so it is the signal red with a white keyline.
            Fill = new SolidColorBrush(Color.FromRgb(0xE5, 0x30, 0x2B)),
            Stroke = new SolidColorBrush(Colors.White),
            StrokeThickness = 1.5,
        };
        DrawCanvas.Children.Add(_markerGlow);
        DrawCanvas.Children.Add(_marker);
    }

    private void UpdateMarker()
    {
        if (_marker is null || _markerGlow is null) return;
        var x = UseSimulationPosition ? SimulationX : MachineX;
        var y = UseSimulationPosition ? SimulationY : MachineY;
        Canvas.SetLeft(_marker, ToCanvasX(x) - _marker.Width / 2);
        Canvas.SetTop(_marker, ToCanvasY(y) - _marker.Height / 2);
        Canvas.SetLeft(_markerGlow, ToCanvasX(x) - _markerGlow.Width / 2);
        Canvas.SetTop(_markerGlow, ToCanvasY(y) - _markerGlow.Height / 2);
    }

    private double ToCanvasX(double workX) => MarginPx + (workX - _offsetXMm) * _scale;

    // Canvas Y grows downward; work-coordinate Y grows upward — flip it.
    private double ToCanvasY(double workY) =>
        ActualHeight - MarginPx - (workY - _offsetYMm) * _scale;

    private double ToWorldX(double canvasX) => (canvasX - MarginPx) / _scale + _offsetXMm;

    private double ToWorldY(double canvasY) =>
        (ActualHeight - MarginPx - canvasY) / _scale + _offsetYMm;

    private static Brush CreateFrozenBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
