using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Lasero.App.ViewModels;
using Lasero.Core.Grbl;
using Lasero.Core.Import;
using Lasero.Core.Layers;
using Lasero.Core.Scene;
using Lasero.Core.Scene.Snapping;

namespace Lasero.App.Controls;

/// <summary>
/// The "Čára" tool (multi-click vector path drawing) and Node Edit mode (double-click a drawn path to
/// edit its nodes/handles). Split into its own partial-class file rather than growing the already large
/// SceneCanvas.xaml.cs further; touches to the main file are limited to a handful of small dispatch
/// hooks (see the "VECTOR PATH TOOL HOOK" comments there): the DragMode enum gaining PathTool/NodeEdit,
/// mouse-down/move/up routing, Escape/Enter/Delete layering in OnCanvasKeyDown, IsSelectionHandle
/// recognizing node/handle hit targets, and RedrawSelectionOverlay branching to DrawNodeEditOverlay.
///
/// Design notes (see also the boundary comment at the top of VectorPath.cs):
/// - While a path is being drawn, nothing is added to the scene yet — only a local `_pathNodes` list
///   and a dashed WPF preview are kept, reusing the existing `_toolPreviewVisual`/EnsureToolPreviewVisual
///   the old drag-based shape tools already use. The whole multi-click drawing session becomes ONE
///   AddObjectCommand when finished (double-click/Enter/close), matching "one undo step per gesture"
///   where the gesture is "draw this path", not "place this node".
/// - While a node/handle is being dragged in Node Edit mode, the live curve update goes straight into
///   the selected object's own LocalShapes (bypassing the command stack entirely, the same way
///   ApplyMovePreview/ClearMovePreview give whole-object drags a live preview before FinishMove commits
///   one TransformObjectCommand) and reuses the existing UpdateObjectGeometry repaint path — no new
///   rendering code duplicating BuildCompoundGeometry/grouping. Only mouse-up commits, via
///   SceneViewModel.CommitVectorPathEdit (one ReplaceObjectsCommand), matching CommitTextEdit's
///   precedent for "this object's geometry changed" edits.
/// </summary>
public partial class SceneCanvas
{
    private readonly record struct NodeHitTag(int Subpath, int Node, bool IsHandle, bool IsOutHandle);

    // The visual-refinement pass's 6px shrink (from an original 9/7) read as too small to see/hit
    // comfortably once live — reverted upward past the original size, to 10/8, per direct user
    // feedback. NodeHitSizePx/HandleDotHitSizePx (the invisible hit-test targets) are untouched by
    // design either way — see their own usages further down, each a separate transparent Ellipse
    // the visible dot merely sits inside.
    private const double NodeVisibleSizePx = 10;
    private const double NodeHitSizePx = 18;
    private const double HandleVisibleSizePx = 8;
    private const double HandleDotHitSizePx = 16;
    private const double CloseHoverThresholdPx = 12;
    private const double SegmentInsertTolerancePx = 8;

    // Screen-space so the snap "feel" stays constant across zoom levels, matching every other hit
    // tolerance in this file (NodeHitSizePx etc.) — converted to world mm once per drag via
    // SnapToleranceScreenPx / _scale, never compared in screen space itself.
    private const double SnapToleranceScreenPx = 10;
    private const double SnapIndicatorVisibleSizePx = 14;

    private static readonly RgbColor VectorPathDefaultColor = new(18, 18, 18);

    // --- multi-click path drawing state -------------------------------------------------------
    private readonly List<VectorNode> _pathNodes = [];
    private readonly List<FrameworkElement> _pathToolVisuals = [];
    private bool _pathToolDrawing;
    private bool _pathClosingClick;
    private Position _pathNodeDownWorld;
    private Point _pathNodeDownScreen;

    // --- node edit mode state -------------------------------------------------------------------
    private SceneObject? _nodeEditObject;
    private bool _committingNodeEdit;
    private VectorPath? _nodeEditWorkingPath;
    private readonly HashSet<(int Subpath, int Node)> _selectedNodeKeys = [];
    private (int Subpath, int Node, bool IsOutHandle)? _draggedHandle;
    private VectorPathHitTester.Hit? _draggedSegment;
    private VectorPath? _nodeDragOriginalPath;
    private VectorPathDragSession? _nodeDragSession;
    private Position _nodeDragStartWorld;
    private bool _nodeMarqueeHitObject;

    // --- node/handle/segment hover state (presentation only — see LIGHTBURN_VECTOR_PARITY.md §63) ---
    // Node/handle hover is driven by WPF MouseEnter/Leave on their own hit-test elements (cheap: only
    // fires on element-boundary crossings, not per pointer-move sample). Segment hover has no
    // dedicated per-segment element, so it is polled from OnCanvasMouseMove, but only while idle
    // (_dragMode == None) and only triggers a redraw when the hovered segment actually changes.
    private (int Subpath, int Node)? _hoveredNodeKey;
    private (int Subpath, int Node, bool IsOutHandle)? _hoveredHandleKey;
    private VectorPathHitTester.Hit? _hoveredSegment;

    // --- snapping (node/endpoint drag only — see BuildNodeDragSnapCandidates) -------------------
    // Built ONCE at drag-start from the pre-drag path snapshot, then only queried (never rebuilt)
    // for every pointer-move sample of that same drag — see SnapCandidateBuilder's own doc comment
    // on why that is both correct (no drift from the dragged node's own live motion) and cheap.
    private List<SnapCandidate> _nodeDragSnapCandidates = [];
    private SnapCandidate? _activeSnapTarget;

    // ------------------------------------------------------------------
    // Drawing — one click stream builds one VectorSubpath, committed as a single SceneObject only
    // when the path is finished (double-click / Enter / close-on-first-node) or discarded on Escape.
    // ------------------------------------------------------------------

    private void BeginVectorPathClick(Point screen)
    {
        var world = new Position(ToWorldX(screen.X), ToWorldY(screen.Y), 0);
        _pathClosingClick = _pathToolDrawing && _pathNodes.Count >= 2 && IsNearFirstNode(screen);
        _pathNodeDownScreen = screen;
        _pathNodeDownWorld = world;
        _dragMode = DragMode.PathTool;
        DrawCanvas.CaptureMouse();
        UpdateVectorPathPreview(screen);
    }

    private void EndVectorPathClick(Point screen)
    {
        var world = new Position(ToWorldX(screen.X), ToWorldY(screen.Y), 0);
        var dxPx = screen.X - _pathNodeDownScreen.X;
        var dyPx = screen.Y - _pathNodeDownScreen.Y;
        // Same "was this a real drag" pixel threshold FinishDraw already uses for the old shape tools —
        // below it, the node is a plain corner even if the pointer trembled a pixel or two.
        var isDrag = Math.Abs(dxPx) >= 3 || Math.Abs(dyPx) >= 3;
        Position? dragOffset = isDrag
            ? new Position(world.X - _pathNodeDownWorld.X, world.Y - _pathNodeDownWorld.Y, 0)
            : null;

        if (_pathClosingClick)
        {
            var subpath = new VectorSubpath { Nodes = [.. _pathNodes], IsClosed = false };
            var closed = VectorPathEditor.Close(subpath, dragOffset);
            CommitVectorPath(closed);
        }
        else
        {
            var subpath = VectorPathEditor.AppendNode(
                new VectorSubpath { Nodes = [.. _pathNodes], IsClosed = false }, _pathNodeDownWorld, dragOffset);
            _pathNodes.Clear();
            _pathNodes.AddRange(subpath.Nodes);
            _pathToolDrawing = true;
            UpdateVectorPathPreview(screen);
        }

        _dragMode = DragMode.None;
        if (DrawCanvas.IsMouseCaptured) DrawCanvas.ReleaseMouseCapture();
        UpdateToolCursor();
    }

    /// <summary>Finishes the in-progress path as OPEN (double-click or Enter). Fewer than two nodes is
    /// not a usable object — silently discarded, tool stays active for another attempt.</summary>
    private void FinishVectorPath()
    {
        if (_pathNodes.Count >= 2)
            CommitVectorPath(new VectorSubpath { Nodes = [.. _pathNodes], IsClosed = false });
        else
            ResetVectorPathToolState();

        _dragMode = DragMode.None;
        if (DrawCanvas.IsMouseCaptured) DrawCanvas.ReleaseMouseCapture();
        UpdateToolCursor();
    }

    private void CommitVectorPath(VectorSubpath subpath)
    {
        if (ViewModel is not null && subpath.Nodes.Count >= 2)
            ViewModel.AddVectorPath(new VectorPath { Subpaths = [subpath] });
        ResetVectorPathToolState();
    }

    /// <summary>Esc's first layer while drawing: undo the most recently placed node (the "uncommitted
    /// segment" the spec refers to — this tool commits a node the instant its click is released, so the
    /// closest equivalent is dropping that last commit). A second Esc with no nodes left exits the tool,
    /// matching the layered-Escape convention the text tool already established elsewhere in this file.</summary>
    private void CancelLastVectorPathNode()
    {
        if (_pathNodes.Count > 0)
        {
            _pathNodes.RemoveAt(_pathNodes.Count - 1);
            if (_pathNodes.Count == 0)
            {
                ResetVectorPathToolState();
                if (ViewModel is not null) ViewModel.ActiveTool = DesignerTool.Select;
            }
            else
            {
                UpdateVectorPathPreview(Mouse.GetPosition(DrawCanvas));
            }
        }
        else if (ViewModel is not null)
        {
            ViewModel.ActiveTool = DesignerTool.Select;
        }
    }

    private void ResetVectorPathToolState()
    {
        _pathNodes.Clear();
        _pathToolDrawing = false;
        _pathClosingClick = false;
        ClearVectorPathPreviewVisuals();
        if (_toolPreviewVisual is not null) _toolPreviewVisual.Visibility = Visibility.Collapsed;
    }

    private bool IsNearFirstNode(Point screen)
    {
        if (_pathNodes.Count == 0) return false;
        var first = new Point(ToCanvasX(_pathNodes[0].Anchor.X), ToCanvasY(_pathNodes[0].Anchor.Y));
        return Distance(first, screen) <= CloseHoverThresholdPx;
    }

    private void UpdateVectorPathPreview(Point cursorScreen)
    {
        if (ViewModel is null || ViewModel.ActiveTool != DesignerTool.Line || !_pathToolDrawing)
        {
            ClearVectorPathPreviewVisuals();
            return;
        }

        ClearVectorPathPreviewVisuals();
        var cursorWorld = new Position(ToWorldX(cursorScreen.X), ToWorldY(cursorScreen.Y), 0);
        var isPressed = _dragMode == DragMode.PathTool;
        IReadOnlyList<VectorNode> previewNodes = _pathNodes;

        if (!(isPressed && _pathClosingClick))
        {
            var tentativeAnchor = isPressed ? _pathNodeDownWorld : cursorWorld;
            Position? dragOffset = isPressed
                ? new Position(cursorWorld.X - _pathNodeDownWorld.X, cursorWorld.Y - _pathNodeDownWorld.Y, 0)
                : null;
            var previewSubpath = VectorPathEditor.AppendNode(
                new VectorSubpath { Nodes = previewNodes, IsClosed = false }, tentativeAnchor, dragOffset);
            previewNodes = previewSubpath.Nodes;
        }

        var flattened = new VectorSubpath { Nodes = previewNodes, IsClosed = false }.Flatten();
        EnsureToolPreviewVisual();
        _toolPreviewVisual!.Data = BuildCompoundGeometry(
        [
            new ImportedShape
            {
                Points = flattened, IsClosed = false, LayerColor = VectorPathDefaultColor, PreferredMode = LayerMode.Cut,
            },
        ]);
        _toolPreviewVisual.Visibility = Visibility.Visible;

        var hoveringClose = !isPressed && _pathNodes.Count >= 2 && IsNearFirstNode(cursorScreen);
        for (var i = 0; i < _pathNodes.Count; i++)
        {
            var screen = new Point(ToCanvasX(_pathNodes[i].Anchor.X), ToCanvasY(_pathNodes[i].Anchor.Y));
            var highlight = i == 0 && (hoveringClose || (isPressed && _pathClosingClick));
            AddPathToolDot(screen, highlight);
        }
    }

    private void AddPathToolDot(Point screen, bool highlight)
    {
        var size = highlight ? NodeVisibleSizePx + 4 : NodeVisibleSizePx;
        var dot = new Ellipse
        {
            Width = size,
            Height = size,
            Fill = highlight ? SelectionBrush : SelectionHandleFill,
            Stroke = SelectionBrush,
            StrokeThickness = highlight ? 1.6 : 1.2,
            IsHitTestVisible = false,
        };
        Canvas.SetLeft(dot, screen.X - size / 2);
        Canvas.SetTop(dot, screen.Y - size / 2);
        DrawCanvas.Children.Add(dot);
        _pathToolVisuals.Add(dot);
    }

    private void ClearVectorPathPreviewVisuals()
    {
        foreach (var element in _pathToolVisuals) DrawCanvas.Children.Remove(element);
        _pathToolVisuals.Clear();
    }

    // ------------------------------------------------------------------
    // Node Edit mode — double-click a selected VectorPath object (see the main file's double-click
    // dispatch) to show its nodes/handles. Only objects created/edited by this tool (SceneObject.
    // VectorPath is not null) support this, per this pass's scope decision.
    // ------------------------------------------------------------------

    /// <summary>Public — SelectionPropertiesBar's "Upravit uzly" button (and the double-click gesture
    /// this control's own mouse handling already uses) both need a mouse-driven way to enter Node
    /// Edit: double-clicking a thin curved stroke precisely is not a reliable or discoverable primary
    /// entry point on its own, so a plain button on the selection toolbar — visible whenever exactly
    /// one selected object is node-editable — is the one this app should lead with.</summary>
    public void EnterNodeEditMode(SceneObject obj)
    {
        if (obj.VectorPath is null || ViewModel is null) return;
        _nodeEditObject = obj;
        _nodeEditWorkingPath = obj.VectorPath;
        _selectedNodeKeys.Clear();
        if (ViewModel.SelectedObjects.Count != 1 || !ViewModel.SelectedObjects.Contains(obj))
        {
            ViewModel.SelectedObjects.Clear();
            ViewModel.SelectedObjects.Add(obj);
        }
        RedrawSelectionOverlay();
    }

    /// <summary>Visible toolbar exit uses the same cancellation and cleanup as Enter/Escape.</summary>
    public void FinishNodeEditMode() => ExitNodeEditMode();

    private void ExitNodeEditMode()
    {
        if (_dragMode is DragMode.NodeEdit or DragMode.NodeSegmentDrag)
            CancelActiveInteraction();

        // In case this fires mid-drag (e.g. a tool-switch shortcut while a node is captured), restore
        // the object's LocalShapes to their pre-drag snapshot first — same guarantee CancelNodeEditDrag
        // gives an explicit Escape.
        var interrupted = _nodeEditObject is not null && _nodeDragSession is not null ? _nodeEditObject : null;
        if (interrupted is not null)
            interrupted.LocalShapes = _nodeDragSession!.OriginalShapes;
        _nodeEditObject = null;
        _nodeEditWorkingPath = null;
        _selectedNodeKeys.Clear();
        _draggedHandle = null;
        _draggedSegment = null;
        _nodeDragOriginalPath = null;
        _nodeDragSession = null;
        _nodeDragSnapCandidates = [];
        _activeSnapTarget = null;
        _hoveredNodeKey = null;
        _hoveredHandleKey = null;
        _hoveredSegment = null;
        // An interrupted drag had its live frame drawn; put the object's own curve back.
        if (interrupted is not null && _objectVisuals.ContainsKey(interrupted))
            UpdateObjectGeometry(interrupted);
        RedrawSelectionOverlay();
    }

    /// <summary>Every Select-tool click on the canvas while node-edit is active is handled here (node/
    /// handle hit targets have already routed themselves away via IsSelectionHandle before this runs).
    /// Double-click near a segment inserts a node there. A plain click that is not on the edited
    /// object's own geometry exits node-edit, same as before; a click that turns into a real drag
    /// starts a marquee instead (see FinishNodeMarquee, which owns the "was this actually a click"
    /// decision once the gesture ends, mirroring FinishRubberBand's own threshold for whole-object
    /// selection).</summary>
    private void HandleNodeEditCanvasMouseDown(Point screen, int clickCount)
    {
        if (_nodeEditObject is null || _nodeEditWorkingPath is null || ViewModel is null) return;
        var obj = _nodeEditObject;

        if (clickCount >= 2)
        {
            var hit = HitTestNearestSegment(obj, _nodeEditWorkingPath, screen, SegmentInsertTolerancePx);
            if (hit is { } segmentHit)
            {
                var subpath = _nodeEditWorkingPath.Subpaths[segmentHit.SubpathIndex];
                var updatedSubpath = VectorPathEditor.InsertNode(subpath, segmentHit.SegmentIndex, segmentHit.T);
                _selectedNodeKeys.Clear(); // see InsertNodeAtHoveredSegmentMidpoint's own comment
                CommitNodeEdit(_nodeEditWorkingPath.ReplaceSubpath(segmentHit.SubpathIndex, updatedSubpath));
                return;
            }
        }
        else
        {
            // LIGHTBURN_VECTOR_PARITY.md §4/§15: node/handle hits have already routed themselves away
            // via their own WPF elements before this dispatch runs, so a plain single click that lands
            // on a segment body (next in the hit-test priority) grabs and reshapes that segment,
            // clamping t away from the endpoints so this never fights with node/handle hit targets.
            var segmentHit = HitTestNearestSegment(obj, _nodeEditWorkingPath, screen, SegmentInsertTolerancePx);
            if (segmentHit is { } hit && hit.T is > 0.08 and < 0.92)
            {
                BeginSegmentDrag(hit);
                return;
            }
        }

        _nodeMarqueeHitObject = HitTestScene(screen).Any(candidate => candidate.Object == obj);
        _dragMode = DragMode.NodeMarquee;
        _dragStartScreen = screen;
        EnsureRubberBandVisual();
        _rubberBandVisual!.Visibility = Visibility.Visible;
        Canvas.SetLeft(_rubberBandVisual, screen.X);
        Canvas.SetTop(_rubberBandVisual, screen.Y);
        _rubberBandVisual.Width = 0;
        _rubberBandVisual.Height = 0;
        DrawCanvas.CaptureMouse();
    }

    /// <summary>Node-edit's own marquee, reusing the same rubber-band visual whole-object selection
    /// draws (EnsureRubberBandVisual/UpdateRubberBand) — just a different "what does the rectangle
    /// mean" on release. Below the real-drag threshold this is a plain click: exits node-edit if it
    /// missed the edited object's geometry, same as the pre-marquee behaviour, and otherwise does
    /// nothing (clicking the shape's own fill without hitting a node keeps the current selection).</summary>
    private void FinishNodeMarquee()
    {
        if (_rubberBandVisual is null) return;
        var left = Canvas.GetLeft(_rubberBandVisual);
        var top = Canvas.GetTop(_rubberBandVisual);
        var w = _rubberBandVisual.Width;
        var h = _rubberBandVisual.Height;
        _rubberBandVisual.Visibility = Visibility.Collapsed;

        if (w <= 2 && h <= 2)
        {
            if (!_nodeMarqueeHitObject) ExitNodeEditMode();
            return;
        }

        if (_nodeEditObject is null || _nodeEditWorkingPath is null) return;
        var obj = _nodeEditObject;
        var worldMinX = ToWorldX(left);
        var worldMaxX = ToWorldX(left + w);
        var worldMinY = ToWorldY(top + h);
        var worldMaxY = ToWorldY(top);

        // LIGHTBURN_VECTOR_PARITY.md §6: none = replace, Shift = add, Ctrl = subtract.
        var subtract = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        if (!Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) && !subtract) _selectedNodeKeys.Clear();

        var path = _nodeEditWorkingPath;
        for (var s = 0; s < path.Subpaths.Count; s++)
        {
            var nodes = path.Subpaths[s].Nodes;
            for (var n = 0; n < nodes.Count; n++)
            {
                var world = obj.Transform.Apply(nodes[n].Anchor, obj.LocalPivot);
                if (world.X < worldMinX || world.X > worldMaxX || world.Y < worldMinY || world.Y > worldMaxY) continue;
                if (subtract) _selectedNodeKeys.Remove((s, n));
                else _selectedNodeKeys.Add((s, n));
            }
        }

        RedrawSelectionOverlay();
    }

    private VectorPathHitTester.Hit? HitTestNearestSegment(SceneObject obj, VectorPath path, Point screen, double toleranceScreenPx)
    {
        var pointer = new Position(screen.X, screen.Y, 0);
        return VectorPathHitTester.FindNearest(
            path,
            pointer,
            toleranceScreenPx,
            local =>
            {
                var world = obj.Transform.Apply(local, obj.LocalPivot);
                return new Position(ToCanvasX(world.X), ToCanvasY(world.Y), 0);
            });
    }

    private void DrawNodeEditOverlay(SceneObject obj)
    {
        var path = _nodeEditWorkingPath ?? obj.VectorPath;
        if (path is null) return;

        // Hover feedback only makes sense while idle -- mid-drag, the dragged item's own live motion
        // already is the feedback, and a stale hover from before the drag started would be misleading.
        var showHover = _dragMode is DragMode.None or DragMode.NodeMarquee;
        if (showHover && _hoveredSegment is { } hoveredSeg) DrawSegmentHoverHighlight(obj, path, hoveredSeg);

        for (var s = 0; s < path.Subpaths.Count; s++)
        {
            var subpath = path.Subpaths[s];
            for (var n = 0; n < subpath.Nodes.Count; n++)
            {
                var node = subpath.Nodes[n];
                var worldAnchor = obj.Transform.Apply(node.Anchor, obj.LocalPivot);
                var screenAnchor = new Point(ToCanvasX(worldAnchor.X), ToCanvasY(worldAnchor.Y));
                var isSelected = _selectedNodeKeys.Contains((s, n));

                if (node.HandleIn is { } handleIn)
                    DrawHandle(obj, s, n, handleIn, screenAnchor, isOutHandle: false, isHovered: showHover && _hoveredHandleKey == (s, n, false));
                if (node.HandleOut is { } handleOut)
                    DrawHandle(obj, s, n, handleOut, screenAnchor, isOutHandle: true, isHovered: showHover && _hoveredHandleKey == (s, n, true));

                var isOpenEndpoint = !subpath.IsClosed && (n == 0 || n == subpath.Nodes.Count - 1);
                DrawNodeDot(s, n, screenAnchor, isSelected, node.Type, isOpenEndpoint,
                    isHovered: showHover && _hoveredNodeKey == (s, n));
            }
        }

        if (_activeSnapTarget is { } snap) DrawActiveSnapIndicator(snap);
    }

    /// <summary>Priority 3 — the one visual cue every mainstream vector/CAD editor gives while a
    /// point is snapped: a small ring at the resolved target, distinct in colour from selection
    /// (Brush.Success — matches the theme's own "confirmed/good" role and is already validated for
    /// both Light and Dark, unlike inventing a new token here) and in shape from a plain node dot, so
    /// it reads as "you are here" rather than "here is another node". Drawn AFTER (on top of) every
    /// node/handle in DrawNodeEditOverlay so it is never occluded by the geometry it is annotating.</summary>
    private void DrawActiveSnapIndicator(SnapCandidate snap)
    {
        var screen = new Point(ToCanvasX(snap.Point.X), ToCanvasY(snap.Point.Y));
        var brush = (Brush)FindResource("Brush.Success");
        var ring = new Ellipse
        {
            Width = SnapIndicatorVisibleSizePx,
            Height = SnapIndicatorVisibleSizePx,
            Stroke = brush,
            StrokeThickness = 2,
            Fill = Brushes.Transparent,
            IsHitTestVisible = false,
        };
        Canvas.SetLeft(ring, screen.X - SnapIndicatorVisibleSizePx / 2);
        Canvas.SetTop(ring, screen.Y - SnapIndicatorVisibleSizePx / 2);
        DrawCanvas.Children.Add(ring);
        _selectionVisuals.Add(ring);

        // Grid/Intersection/Midpoint targets have no node of their own already drawn at that spot to
        // anchor on, so a plain ring alone reads ambiguous there — a small crosshair through it makes
        // "this exact point" unmistakable. Node/Endpoint targets already have their own dot underneath
        // the ring, so the crosshair would just be visual noise on top of it.
        if (snap.Kind is SnapKind.Node or SnapKind.Endpoint) return;
        var half = SnapIndicatorVisibleSizePx / 2 + 3;
        var crosshairH = new Line { X1 = screen.X - half, X2 = screen.X + half, Y1 = screen.Y, Y2 = screen.Y, Stroke = brush, StrokeThickness = 1, IsHitTestVisible = false };
        var crosshairV = new Line { X1 = screen.X, X2 = screen.X, Y1 = screen.Y - half, Y2 = screen.Y + half, Stroke = brush, StrokeThickness = 1, IsHitTestVisible = false };
        DrawCanvas.Children.Add(crosshairH);
        DrawCanvas.Children.Add(crosshairV);
        _selectionVisuals.Add(crosshairH);
        _selectionVisuals.Add(crosshairV);
    }

    /// <summary>A translucent, thickened overlay stroke tracing the hovered segment's own flattened
    /// points (not a generic straight line) — drawn UNDERNEATH the node dots (added to the overlay
    /// before the node loop below) so the dots themselves stay on top, matching LIGHTBURN_VECTOR_
    /// PARITY.md §63's "highlight the target before a contextual operation" (here: before Delete).</summary>
    private void DrawSegmentHoverHighlight(SceneObject obj, VectorPath path, VectorPathHitTester.Hit hit)
    {
        var subpath = path.Subpaths[hit.SubpathIndex];
        var (a, b) = subpath.Segment(hit.SegmentIndex);
        var localPoints = new List<Position> { a.Anchor };
        if (VectorSubpath.IsStraightSegment(a, b))
        {
            localPoints.Add(b.Anchor);
        }
        else
        {
            CubicBezier.Flatten(a.Anchor, a.HandleOut ?? a.Anchor, b.HandleIn ?? b.Anchor, b.Anchor, VectorPath.DefaultFlattenToleranceMm, localPoints);
        }

        var poly = new Polyline
        {
            Stroke = SelectionBrush,
            StrokeThickness = 4,
            Opacity = 0.35,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            IsHitTestVisible = false,
        };
        foreach (var local in localPoints)
        {
            var world = obj.Transform.Apply(local, obj.LocalPivot);
            poly.Points.Add(new Point(ToCanvasX(world.X), ToCanvasY(world.Y)));
        }
        DrawCanvas.Children.Add(poly);
        _selectionVisuals.Add(poly);
    }

    /// <summary>Corner nodes draw as a small square, smooth nodes as a circle — the same
    /// square-corner/round-smooth convention Illustrator and Figma both use, so the node's type is
    /// legible on the canvas itself instead of only through the right-click toggle that sets it. The
    /// hit target underneath is always the same transparent circle regardless of type; only the
    /// visible dot's shape changes. Hover grows the dot by 2px and thickens its stroke (the same
    /// "grow + thicken, don't recolor" treatment AddPathToolDot already uses for the draw-tool's own
    /// close-hover highlight) — kept deliberately distinct from Selected (solid fill) so hover and
    /// selection never read as the same state, per UI_DESIGN_PRINCIPLES.md. An OPEN ENDPOINT
    /// additionally gets a thin outer ring around whichever of the two shapes it already is —
    /// LIGHTBURN_VECTOR_PARITY.md §3 (an open path's ends must be visually unmistakable, not inferred
    /// by counting nodes) — rather than a third dot shape, so Corner/Smooth legibility is not lost for
    /// the common case of an open path made of straight or curved segments alike.</summary>
    private void DrawNodeDot(int subpathIndex, int nodeIndex, Point screen, bool isSelected, VectorNodeType type, bool isOpenEndpoint, bool isHovered = false)
    {
        var target = new Ellipse
        {
            Width = NodeHitSizePx,
            Height = NodeHitSizePx,
            Fill = Brushes.Transparent,
            Tag = new NodeHitTag(subpathIndex, nodeIndex, IsHandle: false, IsOutHandle: false),
            Cursor = Cursors.Hand,
        };
        target.MouseLeftButtonDown += OnNodeOrHandleMouseLeftButtonDown;
        target.MouseRightButtonDown += OnNodeRightButtonDown;
        target.MouseEnter += (_, _) => SetHoveredNode(subpathIndex, nodeIndex);
        target.MouseLeave += (_, _) => ClearHoveredNode(subpathIndex, nodeIndex);
        Canvas.SetLeft(target, screen.X - NodeHitSizePx / 2);
        Canvas.SetTop(target, screen.Y - NodeHitSizePx / 2);
        DrawCanvas.Children.Add(target);
        _selectionVisuals.Add(target);

        var size = isHovered ? NodeVisibleSizePx + 2 : NodeVisibleSizePx;
        var fill = isSelected ? SelectionBrush : SelectionHandleFill;

        if (isOpenEndpoint)
        {
            var ringSize = size + 6;
            var ring = new Ellipse
            {
                Width = ringSize,
                Height = ringSize,
                Stroke = SelectionBrush,
                StrokeThickness = 1.2,
                Fill = Brushes.Transparent,
                IsHitTestVisible = false,
            };
            Canvas.SetLeft(ring, screen.X - ringSize / 2);
            Canvas.SetTop(ring, screen.Y - ringSize / 2);
            DrawCanvas.Children.Add(ring);
            _selectionVisuals.Add(ring);
        }

        Shape dot = type == VectorNodeType.Corner
            ? new Rectangle { Width = size, Height = size }
            : new Ellipse { Width = size, Height = size };
        dot.Fill = fill;
        dot.Stroke = SelectionBrush;
        dot.StrokeThickness = isHovered ? 1.8 : 1.2;
        dot.IsHitTestVisible = false;
        Canvas.SetLeft(dot, screen.X - size / 2);
        Canvas.SetTop(dot, screen.Y - size / 2);
        DrawCanvas.Children.Add(dot);
        _selectionVisuals.Add(dot);
    }

    private void SetHoveredNode(int subpathIndex, int nodeIndex)
    {
        _hoveredNodeKey = (subpathIndex, nodeIndex);
        RedrawSelectionOverlay();
    }

    private void ClearHoveredNode(int subpathIndex, int nodeIndex)
    {
        if (_hoveredNodeKey != (subpathIndex, nodeIndex)) return; // a newer hover already replaced it
        _hoveredNodeKey = null;
        RedrawSelectionOverlay();
    }

    private void DrawHandle(SceneObject obj, int subpathIndex, int nodeIndex, Position localHandle, Point anchorScreen, bool isOutHandle, bool isHovered = false)
    {
        var world = obj.Transform.Apply(localHandle, obj.LocalPivot);
        var screen = new Point(ToCanvasX(world.X), ToCanvasY(world.Y));

        var line = new Line
        {
            X1 = anchorScreen.X, Y1 = anchorScreen.Y, X2 = screen.X, Y2 = screen.Y,
            Stroke = SelectionBrush, StrokeThickness = 1, Opacity = 0.6, IsHitTestVisible = false,
        };
        DrawCanvas.Children.Add(line);
        _selectionVisuals.Add(line);

        var target = new Ellipse
        {
            Width = HandleDotHitSizePx,
            Height = HandleDotHitSizePx,
            Fill = Brushes.Transparent,
            Tag = new NodeHitTag(subpathIndex, nodeIndex, IsHandle: true, IsOutHandle: isOutHandle),
            Cursor = Cursors.Hand,
        };
        target.MouseLeftButtonDown += OnNodeOrHandleMouseLeftButtonDown;
        target.MouseEnter += (_, _) => SetHoveredHandle(subpathIndex, nodeIndex, isOutHandle);
        target.MouseLeave += (_, _) => ClearHoveredHandle(subpathIndex, nodeIndex, isOutHandle);
        Canvas.SetLeft(target, screen.X - HandleDotHitSizePx / 2);
        Canvas.SetTop(target, screen.Y - HandleDotHitSizePx / 2);
        DrawCanvas.Children.Add(target);
        _selectionVisuals.Add(target);

        var handleSize = isHovered ? HandleVisibleSizePx + 2 : HandleVisibleSizePx;
        var dot = new Ellipse
        {
            Width = handleSize,
            Height = handleSize,
            Fill = SelectionHandleFill,
            Stroke = SelectionBrush,
            StrokeThickness = isHovered ? 1.6 : 1,
            IsHitTestVisible = false,
        };
        Canvas.SetLeft(dot, screen.X - handleSize / 2);
        Canvas.SetTop(dot, screen.Y - handleSize / 2);
        DrawCanvas.Children.Add(dot);
        _selectionVisuals.Add(dot);
    }

    private void SetHoveredHandle(int subpathIndex, int nodeIndex, bool isOutHandle)
    {
        _hoveredHandleKey = (subpathIndex, nodeIndex, isOutHandle);
        RedrawSelectionOverlay();
    }

    private void ClearHoveredHandle(int subpathIndex, int nodeIndex, bool isOutHandle)
    {
        if (_hoveredHandleKey != (subpathIndex, nodeIndex, isOutHandle)) return;
        _hoveredHandleKey = null;
        RedrawSelectionOverlay();
    }

    private void OnNodeOrHandleMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_nodeEditObject is null || _nodeEditWorkingPath is null) return;
        var tag = (NodeHitTag)((FrameworkElement)sender).Tag;
        e.Handled = true;
        Focus();

        _draggedSegment = null;
        if (tag.IsHandle)
        {
            _draggedHandle = (tag.Subpath, tag.Node, tag.IsOutHandle);
            _selectedNodeKeys.Clear();
            _selectedNodeKeys.Add((tag.Subpath, tag.Node));
        }
        else
        {
            _draggedHandle = null;
            var key = (tag.Subpath, tag.Node);
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
            {
                if (!_selectedNodeKeys.Add(key)) _selectedNodeKeys.Remove(key);
            }
            else if (!_selectedNodeKeys.Contains(key))
            {
                _selectedNodeKeys.Clear();
                _selectedNodeKeys.Add(key);
            }
            // Clicking an already-selected node without Shift keeps the whole multi-selection, so
            // dragging any one of several selected nodes moves the group together.
        }

        var screen = e.GetPosition(DrawCanvas);
        _nodeDragStartWorld = new Position(ToWorldX(screen.X), ToWorldY(screen.Y), 0);
        _nodeDragOriginalPath = _nodeEditWorkingPath;
        // Captured BEFORE any live-preview frame runs, so this reference (immutable ImportedShape
        // records) is the true pre-drag state even after RenderVectorPathLive starts reassigning
        // obj.LocalShapes on every pointer move — see VectorPathDragSession's own comment.
        _nodeDragSession = new VectorPathDragSession(_nodeEditObject.LocalShapes);
        _dragMode = DragMode.NodeEdit;
        _activeSnapTarget = null;
        // Object/path snap candidates (node/endpoint/midpoint/intersection) only make sense for a
        // plain node drag — snapping a Bezier HANDLE onto an unrelated node/midpoint is not a
        // meaningful gesture in any mainstream vector editor (handles only ever snap to the grid,
        // applied directly in UpdateNodeEditDrag). Only built for a single selected node: a
        // multi-node drag has no one point to snap "onto" a candidate without the rest of the
        // selection landing off-grid relative to each other.
        _nodeDragSnapCandidates = !tag.IsHandle && _selectedNodeKeys.Count == 1
            ? SnapCandidateBuilder.FromVectorPath(_nodeDragOriginalPath, WorldOf, _selectedNodeKeys)
            : [];
        DrawCanvas.CaptureMouse();
        RedrawSelectionOverlay();
    }

    private Position WorldOf(Position local) =>
        _nodeEditObject!.Transform.Apply(local, _nodeEditObject.LocalPivot);

    /// <summary>Right-click on a node dot opens the node menu (Corner/Smooth, break, close, delete). The
    /// old direct Corner/Smooth toggle is one click away in that menu and on the Node Edit toolbar.</summary>
    private void OnNodeRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_nodeEditObject is null || _nodeEditWorkingPath is null || !CanOpenContextMenu) return;
        var tag = (NodeHitTag)((FrameworkElement)sender).Tag;
        if (tag.IsHandle) return;
        e.Handled = true;
        Focus();
        ShowNodeContextMenuFor(tag.Subpath, tag.Node, e.GetPosition(DrawCanvas));
    }

    /// <summary>Starts a segment-body drag (LIGHTBURN_VECTOR_PARITY.md §15): captures the pre-drag
    /// snapshot exactly like a node/handle drag (same VectorPathDragSession, same
    /// FinishNodeEditDrag/CancelNodeEditDrag commit/cancel contract) so this is just a third kind of
    /// drag sharing all the transactional machinery, not a parallel implementation of it.</summary>
    private void BeginSegmentDrag(VectorPathHitTester.Hit hit)
    {
        if (_nodeEditObject is null || _nodeEditWorkingPath is null) return;
        _draggedHandle = null;
        _draggedSegment = hit;
        _selectedNodeKeys.Clear();
        _nodeDragOriginalPath = _nodeEditWorkingPath;
        _nodeDragSession = new VectorPathDragSession(_nodeEditObject.LocalShapes);
        _dragMode = DragMode.NodeSegmentDrag;
        _activeSnapTarget = null;
        _nodeDragSnapCandidates = []; // a segment reshape point snaps to the grid only, see UpdateNodeSegmentDrag
        DrawCanvas.CaptureMouse();
        RedrawSelectionOverlay();
    }

    /// <summary>Polled from the canvas's general mouse-move (only while idle, see the call site) to
    /// track segment hover — unlike node/handle hover, no dedicated per-segment WPF element exists to
    /// raise MouseEnter/Leave on, so this re-runs the same distance-based hit-test BeginSegmentDrag/
    /// DeleteHoveredSegment already use. A node or handle hover always wins (LIGHTBURN_VECTOR_PARITY.md
    /// §4 priority: node/handle above segment) since their own hit-test elements sit on top and are
    /// larger than the segment tolerance, so this suppresses itself whenever either is active. Redraws
    /// only when the hovered segment actually changes, not on every sampled pointer position.</summary>
    private void UpdateNodeEditHover(Point screen)
    {
        if (_nodeEditObject is null || _nodeEditWorkingPath is null) return;

        VectorPathHitTester.Hit? next = _hoveredNodeKey is not null || _hoveredHandleKey is not null
            ? null
            : HitTestNearestSegment(_nodeEditObject, _nodeEditWorkingPath, screen, SegmentInsertTolerancePx);

        // Priority 3 cursor feedback: a segment under the pointer is reshapeable by dragging (Cross),
        // distinct from the Hand a node/handle's own hit-test element already sets on itself (see
        // DrawNodeDot/DrawHandle), and from the plain Arrow everywhere else in node-edit mode. Only
        // touched while idle (this method's own call site already guards _dragMode == None) so an
        // in-progress drag's own cursor logic (UpdateNodeEditDrag) is never fought over.
        Cursor = next is not null ? Cursors.Cross : Cursors.Arrow;

        if (_hoveredSegment?.SubpathIndex == next?.SubpathIndex && _hoveredSegment?.SegmentIndex == next?.SegmentIndex)
            return; // same segment (or still nothing) -- t drifting slightly within it is not a change worth a redraw
        _hoveredSegment = next;
        RedrawSelectionOverlay();
    }

    private void UpdateNodeSegmentDrag(Point screen)
    {
        if (_nodeEditObject is null || _nodeDragOriginalPath is null || _draggedSegment is not { } segment) return;
        var obj = _nodeEditObject;
        var currentWorld = new Position(ToWorldX(screen.X), ToWorldY(screen.Y), 0);
        currentWorld = ApplyGridSnap(currentWorld);
        var currentLocal = obj.Transform.Inverse(currentWorld, obj.LocalPivot);

        var subpath = _nodeDragOriginalPath.Subpaths[segment.SubpathIndex];
        var updatedSubpath = VectorPathEditor.DragSegmentPoint(subpath, segment.SegmentIndex, segment.T, currentLocal);
        var updated = _nodeDragOriginalPath.ReplaceSubpath(segment.SubpathIndex, updatedSubpath);

        _nodeEditWorkingPath = updated;
        RenderVectorPathLive(obj, updated);
        RedrawSelectionOverlay();
    }

    private void UpdateNodeEditDrag(Point screen)
    {
        if (_nodeEditObject is null || _nodeDragOriginalPath is null) return;
        var obj = _nodeEditObject;
        var currentWorld = new Position(ToWorldX(screen.X), ToWorldY(screen.Y), 0);
        var currentLocal = obj.Transform.Inverse(currentWorld, obj.LocalPivot);

        VectorPath updated;
        if (_draggedHandle is { } handle)
        {
            // Handles snap to the grid only, never to path geometry — see the drag-start comment in
            // OnNodeOrHandleMouseLeftButtonDown for why.
            currentLocal = obj.Transform.Inverse(ApplyGridSnap(currentWorld), obj.LocalPivot);
            var subpath = _nodeDragOriginalPath.Subpaths[handle.Subpath];
            var node = subpath.Nodes[handle.Node];
            var breakSymmetry = Keyboard.Modifiers.HasFlag(ModifierKeys.Alt);
            var nodes = subpath.Nodes.ToList();
            nodes[handle.Node] = VectorPathEditor.MoveHandle(node, handle.IsOutHandle, currentLocal, breakSymmetry);
            updated = _nodeDragOriginalPath.ReplaceSubpath(handle.Subpath, subpath with { Nodes = nodes });
        }
        else
        {
            var shiftHeld = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
            // Object/path snapping only applies to the default (unconstrained) drag — combining it
            // with Shift's angle-lock would be ambiguous about which one wins, so Shift always means
            // "constrain angle", never "and also snap". Grid snap still solves for the whole delta
            // (not decomposed per selected node), so it only engages for a single selected node —
            // see _nodeDragSnapCandidates' own build-time restriction.
            if (!shiftHeld && _selectedNodeKeys.Count == 1)
                currentLocal = obj.Transform.Inverse(ApplyNodeSnap(currentWorld), obj.LocalPivot);
            else
                _activeSnapTarget = null;

            var startLocal = obj.Transform.Inverse(_nodeDragStartWorld, obj.LocalPivot);
            var dx = currentLocal.X - startLocal.X;
            var dy = currentLocal.Y - startLocal.Y;
            if (shiftHeld)
                (dx, dy) = ConstrainToNearestEighthTurn(dx, dy);
            var subpaths = _nodeDragOriginalPath.Subpaths.ToList();
            for (var s = 0; s < subpaths.Count; s++)
            {
                var nodes = subpaths[s].Nodes.ToList();
                var changed = false;
                for (var n = 0; n < nodes.Count; n++)
                {
                    if (!_selectedNodeKeys.Contains((s, n))) continue;
                    nodes[n] = nodes[n].Translated(dx, dy);
                    changed = true;
                }
                if (changed) subpaths[s] = subpaths[s] with { Nodes = nodes };
            }
            updated = _nodeDragOriginalPath with { Subpaths = subpaths };
            Cursor = IsDraggingEndpointOntoJoinTarget(updated) ? Cursors.UpArrow : Cursors.Hand;
        }

        _nodeEditWorkingPath = updated;
        RenderVectorPathLive(obj, updated);
        RedrawSelectionOverlay();
    }

    /// <summary>Priority 3 cursor feedback for TryCloseByEndpointJoin's own gesture (dragging an open
    /// subpath's endpoint onto its other endpoint closes the path on release) — previews that outcome
    /// with a distinct cursor WHILE dragging, using the identical "single endpoint node, within
    /// CloseHoverThresholdPx of the subpath's other (pre-drag) endpoint" test TryCloseByEndpointJoin
    /// itself runs on mouse-up, just evaluated live instead of once at the end.</summary>
    private bool IsDraggingEndpointOntoJoinTarget(VectorPath workingPath)
    {
        if (_nodeEditObject is null || _nodeDragOriginalPath is null || _selectedNodeKeys.Count != 1) return false;
        var (subpathIndex, nodeIndex) = _selectedNodeKeys.Single();
        var originalSubpath = _nodeDragOriginalPath.Subpaths[subpathIndex];
        if (originalSubpath.IsClosed || originalSubpath.Nodes.Count < 2) return false;
        var lastIndex = originalSubpath.Nodes.Count - 1;
        if (nodeIndex != 0 && nodeIndex != lastIndex) return false;
        var otherIndex = nodeIndex == 0 ? lastIndex : 0;

        var obj = _nodeEditObject;
        var draggedWorld = obj.Transform.Apply(workingPath.Subpaths[subpathIndex].Nodes[nodeIndex].Anchor, obj.LocalPivot);
        var otherWorld = obj.Transform.Apply(originalSubpath.Nodes[otherIndex].Anchor, obj.LocalPivot);
        var draggedScreen = new Point(ToCanvasX(draggedWorld.X), ToCanvasY(draggedWorld.Y));
        var otherScreen = new Point(ToCanvasX(otherWorld.X), ToCanvasY(otherWorld.Y));
        return Distance(draggedScreen, otherScreen) <= CloseHoverThresholdPx;
    }

    /// <summary>Node/endpoint drag snapping: tries the pre-built geometry candidates first (node,
    /// endpoint, midpoint, intersection — see _nodeDragSnapCandidates), falling back to the grid, and
    /// records the resolved target (or clears it) for DrawActiveSnapIndicator. Hold Ctrl to bypass
    /// snapping entirely for this pointer-move sample — Ctrl has no other meaning during a live node
    /// drag (Shift means "constrain angle", Alt means "break handle symmetry" for a handle drag), so
    /// it was free to reuse here rather than invent a fourth modifier.</summary>
    private Position ApplyNodeSnap(Position currentWorld)
    {
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            _activeSnapTarget = null;
            return currentWorld;
        }

        var toleranceWorld = SnapToleranceScreenPx / _scale;
        var gridStep = RulerMath.PickStep(_scale);
        var result = SnapEngine.Resolve(currentWorld, _nodeDragSnapCandidates, toleranceWorld, gridStep);
        _activeSnapTarget = result.Target;
        return result.Point;
    }

    /// <summary>Handle/segment-point drag snapping: grid only (see the drag-start comments on why
    /// geometry candidates are not built for these gestures) — same Ctrl bypass as ApplyNodeSnap.</summary>
    private Position ApplyGridSnap(Position currentWorld)
    {
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            _activeSnapTarget = null;
            return currentWorld;
        }

        var toleranceWorld = SnapToleranceScreenPx / _scale;
        var gridStep = RulerMath.PickStep(_scale);
        var result = SnapEngine.Resolve(currentWorld, [], toleranceWorld, gridStep);
        _activeSnapTarget = result.Target;
        return result.Point;
    }

    /// <summary>Structural (not reference) equality between two VectorPaths — VectorSubpath.Nodes is a
    /// plain IReadOnlyList, so the record's own generated Equals compares list references, not
    /// contents, and would treat two paths rebuilt with identical node values as different. Used only
    /// for the §59 "drag back to exactly where it started" no-op guard.</summary>
    private static bool VectorPathsStructurallyEqual(VectorPath a, VectorPath b)
    {
        if (a.Subpaths.Count != b.Subpaths.Count) return false;
        for (var i = 0; i < a.Subpaths.Count; i++)
        {
            var sa = a.Subpaths[i];
            var sb = b.Subpaths[i];
            if (sa.IsClosed != sb.IsClosed) return false;
            if (!sa.Nodes.SequenceEqual(sb.Nodes)) return false;
        }
        return true;
    }

    /// <summary>Shift-constrains a node-drag delta to the nearest 0/45/90/135/180/225/270/315° from
    /// the drag's own starting point (LIGHTBURN_VECTOR_PARITY.md §8) — snaps the direction, keeps the
    /// dragged distance (magnitude) unchanged, so the constrained point still tracks the pointer's
    /// distance from the anchor, just locked to an eighth-turn.</summary>
    private static (double X, double Y) ConstrainToNearestEighthTurn(double dx, double dy)
    {
        var magnitude = Math.Sqrt(dx * dx + dy * dy);
        if (magnitude < 1e-9) return (dx, dy);
        var angle = Math.Atan2(dy, dx);
        const double step = Math.PI / 4;
        var snapped = Math.Round(angle / step) * step;
        return (magnitude * Math.Cos(snapped), magnitude * Math.Sin(snapped));
    }

    /// <summary>Live preview during a node/handle drag — mutates the object's own LocalShapes directly
    /// (geometry tracks the pointer with no lag/easing, per the spec's hard requirement) and repaints
    /// through the existing UpdateObjectGeometry pipeline. Nothing here touches the command stack; only
    /// FinishNodeEditDrag/CommitNodeEdit does, once, on mouse-up. Shapes are built via
    /// VectorPathDragSession so every preview frame keeps the pre-drag LayerId/GeometrySetId instead of
    /// losing them the moment a live frame is rendered.</summary>
    private void RenderVectorPathLive(SceneObject obj, VectorPath path)
    {
        var session = _nodeDragSession ??= new VectorPathDragSession(obj.LocalShapes);
        obj.LocalShapes = session.BuildPreviewShapes(path, VectorPathDefaultColor);
        UpdateObjectGeometry(obj);
    }

    /// <summary>Mouse-up: restores the object's LocalShapes to the exact pre-drag snapshot BEFORE
    /// anything touches the command stack, so CommitNodeEdit/VectorPathSceneFactory.Rebuild always
    /// reads the true original metadata rather than the last live-preview frame, and so the object
    /// ReplaceObjectsCommand stores as "removed" (and restores verbatim on Undo) is pristine, not the
    /// dragged-to position. A gesture that never actually moved anything (a plain node click/select)
    /// leaves _nodeEditWorkingPath unchanged and is not committed at all — no spurious undo step.</summary>
    private void FinishNodeEditDrag()
    {
        if (_nodeEditObject is not null)
        {
            if (_nodeDragSession is not null) _nodeEditObject.LocalShapes = _nodeDragSession.OriginalShapes;

            if (TryCloseByEndpointJoin(out var closedPath))
            {
                CommitNodeEdit(closedPath);
            }
            // §59: neither a plain click (reference-equal, UpdateNodeEditDrag never ran) nor a drag
            // that ends up back exactly where it started (structurally equal despite being rebuilt
            // through new VectorPath/VectorSubpath instances every frame) may create an undo entry.
            else if (_nodeEditWorkingPath is not null
                && !ReferenceEquals(_nodeEditWorkingPath, _nodeDragOriginalPath)
                && (_nodeDragOriginalPath is null || !VectorPathsStructurallyEqual(_nodeEditWorkingPath, _nodeDragOriginalPath)))
            {
                CommitNodeEdit(_nodeEditWorkingPath);
            }
        }
        _draggedHandle = null;
        _draggedSegment = null;
        _nodeDragOriginalPath = null;
        _nodeDragSession = null;
        _nodeDragSnapCandidates = [];
        _activeSnapTarget = null;
        // A drag that ends without a commit (dragged back to where it started, or the edit was
        // rejected) leaves the last live frame on screen; with the drag over, the object's own curve
        // is the truth again. After a commit this re-renders the replacement with the curve it already
        // has.
        if (_nodeEditObject is not null && _objectVisuals.ContainsKey(_nodeEditObject))
            UpdateObjectGeometry(_nodeEditObject);
        // OnDrawCanvasMouseLeftButtonUp releases capture after it sets _dragMode to None. Keeping the
        // release there prevents our LostMouseCapture cancellation path from mistaking a normal
        // completed drag for an interrupted one.
    }

    /// <summary>LIGHTBURN_VECTOR_PARITY.md §23, same-path case: dragging an open subpath's endpoint
    /// onto its OTHER endpoint closes the path instead of leaving the node sitting on top of it.
    /// Closing always connects the subpath's last node back to its first regardless of which endpoint
    /// was physically dragged, so the ORIGINAL (pre-drag) subpath is closed as-is — the gesture snaps
    /// exactly onto the existing far endpoint, it does not keep the dragged one's slightly-off
    /// position. Screen-space tolerance, matching the path tool's own close-hover threshold.</summary>
    private bool TryCloseByEndpointJoin(out VectorPath closedPath)
    {
        closedPath = VectorPath.Empty;
        if (_nodeEditObject is null || _nodeDragOriginalPath is null || _nodeEditWorkingPath is null) return false;
        if (_draggedHandle is not null || _draggedSegment is not null || _selectedNodeKeys.Count != 1) return false;

        var (subpathIndex, nodeIndex) = _selectedNodeKeys.Single();
        var originalSubpath = _nodeDragOriginalPath.Subpaths[subpathIndex];
        if (originalSubpath.IsClosed || originalSubpath.Nodes.Count < 2) return false;
        var lastIndex = originalSubpath.Nodes.Count - 1;
        if (nodeIndex != 0 && nodeIndex != lastIndex) return false; // must be an endpoint
        var otherIndex = nodeIndex == 0 ? lastIndex : 0;

        var draggedWorkingSubpath = _nodeEditWorkingPath.Subpaths[subpathIndex];
        var obj = _nodeEditObject;
        var draggedWorld = obj.Transform.Apply(draggedWorkingSubpath.Nodes[nodeIndex].Anchor, obj.LocalPivot);
        var otherWorld = obj.Transform.Apply(originalSubpath.Nodes[otherIndex].Anchor, obj.LocalPivot);
        var draggedScreen = new Point(ToCanvasX(draggedWorld.X), ToCanvasY(draggedWorld.Y));
        var otherScreen = new Point(ToCanvasX(otherWorld.X), ToCanvasY(otherWorld.Y));
        if (Distance(draggedScreen, otherScreen) > CloseHoverThresholdPx) return false;

        closedPath = _nodeDragOriginalPath.ReplaceSubpath(subpathIndex, VectorPathEditor.Close(originalSubpath));
        return true;
    }

    /// <summary>Escape/right-click-cancel: restores both the working path and the object's LocalShapes
    /// to the exact pre-drag snapshot (not a re-flatten of the original path, which would risk drift
    /// from the live-mutated geometry) — the object is left exactly as it was before the gesture
    /// started, per the "cancel must restore the exact pre-drag object" requirement.</summary>
    private void CancelNodeEditDrag()
    {
        if (_nodeEditObject is not null && _nodeDragOriginalPath is not null)
        {
            _nodeEditWorkingPath = _nodeDragOriginalPath;
            if (_nodeDragSession is not null) _nodeEditObject.LocalShapes = _nodeDragSession.OriginalShapes;
            UpdateObjectGeometry(_nodeEditObject);
        }
        _draggedHandle = null;
        _draggedSegment = null;
        _nodeDragOriginalPath = null;
        _nodeDragSession = null;
        _nodeDragSnapCandidates = [];
        _activeSnapTarget = null;
        RedrawSelectionOverlay();
    }

    /// <summary>Undo and redo swap the edited object for another instance with the same Id. Without this
    /// the overlay keeps drawing the retired path (a phantom node where the undone move ended) and the
    /// next edit would commit that stale working path, bringing the undone change back.</summary>
    private void ResyncNodeEditAfterExternalChange()
    {
        if (_nodeEditObject is null || _committingNodeEdit || ViewModel is null) return;
        if (ViewModel.Objects.Contains(_nodeEditObject)) return;

        var replacement = ViewModel.Objects.FirstOrDefault(o => o.Id == _nodeEditObject.Id && o.IsVectorPath && !o.IsLocked);
        if (replacement is null)
        {
            ExitNodeEditMode();
            return;
        }

        _nodeEditObject = replacement;
        _nodeEditWorkingPath = replacement.VectorPath;
        _selectedNodeKeys.Clear();
        _draggedHandle = null;
        _draggedSegment = null;
        _nodeDragOriginalPath = null;
        _nodeDragSession = null;
        _hoveredNodeKey = null;
        _hoveredHandleKey = null;
        _hoveredSegment = null;
        RedrawSelectionOverlay();
    }

    /// <summary>Pushes one ReplaceObjectsCommand (one undo step) for a completed node edit and re-syncs
    /// this control's notion of "the object being edited" to the replacement CommitVectorPathEdit
    /// creates — the original `obj` reference is retired from the scene the moment this runs.</summary>
    private void CommitNodeEdit(VectorPath updated)
    {
        if (_nodeEditObject is null || ViewModel is null) return;
        _committingNodeEdit = true;
        try { ViewModel.CommitVectorPathEdit(_nodeEditObject, updated); }
        finally { _committingNodeEdit = false; }
        _nodeEditObject = ViewModel.SelectedObjects.Count == 1 && ViewModel.SelectedObjects[0].IsVectorPath
            ? ViewModel.SelectedObjects[0]
            : null;
        _nodeEditWorkingPath = _nodeEditObject?.VectorPath;
        if (_nodeEditObject is null) ExitNodeEditMode();
        else RedrawSelectionOverlay();
    }

    /// <summary>Public — the node-edit context toolbar (NodeEditToolbar.xaml) calls this directly,
    /// the same way CanvasViewControls calls SceneCanvas.ZoomIn/SetZoomPercent: this is view state
    /// with no ViewModel equivalent, not a command that belongs on SceneViewModel.</summary>
    public void DeleteSelectedNodes()
    {
        if (_nodeEditObject is null || _nodeEditWorkingPath is null || _selectedNodeKeys.Count == 0) return;

        var subpaths = _nodeEditWorkingPath.Subpaths.ToList();
        foreach (var group in _selectedNodeKeys.GroupBy(key => key.Subpath))
        {
            var subpath = subpaths[group.Key];
            // Highest node index first so removing one does not shift the indices still queued.
            foreach (var nodeIndex in group.Select(key => key.Node).OrderByDescending(i => i))
                subpath = VectorPathEditor.RemoveNode(subpath, nodeIndex);
            subpaths[group.Key] = subpath;
        }

        _selectedNodeKeys.Clear();
        CommitNodeEdit(_nodeEditWorkingPath with { Subpaths = subpaths });
    }

    /// <summary>Arrow-key nudge for the current node selection (LIGHTBURN_VECTOR_PARITY.md §9) — moves
    /// only the selected nodes by (dx, dy) in the object's LOCAL space and commits one
    /// ReplaceObjectsCommand per key press, the same one-gesture-one-undo-step contract every other
    /// committed node edit already uses. Each press is its own undo step (matching the existing
    /// whole-object Nudge()'s convention below; coalescing a held key into one transaction is
    /// explicitly optional per spec and not implemented here).</summary>
    private void NudgeSelectedNodes(Key arrowKey, double stepMm)
    {
        if (_nodeEditWorkingPath is null || _selectedNodeKeys.Count == 0) return;
        var dx = arrowKey switch { Key.Left => -stepMm, Key.Right => stepMm, _ => 0 };
        var dy = arrowKey switch { Key.Up => stepMm, Key.Down => -stepMm, _ => 0 };
        if (dx == 0 && dy == 0) return;

        var subpaths = _nodeEditWorkingPath.Subpaths.ToList();
        foreach (var group in _selectedNodeKeys.GroupBy(nodeKey => nodeKey.Subpath))
        {
            var subpath = subpaths[group.Key];
            var nodes = subpath.Nodes.ToList();
            foreach (var nodeKey in group)
                nodes[nodeKey.Node] = nodes[nodeKey.Node].Translated(dx, dy);
            subpaths[group.Key] = subpath with { Nodes = nodes };
        }

        CommitNodeEdit(_nodeEditWorkingPath with { Subpaths = subpaths });
    }

    /// <summary>Sets every selected node to the same Corner/Smooth type in one gesture — the toolbar's
    /// two-button equivalent of OnNodeRightButtonDown's single-node toggle.</summary>
    public void ConvertSelectedNodes(VectorNodeType type)
    {
        if (_nodeEditWorkingPath is null || _selectedNodeKeys.Count == 0) return;

        var subpaths = _nodeEditWorkingPath.Subpaths.ToList();
        foreach (var group in _selectedNodeKeys.GroupBy(key => key.Subpath))
        {
            var subpath = subpaths[group.Key];
            var nodes = subpath.Nodes.ToList();
            foreach (var key in group)
                nodes[key.Node] = VectorPathEditor.ConvertNodeType(nodes[key.Node], type);
            subpaths[group.Key] = subpath with { Nodes = nodes };
        }

        CommitNodeEdit(_nodeEditWorkingPath with { Subpaths = subpaths });
    }

    /// <summary>Closes or reopens whichever subpath the current node selection belongs to (or, with
    /// nothing selected, the path's only subpath — the common case for a hand-drawn shape). Disabled
    /// from the toolbar (see IsSelectedSubpathClosed) whenever that is ambiguous: a selection spanning
    /// more than one subpath, or no selection on a multi-subpath path.</summary>
    public void ToggleSelectedSubpathClosed()
    {
        if (_nodeEditWorkingPath is null || ResolveContextSubpathIndex() is not { } index) return;
        var subpaths = _nodeEditWorkingPath.Subpaths.ToList();
        var subpath = subpaths[index];
        subpaths[index] = subpath.IsClosed ? VectorPathEditor.Open(subpath) : VectorPathEditor.Close(subpath);
        CommitNodeEdit(_nodeEditWorkingPath with { Subpaths = subpaths });
    }

    /// <summary>Selects every node of every subpath — the toolbar/context-menu's "Select all"
    /// action, giving Delete/Smooth/Corner/Reverse a mouse-only way to target the whole path without
    /// a marquee or Shift-clicking every node individually.</summary>
    public void SelectAllNodes()
    {
        if (_nodeEditWorkingPath is null) return;
        _selectedNodeKeys.Clear();
        for (var s = 0; s < _nodeEditWorkingPath.Subpaths.Count; s++)
            for (var n = 0; n < _nodeEditWorkingPath.Subpaths[s].Nodes.Count; n++)
                _selectedNodeKeys.Add((s, n));
        RedrawSelectionOverlay();
    }

    /// <summary>Clears the node selection without leaving Node Edit mode — the toolbar/context-menu
    /// counterpart of clicking empty canvas space, for a user who has not discovered that gesture.</summary>
    public void ClearNodeSelection()
    {
        if (_selectedNodeKeys.Count == 0) return;
        _selectedNodeKeys.Clear();
        RedrawSelectionOverlay();
    }

    /// <summary>Reverses the direction of whichever subpath ResolveContextSubpathIndex resolves —
    /// same node/handle positions and curve shapes, just flips which endpoint is "start" and which
    /// is "end" (VectorPathEditor.ReverseSubpath). Works on both open and closed subpaths.</summary>
    public void ReverseSelectedSubpath()
    {
        if (_nodeEditWorkingPath is null || ResolveContextSubpathIndex() is not { } index) return;
        var reversed = VectorPathEditor.ReverseSubpath(_nodeEditWorkingPath.Subpaths[index]);
        _selectedNodeKeys.Clear();
        CommitNodeEdit(_nodeEditWorkingPath.ReplaceSubpath(index, reversed));
    }

    // ------------------------------------------------------------------
    // Cross-object endpoint join — the mouse-only counterpart of TryCloseByEndpointJoin's own
    // drag-to-close gesture, but targeting a DIFFERENT node-editable object's open endpoint. Exposed
    // as a toolbar/context-menu action (CanJoinSelectedEndpoint / JoinSelectedEndpointToCandidate)
    // so a user who never discovers "drag one endpoint onto another object's endpoint" can still join
    // two paths — select the endpoint node, click Join.
    // ------------------------------------------------------------------

    private readonly record struct JoinCandidate(SceneObject Target, int SubpathIndex, bool TargetAtStart);

    /// <summary>Null unless exactly one node is selected, it is an open-subpath endpoint, and some
    /// OTHER node-editable object in the scene has an open endpoint within screen-space tolerance of
    /// it right now (a static check — no drag in progress — so this can back a toolbar button's
    /// IsEnabled binding, re-evaluated every RedrawSelectionOverlay via UpdateNodeEditToolbarState).</summary>
    private JoinCandidate? FindCrossObjectJoinCandidate()
    {
        if (ViewModel is null || _nodeEditObject is null || _nodeEditWorkingPath is null) return null;
        if (_selectedNodeKeys.Count != 1) return null;
        var (subpathIndex, nodeIndex) = _selectedNodeKeys.Single();
        var subpath = _nodeEditWorkingPath.Subpaths[subpathIndex];
        if (subpath.IsClosed || subpath.Nodes.Count == 0) return null;
        var lastIndex = subpath.Nodes.Count - 1;
        if (nodeIndex != 0 && nodeIndex != lastIndex) return null;

        var selectedWorld = WorldOf(subpath.Nodes[nodeIndex].Anchor);
        var selectedScreen = new Point(ToCanvasX(selectedWorld.X), ToCanvasY(selectedWorld.Y));

        JoinCandidate? best = null;
        var bestDistPx = double.MaxValue;
        foreach (var candidate in ViewModel.Objects)
        {
            if (ReferenceEquals(candidate, _nodeEditObject) || candidate.VectorPath is not { Subpaths.Count: 1 } path)
                continue;
            var candidateSubpath = path.Subpaths[0];
            if (candidateSubpath.IsClosed || candidateSubpath.Nodes.Count == 0) continue;

            foreach (var atStart in new[] { true, false })
            {
                var endpointIndex = atStart ? 0 : candidateSubpath.Nodes.Count - 1;
                var endpointWorld = candidate.Transform.Apply(candidateSubpath.Nodes[endpointIndex].Anchor, candidate.LocalPivot);
                var endpointScreen = new Point(ToCanvasX(endpointWorld.X), ToCanvasY(endpointWorld.Y));
                var dist = Distance(selectedScreen, endpointScreen);
                if (dist <= CloseHoverThresholdPx && dist < bestDistPx)
                {
                    bestDistPx = dist;
                    best = new JoinCandidate(candidate, 0, atStart);
                }
            }
        }

        return best;
    }

    /// <summary>Public — the toolbar/context-menu "Spojit" (Join) action. Re-resolves the candidate
    /// rather than caching FindCrossObjectJoinCandidate's own result from the last toolbar refresh,
    /// since the scene could have changed (another command, a different selection) in between.
    /// On success, re-enters Node Edit on the merged result ViewModel.JoinObjectEndpoints already
    /// selected, so editing continues on the now-single combined path without an extra click.</summary>
    public void JoinSelectedEndpointToCandidate()
    {
        if (ViewModel is null || _nodeEditObject is null || _nodeEditWorkingPath is null) return;
        if (_selectedNodeKeys.Count != 1) return;
        var (subpathIndex, nodeIndex) = _selectedNodeKeys.Single();
        var subpath = _nodeEditWorkingPath.Subpaths[subpathIndex];
        var draggedAtStart = nodeIndex == 0;

        var candidate = FindCrossObjectJoinCandidate();
        if (candidate is not { } join) return;

        var draggedObject = _nodeEditObject;
        if (ViewModel.JoinObjectEndpoints(draggedObject, draggedAtStart, join.Target, join.TargetAtStart))
        {
            var merged = ViewModel.SelectedObjects.Count == 1 ? ViewModel.SelectedObjects[0] : null;
            if (merged is { IsVectorPath: true }) EnterNodeEditMode(merged);
            else ExitNodeEditMode();
        }
    }

    /// <summary>Delete-key path when nothing is selected (LIGHTBURN_VECTOR_PARITY.md §21/§64):
    /// whatever segment the pointer currently sits over is deleted, opening a closed subpath or
    /// splitting/shrinking an open one (see VectorPathEditor.DeleteSegment). Silently does nothing if
    /// the pointer is not within tolerance of any segment of the edited path.</summary>
    private void DeleteHoveredSegment(Point screen)
    {
        if (_nodeEditObject is null || _nodeEditWorkingPath is null) return;
        var hit = HitTestNearestSegment(_nodeEditObject, _nodeEditWorkingPath, screen, SegmentInsertTolerancePx);
        if (hit is not { } segmentHit) return;
        DeleteSegment(segmentHit);
    }

    private void DeleteSegment(VectorPathHitTester.Hit hit)
    {
        if (_nodeEditWorkingPath is null) return;
        _selectedNodeKeys.Clear();
        CommitNodeEdit(VectorPathEditor.DeleteSegment(_nodeEditWorkingPath, hit.SubpathIndex, hit.SegmentIndex));
    }

    // ------------------------------------------------------------------
    // Priority 1 — segment-scoped commands the model layer (VectorPathEditor) already implements but
    // this file previously exposed only implicitly (line→curve via reshaping a segment by hand;
    // curve→line and insert-at-exact-midpoint had NO UI path at all — see
    // docs/reference/NODE_EDIT_PARITY_AUDIT_2026-09-16.md, "Curve↔line is not user-reachable"). All
    // four act on whichever segment the pointer is currently HOVERING, the same targeting convention
    // DeleteHoveredSegment above already established (there is no separate "click to select a
    // segment" state in this tool — see HandleNodeEditCanvasMouseDown's own doc comment on why a
    // plain click on a segment starts reshaping it instead of merely selecting it). Public so
    // NodeEditToolbar's buttons and OnCanvasKeyDown's Shift+L/Shift+C/Shift+M shortcuts can call them
    // directly, the same way DeleteSelectedNodes/BreakSelectedNode already are.
    // ------------------------------------------------------------------

    public void ConvertHoveredSegmentToLine()
    {
        if (_nodeEditWorkingPath is null || _hoveredSegment is not { } hit) return;
        var subpath = _nodeEditWorkingPath.Subpaths[hit.SubpathIndex];
        var updated = VectorPathEditor.ConvertSegmentToLine(subpath, hit.SegmentIndex);
        CommitNodeEdit(_nodeEditWorkingPath.ReplaceSubpath(hit.SubpathIndex, updated));
    }

    public void ConvertHoveredSegmentToCurve()
    {
        if (_nodeEditWorkingPath is null || _hoveredSegment is not { } hit) return;
        var subpath = _nodeEditWorkingPath.Subpaths[hit.SubpathIndex];
        var updated = VectorPathEditor.ConvertSegmentToCurve(subpath, hit.SegmentIndex);
        CommitNodeEdit(_nodeEditWorkingPath.ReplaceSubpath(hit.SubpathIndex, updated));
    }

    /// <summary>Inserts a node at the EXACT curve-parameter midpoint (t = 0.5) of the hovered segment —
    /// unlike the double-click gesture (HandleNodeEditCanvasMouseDown), which inserts at the nearest
    /// approximate point under the pointer, this is deliberately the precise midpoint every time,
    /// matching LightBurn/Inkscape's own "Insert midpoint" command semantics.</summary>
    public void InsertNodeAtHoveredSegmentMidpoint()
    {
        if (_nodeEditWorkingPath is null || _hoveredSegment is not { } hit) return;
        var subpath = _nodeEditWorkingPath.Subpaths[hit.SubpathIndex];
        var updated = VectorPathEditor.InsertNode(subpath, hit.SegmentIndex, 0.5);
        // Node identity here is purely positional (Subpath, Node) index pairs — inserting a node
        // shifts every later index in this subpath, so an outstanding selection elsewhere in it would
        // silently point at the wrong node after the redraw if left alone. Clearing it matches every
        // other topology-changing operation (Delete/Break/Reverse) already does for the same reason.
        _selectedNodeKeys.Clear();
        CommitNodeEdit(_nodeEditWorkingPath.ReplaceSubpath(hit.SubpathIndex, updated));
    }

    /// <summary>Inserts a node at the hovered segment's own approximate closest-point parameter (the
    /// same t HitTestNearestSegment already resolved for that hover) — the context menu's "Vložit
    /// uzel zde" (Insert Node Here), distinct from InsertNodeAtHoveredSegmentMidpoint's always-exact-
    /// middle behaviour. Clamped away from the endpoints so this can never degenerate into inserting
    /// on top of an existing node.</summary>
    public void InsertNodeAtHoveredSegmentPoint()
    {
        if (_nodeEditWorkingPath is null || _hoveredSegment is not { } hit) return;
        var subpath = _nodeEditWorkingPath.Subpaths[hit.SubpathIndex];
        var t = Math.Clamp(hit.T, 0.02, 0.98);
        var updated = VectorPathEditor.InsertNode(subpath, hit.SegmentIndex, t);
        _selectedNodeKeys.Clear(); // see InsertNodeAtHoveredSegmentMidpoint's own comment
        CommitNodeEdit(_nodeEditWorkingPath.ReplaceSubpath(hit.SubpathIndex, updated));
    }

    /// <summary>Toolbar/shortcut equivalent of DeleteHoveredSegment(Point) — acts on the already-
    /// tracked _hoveredSegment directly instead of re-hit-testing from a screen point, since a
    /// toolbar click or keyboard shortcut has no pointer-over-canvas coordinate of its own to hit-test
    /// with.</summary>
    public void DeleteHoveredSegment()
    {
        if (_hoveredSegment is { } hit) DeleteSegment(hit);
    }

    /// <summary>Public — NodeEditToolbar's "Rozdělit v uzlu" button (LIGHTBURN_VECTOR_PARITY.md §22).
    /// Only meaningful for exactly one selected node (the break location); a multi-node or empty
    /// selection is a no-op, matching the button's own IsEnabled binding.</summary>
    public void BreakSelectedNode()
    {
        if (_nodeEditWorkingPath is null || _selectedNodeKeys.Count != 1) return;
        var (subpathIndex, nodeIndex) = _selectedNodeKeys.Single();
        var subpath = _nodeEditWorkingPath.Subpaths[subpathIndex];
        // BreakAtNode throws for an endpoint of an already-open subpath (nothing to break there) --
        // guard here rather than let a stray click on an end node fault the UI thread.
        if (!subpath.IsClosed && (nodeIndex == 0 || nodeIndex == subpath.Nodes.Count - 1)) return;

        _selectedNodeKeys.Clear();
        CommitNodeEdit(VectorPathEditor.BreakAtNode(_nodeEditWorkingPath, subpathIndex, nodeIndex));
    }

    private int? ResolveContextSubpathIndex()
    {
        if (_nodeEditWorkingPath is null) return null;
        if (_selectedNodeKeys.Count > 0)
        {
            var distinctSubpaths = _selectedNodeKeys.Select(key => key.Subpath).Distinct().ToList();
            return distinctSubpaths.Count == 1 ? distinctSubpaths[0] : null;
        }
        return _nodeEditWorkingPath.Subpaths.Count == 1 ? 0 : null;
    }

    // ------------------------------------------------------------------
    // Bindable view state for NodeEditToolbar.xaml — mirrors ZoomPercent's read-only-DP pattern in
    // the main file: this is transient interaction state the canvas owns, not scene data, so it is
    // exposed directly rather than routed through SceneViewModel. Recomputed from one call site,
    // UpdateNodeEditToolbarState() at the top of RedrawSelectionOverlay(), since every state change
    // in this file (enter/exit, node/handle selection, marquee, corner/smooth toggle, delete, drag)
    // already ends by calling that.
    // ------------------------------------------------------------------

    private static readonly DependencyPropertyKey IsNodeEditActivePropertyKey = DependencyProperty.RegisterReadOnly(
        nameof(IsNodeEditActive), typeof(bool), typeof(SceneCanvas), new PropertyMetadata(false));
    public static readonly DependencyProperty IsNodeEditActiveProperty = IsNodeEditActivePropertyKey.DependencyProperty;
    public bool IsNodeEditActive => (bool)GetValue(IsNodeEditActiveProperty);

    private static readonly DependencyPropertyKey SelectedNodeCountPropertyKey = DependencyProperty.RegisterReadOnly(
        nameof(SelectedNodeCount), typeof(int), typeof(SceneCanvas), new PropertyMetadata(0));
    public static readonly DependencyProperty SelectedNodeCountProperty = SelectedNodeCountPropertyKey.DependencyProperty;
    public int SelectedNodeCount => (int)GetValue(SelectedNodeCountProperty);

    /// <summary>Null when nothing is selected or the selection mixes Corner and Smooth nodes — the
    /// toolbar shows neither button pressed in that case, same convention as a mixed-value field.</summary>
    private static readonly DependencyPropertyKey SelectedNodeTypePropertyKey = DependencyProperty.RegisterReadOnly(
        nameof(SelectedNodeType), typeof(VectorNodeType?), typeof(SceneCanvas), new PropertyMetadata(null));
    public static readonly DependencyProperty SelectedNodeTypeProperty = SelectedNodeTypePropertyKey.DependencyProperty;
    public VectorNodeType? SelectedNodeType => (VectorNodeType?)GetValue(SelectedNodeTypeProperty);

    /// <summary>Null when ResolveContextSubpathIndex can't resolve one subpath — the toolbar disables
    /// the open/close button rather than guessing which subpath a multi-subpath selection means.</summary>
    private static readonly DependencyPropertyKey IsSelectedSubpathClosedPropertyKey = DependencyProperty.RegisterReadOnly(
        nameof(IsSelectedSubpathClosed), typeof(bool?), typeof(SceneCanvas), new PropertyMetadata(null));
    public static readonly DependencyProperty IsSelectedSubpathClosedProperty = IsSelectedSubpathClosedPropertyKey.DependencyProperty;
    public bool? IsSelectedSubpathClosed => (bool?)GetValue(IsSelectedSubpathClosedProperty);

    /// <summary>Null when no segment is hovered — NodeEditToolbar's Convert-to-Line/Convert-to-Curve/
    /// Insert-Midpoint/Delete-Segment buttons all target whichever segment the pointer is currently
    /// over (see the "Priority 1" block above for why there is no separate segment-selection state),
    /// so they disable themselves together via this one property rather than each re-deriving hover
    /// state independently. True when that segment already has no handles on either end (straight);
    /// the toolbar shows only the applicable one of Convert-to-Line/Convert-to-Curve, matching
    /// VectorPathEditor.ConvertSegmentToCurve's own "already curved, no-op" note.</summary>
    private static readonly DependencyPropertyKey IsHoveredSegmentStraightPropertyKey = DependencyProperty.RegisterReadOnly(
        nameof(IsHoveredSegmentStraight), typeof(bool?), typeof(SceneCanvas), new PropertyMetadata(null));
    public static readonly DependencyProperty IsHoveredSegmentStraightProperty = IsHoveredSegmentStraightPropertyKey.DependencyProperty;
    public bool? IsHoveredSegmentStraight => (bool?)GetValue(IsHoveredSegmentStraightProperty);

    /// <summary>True when the current (single-node) selection is an open subpath's endpoint — drives
    /// the toolbar/context-menu's Join button group and the "open endpoint" context-menu variant.</summary>
    private static readonly DependencyPropertyKey IsSelectedNodeOpenEndpointPropertyKey = DependencyProperty.RegisterReadOnly(
        nameof(IsSelectedNodeOpenEndpoint), typeof(bool), typeof(SceneCanvas), new PropertyMetadata(false));
    public static readonly DependencyProperty IsSelectedNodeOpenEndpointProperty = IsSelectedNodeOpenEndpointPropertyKey.DependencyProperty;
    public bool IsSelectedNodeOpenEndpoint => (bool)GetValue(IsSelectedNodeOpenEndpointProperty);

    /// <summary>True only when the selected endpoint AND another object's open endpoint are
    /// currently within join tolerance of each other — see FindCrossObjectJoinCandidate. The Join
    /// button/menu item is visible whenever IsSelectedNodeOpenEndpoint but only ENABLED when this is
    /// also true, so its own tooltip can explain why ("move the path near another open endpoint")
    /// instead of the button simply not existing.</summary>
    private static readonly DependencyPropertyKey CanJoinSelectedEndpointPropertyKey = DependencyProperty.RegisterReadOnly(
        nameof(CanJoinSelectedEndpoint), typeof(bool), typeof(SceneCanvas), new PropertyMetadata(false));
    public static readonly DependencyProperty CanJoinSelectedEndpointProperty = CanJoinSelectedEndpointPropertyKey.DependencyProperty;
    public bool CanJoinSelectedEndpoint => (bool)GetValue(CanJoinSelectedEndpointProperty);

    private void UpdateNodeEditToolbarState()
    {
        SetValue(IsNodeEditActivePropertyKey, _nodeEditObject is not null);
        SetValue(SelectedNodeCountPropertyKey, _selectedNodeKeys.Count);

        VectorNodeType? type = null;
        if (_nodeEditWorkingPath is not null && _selectedNodeKeys.Count > 0)
        {
            var types = _selectedNodeKeys
                .Select(key => _nodeEditWorkingPath.Subpaths[key.Subpath].Nodes[key.Node].Type)
                .Distinct()
                .ToList();
            type = types.Count == 1 ? types[0] : null;
        }
        SetValue(SelectedNodeTypePropertyKey, type);

        bool? closed = null;
        if (_nodeEditWorkingPath is not null && ResolveContextSubpathIndex() is { } index)
            closed = _nodeEditWorkingPath.Subpaths[index].IsClosed;
        SetValue(IsSelectedSubpathClosedPropertyKey, closed);

        bool? straight = null;
        if (_nodeEditWorkingPath is not null && _hoveredSegment is { } hit)
        {
            var (a, b) = _nodeEditWorkingPath.Subpaths[hit.SubpathIndex].Segment(hit.SegmentIndex);
            straight = VectorSubpath.IsStraightSegment(a, b);
        }
        SetValue(IsHoveredSegmentStraightPropertyKey, straight);

        var isOpenEndpoint = false;
        if (_nodeEditWorkingPath is not null && _selectedNodeKeys.Count == 1)
        {
            var (s, n) = _selectedNodeKeys.Single();
            var subpath = _nodeEditWorkingPath.Subpaths[s];
            isOpenEndpoint = !subpath.IsClosed && subpath.Nodes.Count > 0 && (n == 0 || n == subpath.Nodes.Count - 1);
        }
        SetValue(IsSelectedNodeOpenEndpointPropertyKey, isOpenEndpoint);
        SetValue(CanJoinSelectedEndpointPropertyKey, isOpenEndpoint && FindCrossObjectJoinCandidate() is not null);
    }

    /// <summary>Minimal ICommand wrapping a parameterless Action — ShowSelectionContextMenu's own
    /// sibling menus (OnObjectMouseRightButtonDown) can bind MenuItem.Command straight to a
    /// SceneViewModel RelayCommand, but these node-edit menus call plain SceneCanvas methods with no
    /// ICommand of their own, so this is the least code needed to keep the same declarative
    /// object-initializer style (Command=, IsEnabled follows automatically) rather than wiring a
    /// Click event handler per item.</summary>
    private sealed class RelayUiCommand(Action execute, Func<bool>? canExecute = null) : ICommand
    {
        public event EventHandler? CanExecuteChanged { add { } remove { } }
        public bool CanExecute(object? parameter) => canExecute?.Invoke() ?? true;
        public void Execute(object? parameter) => execute();
    }
}
