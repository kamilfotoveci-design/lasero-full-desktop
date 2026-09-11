using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Lasero.App.ViewModels;
using Lasero.Core.Grbl;
using Lasero.Core.Import;
using Lasero.Core.Layers;
using Lasero.Core.Scene;

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
    private VectorPath? _nodeEditWorkingPath;
    private readonly HashSet<(int Subpath, int Node)> _selectedNodeKeys = [];
    private (int Subpath, int Node, bool IsOutHandle)? _draggedHandle;
    private VectorPath? _nodeDragOriginalPath;
    private Position _nodeDragStartWorld;
    private bool _nodeMarqueeHitObject;

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

    private void EnterNodeEditMode(SceneObject obj)
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

    private void ExitNodeEditMode()
    {
        _nodeEditObject = null;
        _nodeEditWorkingPath = null;
        _selectedNodeKeys.Clear();
        _draggedHandle = null;
        _nodeDragOriginalPath = null;
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
                CommitNodeEdit(_nodeEditWorkingPath.ReplaceSubpath(segmentHit.SubpathIndex, updatedSubpath));
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

        if (!Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) _selectedNodeKeys.Clear();

        var path = _nodeEditWorkingPath;
        for (var s = 0; s < path.Subpaths.Count; s++)
        {
            var nodes = path.Subpaths[s].Nodes;
            for (var n = 0; n < nodes.Count; n++)
            {
                var world = obj.Transform.Apply(nodes[n].Anchor, obj.LocalPivot);
                if (world.X >= worldMinX && world.X <= worldMaxX && world.Y >= worldMinY && world.Y <= worldMaxY)
                    _selectedNodeKeys.Add((s, n));
            }
        }

        RedrawSelectionOverlay();
    }

    private readonly record struct SegmentHit(int SubpathIndex, int SegmentIndex, double T);

    /// <summary>Samples each ORIGINAL curve segment (not the flattened polyline, which cannot be mapped
    /// back to a curve parameter accurately) at a fixed resolution and returns the closest sample within
    /// tolerance — an approximation of t, not exact, but a double-click "add a node roughly here" gesture
    /// does not need sub-percent precision the way InsertNode's own De Casteljau split does.</summary>
    private SegmentHit? HitTestNearestSegment(SceneObject obj, VectorPath path, Point screen, double toleranceScreenPx)
    {
        SegmentHit? best = null;
        var bestDistPx = double.MaxValue;

        for (var s = 0; s < path.Subpaths.Count; s++)
        {
            var subpath = path.Subpaths[s];
            for (var seg = 0; seg < subpath.SegmentCount; seg++)
            {
                var (a, b) = subpath.Segment(seg);
                for (var i = 1; i < 40; i++)
                {
                    var t = i / 40.0;
                    var local = VectorSubpath.IsStraightSegment(a, b)
                        ? CubicBezier.Lerp(a.Anchor, b.Anchor, t)
                        : CubicBezier.Evaluate(a.Anchor, a.HandleOut ?? a.Anchor, b.HandleIn ?? b.Anchor, b.Anchor, t);
                    var world = obj.Transform.Apply(local, obj.LocalPivot);
                    var screenPoint = new Point(ToCanvasX(world.X), ToCanvasY(world.Y));
                    var dist = Distance(screenPoint, screen);
                    if (dist < bestDistPx)
                    {
                        bestDistPx = dist;
                        best = new SegmentHit(s, seg, t);
                    }
                }
            }
        }

        return bestDistPx <= toleranceScreenPx ? best : null;
    }

    private void DrawNodeEditOverlay(SceneObject obj)
    {
        var path = _nodeEditWorkingPath ?? obj.VectorPath;
        if (path is null) return;

        for (var s = 0; s < path.Subpaths.Count; s++)
        {
            var subpath = path.Subpaths[s];
            for (var n = 0; n < subpath.Nodes.Count; n++)
            {
                var node = subpath.Nodes[n];
                var worldAnchor = obj.Transform.Apply(node.Anchor, obj.LocalPivot);
                var screenAnchor = new Point(ToCanvasX(worldAnchor.X), ToCanvasY(worldAnchor.Y));
                var isSelected = _selectedNodeKeys.Contains((s, n));

                if (node.HandleIn is { } handleIn) DrawHandle(obj, s, n, handleIn, screenAnchor, isOutHandle: false);
                if (node.HandleOut is { } handleOut) DrawHandle(obj, s, n, handleOut, screenAnchor, isOutHandle: true);

                DrawNodeDot(s, n, screenAnchor, isSelected, node.Type);
            }
        }
    }

    /// <summary>Corner nodes draw as a small square, smooth nodes as a circle — the same
    /// square-corner/round-smooth convention Illustrator and Figma both use, so the node's type is
    /// legible on the canvas itself instead of only through the right-click toggle that sets it. The
    /// hit target underneath is always the same transparent circle regardless of type; only the
    /// visible dot's shape changes.</summary>
    private void DrawNodeDot(int subpathIndex, int nodeIndex, Point screen, bool isSelected, VectorNodeType type)
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
        Canvas.SetLeft(target, screen.X - NodeHitSizePx / 2);
        Canvas.SetTop(target, screen.Y - NodeHitSizePx / 2);
        DrawCanvas.Children.Add(target);
        _selectionVisuals.Add(target);

        var fill = isSelected ? SelectionBrush : SelectionHandleFill;
        Shape dot = type == VectorNodeType.Corner
            ? new Rectangle { Width = NodeVisibleSizePx, Height = NodeVisibleSizePx }
            : new Ellipse { Width = NodeVisibleSizePx, Height = NodeVisibleSizePx };
        dot.Fill = fill;
        dot.Stroke = SelectionBrush;
        dot.StrokeThickness = 1.2;
        dot.IsHitTestVisible = false;
        Canvas.SetLeft(dot, screen.X - NodeVisibleSizePx / 2);
        Canvas.SetTop(dot, screen.Y - NodeVisibleSizePx / 2);
        DrawCanvas.Children.Add(dot);
        _selectionVisuals.Add(dot);
    }

    private void DrawHandle(SceneObject obj, int subpathIndex, int nodeIndex, Position localHandle, Point anchorScreen, bool isOutHandle)
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
        Canvas.SetLeft(target, screen.X - HandleDotHitSizePx / 2);
        Canvas.SetTop(target, screen.Y - HandleDotHitSizePx / 2);
        DrawCanvas.Children.Add(target);
        _selectionVisuals.Add(target);

        var dot = new Ellipse
        {
            Width = HandleVisibleSizePx,
            Height = HandleVisibleSizePx,
            Fill = SelectionHandleFill,
            Stroke = SelectionBrush,
            StrokeThickness = 1,
            IsHitTestVisible = false,
        };
        Canvas.SetLeft(dot, screen.X - HandleVisibleSizePx / 2);
        Canvas.SetTop(dot, screen.Y - HandleVisibleSizePx / 2);
        DrawCanvas.Children.Add(dot);
        _selectionVisuals.Add(dot);
    }

    private void OnNodeOrHandleMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_nodeEditObject is null || _nodeEditWorkingPath is null) return;
        var tag = (NodeHitTag)((FrameworkElement)sender).Tag;
        e.Handled = true;
        Focus();

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
        _dragMode = DragMode.NodeEdit;
        DrawCanvas.CaptureMouse();
        RedrawSelectionOverlay();
    }

    private void OnNodeRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_nodeEditObject is null || _nodeEditWorkingPath is null) return;
        var tag = (NodeHitTag)((FrameworkElement)sender).Tag;
        if (tag.IsHandle) return;
        e.Handled = true;

        // Compact contextual action instead of a big inspector, per the spec: right-click toggles
        // Corner <-> Smooth directly rather than opening a menu for a two-way choice.
        var subpath = _nodeEditWorkingPath.Subpaths[tag.Subpath];
        var node = subpath.Nodes[tag.Node];
        var newType = node.Type == VectorNodeType.Corner ? VectorNodeType.Smooth : VectorNodeType.Corner;
        var nodes = subpath.Nodes.ToList();
        nodes[tag.Node] = VectorPathEditor.ConvertNodeType(node, newType);
        CommitNodeEdit(_nodeEditWorkingPath.ReplaceSubpath(tag.Subpath, subpath with { Nodes = nodes }));
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
            var subpath = _nodeDragOriginalPath.Subpaths[handle.Subpath];
            var node = subpath.Nodes[handle.Node];
            var breakSymmetry = Keyboard.Modifiers.HasFlag(ModifierKeys.Alt);
            var nodes = subpath.Nodes.ToList();
            nodes[handle.Node] = VectorPathEditor.MoveHandle(node, handle.IsOutHandle, currentLocal, breakSymmetry);
            updated = _nodeDragOriginalPath.ReplaceSubpath(handle.Subpath, subpath with { Nodes = nodes });
        }
        else
        {
            var startLocal = obj.Transform.Inverse(_nodeDragStartWorld, obj.LocalPivot);
            var dx = currentLocal.X - startLocal.X;
            var dy = currentLocal.Y - startLocal.Y;
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
        }

        _nodeEditWorkingPath = updated;
        RenderVectorPathLive(obj, updated);
        RedrawSelectionOverlay();
    }

    /// <summary>Live preview during a node/handle drag — mutates the object's own LocalShapes directly
    /// (geometry tracks the pointer with no lag/easing, per the spec's hard requirement) and repaints
    /// through the existing UpdateObjectGeometry pipeline. Nothing here touches the command stack; only
    /// FinishNodeEditDrag/CommitNodeEdit does, once, on mouse-up.</summary>
    private void RenderVectorPathLive(SceneObject obj, VectorPath path)
    {
        var color = obj.LocalShapes.FirstOrDefault()?.LayerColor ?? VectorPathDefaultColor;
        obj.LocalShapes = path.Subpaths.Select(subpath => new ImportedShape
        {
            Points = subpath.Flatten(),
            IsClosed = subpath.IsClosed,
            LayerColor = color,
            PreferredMode = subpath.IsClosed ? LayerMode.Fill : LayerMode.Cut,
        }).ToList();
        UpdateObjectGeometry(obj);
    }

    private void FinishNodeEditDrag()
    {
        if (_nodeEditObject is not null && _nodeEditWorkingPath is not null)
            CommitNodeEdit(_nodeEditWorkingPath);
        _draggedHandle = null;
        _nodeDragOriginalPath = null;
        if (DrawCanvas.IsMouseCaptured) DrawCanvas.ReleaseMouseCapture();
    }

    private void CancelNodeEditDrag()
    {
        if (_nodeEditObject is not null && _nodeDragOriginalPath is not null)
        {
            _nodeEditWorkingPath = _nodeDragOriginalPath;
            RenderVectorPathLive(_nodeEditObject, _nodeDragOriginalPath);
        }
        _draggedHandle = null;
        _nodeDragOriginalPath = null;
        RedrawSelectionOverlay();
    }

    /// <summary>Pushes one ReplaceObjectsCommand (one undo step) for a completed node edit and re-syncs
    /// this control's notion of "the object being edited" to the replacement CommitVectorPathEdit
    /// creates — the original `obj` reference is retired from the scene the moment this runs.</summary>
    private void CommitNodeEdit(VectorPath updated)
    {
        if (_nodeEditObject is null || ViewModel is null) return;
        ViewModel.CommitVectorPathEdit(_nodeEditObject, updated);
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
    }
}
