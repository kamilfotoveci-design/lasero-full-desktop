using Serilog;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
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

    // ------------------------------------------------------------------
    // Marching ants. A static dashed outline is easy to miss on a busy canvas, and on a filled
    // object it reads as part of the artwork; a crawling dash does not. Both the selection box and
    // the selected object's own contours carry it, so what is selected is unmistakable even when the
    // box happens to sit over other geometry.
    // ------------------------------------------------------------------

    /// <summary>Dash pattern of the selection outline, in multiples of its stroke thickness. Frozen
    /// because one instance is handed to every dashed visual on the overlay.</summary>
    private static readonly DoubleCollection SelectionDashes = CreateSelectionDashes();

    private static DoubleCollection CreateSelectionDashes()
    {
        var dashes = new DoubleCollection { 4, 2 };
        dashes.Freeze();
        return dashes;
    }

    /// <summary>One full cycle of the pattern. Animating the offset over exactly this distance is what
    /// makes the crawl seamless rather than visibly snapping back.</summary>
    private const double SelectionDashPeriod = 6;

    private static readonly Duration SelectionDashCycle = new(TimeSpan.FromMilliseconds(700));

    /// <summary>
    /// Where the dash pattern currently sits. Every dashed selection visual binds its
    /// StrokeDashOffset to this one property instead of animating itself, because the overlay is
    /// rebuilt on every mouse-move of a drag: per-shape animations would restart from zero each time
    /// and the ants would stand still exactly while the object is being moved.
    /// </summary>
    private static readonly DependencyProperty MarchingAntsPhaseProperty = DependencyProperty.Register(
        nameof(MarchingAntsPhase), typeof(double), typeof(SceneCanvas), new PropertyMetadata(0.0));

    private double MarchingAntsPhase => (double)GetValue(MarchingAntsPhaseProperty);

    private bool _antsRunning;

    private enum DragMode { None, Select, Move, Resize, Rotate, Pan, Draw, PathTool, NodeEdit, NodeMarquee }

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
    private double _pendingMoveDx;
    private double _pendingMoveDy;
    private Point _pendingExpensiveDragScreen;
    private bool _expensiveDragUpdateScheduled;

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

    // Inline text editing — a plain TextBox laid directly on DrawCanvas, positioned/sized/rotated
    // in screen space from the object's own corners exactly the way the selection handles above are
    // (Transform.Apply -> ToCanvasX/Y), so it needs no separate popup window and no ScaleTransform
    // hacks. Only one object can be edited at a time; the object's own rendered Path visuals are
    // hidden for the duration so the live TextBox is the only thing on screen (see BeginInlineTextEdit
    // / CommitInlineTextEdit).
    private SceneObject? _editingTextObject;
    private TextBox? _inlineTextEditor;

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
        if (e.PropertyName != nameof(SceneViewModel.ActiveTool)) return;
        UpdateToolCursor();

        // VECTOR PATH TOOL HOOK: switching tools any other way than the layered-Escape/double-click/
        // Enter paths above (clicking a different toolbar button, a keyboard shortcut, ...) must still
        // discard an abandoned in-progress path and its dashed preview, and drop out of Node Edit mode,
        // rather than leaving stale state/visuals behind for the next time either tool is used.
        if (ViewModel?.ActiveTool != DesignerTool.Line && _pathToolDrawing) ResetVectorPathToolState();
        if (ViewModel?.ActiveTool != DesignerTool.Select && _nodeEditObject is not null) ExitNodeEditMode();
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

    /// <summary>
    /// Puts the machine bed in the middle of the view without changing the zoom.
    ///
    /// Distinct from FitToView, which also picks a scale: after working at a chosen magnification,
    /// "where is the bed" and "show me everything" are different questions, and answering the first
    /// with the second throws away the magnification the operator set.
    ///
    /// Only the pan offset moves. The scale, the world/screen transforms and everything derived from
    /// them are the same ones ApplyFit already uses.
    /// </summary>
    public void CenterWorkArea()
    {
        if (ActualWidth <= 0 || ActualHeight <= 0 || _scale <= 0) return;
        if (WorkAreaWidthMm <= 0 || WorkAreaHeightMm <= 0) return;

        _autoFit = false;
        _offsetXMm = WorkAreaWidthMm / 2 - (ActualWidth / 2 - MarginPx) / _scale;
        _offsetYMm = WorkAreaHeightMm / 2 - (ActualHeight / 2 - MarginPx) / _scale;
        RepositionAll();
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

    /// <summary>Zooms to an exact percentage, keeping the current centre. The toolbar's zoom picker
    /// calls this; ZoomPercent itself stays read-only, so the readout still reports the scale the
    /// canvas actually reached — including when the request was clamped.</summary>
    public void SetZoomPercent(int percent)
    {
        if (ActualWidth <= 0 || ActualHeight <= 0 || percent <= 0 || _scale <= 0) return;
        ZoomAroundCenter(DefaultScale * percent / 100d / _scale);
    }

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
        // Defensive only — the normal commit path (CommitInlineTextEdit) always detaches the editor
        // itself before touching Objects, so this should not fire mid-edit. It exists so that if some
        // other change ever rebuilds the scene while a canvas text edit is open, the stale TextBox
        // reference (about to be swept up by the Children.Clear() below) is not left dangling.
        if (_inlineTextEditor is not null)
        {
            _inlineTextEditor.PreviewKeyDown -= OnInlineEditorPreviewKeyDown;
            _inlineTextEditor.LostKeyboardFocus -= OnInlineEditorLostFocus;
            _inlineTextEditor = null;
            _editingTextObject = null;
        }

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

        // One visual per compound path, not per contour. A letter's counter and a logo's cut-out are
        // separate contours of the same compound path; filling each contour on its own painted them
        // solid instead of punching them out, so every "e" and "o" came out as a blob.
        var paths = new List<Path>();
        foreach (var group in CompoundGroups(obj.LocalShapes))
        {
            var color = group.First().LayerColor;
            var path = new Path
            {
                Fill = null,
                Stroke = new SolidColorBrush(Color.FromRgb(color.R, color.G, color.B)),
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

    /// <summary>
    /// The contours of one object split into compound paths: everything that fills as a single region
    /// with its holes punched out.
    ///
    /// GeometrySetId is the grouping key, and Guid.Empty means "all contours in this object are one
    /// compound path" — the documented legacy value that SVG imports still use. Layer identity splits
    /// a group further, because two layers are burned separately and cannot share a fill.
    /// </summary>
    private static List<IGrouping<(Guid Set, Guid Layer, RgbColor Color), ImportedShape>> CompoundGroups(
        IReadOnlyList<ImportedShape> shapes) => shapes
        .GroupBy(shape => (Set: shape.GeometrySetId, Layer: shape.LayerId, Color: shape.LayerColor))
        .ToList();

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
        var groups = CompoundGroups(obj.GetWorldShapes());

        // Recolouring or reassigning a layer changes the grouping key, so the number of compound paths
        // can change without the object itself being replaced. Rebuild rather than update in place.
        if (groups.Count != paths.Count)
        {
            RemoveObjectVisuals(obj);
            AddObjectVisuals(obj);
            return;
        }

        for (var i = 0; i < groups.Count; i++)
        {
            var group = groups[i];
            var layerColor = group.Key.Color;
            var layerId = group.Key.Layer;
            var color = Color.FromRgb(layerColor.R, layerColor.G, layerColor.B);
            // A raster's LocalShapes contain only its placement rectangle. Filling that rectangle
            // paints an opaque layer-colour slab over the actual bitmap preview below it.
            var fills = !obj.IsRaster &&
                (ViewModel?.LayerModeFor(layerId, layerColor) is LayerMode.Fill or LayerMode.FillAndCut);

            paths[i].Data = BuildCompoundGeometry(group);
            paths[i].Stroke = new SolidColorBrush(color);
            paths[i].StrokeThickness = 1.4;
            paths[i].Fill = fills && group.Any(shape => shape.IsClosed)
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

    /// <summary>
    /// One compound path's contours as a single geometry.
    ///
    /// FillRule is Nonzero, the rule type and SVG both use: a contour wound against its parent — a
    /// letter's counter, a washer's bore — punches a hole, while two same-wound contours that happen
    /// to overlap simply merge. EvenOdd would instead cut a hole out of any overlap between two
    /// separate shapes on the same layer.
    ///
    /// Open contours are marked IsFilled=false so a stray line inside a filled group contributes its
    /// stroke without dragging a fill region along with it.
    /// </summary>
    private PathGeometry BuildCompoundGeometry(IEnumerable<ImportedShape> shapes)
    {
        var figures = new PathFigureCollection();
        foreach (var shape in shapes)
        {
            if (shape.Points.Count == 0) continue;
            var figure = new PathFigure
            {
                StartPoint = new Point(ToCanvasX(shape.Points[0].X), ToCanvasY(shape.Points[0].Y)),
                IsClosed = shape.IsClosed,
                IsFilled = shape.IsClosed,
            };
            figure.Segments.Add(new PolyLineSegment(
                shape.Points.Skip(1).Select(p => new Point(ToCanvasX(p.X), ToCanvasY(p.Y))), isStroked: true));
            figures.Add(figure);
        }

        return new PathGeometry(figures) { FillRule = FillRule.Nonzero };
    }

    private void RedrawGrid()
    {
        foreach (var line in _gridLines) DrawCanvas.Children.Remove(line);
        _gridLines.Clear();
        if (ActualWidth <= 0 || ActualHeight <= 0) return;

        var step = RulerMath.PickStep(_scale);

        var gridBrush = (Brush)FindResource("Brush.Canvas.GridMinor");
        var axisBrush = (Brush)FindResource("Brush.Canvas.GridMajor");

        // The grid belongs to the bed, not to the viewport. Drawn across the whole canvas it used to
        // run out over the surround, which is nearly black in this theme — a dark grid line on it is
        // invisible, and a light one would draw a rectangle of graph paper where there is no machine.
        var hasBed = WorkAreaWidthMm > 0 && WorkAreaHeightMm > 0;
        var bedLeft = hasBed ? ToCanvasX(0) : 0;
        var bedRight = hasBed ? ToCanvasX(WorkAreaWidthMm) : ActualWidth;
        var bedTop = hasBed ? ToCanvasY(WorkAreaHeightMm) : 0;
        var bedBottom = hasBed ? ToCanvasY(0) : ActualHeight;

        var firstX = RulerMath.FirstTick(_offsetXMm, step);
        for (var x = firstX; x < _offsetXMm + ActualWidth / _scale; x += step)
        {
            var px = ToCanvasX(x);
            if (hasBed && (px < bedLeft - 0.5 || px > bedRight + 0.5)) continue;
            var line = new Line
            {
                X1 = px, X2 = px, Y1 = bedTop, Y2 = bedBottom,
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
            if (hasBed && (py < bedTop - 0.5 || py > bedBottom + 0.5)) continue;
            var line = new Line
            {
                X1 = bedLeft, X2 = bedRight, Y1 = py, Y2 = py,
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
                Fill = (Brush)FindResource("Brush.Canvas.WorkArea"),
                Stroke = (Brush)FindResource("Brush.Canvas.WorkAreaBorder"),
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
        // FontFamily is inherited from the Window and these labels aren't in the visual
        // tree yet when measured, so an explicit FontFamily keeps Measure() honest about
        // the width they'll actually render at (an unparented element has no inheritance
        // context and would otherwise measure against the default WPF font, not Inter).
        var labelFont = (FontFamily)FindResource("Font.Numeric");

        for (var x = firstX; x < _offsetXMm + ActualWidth / _scale; x += step)
        {
            var px = ToCanvasX(x);
            var tick = new Line { X1 = px, X2 = px, Y1 = TopRuler.ActualHeight - 6, Y2 = TopRuler.ActualHeight, Stroke = tickBrush, StrokeThickness = 1 };
            TopRuler.Children.Add(tick);
            _topRulerVisuals.Add(tick);

            var label = new TextBlock { Text = FormatTickMm(x), FontSize = 11, FontFamily = labelFont, Foreground = labelBrush };
            label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            if (px + 3 + label.DesiredSize.Width > TopRuler.ActualWidth) continue;

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

            var label = new TextBlock { Text = FormatTickMm(y), FontSize = 11, FontFamily = labelFont, Foreground = labelBrush };
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
        // VECTOR PATH TOOL HOOK: every node-edit state change in SceneCanvas.VectorPathTool.cs ends
        // by calling this method already, so NodeEditToolbar's bindable view-state (IsNodeEditActive,
        // SelectedNodeCount, etc.) is kept current from this one call site rather than repeated at
        // each individual state change.
        UpdateNodeEditToolbarState();

        foreach (var el in _selectionVisuals) DrawCanvas.Children.Remove(el);
        _selectionVisuals.Clear();

        // VECTOR PATH TOOL HOOK: Node Edit mode replaces the normal resize/rotate handles with node
        // and Bezier-handle dots for the one object being edited.
        if (_nodeEditObject is not null)
        {
            StopMarchingAnts();
            DrawNodeEditOverlay(_nodeEditObject);
            return;
        }

        if (ViewModel is null || ViewModel.SelectedObjects.Count == 0)
        {
            StopMarchingAnts();
            return;
        }

        StartMarchingAnts();

        if (ViewModel.SelectedObjects.Count == 1)
        {
            var selected = ViewModel.SelectedObjects[0];
            if (IsObjectVisibleOnCanvas(selected))
                DrawSingleObjectHandles(selected);
        }
        else
            DrawMultiSelectionBox();

        // Every selected object's own contours crawl, not just the box around them. On a filled
        // object the box alone is ambiguous about which shape it belongs to.
        foreach (var obj in ViewModel.SelectedObjects)
        {
            if (IsObjectVisibleOnCanvas(obj))
                DrawObjectAnts(obj);
        }
    }

    /// <summary>
    /// The selected object's contours, traced in one crawling dashed path. One visual for the whole
    /// object rather than one per contour: a traced SVG can carry hundreds of contours, and the
    /// overlay is rebuilt on every mouse-move while dragging.
    /// </summary>
    private void DrawObjectAnts(SceneObject obj)
    {
        var figures = new PathFigureCollection();
        foreach (var shape in obj.GetWorldShapes())
        {
            if (shape.Points.Count < 2) continue;
            if (!(ViewModel?.IsLayerVisible(shape.LayerId, shape.LayerColor) ?? true)) continue;
            figures.Add(new PathFigure
            {
                StartPoint = new Point(ToCanvasX(shape.Points[0].X), ToCanvasY(shape.Points[0].Y)),
                IsClosed = shape.IsClosed,
                IsFilled = false,
                Segments =
                {
                    new PolyLineSegment(
                        shape.Points.Skip(1).Select(point => new Point(ToCanvasX(point.X), ToCanvasY(point.Y))),
                        isStroked: true),
                },
            });
        }

        if (figures.Count == 0) return;

        var ants = new Path
        {
            Data = new PathGeometry(figures),
            Stroke = SelectionBrush,
            StrokeThickness = 1.6,
            Fill = null,
            IsHitTestVisible = false,
        };
        ApplyMarchingAnts(ants);
        DrawCanvas.Children.Add(ants);
        _selectionVisuals.Add(ants);
    }

    /// <summary>Dashes the shape and ties its dash offset to the shared crawl.</summary>
    private void ApplyMarchingAnts(Shape shape)
    {
        shape.StrokeDashArray = SelectionDashes;
        shape.SetBinding(Shape.StrokeDashOffsetProperty, new System.Windows.Data.Binding(nameof(MarchingAntsPhase))
        {
            Source = this,
            Mode = System.Windows.Data.BindingMode.OneWay,
        });
    }

    private void StartMarchingAnts()
    {
        if (_antsRunning) return;
        _antsRunning = true;

        // Windows' own "animate controls and elements inside windows" setting. With animation turned
        // off the dashes stay put: the outline still reads as a selection, it just does not move.
        if (!SystemParameters.ClientAreaAnimation)
        {
            SetValue(MarchingAntsPhaseProperty, 0.0);
            return;
        }

        BeginAnimation(MarchingAntsPhaseProperty, new System.Windows.Media.Animation.DoubleAnimation
        {
            From = SelectionDashPeriod,
            To = 0,
            Duration = SelectionDashCycle,
            RepeatBehavior = System.Windows.Media.Animation.RepeatBehavior.Forever,
        });
    }

    /// <summary>
    /// Stops the crawl when nothing is selected. The animation ticks the property at frame rate, and
    /// leaving it running would keep re-rendering an overlay that has nothing to outline.
    /// </summary>
    private void StopMarchingAnts()
    {
        if (!_antsRunning) return;
        _antsRunning = false;
        BeginAnimation(MarchingAntsPhaseProperty, null);
        SetValue(MarchingAntsPhaseProperty, 0.0);
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
            Fill = Brushes.Transparent,
            IsHitTestVisible = false,
        };
        ApplyMarchingAnts(outline);
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
            Fill = Brushes.Transparent,
            IsHitTestVisible = false,
        };
        ApplyMarchingAnts(rect);
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
        _pendingMoveDx = 0;
        _pendingMoveDy = 0;
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
        Ellipse { Tag: NodeHitTag } => true,
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

        // A click inside the live inline text editor is not canvas interaction — let the TextBox
        // handle its own caret placement/selection. GetPosition transforms into the editor's own local
        // space, so this rectangle test is correct even while the editor is rotated to match the text.
        if (_inlineTextEditor is not null && IsWithinInlineEditor(e))
            return;

        var screen = e.GetPosition(DrawCanvas);

        if (_isSpacePressed || ViewModel.ActiveTool == DesignerTool.Pan)
        {
            Focus();
            BeginPan(screen);
            e.Handled = true;
            return;
        }

        // VECTOR PATH TOOL HOOK: Line is a multi-click path tool (see SceneCanvas.VectorPathTool.cs),
        // not a one-shot drag primitive, so it is intercepted here before DesignerPrimitiveFactory ever
        // sees it. IsPrimitive(Line) still reports true elsewhere (cursor selection etc.), which is
        // harmless since every real drag path for Line now returns before reaching that check.
        if (ViewModel.ActiveTool == DesignerTool.Line)
        {
            Focus();
            if (e.ClickCount >= 2 && _pathToolDrawing)
                FinishVectorPath();
            else
                BeginVectorPathClick(screen);
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

        // Double-click an existing text object enters inline editing, regardless of which of these two
        // tools is active — Pan and shape-drawing already returned above, so Select/Text are the only
        // tools left where "the user double-clicked a piece of text" is unambiguous.
        // VECTOR PATH TOOL HOOK: double-click a selected vector path enters Node Edit mode; while that mode
        // is active every further Select-tool click on the canvas (that a node/handle hit target did not
        // already claim -- those route away via IsSelectionHandle above) belongs to
        // HandleNodeEditCanvasMouseDown, not the generic move/rubber-band logic below.
        if (e.ClickCount >= 2 && ViewModel.ActiveTool == DesignerTool.Select && _nodeEditObject is null)
        {
            var doubleClickedPath = HitTestScene(screen)
                .Select(candidate => candidate.Object)
                .FirstOrDefault(candidate => candidate.IsVectorPath && !candidate.IsLocked);
            if (doubleClickedPath is not null)
            {
                Focus();
                EnterNodeEditMode(doubleClickedPath);
                e.Handled = true;
                return;
            }
        }

        if (ViewModel.ActiveTool == DesignerTool.Select && _nodeEditObject is not null)
        {
            Focus();
            HandleNodeEditCanvasMouseDown(screen, e.ClickCount);
            e.Handled = true;
            return;
        }

        if (e.ClickCount >= 2 && ViewModel.ActiveTool is DesignerTool.Select or DesignerTool.Text)
        {
            var doubleClicked = HitTestScene(screen)
                .Select(candidate => candidate.Object)
                .FirstOrDefault(candidate => candidate.IsText && !candidate.IsLocked);
            if (doubleClicked is not null)
            {
                Focus();
                BeginInlineTextEdit(doubleClicked);
                e.Handled = true;
                return;
            }
        }

        if (ViewModel.ActiveTool == DesignerTool.Text)
        {
            Focus();

            // Never create a new TEXT object underneath an existing one — select what is already
            // there instead, the same way clicking existing artwork does with the Select tool.
            var hit = HitTestScene(screen).FirstOrDefault();
            if (hit is not null)
            {
                ViewModel.SelectedObjects.Clear();
                ViewModel.SelectedObjects.Add(hit.Object);
                e.Handled = true;
                return;
            }

            TextPlacementRequested?.Invoke(new Position(ToWorldX(screen.X), ToWorldY(screen.Y), 0));
            e.Handled = true;
            return;
        }

        if (ViewModel.ActiveTool == DesignerTool.Select)
        {
            var pointer = new Position(ToWorldX(screen.X), ToWorldY(screen.Y), 0);
            if (Keyboard.Modifiers == ModifierKeys.None &&
                ViewModel.SelectedObjects.Any(obj => !obj.IsLocked) &&
                SceneHitTester.IsInsideSelectionBounds(ViewModel.SelectedObjects, pointer))
            {
                // Photoshop/Inkscape-style move surface: after selection, the transform frame is the
                // target. Users should not have to re-acquire a 1 px cut contour to move it. Handles
                // already returned above, so resize/rotate keep priority over this broad move target.
                BeginMove(e);
                e.Handled = true;
                return;
            }

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

    // ------------------------------------------------------------------
    // Inline text editing - a TextBox laid directly over the rendered object, in the same screen
    // space (and using the same Transform.Apply -> ToCanvasX/Y conversion) as the selection handles,
    // so it tracks zoom, pan and the object own rotation without any ScaleTransform trick. Only the
    // wording is edited here; font/height/bold/italic/uppercase/weld stay on the selection bar text
    // controls, which already go through SceneViewModel.ApplyTextSource the same way this commits
    // through CommitTextEdit - both are one ReplaceObjectsCommand per commit, sharing one undo history.
    // ------------------------------------------------------------------

    private bool IsWithinInlineEditor(MouseButtonEventArgs e)
    {
        if (_inlineTextEditor is null) return false;
        var local = e.GetPosition(_inlineTextEditor);
        return local.X >= 0 && local.Y >= 0 &&
               local.X <= _inlineTextEditor.ActualWidth && local.Y <= _inlineTextEditor.ActualHeight;
    }

    private void BeginInlineTextEdit(SceneObject obj)
    {
        if (ViewModel is null || obj.Text is null || obj.IsLocked) return;
        if (ReferenceEquals(_editingTextObject, obj)) return;
        if (_editingTextObject is not null) CommitInlineTextEdit(applyChanges: true);

        if (ViewModel.SelectedObjects.Count != 1 || !ViewModel.SelectedObjects.Contains(obj))
        {
            ViewModel.SelectedObjects.Clear();
            ViewModel.SelectedObjects.Add(obj);
        }

        // Swap cleanly: the flattened contours and the live editor must never both be visible, or the
        // wording would appear to double while typing.
        if (_objectVisuals.TryGetValue(obj, out var hiddenPaths))
            foreach (var path in hiddenPaths) path.Visibility = Visibility.Collapsed;

        var editor = new TextBox
        {
            Text = obj.Text.Text,
            AcceptsReturn = true,
            AcceptsTab = false,
            TextWrapping = TextWrapping.NoWrap,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            Margin = new Thickness(0),
            Background = Brushes.Transparent,
            SelectionBrush = SelectionBrush,
            Foreground = TextForegroundBrush(obj),
            CaretBrush = TextForegroundBrush(obj),
            FontStyle = obj.Text.Italic ? FontStyles.Italic : FontStyles.Normal,
            FontWeight = obj.Text.Bold ? FontWeights.Bold : FontWeights.Normal,
            CharacterCasing = obj.Text.Uppercase ? CharacterCasing.Upper : CharacterCasing.Normal,
            RenderTransformOrigin = new Point(0, 0),
        };

        try
        {
            editor.FontFamily = new FontFamily(string.IsNullOrWhiteSpace(obj.Text.FontFamily)
                ? Lasero.Core.Scene.TextSource.DefaultFontFamily
                : obj.Text.FontFamily);
        }
        catch
        {
            editor.FontFamily = new FontFamily(Lasero.Core.Scene.TextSource.DefaultFontFamily);
        }

        editor.PreviewKeyDown += OnInlineEditorPreviewKeyDown;
        editor.LostKeyboardFocus += OnInlineEditorLostFocus;

        _editingTextObject = obj;
        _inlineTextEditor = editor;

        DrawCanvas.Children.Add(editor);
        PositionInlineTextEditor(obj, editor);

        editor.Focus();
        Keyboard.Focus(editor);
        // Typing over the placeholder should replace it immediately - the same reason double-clicking
        // a word normally selects it, just extended to the whole field on entry.
        editor.SelectAll();
    }

    private void PositionInlineTextEditor(SceneObject obj, TextBox editor)
    {
        var pivot = obj.LocalPivot;
        var topLeftWorld = obj.Transform.Apply(
            ObjectTransform.HandleLocalPoint(obj.LocalBounds, pivot, ResizeHandle.TopLeft), pivot);
        var topRightWorld = obj.Transform.Apply(
            ObjectTransform.HandleLocalPoint(obj.LocalBounds, pivot, ResizeHandle.TopRight), pivot);
        var bottomLeftWorld = obj.Transform.Apply(
            ObjectTransform.HandleLocalPoint(obj.LocalBounds, pivot, ResizeHandle.BottomLeft), pivot);

        var topLeftScreen = new Point(ToCanvasX(topLeftWorld.X), ToCanvasY(topLeftWorld.Y));
        var topRightScreen = new Point(ToCanvasX(topRightWorld.X), ToCanvasY(topRightWorld.Y));
        var bottomLeftScreen = new Point(ToCanvasX(bottomLeftWorld.X), ToCanvasY(bottomLeftWorld.Y));

        var widthPx = Distance(topLeftScreen, topRightScreen);
        var heightPx = Distance(topLeftScreen, bottomLeftScreen);
        // Screen-space angle derived from the same rotated corners the selection outline already
        // draws, rather than negating Transform.RotationDeg by hand - that keeps this correct under
        // the canvas Y-flip without having to reason about its sign separately.
        var angleDeg = Math.Atan2(
            topRightScreen.Y - topLeftScreen.Y, topRightScreen.X - topLeftScreen.X) * 180.0 / Math.PI;

        editor.Width = Math.Max(widthPx, 24);
        editor.Height = Math.Max(heightPx, 18);
        editor.FontSize = Math.Max(4, obj.Text!.HeightMm * Math.Abs(obj.Transform.ScaleY) * _scale);
        editor.RenderTransform = new RotateTransform(angleDeg);
        Canvas.SetLeft(editor, topLeftScreen.X);
        Canvas.SetTop(editor, topLeftScreen.Y);
    }

    private void OnInlineEditorPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            CommitInlineTextEdit(applyChanges: true);
            // Same Escape convention OnCanvasKeyDown already uses elsewhere: leave a non-Select tool
            // first, only clear the selection once already on Select.
            if (ViewModel is not null)
            {
                if (ViewModel.ActiveTool != DesignerTool.Select)
                    ViewModel.ActiveTool = DesignerTool.Select;
                else
                    ViewModel.SelectedObjects.Clear();
            }
            Focus();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Enter && !Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            CommitInlineTextEdit(applyChanges: true);
            Focus();
            e.Handled = true;
        }
    }

    private void OnInlineEditorLostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (_inlineTextEditor is null || !ReferenceEquals(sender, _inlineTextEditor)) return;
        CommitInlineTextEdit(applyChanges: true);
    }

    /// <summary>
    /// Detaches the live editor and, unless cancelled, pushes the edited wording as one undoable
    /// ReplaceObjectsCommand (or removes the object if it was emptied) via SceneViewModel.CommitTextEdit
    /// - never more than once per edit session, since this only runs from Escape, Enter, or losing
    /// keyboard focus, not from every keystroke.
    /// </summary>
    private void CommitInlineTextEdit(bool applyChanges)
    {
        if (_inlineTextEditor is null || _editingTextObject is null) return;

        var editor = _inlineTextEditor;
        var obj = _editingTextObject;
        _inlineTextEditor = null;
        _editingTextObject = null;

        editor.PreviewKeyDown -= OnInlineEditorPreviewKeyDown;
        editor.LostKeyboardFocus -= OnInlineEditorLostFocus;
        DrawCanvas.Children.Remove(editor);

        if (_objectVisuals.TryGetValue(obj, out var paths))
            foreach (var path in paths) path.Visibility = Visibility.Visible;

        if (applyChanges) ViewModel?.CommitTextEdit(obj, editor.Text);
    }

    private static Brush TextForegroundBrush(SceneObject obj)
    {
        var color = obj.LocalShapes.FirstOrDefault()?.LayerColor ?? Lasero.App.VectorTextFactory.DefaultColor;
        return new SolidColorBrush(Color.FromRgb(color.R, color.G, color.B));
    }

    private static double Distance(Point a, Point b) =>
        Math.Sqrt((b.X - a.X) * (b.X - a.X) + (b.Y - a.Y) * (b.Y - a.Y));

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
        var end = GetDrawEnd(screen);
        var preview = DesignerPrimitiveFactory.Create(
            _drawingTool,
            _drawStartWorld,
            end,
            Lasero.Core.Layers.RgbColor.Black);
        // Primitive factories normalize geometry around a local pivot and store the requested
        // position in Transform. Rendering LocalShapes directly therefore placed the dashed
        // preview around the scene origin instead of under the pointer. Use the same world-space
        // geometry path as the finished object so preview and result are pixel-identical.
        _toolPreviewVisual.Data = BuildCompoundGeometry(preview.GetWorldShapes());
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
        FlushExpensiveDragUpdate();
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

        // VECTOR PATH TOOL HOOK: the path preview (dashed line-so-far + tentative segment to the
        // cursor) needs to track the pointer on every move, not just while a mouse button is held --
        // between clicks _dragMode is None, which the switch below does not otherwise cover.
        if (ViewModel?.ActiveTool == DesignerTool.Line) UpdateVectorPathPreview(screen);

        switch (_dragMode)
        {
            case DragMode.Move:
            {
                var world = new Position(ToWorldX(screen.X), ToWorldY(screen.Y), 0);
                _pendingMoveDx = world.X - _dragStartWorld.X;
                _pendingMoveDy = world.Y - _dragStartWorld.Y;
                ApplyMovePreview(_pendingMoveDx, _pendingMoveDy);
                break;
            }
            case DragMode.Resize:
            case DragMode.Rotate:
            case DragMode.Pan:
                ScheduleExpensiveDragUpdate(screen);
                break;
            case DragMode.Select:
                UpdateRubberBand(screen);
                break;
            case DragMode.Draw:
                UpdateToolPreview(screen);
                break;
            case DragMode.PathTool:
                UpdateVectorPathPreview(screen);
                break;
            case DragMode.NodeEdit:
                UpdateNodeEditDrag(screen);
                break;
            case DragMode.NodeMarquee:
                UpdateRubberBand(screen);
                break;
        }
    }

    private void OnDrawCanvasMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        FlushExpensiveDragUpdate();
        switch (_dragMode)
        {
            case DragMode.Move: FinishMove(); break;
            case DragMode.Resize: FinishResize(); break;
            case DragMode.Rotate: FinishRotate(); break;
            case DragMode.Select: FinishRubberBand(); break;
            case DragMode.Draw: FinishDraw(e.GetPosition(DrawCanvas)); break;
            case DragMode.PathTool: EndVectorPathClick(e.GetPosition(DrawCanvas)); break;
            case DragMode.NodeEdit: FinishNodeEditDrag(); break;
            case DragMode.NodeMarquee: FinishNodeMarquee(); break;
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

        var endWorld = GetDrawEnd(endScreen);
        ViewModel.DrawPrimitive(_drawingTool, _drawStartWorld, endWorld);
    }

    private Position GetDrawEnd(Point screen)
    {
        var end = new Position(ToWorldX(screen.X), ToWorldY(screen.Y), 0);
        return Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)
            ? DesignerPrimitiveFactory.ConstrainEnd(_drawingTool, _drawStartWorld, end)
            : end;
    }

    private void FinishMove()
    {
        ClearMovePreview();
        if (ViewModel is null) { _dragStartTransforms.Clear(); return; }
        var commands = new List<ISceneCommand>();
        foreach (var (obj, before) in _dragStartTransforms)
        {
            if (obj.IsLocked || (_pendingMoveDx == 0 && _pendingMoveDy == 0)) continue;
            var after = before with { X = before.X + _pendingMoveDx, Y = before.Y + _pendingMoveDy };
            commands.Add(new TransformObjectCommand(obj, before, after));
        }
        _dragStartTransforms.Clear();
        _pendingMoveDx = 0;
        _pendingMoveDy = 0;

        if (commands.Count == 1) ViewModel.Execute(commands[0]);
        else if (commands.Count > 1) ViewModel.Execute(new CompositeSceneCommand(commands));
    }

    private void ApplyMovePreview(double dx, double dy)
    {
        var translation = new TranslateTransform(dx * _scale, -dy * _scale);
        foreach (var obj in _dragStartTransforms.Keys.Where(item => !item.IsLocked))
        {
            if (_objectVisuals.TryGetValue(obj, out var paths))
                foreach (var path in paths) path.RenderTransform = translation;
            if (_rasterImageVisuals.TryGetValue(obj, out var image)) image.RenderTransform = translation;
        }
        foreach (var visual in _selectionVisuals) visual.RenderTransform = translation;
    }

    private void ClearMovePreview()
    {
        foreach (var obj in _dragStartTransforms.Keys)
        {
            if (_objectVisuals.TryGetValue(obj, out var paths))
                foreach (var path in paths) path.RenderTransform = Transform.Identity;
            if (_rasterImageVisuals.TryGetValue(obj, out var image)) image.RenderTransform = Transform.Identity;
        }
        foreach (var visual in _selectionVisuals) visual.RenderTransform = Transform.Identity;
    }

    private void ScheduleExpensiveDragUpdate(Point screen)
    {
        _pendingExpensiveDragScreen = screen;
        if (_expensiveDragUpdateScheduled) return;

        _expensiveDragUpdateScheduled = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Render, () =>
        {
            if (!_expensiveDragUpdateScheduled) return;
            _expensiveDragUpdateScheduled = false;
            ApplyExpensiveDragUpdate(_pendingExpensiveDragScreen);
        });
    }

    private void FlushExpensiveDragUpdate()
    {
        if (!_expensiveDragUpdateScheduled) return;
        _expensiveDragUpdateScheduled = false;
        ApplyExpensiveDragUpdate(_pendingExpensiveDragScreen);
    }

    private void ApplyExpensiveDragUpdate(Point screen)
    {
        switch (_dragMode)
        {
            case DragMode.Resize when _activeSingleObject is not null:
            {
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
            case DragMode.Rotate when _activeSingleObject is not null:
            {
                var pivotWorld = _rotateStartTransform.Apply(_activeSingleObject.LocalPivot, _activeSingleObject.LocalPivot);
                var world = new Position(ToWorldX(screen.X), ToWorldY(screen.Y), 0);
                var currentAngle = Math.Atan2(world.Y - pivotWorld.Y, world.X - pivotWorld.X) * 180.0 / Math.PI;
                var newRotation = _rotateStartTransform.RotationDeg + (currentAngle - _rotateStartAngleDeg);
                if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
                    newRotation = Math.Round(newRotation / 15.0) * 15.0;
                _activeSingleObject.Transform = _rotateStartTransform with { RotationDeg = newRotation };
                break;
            }
            case DragMode.Pan:
            {
                var dxPx = screen.X - _dragStartScreen.X;
                var dyPx = screen.Y - _dragStartScreen.Y;
                _offsetXMm = _panStartOffsetX - dxPx / _scale;
                _offsetYMm = _panStartOffsetY + dyPx / _scale;
                RepositionAll();
                break;
            }
        }
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
        if (e.Key is Key.LeftShift or Key.RightShift && _dragMode == DragMode.Draw)
        {
            UpdateToolPreview(Mouse.GetPosition(DrawCanvas));
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Space)
        {
            _isSpacePressed = true;
            if (_dragMode == DragMode.None) Cursor = Cursors.ScrollAll;
            e.Handled = true;
            return;
        }

        if (ViewModel is null) return;

        // VECTOR PATH TOOL HOOK: Delete removes selected nodes while Node Edit mode is active, instead
        // of the whole-object Delete bound at the Window level (MainWindow.xaml) -- marking Handled here
        // stops that binding from also firing on the same key press.
        if (e.Key == Key.Delete && _nodeEditObject is not null)
        {
            DeleteSelectedNodes();
            e.Handled = true;
            return;
        }

        // VECTOR PATH TOOL HOOK: Enter finishes the in-progress open path, mirroring double-click.
        if (e.Key == Key.Enter && _pathToolDrawing)
        {
            FinishVectorPath();
            e.Handled = true;
            return;
        }

        // VECTOR PATH TOOL HOOK: layered Escape, both cases only between clicks/drags (_dragMode ==
        // None) -- while an actual drag is in progress the generic CancelActiveInteraction block below
        // already owns Escape via its own DragMode.PathTool/NodeEdit cases.
        if (e.Key == Key.Escape && _dragMode == DragMode.None)
        {
            if (_nodeEditObject is not null)
            {
                ExitNodeEditMode();
                e.Handled = true;
                return;
            }
            if (_pathToolDrawing)
            {
                CancelLastVectorPathNode();
                e.Handled = true;
                return;
            }
        }

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
        if (e.Key is Key.LeftShift or Key.RightShift && _dragMode == DragMode.Draw)
        {
            UpdateToolPreview(Mouse.GetPosition(DrawCanvas));
            e.Handled = true;
            return;
        }

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
                ClearMovePreview();
                _dragStartTransforms.Clear();
                _pendingMoveDx = 0;
                _pendingMoveDy = 0;
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
            case DragMode.PathTool:
                // Cancels only the node currently being pressed/dragged, not the whole in-progress
                // path -- CancelLastVectorPathNode (wired from OnCanvasKeyDown) owns the "undo the
                // last committed node, then exit the tool" layering for Escape between clicks.
                break;
            case DragMode.NodeEdit:
                CancelNodeEditDrag();
                break;
            case DragMode.NodeMarquee when _rubberBandVisual is not null:
                _rubberBandVisual.Visibility = Visibility.Collapsed;
                break;
        }

        _expensiveDragUpdateScheduled = false;
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
