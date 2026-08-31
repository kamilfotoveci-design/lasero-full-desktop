using Serilog;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Lasero.App.ViewModels;
using Lasero.Core.GCode;
using Lasero.Core.Grbl;
using Lasero.Core.Import;
using Lasero.Core.Layers;
using Lasero.Core.Scene;
using Lasero.Core.Scene.Commands;

namespace Lasero.App.Controls;

/// <summary>
/// The interactive design canvas — select/move/rotate/resize/duplicate/delete SceneObjects, multi-select
/// via rubber-band, undo/redo, pan/zoom. Deliberately separate from WorkspaceCanvas (the read-only
/// toolpath/motion preview): different concerns, same as LightBurn splitting Design view from a literal
/// motion preview. Coordinate math mirrors WorkspaceCanvas's ToCanvasX/Y (mm <-> screen px, Y flipped)
/// with the missing inverse (ToWorldX/Y) added, plus pan/zoom state WorkspaceCanvas never needed since
/// it always auto-fits.
/// </summary>
public partial class SceneCanvas : UserControl
{
    private const double MarginPx = 20;
    private const double HandleSizePx = 8;
    // Invisible grab area around each grip. Interaction geometry only — it never reaches the scene
    // model or the toolpath.
    private const double HandleHitSizePx = 20;
    private const double RotateHandleOffsetPx = 26;
    private const double MinScale = 0.05;
    private const double MaxScale = 200;

    /// <summary>Pixels per millimetre at rest. This is what the zoom readout calls 100%: the scale
    /// the canvas opens at and returns to on an empty scene, not a physical 1:1, which would depend
    /// on the monitor.</summary>
    private const double DefaultScale = 4;

    private static readonly ResizeHandle[] AllResizeHandles = Enum.GetValues<ResizeHandle>();
    private Brush SelectionBrush => (Brush)FindResource("Brush.Accent");
    private Brush SelectionHandleFill => (Brush)FindResource("Brush.OnAccent");

    private enum DragMode { None, Select, Move, Resize, Rotate, Pan, Draw }

    private double _scale = DefaultScale;
    private double _offsetXMm;
    private double _offsetYMm;

    // True until the user manually pans/zooms — while true, any size change, work-area update, or
    // scene-content change keeps re-fitting (framing the bed + content) instead of preserving the
    // current view. This used to be a one-shot "_hasFitOnce" latch gated on Objects.Count > 0, which
    // meant a brand-new empty project (no objects yet, and WorkAreaWidthMm/HeightMm arriving from a
    // data binding slightly after the canvas's first SizeChanged) never framed the bed at all — it got
    // stuck on the arbitrary 4px/mm empty-state fallback permanently, since nothing ever re-triggered
    // a real fit afterwards. Re-fitting on every relevant change until the user actually touches
    // pan/zoom fixes that without giving up their view once they've started working.
    private bool _autoFit = true;

    private readonly Dictionary<SceneObject, List<Path>> _objectVisuals = new();
    private readonly Dictionary<SceneObject, Image> _rasterImageVisuals = new();
    private readonly List<Line> _gridLines = new();
    private readonly List<FrameworkElement> _selectionVisuals = new();
    private readonly List<UIElement> _topRulerVisuals = new();
    private readonly List<UIElement> _leftRulerVisuals = new();
    private Rectangle? _bedRectVisual;
    private Rectangle? _rubberBandVisual;
    private Path? _toolPreviewVisual;

    private DragMode _dragMode = DragMode.None;
    private Point _dragStartScreen;
    private Position _dragStartWorld;
    private readonly Dictionary<SceneObject, ObjectTransform> _dragStartTransforms = new();

    private SceneObject? _activeSingleObject;
    private ResizeHandle _activeResizeHandle;
    private ObjectTransform _resizeStartTransform;
    private ObjectTransform _rotateStartTransform;
    private double _rotateStartAngleDeg;
    private double _panStartOffsetX;
    private double _panStartOffsetY;
    private bool _isSpacePressed;
    private DesignerTool _drawingTool;
    private Position _drawStartWorld;

    public event Action<Position>? TextPlacementRequested;

    public static readonly DependencyProperty ViewModelProperty = DependencyProperty.Register(
        nameof(ViewModel), typeof(SceneViewModel), typeof(SceneCanvas),
        new PropertyMetadata(null, OnViewModelChanged));

    public SceneViewModel? ViewModel
    {
        get => (SceneViewModel?)GetValue(ViewModelProperty);
        set => SetValue(ViewModelProperty, value);
    }

    // Machine bed size in mm — bound from MainWindow to Settings.Machine.WorkAreaWidthMm/HeightMm,
    // which is itself auto-populated from the connected GRBL controller's $130/$131 on connect (see
    // ConnectionViewModel.IdentifyDeviceAsync). Zero/unset means "no known bed" — FitToView and the
    // bed-rectangle draw both skip it in that case rather than drawing a bogus 0x0 rectangle.
    public static readonly DependencyProperty WorkAreaWidthMmProperty = DependencyProperty.Register(
        nameof(WorkAreaWidthMm), typeof(double), typeof(SceneCanvas),
        new PropertyMetadata(0.0, OnWorkAreaChanged));

    public double WorkAreaWidthMm
    {
        get => (double)GetValue(WorkAreaWidthMmProperty);
        set => SetValue(WorkAreaWidthMmProperty, value);
    }

    public static readonly DependencyProperty WorkAreaHeightMmProperty = DependencyProperty.Register(
        nameof(WorkAreaHeightMm), typeof(double), typeof(SceneCanvas),
        new PropertyMetadata(0.0, OnWorkAreaChanged));

    public double WorkAreaHeightMm
    {
        get => (double)GetValue(WorkAreaHeightMmProperty);
        set => SetValue(WorkAreaHeightMmProperty, value);
    }

    private static void OnWorkAreaChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var canvas = (SceneCanvas)d;
        if (canvas._autoFit) canvas.FitToView();
        else canvas.RepositionAll();
    }

    public SceneCanvas() => InitializeComponent();

    private static void OnViewModelChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var canvas = (SceneCanvas)d;
        if (e.OldValue is SceneViewModel oldVm)
        {
            oldVm.Objects.CollectionChanged -= canvas.OnObjectsChanged;
            oldVm.SelectedObjects.CollectionChanged -= canvas.OnSelectionChanged;
            oldVm.Changed -= canvas.OnViewModelContentChanged;
            oldVm.PropertyChanged -= canvas.OnViewModelPropertyChanged;
        }
        if (e.NewValue is SceneViewModel newVm)
        {
            newVm.Objects.CollectionChanged += canvas.OnObjectsChanged;
            newVm.SelectedObjects.CollectionChanged += canvas.OnSelectionChanged;
            newVm.Changed += canvas.OnViewModelContentChanged;
            newVm.PropertyChanged += canvas.OnViewModelPropertyChanged;
        }
        canvas.RebuildAll();
        canvas.UpdateToolCursor();
    }

    private void OnObjectsChanged(object? sender, NotifyCollectionChangedEventArgs e) => RebuildAll();
    private void OnSelectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => RedrawSelectionOverlay();
    private void OnViewModelContentChanged() => RepositionAll();

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SceneViewModel.ActiveTool)) UpdateToolCursor();
    }

    private void UpdateToolCursor()
    {
        if (_isSpacePressed || _dragMode == DragMode.Pan)
        {
            Cursor = Cursors.ScrollAll;
            return;
        }

        Cursor = ViewModel?.ActiveTool switch
        {
            DesignerTool.Pan => Cursors.Hand,
            DesignerTool.Text => Cursors.IBeam,
            DesignerTool.Select or null => Cursors.Arrow,
            _ => Cursors.Cross,
        };
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_autoFit) FitToView();
        else RepositionAll();
    }

    // ------------------------------------------------------------------
    // Public API — used by MainWindow (a "Fit"/zoom toolbar) and by the
    // canvas's own 'F' keyboard shortcut.
    // ------------------------------------------------------------------

    public void FitToView()
    {
        var box = BoundingBox2D.Empty;
        if (WorkAreaWidthMm > 0 && WorkAreaHeightMm > 0)
            box = Union(box, new BoundingBox2D(0, 0, WorkAreaWidthMm, WorkAreaHeightMm));
        if (ViewModel is not null)
            foreach (var obj in ViewModel.Objects)
                box = Union(box, obj.WorldBounds());
        ApplyFit(box);
    }

    private static readonly DependencyPropertyKey ZoomPercentPropertyKey = DependencyProperty.RegisterReadOnly(
        nameof(ZoomPercent), typeof(int), typeof(SceneCanvas), new PropertyMetadata(100));

    /// <summary>Current zoom as a whole percentage of <see cref="DefaultScale"/>, for the toolbar
    /// readout. Read-only: the canvas owns the scale, and a settable property would let the chrome
    /// claim a zoom the view is not at.</summary>
    public static readonly DependencyProperty ZoomPercentProperty = ZoomPercentPropertyKey.DependencyProperty;

    public int ZoomPercent => (int)GetValue(ZoomPercentProperty);

    public void ZoomIn() => ZoomAroundCenter(1.25);
    public void ZoomOut() => ZoomAroundCenter(0.8);

    private void ZoomAroundCenter(double factor)
    {
        if (ActualWidth <= 0 || ActualHeight <= 0) return;
        _autoFit = false;
        var center = new Point(ActualWidth / 2, ActualHeight / 2);
        var worldBefore = new Position(ToWorldX(center.X), ToWorldY(center.Y), 0);
        _scale = Math.Clamp(_scale * factor, MinScale, MaxScale);
        _offsetXMm = worldBefore.X - (center.X - MarginPx) / _scale;
        _offsetYMm = worldBefore.Y - (ActualHeight - MarginPx - center.Y) / _scale;
        RepositionAll();
    }

    private void ApplyFit(BoundingBox2D box)
    {
        if (ActualWidth <= 0 || ActualHeight <= 0) return;

        if (box.IsEmpty)
        {
            _scale = DefaultScale;
            _offsetXMm = -ActualWidth / 2 / _scale;
            _offsetYMm = -ActualHeight / 2 / _scale;
        }
        else
        {
            var availableW = Math.Max(1, ActualWidth - 2 * MarginPx);
            var availableH = Math.Max(1, ActualHeight - 2 * MarginPx);
            var scaleX = box.Width < 1e-6 ? availableW : availableW / box.Width;
            var scaleY = box.Height < 1e-6 ? availableH : availableH / box.Height;
            _scale = Math.Clamp(Math.Min(scaleX, scaleY), MinScale, MaxScale);
            _offsetXMm = box.MinX - MarginPx / _scale;
            _offsetYMm = box.MinY - MarginPx / _scale;
        }

        RepositionAll();
    }

    private static BoundingBox2D Union(BoundingBox2D a, BoundingBox2D b) =>
        b.IsEmpty ? a : a.Include(b.MinX, b.MinY).Include(b.MaxX, b.MaxY);

    // ------------------------------------------------------------------
    // Coordinate mapping — mirrors WorkspaceCanvas's ToCanvasX/Y, plus the
    // world-space inverse it never needed (it's read-only).
    // ------------------------------------------------------------------

    private double ToCanvasX(double worldX) => MarginPx + (worldX - _offsetXMm) * _scale;
    private double ToCanvasY(double worldY) => ActualHeight - MarginPx - (worldY - _offsetYMm) * _scale;
    private double ToWorldX(double px) => _offsetXMm + (px - MarginPx) / _scale;
    private double ToWorldY(double py) => _offsetYMm + (ActualHeight - MarginPx - py) / _scale;

    // ------------------------------------------------------------------
    // Rendering — RebuildAll recreates every object's Path (and re-subscribes
    // PropertyChanged) after a structural scene change (add/remove/reorder);
    // RepositionAll only updates existing visuals' coordinates (pan/zoom/resize/fit).
    // Dragging a single object's Transform goes through neither — see
    // OnObjectPropertyChanged, which patches just that one object's geometry.
    // ------------------------------------------------------------------

    private void RebuildAll()
    {
        foreach (var obj in _objectVisuals.Keys.ToList())
            RemoveObjectVisuals(obj);
        DrawCanvas.Children.Clear();
        _gridLines.Clear();
        _selectionVisuals.Clear();
        _rubberBandVisual = null;
        _toolPreviewVisual = null;

        RedrawGrid();

        if (ViewModel is not null)
        {
            foreach (var obj in ViewModel.Objects)
                AddObjectVisuals(obj);

            if (_autoFit)
            {
                FitToView(); // calls RepositionAll, which redraws grid/geometry/selection at the new scale
                return;
            }
        }

        RedrawSelectionOverlay();
    }

    private void RepositionAll()
    {
        SetValue(ZoomPercentPropertyKey, (int)Math.Round(_scale / DefaultScale * 100));
        RedrawGrid();
        if (ViewModel is not null)
            foreach (var obj in ViewModel.Objects)
                UpdateObjectGeometry(obj);
        RedrawSelectionOverlay();
    }

    private void AddObjectVisuals(SceneObject obj)
    {
        // For a raster object, draw the actual source photo first so the outline (added below,
        // later in z-order) frames it rather than leaving the placement rectangle empty.
        if (obj.IsRaster)
        {
            var image = TryLoadRasterPreview(obj);
            if (image is not null)
            {
                DrawCanvas.Children.Add(image);
                _rasterImageVisuals[obj] = image;
            }
        }

        var paths = new List<Path>();
        foreach (var shape in obj.LocalShapes)
        {
            var path = new Path
            {
                Fill = null,
                Stroke = new SolidColorBrush(Color.FromRgb(shape.LayerColor.R, shape.LayerColor.G, shape.LayerColor.B)),
                StrokeThickness = 1.4,
                Tag = obj,
                Cursor = obj.IsLocked ? Cursors.Arrow : Cursors.SizeAll,
                ToolTip = $"{obj.Name} · kliknutím vybrat",
            };
            path.MouseLeftButtonDown += OnObjectMouseLeftButtonDown;
            path.MouseRightButtonDown += OnObjectMouseRightButtonDown;
            path.MouseEnter += OnObjectMouseEnter;
            path.MouseLeave += OnObjectMouseLeave;
            DrawCanvas.Children.Add(path);
            paths.Add(path);
        }

        _objectVisuals[obj] = paths;
        obj.PropertyChanged += OnObjectPropertyChanged;
        UpdateObjectGeometry(obj);
    }

    /// <summary>Loads the same processed grayscale pixels used by raster G-code. The canvas therefore
    /// previews what will be engraved instead of showing a misleading full-color source image.</summary>
    private static Image? TryLoadRasterPreview(SceneObject obj)
    {
        try
        {
            if (obj.RasterFilePath is null || obj.RasterOptions is null) return null;
            var preview = ProcessedImagePreviewRenderer.RenderFileForCanvas(obj.RasterFilePath, obj.RasterOptions);
            return new Image { Source = preview, Stretch = Stretch.Fill, IsHitTestVisible = false };
        }
        catch (Exception ex)
        {
            // Missing or corrupt source file — fall back to the outline-only rendering rather than
            // taking the canvas down. Logged because a silently blank raster looks identical to a
            // raster that simply has not been drawn yet.
            Log.Warning(ex, "Could not build the canvas preview for raster {Path}", obj.RasterFilePath);
            return null;
        }
    }

    private void RemoveObjectVisuals(SceneObject obj)
    {
        if (_rasterImageVisuals.Remove(obj, out var image))
            DrawCanvas.Children.Remove(image);

        if (!_objectVisuals.TryGetValue(obj, out var paths)) return;
        foreach (var path in paths)
        {
            path.MouseLeftButtonDown -= OnObjectMouseLeftButtonDown;
            path.MouseRightButtonDown -= OnObjectMouseRightButtonDown;
            path.MouseEnter -= OnObjectMouseEnter;
            path.MouseLeave -= OnObjectMouseLeave;
            DrawCanvas.Children.Remove(path);
        }
        _objectVisuals.Remove(obj);
        obj.PropertyChanged -= OnObjectPropertyChanged;
    }

    private void OnObjectPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not SceneObject obj) return;
        if (e.PropertyName is not (nameof(SceneObject.Transform) or nameof(SceneObject.IsVisible) or
            nameof(SceneObject.IncludeInOutput) or nameof(SceneObject.LocalShapes))) return;

        UpdateObjectGeometry(obj);
        if (ViewModel is not null && ViewModel.SelectedObjects.Contains(obj))
            RedrawSelectionOverlay(); // handles/bbox must track live transform changes during a drag
    }

    private void UpdateObjectGeometry(SceneObject obj)
    {
        // Same dimming convention used for disabled controls elsewhere in this theme — a "won't be
        // burned" object reads as visually distinct without a new visual language.
        var opacity = obj.IncludeInOutput ? 1.0 : 0.4;

        if (_rasterImageVisuals.TryGetValue(obj, out var image))
        {
            // Raster rotation remains disabled because the engraving planner is axis-aligned. Scaling
            // is supported, so WorldBounds keeps the preview aligned with the generated output size.
            var bounds = obj.WorldBounds();
            Canvas.SetLeft(image, ToCanvasX(bounds.MinX));
            Canvas.SetTop(image, ToCanvasY(bounds.MaxY));
            image.Width = (bounds.MaxX - bounds.MinX) * _scale;
            image.Height = (bounds.MaxY - bounds.MinY) * _scale;
            image.Visibility = IsObjectVisibleOnCanvas(obj) ? Visibility.Visible : Visibility.Collapsed;
            image.Opacity = opacity;
        }

        if (!_objectVisuals.TryGetValue(obj, out var paths)) return;
        var shapes = obj.GetWorldShapes();
        for (var i = 0; i < paths.Count && i < shapes.Count; i++)
        {
            paths[i].Data = BuildGeometry(shapes[i]);
            var layerColor = shapes[i].LayerColor;
            var color = Color.FromRgb(layerColor.R, layerColor.G, layerColor.B);
            paths[i].Stroke = new SolidColorBrush(color);
            paths[i].StrokeThickness = 1.4;
            var layerId = shapes[i].LayerId;
            paths[i].Fill = shapes[i].IsClosed && ViewModel?.LayerModeFor(layerId, layerColor) is LayerMode.Fill or LayerMode.FillAndCut
                ? new SolidColorBrush(color)
                : null;
            paths[i].Visibility = obj.IsVisible && (ViewModel?.IsLayerVisible(layerId, layerColor) ?? true)
                ? Visibility.Visible
                : Visibility.Collapsed;
            paths[i].Opacity = opacity;
        }
    }

    private bool IsObjectVisibleOnCanvas(SceneObject obj)
    {
        if (!obj.IsVisible) return false;
        if (obj.IsRaster)
            return obj.LocalShapes.Any(shape => ViewModel?.IsLayerVisible(shape.LayerId, shape.LayerColor) ?? true);
        return obj.LocalShapes.Any(shape => ViewModel?.IsLayerVisible(shape.LayerId, shape.LayerColor) ?? true);
    }

    private PathGeometry BuildGeometry(ImportedShape shape)
    {
        if (shape.Points.Count == 0) return new PathGeometry();

        var figure = new PathFigure
        {
            StartPoint = new Point(ToCanvasX(shape.Points[0].X), ToCanvasY(shape.Points[0].Y)),
            IsClosed = shape.IsClosed,
        };
        figure.Segments.Add(new PolyLineSegment(
            shape.Points.Skip(1).Select(p => new Point(ToCanvasX(p.X), ToCanvasY(p.Y))), isStroked: true));
        return new PathGeometry([figure]);
    }

    private void RedrawGrid()
    {
        foreach (var line in _gridLines) DrawCanvas.Children.Remove(line);
        _gridLines.Clear();
        if (ActualWidth <= 0 || ActualHeight <= 0) return;

        var step = RulerMath.PickStep(_scale);

        var gridBrush = new SolidColorBrush(Color.FromArgb(0x28, 0x65, 0x70, 0x82));
        var axisBrush = new SolidColorBrush(Color.FromArgb(0x68, 0x2F, 0x6F, 0xC9));

        var firstX = RulerMath.FirstTick(_offsetXMm, step);
        for (var x = firstX; x < _offsetXMm + ActualWidth / _scale; x += step)
        {
            var px = ToCanvasX(x);
            var line = new Line
            {
                X1 = px, X2 = px, Y1 = 0, Y2 = ActualHeight,
                Stroke = Math.Abs(x) < 1e-6 ? axisBrush : gridBrush,
                StrokeThickness = Math.Abs(x) < 1e-6 ? 1.2 : 1,
                IsHitTestVisible = false,
            };
            DrawCanvas.Children.Insert(0, line); // always behind object paths, regardless of insertion order among grid lines
            _gridLines.Add(line);
        }

        var firstY = RulerMath.FirstTick(_offsetYMm, step);
        for (var y = firstY; y < _offsetYMm + ActualHeight / _scale; y += step)
        {
            var py = ToCanvasY(y);
            var line = new Line
            {
                X1 = 0, X2 = ActualWidth, Y1 = py, Y2 = py,
                Stroke = Math.Abs(y) < 1e-6 ? axisBrush : gridBrush,
                StrokeThickness = Math.Abs(y) < 1e-6 ? 1.2 : 1,
                IsHitTestVisible = false,
            };
            DrawCanvas.Children.Insert(0, line);
            _gridLines.Add(line);
        }

        // Bed rectangle drawn LAST and inserted at index 0 last, so it ends up behind every grid
        // line just added above (each Insert(0, ...) call above only pushed prior lines back, so
        // inserting the bed now — after all of them — is what actually lands it furthest back).
        if (_bedRectVisual is not null) DrawCanvas.Children.Remove(_bedRectVisual);
        if (WorkAreaWidthMm > 0 && WorkAreaHeightMm > 0)
        {
            _bedRectVisual = new Rectangle
            {
                Width = Math.Max(0, WorkAreaWidthMm * _scale),
                Height = Math.Max(0, WorkAreaHeightMm * _scale),
                Fill = (Brush)FindResource("Brush.Panel"),
                Stroke = (Brush)FindResource("Brush.PanelBorder"),
                StrokeThickness = 1,
                IsHitTestVisible = false,
            };
            Canvas.SetLeft(_bedRectVisual, ToCanvasX(0));
            Canvas.SetTop(_bedRectVisual, ToCanvasY(WorkAreaHeightMm));
            DrawCanvas.Children.Insert(0, _bedRectVisual);
        }
        else
        {
            _bedRectVisual = null;
        }

        RedrawRulers(step, firstX, firstY);
    }

    // Ruler strips are separate fixed-size Canvases (TopRuler/LeftRuler) sharing the same Grid
    // row/column size as DrawCanvas, so a tick drawn at ToCanvasX(x)/ToCanvasY(y) lines up with the
    // matching grid line drawn into DrawCanvas at the same coordinates.
    private void RedrawRulers(double step, double firstX, double firstY)
    {
        foreach (var el in _topRulerVisuals) TopRuler.Children.Remove(el);
        foreach (var el in _leftRulerVisuals) LeftRuler.Children.Remove(el);
        _topRulerVisuals.Clear();
        _leftRulerVisuals.Clear();
        if (ActualWidth <= 0 || ActualHeight <= 0) return;

        var tickBrush = (Brush)FindResource("Brush.PanelBorder");
        var labelBrush = (Brush)FindResource("Brush.TextSecondary");

        for (var x = firstX; x < _offsetXMm + ActualWidth / _scale; x += step)
        {
            var px = ToCanvasX(x);
            var tick = new Line { X1 = px, X2 = px, Y1 = TopRuler.ActualHeight - 6, Y2 = TopRuler.ActualHeight, Stroke = tickBrush, StrokeThickness = 1 };
            TopRuler.Children.Add(tick);
            _topRulerVisuals.Add(tick);

            var label = new TextBlock { Text = FormatTickMm(x), FontSize = 11, Foreground = labelBrush };
            Canvas.SetLeft(label, px + 3);
            Canvas.SetTop(label, 2);
            TopRuler.Children.Add(label);
            _topRulerVisuals.Add(label);
        }

        for (var y = firstY; y < _offsetYMm + ActualHeight / _scale; y += step)
        {
            var py = ToCanvasY(y);
            var tick = new Line { X1 = LeftRuler.ActualWidth - 6, X2 = LeftRuler.ActualWidth, Y1 = py, Y2 = py, Stroke = tickBrush, StrokeThickness = 1 };
            LeftRuler.Children.Add(tick);
            _leftRulerVisuals.Add(tick);

            var label = new TextBlock { Text = FormatTickMm(y), FontSize = 11, Foreground = labelBrush };
            label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Canvas.SetLeft(label, Math.Max(1, LeftRuler.ActualWidth - 8 - label.DesiredSize.Width));
            Canvas.SetTop(label, py - label.DesiredSize.Height / 2);
            LeftRuler.Children.Add(label);
            _leftRulerVisuals.Add(label);
        }
    }

    private static string FormatTickMm(double valueMm) =>
        Math.Abs(valueMm % 1) < 1e-6 ? ((int)Math.Round(valueMm)).ToString() : valueMm.ToString("0.#");

    // ------------------------------------------------------------------
    // Selection overlay — a dashed outline + 8 resize handles for every unlocked
    // single selection. Vector objects also get a rotate handle; raster rotation
    // stays disabled until the raster planner can rotate pixels and toolpaths together.
    // ------------------------------------------------------------------

    private void RedrawSelectionOverlay()
    {
        foreach (var el in _selectionVisuals) DrawCanvas.Children.Remove(el);
        _selectionVisuals.Clear();

        if (ViewModel is null || ViewModel.SelectedObjects.Count == 0) return;

        if (ViewModel.SelectedObjects.Count == 1)
        {
            var selected = ViewModel.SelectedObjects[0];
            if (IsObjectVisibleOnCanvas(selected))
                DrawSingleObjectHandles(selected);
        }
        else
            DrawMultiSelectionBox();
    }

    private void DrawSingleObjectHandles(SceneObject obj)
    {
        var corners = new[] { ResizeHandle.TopLeft, ResizeHandle.TopRight, ResizeHandle.BottomRight, ResizeHandle.BottomLeft }
            .Select(h => ObjectTransform.HandleLocalPoint(obj.LocalBounds, obj.LocalPivot, h))
            .Select(p => obj.Transform.Apply(p, obj.LocalPivot))
            .Select(p => new Point(ToCanvasX(p.X), ToCanvasY(p.Y)))
            .ToArray();

        var outline = new Polygon
        {
            Points = new PointCollection(corners),
            Stroke = SelectionBrush,
            StrokeThickness = 1.2,
            StrokeDashArray = [4, 2],
            Fill = Brushes.Transparent,
            IsHitTestVisible = false,
        };
        DrawCanvas.Children.Add(outline);
        _selectionVisuals.Add(outline);

        if (obj.IsLocked) return;

        foreach (var handle in AllResizeHandles)
        {
            var local = ObjectTransform.HandleLocalPoint(obj.LocalBounds, obj.LocalPivot, handle);
            var world = obj.Transform.Apply(local, obj.LocalPivot);
            var screen = new Point(ToCanvasX(world.X), ToCanvasY(world.Y));

            // Two elements per grip: an invisible target you can actually hit, and a small square
            // that shows where it is. An 8px grip demands pixel-accurate aiming, which is why the
            // corners felt unresponsive even once the event routing was fixed. The visual stays 8px
            // so the selection does not turn into a row of chunky boxes.
            var target = new Rectangle
            {
                Width = HandleHitSizePx,
                Height = HandleHitSizePx,
                Fill = Brushes.Transparent,
                Tag = (obj, handle),
                Cursor = CursorForHandle(handle),
            };
            target.MouseLeftButtonDown += OnResizeHandleMouseLeftButtonDown;
            Canvas.SetLeft(target, screen.X - HandleHitSizePx / 2);
            Canvas.SetTop(target, screen.Y - HandleHitSizePx / 2);
            DrawCanvas.Children.Add(target);
            _selectionVisuals.Add(target);

            var square = new Rectangle
            {
                Width = HandleSizePx,
                Height = HandleSizePx,
                Fill = SelectionHandleFill,
                Stroke = SelectionBrush,
                StrokeThickness = 1.2,
                IsHitTestVisible = false,
            };
            Canvas.SetLeft(square, screen.X - HandleSizePx / 2);
            Canvas.SetTop(square, screen.Y - HandleSizePx / 2);
            DrawCanvas.Children.Add(square);
            _selectionVisuals.Add(square);
        }

        var pivotWorld = obj.Transform.Apply(obj.LocalPivot, obj.LocalPivot);
        var pivotScreen = new Point(ToCanvasX(pivotWorld.X), ToCanvasY(pivotWorld.Y));

        var pivotMarker = new Rectangle
        {
            Width = 6,
            Height = 6,
            Fill = SelectionHandleFill,
            Stroke = SelectionBrush,
            StrokeThickness = 1.2,
            IsHitTestVisible = false,
        };
        Canvas.SetLeft(pivotMarker, pivotScreen.X - 3);
        Canvas.SetTop(pivotMarker, pivotScreen.Y - 3);
        DrawCanvas.Children.Add(pivotMarker);
        _selectionVisuals.Add(pivotMarker);

        // Raster images can be resized, including non-uniformly when aspect lock is off, but they
        // cannot be rotated yet because that would make the canvas diverge from generated G-code.
        if (obj.IsRaster) return;

        // ResizeHandle.Bottom, not Top, because the enum names the minimum-Y corner "Top" while the
        // canvas draws minimum Y at the bottom of the screen. Anchoring on Top therefore hung the
        // rotate grip underneath the selection; every other design tool puts it above.
        // Rotation itself is unaffected: the angle is a delta between where the pointer grabbed and
        // where it is now, both measured against the pivot, so the grip's position is presentation.
        var topLocal = ObjectTransform.HandleLocalPoint(obj.LocalBounds, obj.LocalPivot, ResizeHandle.Bottom);
        var topWorld = obj.Transform.Apply(topLocal, obj.LocalPivot);
        var topScreen = new Point(ToCanvasX(topWorld.X), ToCanvasY(topWorld.Y));

        var dirX = topScreen.X - pivotScreen.X;
        var dirY = topScreen.Y - pivotScreen.Y;
        var len = Math.Sqrt(dirX * dirX + dirY * dirY);
        var rotateScreen = len < 1e-6
            ? new Point(topScreen.X, topScreen.Y - RotateHandleOffsetPx)
            : new Point(topScreen.X + dirX / len * RotateHandleOffsetPx, topScreen.Y + dirY / len * RotateHandleOffsetPx);

        var stalk = new Line
        {
            X1 = topScreen.X, Y1 = topScreen.Y, X2 = rotateScreen.X, Y2 = rotateScreen.Y,
            Stroke = SelectionBrush, StrokeThickness = 1, IsHitTestVisible = false,
        };
        DrawCanvas.Children.Add(stalk);
        _selectionVisuals.Add(stalk);

        var rotateHandle = new Ellipse
        {
            Width = HandleSizePx + 2,
            Height = HandleSizePx + 2,
            Fill = SelectionHandleFill,
            Stroke = SelectionBrush,
            StrokeThickness = 1.2,
            Tag = obj,
            Cursor = Cursors.Hand,
        };
        rotateHandle.MouseLeftButtonDown += OnRotateHandleMouseLeftButtonDown;
        Canvas.SetLeft(rotateHandle, rotateScreen.X - (HandleSizePx + 2) / 2);
        Canvas.SetTop(rotateHandle, rotateScreen.Y - (HandleSizePx + 2) / 2);
        DrawCanvas.Children.Add(rotateHandle);
        _selectionVisuals.Add(rotateHandle);
    }

    private void DrawMultiSelectionBox()
    {
        if (ViewModel is null) return;
        var box = BoundingBox2D.Empty;
        foreach (var obj in ViewModel.SelectedObjects)
        {
            if (!IsObjectVisibleOnCanvas(obj)) continue;
            box = Union(box, obj.WorldBounds());
        }
        if (box.IsEmpty) return;

        var rect = new Rectangle
        {
            Width = Math.Max(0, (box.MaxX - box.MinX) * _scale),
            Height = Math.Max(0, (box.MaxY - box.MinY) * _scale),
            Stroke = SelectionBrush,
            StrokeThickness = 1.2,
            StrokeDashArray = [4, 2],
            Fill = Brushes.Transparent,
            IsHitTestVisible = false,
        };
        Canvas.SetLeft(rect, ToCanvasX(box.MinX));
        Canvas.SetTop(rect, ToCanvasY(box.MaxY));
        DrawCanvas.Children.Add(rect);
        _selectionVisuals.Add(rect);
    }

    /// <summary>
    /// Cursors are chosen for where the handle appears on screen, which is not where its name says.
    /// ResizeHandle is expressed in document space, where Y grows upwards; the canvas draws with Y
    /// growing downwards (see ToCanvasY). So ResizeHandle.TopLeft is rendered at the bottom-left of
    /// the selection, and giving it the north-west/south-east cursor pointed the arrows across the
    /// wrong diagonal — the pointer said "drag this way" and the box grew the other.
    ///
    /// Only the diagonals are affected: Left/Right are unchanged by a vertical flip, and Top/Bottom
    /// both map to the same vertical cursor.
    /// </summary>
    private static Cursor CursorForHandle(ResizeHandle handle) => handle switch
    {
        ResizeHandle.TopLeft or ResizeHandle.BottomRight => Cursors.SizeNESW,
        ResizeHandle.TopRight or ResizeHandle.BottomLeft => Cursors.SizeNWSE,
        ResizeHandle.Left or ResizeHandle.Right => Cursors.SizeWE,
        _ => Cursors.SizeNS,
    };

    // ------------------------------------------------------------------
    // Mouse interaction — select / rubber-band / move / resize / rotate / pan / zoom.
    // Drag gestures mutate Transform live on every MouseMove (for immediate visual
    // feedback) and push exactly ONE command on MouseUp — never one per MouseMove.
    // ------------------------------------------------------------------

    private void OnObjectMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (ViewModel is null) return;
        var obj = (SceneObject)((Path)sender).Tag;
        e.Handled = true;
        SelectObjectAndBeginMove(obj, e);
    }

    private void SelectObjectAndBeginMove(SceneObject obj, MouseButtonEventArgs e)
    {
        if (ViewModel is null) return;
        Focus();

        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            if (ViewModel.SelectedObjects.Contains(obj)) ViewModel.SelectedObjects.Remove(obj);
            else ViewModel.SelectedObjects.Add(obj);
        }
        else if (!ViewModel.SelectedObjects.Contains(obj))
        {
            ViewModel.SelectedObjects.Clear();
            ViewModel.SelectedObjects.Add(obj);
        }
        // else: clicked an already-selected object with no Shift -> keep the current
        // multi-selection intact and drag the whole group.

        if (obj.IsLocked) return;
        BeginMove(e);
    }

    private void OnObjectMouseEnter(object sender, MouseEventArgs e)
    {
        if (ViewModel is null || ((Path)sender).Tag is not SceneObject obj ||
            ViewModel.SelectedObjects.Contains(obj)) return;

        // A restrained blue hover outline reveals which contours belong to one logical object.
        // This is especially important for text: all glyph contours highlight together because
        // they share one SceneObject, while neighbouring independent objects stay untouched.
        if (!_objectVisuals.TryGetValue(obj, out var paths)) return;
        foreach (var path in paths)
        {
            path.Stroke = SelectionBrush;
            path.StrokeThickness = 2;
        }
    }

    private void OnObjectMouseLeave(object sender, MouseEventArgs e)
    {
        if (((Path)sender).Tag is SceneObject obj)
            UpdateObjectGeometry(obj);
    }

    private void OnObjectMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (ViewModel is null || sender is not Path { Tag: SceneObject obj }) return;
        Focus();
        if (!ViewModel.SelectedObjects.Contains(obj))
        {
            ViewModel.SelectedObjects.Clear();
            ViewModel.SelectedObjects.Add(obj);
        }
    }

    private void BeginMove(MouseButtonEventArgs e)
    {
        if (ViewModel is null || ViewModel.SelectedObjects.Count == 0) return;
        _dragMode = DragMode.Move;
        var screen = e.GetPosition(DrawCanvas);
        _dragStartWorld = new Position(ToWorldX(screen.X), ToWorldY(screen.Y), 0);
        _dragStartTransforms.Clear();
        foreach (var obj in ViewModel.SelectedObjects)
            _dragStartTransforms[obj] = obj.Transform;
        DrawCanvas.CaptureMouse();
    }

    /// <summary>
    /// Whether a hit-tested element is part of the selection adorner (a resize grip or the rotate
    /// grip). Both tag themselves with the object they belong to, which is what distinguishes them
    /// from ordinary scene geometry.
    /// </summary>
    private static bool IsSelectionHandle(object? source) => source switch
    {
        Rectangle { Tag: ValueTuple<SceneObject, ResizeHandle> } => true,
        Ellipse { Tag: SceneObject } => true,
        _ => false,
    };

    private void OnResizeHandleMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (ViewModel is null) return;
        var (obj, handle) = ((SceneObject, ResizeHandle))((Rectangle)sender).Tag;
        e.Handled = true;
        Focus();
        _dragMode = DragMode.Resize;
        _activeSingleObject = obj;
        _activeResizeHandle = handle;
        _resizeStartTransform = obj.Transform;
        DrawCanvas.CaptureMouse();
    }

    private void OnRotateHandleMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (ViewModel is null) return;
        var obj = (SceneObject)((Ellipse)sender).Tag;
        e.Handled = true;
        Focus();

        _dragMode = DragMode.Rotate;
        _activeSingleObject = obj;
        _rotateStartTransform = obj.Transform;

        var pivotWorld = obj.Transform.Apply(obj.LocalPivot, obj.LocalPivot);
        var mouseScreen = e.GetPosition(DrawCanvas);
        var mouseWorld = new Position(ToWorldX(mouseScreen.X), ToWorldY(mouseScreen.Y), 0);
        _rotateStartAngleDeg = Math.Atan2(mouseWorld.Y - pivotWorld.Y, mouseWorld.X - pivotWorld.X) * 180.0 / Math.PI;
        DrawCanvas.CaptureMouse();
    }

    private void OnDrawCanvasMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // Only fires when no object/handle already handled the click — i.e. empty space.
        Focus();
        if (ViewModel is null || ViewModel.ActiveTool != DesignerTool.Select) return;

        if (!Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
            ViewModel.SelectedObjects.Clear();

        _dragMode = DragMode.Select;
        _dragStartScreen = e.GetPosition(DrawCanvas);
        EnsureRubberBandVisual();
        _rubberBandVisual!.Visibility = Visibility.Visible;
        Canvas.SetLeft(_rubberBandVisual, _dragStartScreen.X);
        Canvas.SetTop(_rubberBandVisual, _dragStartScreen.Y);
        _rubberBandVisual.Width = 0;
        _rubberBandVisual.Height = 0;
        DrawCanvas.CaptureMouse();
    }

    private void OnDrawCanvasPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (ViewModel is null) return;

        // Selection handles win over everything else on the canvas.
        //
        // This is a tunnelling handler on the parent canvas, so it runs before the bubbling
        // MouseLeftButtonDown on the handle itself. With a drawing tool active it called BeginDraw
        // and set Handled, so grabbing a corner started a new rectangle instead of resizing the
        // selected one — the handles were simply dead whenever a shape tool was chosen. With Select
        // active the object underneath won the hit test instead, turning a corner drag into a move.
        // Bailing out here lets the handle's own handler run in both cases.
        if (IsSelectionHandle(e.OriginalSource)) return;

        var screen = e.GetPosition(DrawCanvas);

        if (_isSpacePressed || ViewModel.ActiveTool == DesignerTool.Pan)
        {
            Focus();
            BeginPan(screen);
            e.Handled = true;
            return;
        }

        if (DesignerPrimitiveFactory.IsPrimitive(ViewModel.ActiveTool))
        {
            Focus();
            BeginDraw(ViewModel.ActiveTool, screen);
            e.Handled = true;
            return;
        }

        if (ViewModel.ActiveTool == DesignerTool.Text)
        {
            Focus();
            TextPlacementRequested?.Invoke(new Position(ToWorldX(screen.X), ToWorldY(screen.Y), 0));
            e.Handled = true;
            return;
        }

        if (ViewModel.ActiveTool == DesignerTool.Select)
        {
            var candidates = HitTestScene(screen)
                .GroupBy(candidate => candidate.Object)
                .Select(group => group.First())
                .ToList();
            if (candidates.Count == 0) return;

            var selected = candidates[0];
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt) && candidates.Count > 1)
            {
                var currentIndex = candidates.FindIndex(candidate => ViewModel.SelectedObjects.Contains(candidate.Object));
                selected = candidates[(currentIndex + 1 + candidates.Count) % candidates.Count];
            }

            SelectObjectAndBeginMove(selected.Object, e);
            e.Handled = true;
        }
    }

    private IReadOnlyList<SceneHitCandidate> HitTestScene(Point screen)
    {
        if (ViewModel is null) return [];
        var pointer = new Position(ToWorldX(screen.X), ToWorldY(screen.Y), 0);
        return SceneHitTester.HitTest(
            ViewModel.Objects,
            pointer,
            _scale,
            (obj, shape) => obj.IsRaster ||
                ViewModel.LayerModeFor(shape.LayerId, shape.LayerColor) is LayerMode.Fill or LayerMode.FillAndCut,
            (obj, shape) => obj.IsVisible && ViewModel.IsLayerVisible(shape.LayerId, shape.LayerColor));
    }

    private void OnDrawCanvasPreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_dragMode != DragMode.Pan || !_isSpacePressed) return;
        EndPan();
        e.Handled = true;
    }

    private void OnDrawCanvasMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Middle) return;
        Focus();
        BeginPan(e.GetPosition(DrawCanvas));
        e.Handled = true;
    }

    private void BeginPan(Point start)
    {
        _autoFit = false;
        _dragMode = DragMode.Pan;
        _dragStartScreen = start;
        _panStartOffsetX = _offsetXMm;
        _panStartOffsetY = _offsetYMm;
        Cursor = Cursors.ScrollAll;
        DrawCanvas.CaptureMouse();
    }

    private void BeginDraw(DesignerTool tool, Point start)
    {
        _dragMode = DragMode.Draw;
        _drawingTool = tool;
        _dragStartScreen = start;
        _drawStartWorld = new Position(ToWorldX(start.X), ToWorldY(start.Y), 0);
        EnsureToolPreviewVisual();
        UpdateToolPreview(start);
        DrawCanvas.CaptureMouse();
    }

    private void EnsureToolPreviewVisual()
    {
        if (_toolPreviewVisual is not null) return;
        _toolPreviewVisual = new Path
        {
            Stroke = SelectionBrush,
            StrokeThickness = 1.5,
            StrokeDashArray = new DoubleCollection { 4, 3 },
            Fill = Brushes.Transparent,
            IsHitTestVisible = false,
        };
        DrawCanvas.Children.Add(_toolPreviewVisual);
    }

    private void UpdateToolPreview(Point screen)
    {
        if (_toolPreviewVisual is null) return;
        var end = new Position(ToWorldX(screen.X), ToWorldY(screen.Y), 0);
        var preview = DesignerPrimitiveFactory.Create(
            _drawingTool,
            _drawStartWorld,
            end,
            Lasero.Core.Layers.RgbColor.Black);
        // Primitive factories normalize geometry around a local pivot and store the requested
        // position in Transform. Rendering LocalShapes directly therefore placed the dashed
        // preview around the scene origin instead of under the pointer. Use the same world-space
        // geometry path as the finished object so preview and result are pixel-identical.
        _toolPreviewVisual.Data = BuildGeometry(preview.GetWorldShapes()[0]);
        _toolPreviewVisual.Visibility = Visibility.Visible;
    }

    private void OnDrawCanvasMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Middle || _dragMode != DragMode.Pan) return;
        EndPan();
        e.Handled = true;
    }

    private void EndPan()
    {
        _dragMode = DragMode.None;
        if (DrawCanvas.IsMouseCaptured) DrawCanvas.ReleaseMouseCapture();
        UpdateToolCursor();
    }

    private void OnDrawCanvasMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragMode == DragMode.Pan &&
            e.MiddleButton != MouseButtonState.Pressed &&
            !(_isSpacePressed && e.LeftButton == MouseButtonState.Pressed))
        {
            EndPan();
            return;
        }

        var screen = e.GetPosition(DrawCanvas);

        switch (_dragMode)
        {
            case DragMode.Move:
            {
                var world = new Position(ToWorldX(screen.X), ToWorldY(screen.Y), 0);
                var dx = world.X - _dragStartWorld.X;
                var dy = world.Y - _dragStartWorld.Y;
                foreach (var (obj, start) in _dragStartTransforms)
                {
                    if (obj.IsLocked) continue;
                    obj.Transform = start with { X = start.X + dx, Y = start.Y + dy };
                }
                break;
            }
            case DragMode.Resize:
            {
                if (_activeSingleObject is null) break;
                var world = new Position(ToWorldX(screen.X), ToWorldY(screen.Y), 0);
                _activeSingleObject.Transform = _resizeStartTransform.ComputeResize(
                    _activeSingleObject.LocalPivot,
                    _activeSingleObject.LocalBounds,
                    _activeResizeHandle,
                    world,
                    lockAspectRatio: ViewModel?.LockAspectRatio == true,
                    allowFlip: !_activeSingleObject.IsRaster);
                break;
            }
            case DragMode.Rotate:
            {
                if (_activeSingleObject is null) break;
                var pivotWorld = _rotateStartTransform.Apply(_activeSingleObject.LocalPivot, _activeSingleObject.LocalPivot);
                var world = new Position(ToWorldX(screen.X), ToWorldY(screen.Y), 0);
                var currentAngle = Math.Atan2(world.Y - pivotWorld.Y, world.X - pivotWorld.X) * 180.0 / Math.PI;
                var newRotation = _rotateStartTransform.RotationDeg + (currentAngle - _rotateStartAngleDeg);
                if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
                    newRotation = Math.Round(newRotation / 15.0) * 15.0;
                _activeSingleObject.Transform = _rotateStartTransform with { RotationDeg = newRotation };
                break;
            }
            case DragMode.Select:
                UpdateRubberBand(screen);
                break;
            case DragMode.Pan:
            {
                var dxPx = screen.X - _dragStartScreen.X;
                var dyPx = screen.Y - _dragStartScreen.Y;
                _offsetXMm = _panStartOffsetX - dxPx / _scale;
                _offsetYMm = _panStartOffsetY + dyPx / _scale;
                RepositionAll();
                break;
            }
            case DragMode.Draw:
                UpdateToolPreview(screen);
                break;
        }
    }

    private void OnDrawCanvasMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        switch (_dragMode)
        {
            case DragMode.Move: FinishMove(); break;
            case DragMode.Resize: FinishResize(); break;
            case DragMode.Rotate: FinishRotate(); break;
            case DragMode.Select: FinishRubberBand(); break;
            case DragMode.Draw: FinishDraw(e.GetPosition(DrawCanvas)); break;
        }
        _dragMode = DragMode.None;
        if (DrawCanvas.IsMouseCaptured) DrawCanvas.ReleaseMouseCapture();
        UpdateToolCursor();
    }

    private void FinishDraw(Point endScreen)
    {
        if (_toolPreviewVisual is not null) _toolPreviewVisual.Visibility = Visibility.Collapsed;
        if (ViewModel is null) return;

        var distance = endScreen - _dragStartScreen;
        if (Math.Abs(distance.X) < 3 && Math.Abs(distance.Y) < 3) return;

        var endWorld = new Position(ToWorldX(endScreen.X), ToWorldY(endScreen.Y), 0);
        ViewModel.DrawPrimitive(_drawingTool, _drawStartWorld, endWorld);
    }

    private void FinishMove()
    {
        if (ViewModel is null) { _dragStartTransforms.Clear(); return; }
        var commands = new List<ISceneCommand>();
        foreach (var (obj, before) in _dragStartTransforms)
        {
            if (before.X != obj.Transform.X || before.Y != obj.Transform.Y)
                commands.Add(new TransformObjectCommand(obj, before, obj.Transform));
        }
        _dragStartTransforms.Clear();

        if (commands.Count == 1) ViewModel.Execute(commands[0]);
        else if (commands.Count > 1) ViewModel.Execute(new CompositeSceneCommand(commands));
    }

    private void FinishResize()
    {
        var obj = _activeSingleObject;
        _activeSingleObject = null;
        if (ViewModel is null || obj is null) return;
        if (!_resizeStartTransform.Equals(obj.Transform))
            ViewModel.Execute(new TransformObjectCommand(obj, _resizeStartTransform, obj.Transform));
    }

    private void FinishRotate()
    {
        var obj = _activeSingleObject;
        _activeSingleObject = null;
        if (ViewModel is null || obj is null) return;
        if (!_rotateStartTransform.Equals(obj.Transform))
            ViewModel.Execute(new TransformObjectCommand(obj, _rotateStartTransform, obj.Transform));
    }

    private void EnsureRubberBandVisual()
    {
        if (_rubberBandVisual is not null) return;
        _rubberBandVisual = new Rectangle
        {
            Stroke = SelectionBrush,
            StrokeThickness = 1,
            StrokeDashArray = [3, 2],
            Fill = CreateSelectionFill(),
            IsHitTestVisible = false,
            Visibility = Visibility.Collapsed,
        };
        DrawCanvas.Children.Add(_rubberBandVisual);
    }

    private Brush CreateSelectionFill()
    {
        var accent = SelectionBrush is SolidColorBrush solid ? solid.Color : Colors.DodgerBlue;
        return new SolidColorBrush(Color.FromArgb(0x22, accent.R, accent.G, accent.B));
    }

    private void UpdateRubberBand(Point current)
    {
        if (_rubberBandVisual is null) return;
        var x = Math.Min(current.X, _dragStartScreen.X);
        var y = Math.Min(current.Y, _dragStartScreen.Y);
        Canvas.SetLeft(_rubberBandVisual, x);
        Canvas.SetTop(_rubberBandVisual, y);
        _rubberBandVisual.Width = Math.Abs(current.X - _dragStartScreen.X);
        _rubberBandVisual.Height = Math.Abs(current.Y - _dragStartScreen.Y);
    }

    private void FinishRubberBand()
    {
        if (_rubberBandVisual is null) { return; }
        var left = Canvas.GetLeft(_rubberBandVisual);
        var top = Canvas.GetTop(_rubberBandVisual);
        var w = _rubberBandVisual.Width;
        var h = _rubberBandVisual.Height;
        _rubberBandVisual.Visibility = Visibility.Collapsed;

        if (ViewModel is not null && (w > 2 || h > 2)) // a real drag, not an accidental click
        {
            var worldMinX = ToWorldX(left);
            var worldMaxX = ToWorldX(left + w);
            var worldMinY = ToWorldY(top + h);
            var worldMaxY = ToWorldY(top);

            foreach (var obj in ViewModel.Objects)
            {
                if (obj.IsLocked || !IsObjectVisibleOnCanvas(obj)) continue;
                var b = obj.WorldBounds();
                var intersects = b.MinX <= worldMaxX && b.MaxX >= worldMinX && b.MinY <= worldMaxY && b.MaxY >= worldMinY;
                if (intersects && !ViewModel.SelectedObjects.Contains(obj))
                    ViewModel.SelectedObjects.Add(obj);
            }
        }
    }

    private void OnDrawCanvasMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (ActualWidth <= 0 || ActualHeight <= 0) return;
        _autoFit = false;

        if (!Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            var distancePx = e.Delta / 120.0 * 48.0;
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
                _offsetXMm -= distancePx / _scale;
            else
                _offsetYMm += distancePx / _scale;

            RepositionAll();
            e.Handled = true;
            return;
        }

        var screen = e.GetPosition(DrawCanvas);
        var worldBefore = new Position(ToWorldX(screen.X), ToWorldY(screen.Y), 0);

        var factor = e.Delta > 0 ? 1.15 : 1 / 1.15;
        _scale = Math.Clamp(_scale * factor, MinScale, MaxScale);

        _offsetXMm = worldBefore.X - (screen.X - MarginPx) / _scale;
        _offsetYMm = worldBefore.Y - (ActualHeight - MarginPx - screen.Y) / _scale;

        RepositionAll();
        e.Handled = true;
    }

    // ------------------------------------------------------------------
    // Keyboard — arrow-key nudge and 'F' fit-to-view live on the canvas itself;
    // Delete/Ctrl+D/Ctrl+Z/Ctrl+Y are bound at the Window level (MainWindow.xaml)
    // so they work regardless of which control currently has focus.
    // ------------------------------------------------------------------

    private void OnCanvasKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Space)
        {
            _isSpacePressed = true;
            if (_dragMode == DragMode.None) Cursor = Cursors.ScrollAll;
            e.Handled = true;
            return;
        }

        if (ViewModel is null) return;

        if (e.Key == Key.Escape)
        {
            if (_dragMode != DragMode.None)
                CancelActiveInteraction();
            else if (ViewModel.ActiveTool != DesignerTool.Select)
                ViewModel.ActiveTool = DesignerTool.Select;
            else
                ViewModel.SelectedObjects.Clear();
            e.Handled = true;
            return;
        }

        if (e.Key is Key.Left or Key.Right or Key.Up or Key.Down)
        {
            var step = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 5.0 : 0.5;
            Nudge(e.Key, step);
            e.Handled = true;
        }
        else if (e.Key == Key.F)
        {
            FitToView();
            e.Handled = true;
        }
    }

    private void OnCanvasKeyUp(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Space) return;
        _isSpacePressed = false;
        if (_dragMode == DragMode.Pan && Mouse.MiddleButton != MouseButtonState.Pressed)
            EndPan();
        else if (_dragMode == DragMode.None)
            UpdateToolCursor();
        e.Handled = true;
    }

    private void OnCanvasLostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        _isSpacePressed = false;
        if (_dragMode == DragMode.Pan && Mouse.MiddleButton != MouseButtonState.Pressed)
            EndPan();
        else if (_dragMode == DragMode.None)
            UpdateToolCursor();
    }

    private void CancelActiveInteraction()
    {
        switch (_dragMode)
        {
            case DragMode.Move:
                foreach (var (obj, transform) in _dragStartTransforms)
                    obj.Transform = transform;
                _dragStartTransforms.Clear();
                break;
            case DragMode.Resize when _activeSingleObject is not null:
                _activeSingleObject.Transform = _resizeStartTransform;
                _activeSingleObject = null;
                break;
            case DragMode.Rotate when _activeSingleObject is not null:
                _activeSingleObject.Transform = _rotateStartTransform;
                _activeSingleObject = null;
                break;
            case DragMode.Select when _rubberBandVisual is not null:
                _rubberBandVisual.Visibility = Visibility.Collapsed;
                break;
            case DragMode.Pan:
                _offsetXMm = _panStartOffsetX;
                _offsetYMm = _panStartOffsetY;
                break;
            case DragMode.Draw when _toolPreviewVisual is not null:
                _toolPreviewVisual.Visibility = Visibility.Collapsed;
                break;
        }

        _dragMode = DragMode.None;
        if (DrawCanvas.IsMouseCaptured) DrawCanvas.ReleaseMouseCapture();
        UpdateToolCursor();
        RepositionAll();
    }

    private void Nudge(Key key, double stepMm)
    {
        if (ViewModel is null || ViewModel.SelectedObjects.Count == 0) return;
        double dx = key switch { Key.Left => -stepMm, Key.Right => stepMm, _ => 0 };
        double dy = key switch { Key.Up => stepMm, Key.Down => -stepMm, _ => 0 };

        var commands = new List<ISceneCommand>();
        foreach (var obj in ViewModel.SelectedObjects)
        {
            if (obj.IsLocked) continue;
            var before = obj.Transform;
            commands.Add(new TransformObjectCommand(obj, before, before with { X = before.X + dx, Y = before.Y + dy }));
        }

        if (commands.Count == 1) ViewModel.Execute(commands[0]);
        else if (commands.Count > 1) ViewModel.Execute(new CompositeSceneCommand(commands));
    }
}
