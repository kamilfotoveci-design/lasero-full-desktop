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
    private double SnapToleranceScreenPx => _preferences.SnapDistancePx;
    private const double SnapIndicatorVisibleSizePx = 14;

    private static readonly RgbColor VectorPathDefaultColor = new(18, 18, 18);

    // --- multi-click path drawing state -------------------------------------------------------
    private readonly PenPathSession _pen = new();
    private readonly List<FrameworkElement> _pathToolVisuals = [];
    private bool _pathToolDrawing => _pen.IsDrawing;
    private bool _pathClosingClick;
    private bool _pathCloseRefused;
    private Position _pathNodeDownWorld;
    private Point _pathNodeDownScreen;

    private const string CloseNeedsThreeNodesMessage =
        "Uzavřít lze jen dráhu nejméně se třemi různými uzly. Dvojitá čára by se vypálila dvakrát.";

    // --- node edit mode state -------------------------------------------------------------------
    private SceneObject? _nodeEditObject;
    private bool _committingNodeEdit;
    private VectorPath? _nodeEditWorkingPath;
    private readonly HashSet<(int Subpath, int Node)> _selectedNodeKeys = [];
    // A clicked segment stays the target of the segment commands (toolbar, Shift+L/C/M/T, Delete)
    // while the pointer travels to the toolbar; hover alone is lost on the way. Exclusive with a node
    // selection. The node count pins it to the topology it was picked on.
    private VectorPathHitTester.Hit? _selectedSegment;
    private int _selectedSegmentNodeCount;
    // The node under the pointer when a node drag began: it is the one that snaps, the rest of the
    // selection follows with the same delta.
    private (int Subpath, int Node)? _grabbedNodeKey;
    private (int Subpath, int Node, bool IsOutHandle)? _draggedHandle;
    private VectorPathHitTester.Hit? _draggedSegment;
    private VectorPath? _nodeDragOriginalPath;
    private VectorPathDragSession? _nodeDragSession;
    private Position _nodeDragStartWorld;
    // A click that jitters a pixel or two must stay a click: no node move, no straight-to-curve
    // conversion, no undo entry. The drag only starts once the pointer has travelled this far.
    private const double NodeDragThresholdPx = 3;
    private Point _nodeDragStartScreen;
    private bool _nodeDragMoved;
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
    private List<SnapCandidate> _gestureSnapCandidates = [];
    private SnapCandidate? _activeSnapTarget;

    // ------------------------------------------------------------------
    // Drawing — one click stream builds one VectorSubpath, committed as a single SceneObject only
    // when the path is finished (double-click / Enter / close-on-first-node) or discarded on Escape.
    // ------------------------------------------------------------------

    private void BeginVectorPathClick(Point screen)
    {
        var pickedUpEnd = false;
        if (!_pen.IsDrawing)
        {
            BeginGestureSnapping([]);
            // LB-VEC-043 / LIGHTBURN_VECTOR_PARITY.md §24: pressing on an open end of an existing
            // editable path picks that path up and keeps drawing it, instead of starting a new object.
            if (FindPenContinuationTarget(screen) is { } target)
            {
                _pen.Continue(target.WorldSubpath, target.AtStart, target.Object.Id, target.SubpathIndex);
                pickedUpEnd = true;
            }
        }

        // The press that picks up an end lands exactly on it: a click adds nothing, a drag pulls that
        // end's outgoing handle (see PenPathSession.Append).
        var world = pickedUpEnd ? _pen.Nodes[^1].Anchor : ApplyPenSnap(new Position(ToWorldX(screen.X), ToWorldY(screen.Y), 0));
        var nearFirst = _pen.Nodes.Count >= 2 && IsNearFirstNode(screen);
        _pathClosingClick = nearFirst && _pen.CanClose;
        _pathCloseRefused = nearFirst && !_pen.CanClose;
        if (_pathClosingClick) world = _pen.Nodes[0].Anchor;
        _pathNodeDownScreen = screen;
        _pathNodeDownWorld = world;
        _dragMode = DragMode.PathTool;
        DrawCanvas.CaptureMouse();
        UpdateVectorPathPreview(screen);
    }

    private void EndVectorPathClick(Point screen)
    {
        if (ViewModel?.ActiveTool != DesignerTool.Line)
        {
            // The tool changed under the press (a shortcut mid-click): the path was already finished
            // by that switch, so this release must not start a new one.
            _pathClosingClick = false;
            _pathCloseRefused = false;
            _dragMode = DragMode.None;
            if (DrawCanvas.IsMouseCaptured) DrawCanvas.ReleaseMouseCapture();
            return;
        }
        var dragOffset = PenDragOffset(screen);
        var breakHandle = Keyboard.Modifiers.HasFlag(ModifierKeys.Alt);

        if (_pathClosingClick)
        {
            if (_pen.BuildClosed(dragOffset, breakHandle) is { } closed)
                CommitVectorPath(closed);
        }
        else if (_pathCloseRefused)
        {
            // A click on the first node of a two-node path would trace the same line back: refused,
            // with the reason, rather than closing it or stacking a node on top of the first one.
            ShowCanvasNotice(CloseNeedsThreeNodesMessage);
        }
        else
        {
            _pen.Append(_pathNodeDownWorld, dragOffset, breakHandle);
        }

        _pathClosingClick = false;
        _pathCloseRefused = false;
        _dragMode = DragMode.None;
        if (DrawCanvas.IsMouseCaptured) DrawCanvas.ReleaseMouseCapture();
        UpdateVectorPathPreview(screen);
        UpdateToolCursor();
    }

    /// <summary>Drag distance of the current pen press as a handle offset, or null for a click (below
    /// the same 3 px threshold FinishDraw uses). Shift locks the handle direction to 15° steps.</summary>
    private Position? PenDragOffset(Point screen)
    {
        var dxPx = screen.X - _pathNodeDownScreen.X;
        var dyPx = screen.Y - _pathNodeDownScreen.Y;
        if (Math.Abs(dxPx) < 3 && Math.Abs(dyPx) < 3) return null;
        var pointer = new Position(ToWorldX(screen.X), ToWorldY(screen.Y), 0);
        var dx = pointer.X - _pathNodeDownWorld.X;
        var dy = pointer.Y - _pathNodeDownWorld.Y;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
            (dx, dy) = AngleConstraint.Constrain(dx, dy, AngleConstraint.PenStepDegrees);
        return new Position(dx, dy, 0);
    }

    /// <summary>Finishes the in-progress path as OPEN (double-click, Enter, Esc). Fewer than two nodes
    /// is not a usable object — discarded, tool stays active for another attempt.</summary>
    private void FinishVectorPath()
    {
        if (_pen.BuildOpen() is { } open)
            CommitVectorPath(open);
        else
            ResetVectorPathToolState();

        _dragMode = DragMode.None;
        if (DrawCanvas.IsMouseCaptured) DrawCanvas.ReleaseMouseCapture();
        UpdateToolCursor();
    }

    /// <summary>One undo step for the whole drawing session: a new path object, or — when the session
    /// continued an existing open path — that object's path with the contour replaced.
    /// <paramref name="switchTool"/> is false when the commit happens BECAUSE the tool is changing (the
    /// operator's new tool must win over the "switch to Select after drawing" preference).</summary>
    private void CommitVectorPath(VectorSubpath subpath, bool switchTool = true)
    {
        var continuation = _pen.Continuation;
        ResetVectorPathToolState();
        if (ViewModel is null || subpath.Nodes.Count < 2) return;

        var continued = continuation is { } c
            ? ViewModel.Objects.FirstOrDefault(o => o.Id == c.ObjectId && o.VectorPath is { } p
                && c.SubpathIndex < p.Subpaths.Count && ViewModel.CanSelectObject(o))
            : null;
        if (continued is not null)
        {
            var local = MapSubpath(subpath, world => continued.Transform.Inverse(world, continued.LocalPivot));
            ViewModel.CommitVectorPathEdit(continued, continued.VectorPath!.ReplaceSubpath(continuation!.Value.SubpathIndex, local));
        }
        else
        {
            ViewModel.AddVectorPath(new VectorPath { Subpaths = [subpath] });
        }

        if (switchTool && _preferences.SwitchToSelectAfterDrawing) ViewModel.ActiveTool = DesignerTool.Select;
    }

    private static VectorSubpath MapSubpath(VectorSubpath subpath, Func<Position, Position> map) => subpath with
    {
        Nodes = subpath.Nodes.Select(node => new VectorNode(
            map(node.Anchor),
            node.HandleIn is { } hi ? map(hi) : null,
            node.HandleOut is { } ho ? map(ho) : null,
            node.Type)).ToList(),
    };

    /// <summary>Tool switch while drawing: the path drawn so far is kept (committed when it has two
    /// usable nodes), never silently thrown away.</summary>
    private void CommitPenPathOnToolSwitch()
    {
        if (_pen.BuildOpen() is { } open) CommitVectorPath(open, switchTool: false);
        else ResetVectorPathToolState();
    }

    /// <summary>Backspace while drawing: removes the newest placed node; the path and the tool stay.
    /// With nothing of its own left, a continuation of an existing path is simply let go (the
    /// existing path was never changed).</summary>
    private void RemoveLastPenNode()
    {
        if (!_pen.RemoveLast()) ResetVectorPathToolState();
        UpdateVectorPathPreview(Mouse.GetPosition(DrawCanvas));
    }

    private void ResetVectorPathToolState()
    {
        _pen.Reset();
        _pathClosingClick = false;
        _pathCloseRefused = false;
        ClearVectorPathPreviewVisuals();
        if (_toolPreviewVisual is not null) _toolPreviewVisual.Visibility = Visibility.Collapsed;
    }

    private bool IsNearFirstNode(Point screen)
    {
        if (_pen.Nodes.Count == 0) return false;
        var first = new Point(ToCanvasX(_pen.Nodes[0].Anchor.X), ToCanvasY(_pen.Nodes[0].Anchor.Y));
        return Distance(first, screen) <= CloseHoverThresholdPx;
    }

    private readonly record struct PenContinuationTarget(SceneObject Object, int SubpathIndex, bool AtStart, VectorSubpath WorldSubpath);

    /// <summary>The open end of an editable path nearest to the pointer (within the close-hover
    /// tolerance), which a pen press would pick up and continue. Locked, hidden and derived offset
    /// paths are not offered.</summary>
    private PenContinuationTarget? FindPenContinuationTarget(Point screen)
    {
        if (ViewModel is null) return null;
        PenContinuationTarget? best = null;
        var bestDistance = double.MaxValue;
        foreach (var obj in ViewModel.Objects)
        {
            if (obj.VectorPath is null || obj.IsLocked || obj.OffsetLink is not null || !ViewModel.CanSelectObject(obj)) continue;
            var world = obj.GetWorldVectorPath();
            if (world is null) continue;
            for (var s = 0; s < world.Subpaths.Count; s++)
            {
                var subpath = world.Subpaths[s];
                if (subpath.IsClosed || subpath.Nodes.Count < 2) continue;
                foreach (var atStart in new[] { true, false })
                {
                    var anchor = subpath.Nodes[atStart ? 0 : subpath.Nodes.Count - 1].Anchor;
                    var distance = Distance(new Point(ToCanvasX(anchor.X), ToCanvasY(anchor.Y)), screen);
                    if (distance <= CloseHoverThresholdPx && distance < bestDistance)
                    {
                        bestDistance = distance;
                        best = new PenContinuationTarget(obj, s, atStart, subpath);
                    }
                }
            }
        }
        return best;
    }

    /// <summary>Pen point resolution. Ctrl: raw pointer. Shift (with a node already placed): the
    /// direction from the last node locks to 15° steps and nothing else snaps. Otherwise the scene's
    /// snap targets plus the pen's OWN nodes placed so far (so a path can return exactly to an earlier
    /// point), then the grid.</summary>
    private Position ApplyPenSnap(Position point)
    {
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            _activeSnapTarget = null;
            return point;
        }

        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) && _pen.Nodes.Count > 0)
        {
            var last = _pen.Nodes[^1].Anchor;
            var (dx, dy) = AngleConstraint.Constrain(point.X - last.X, point.Y - last.Y, AngleConstraint.PenStepDegrees);
            _activeSnapTarget = null;
            return new Position(last.X + dx, last.Y + dy, 0);
        }

        var candidates = new List<SnapCandidate>();
        if (_preferences.SnapToObjects)
        {
            candidates.AddRange(_gestureSnapCandidates);
            for (var i = 0; i < _pen.Nodes.Count - 1; i++)
                candidates.Add(new SnapCandidate(_pen.Nodes[i].Anchor, i == 0 ? SnapKind.Endpoint : SnapKind.Node));
        }
        double? gridStep = _preferences.SnapToGrid ? _preferences.ResolveGridStepMm(_scale) : null;
        var result = SnapEngine.Resolve(point, candidates, SnapToleranceScreenPx / _scale, gridStep);
        _activeSnapTarget = result.Target;
        return result.Point;
    }

    private void UpdateVectorPathPreview(Point cursorScreen)
    {
        if (ViewModel is null || ViewModel.ActiveTool != DesignerTool.Line)
        {
            ClearVectorPathPreviewVisuals();
            return;
        }

        ClearVectorPathPreviewVisuals();
        if (!_pen.IsDrawing)
        {
            // Idle pen over an open end of an existing path: that end lights up, the way a press
            // there will continue the path (LIGHTBURN_VECTOR_PARITY.md §24 "endpoint highlights").
            if (_dragMode == DragMode.None && FindPenContinuationTarget(cursorScreen) is { } target)
            {
                var end = target.WorldSubpath.Nodes[target.AtStart ? 0 : target.WorldSubpath.Nodes.Count - 1].Anchor;
                AddPathToolDot(new Point(ToCanvasX(end.X), ToCanvasY(end.Y)), highlight: true);
            }
            return;
        }

        var isPressed = _dragMode == DragMode.PathTool;
        var cursorWorld = isPressed
            ? new Position(ToWorldX(cursorScreen.X), ToWorldY(cursorScreen.Y), 0)
            : ApplyPenSnap(new Position(ToWorldX(cursorScreen.X), ToWorldY(cursorScreen.Y), 0));
        IReadOnlyList<VectorNode> previewNodes = _pen.Nodes;
        var previewClosed = false;

        if (isPressed && _pathClosingClick)
        {
            if (_pen.BuildClosed(PenDragOffset(cursorScreen), Keyboard.Modifiers.HasFlag(ModifierKeys.Alt)) is { } closing)
            {
                // Drawing order, not the commit orientation: the preview only needs the shape.
                previewNodes = _pen.Continuation is { FromStart: true } ? VectorPathEditor.ReverseSubpath(closing).Nodes : closing.Nodes;
                previewClosed = true;
            }
        }
        else if (!(isPressed && _pathCloseRefused))
        {
            var tentativeAnchor = isPressed ? _pathNodeDownWorld : cursorWorld;
            Position? dragOffset = isPressed ? PenDragOffset(cursorScreen) : null;
            var previewSubpath = VectorPathEditor.AppendNode(
                new VectorSubpath { Nodes = previewNodes, IsClosed = false }, tentativeAnchor, dragOffset,
                isPressed && Keyboard.Modifiers.HasFlag(ModifierKeys.Alt));
            previewNodes = previewSubpath.Nodes;
        }

        var flattened = new VectorSubpath { Nodes = previewNodes, IsClosed = previewClosed }.Flatten();
        EnsureToolPreviewVisual();
        _toolPreviewVisual!.Data = BuildCompoundGeometry(
        [
            new ImportedShape
            {
                Points = flattened, IsClosed = false, LayerColor = VectorPathDefaultColor, PreferredMode = LayerMode.Cut,
            },
        ]);
        _toolPreviewVisual.Visibility = Visibility.Visible;

        // The first node only promises "click to close" when closing is actually allowed.
        var hoveringClose = !isPressed && _pen.CanClose && IsNearFirstNode(cursorScreen);
        for (var i = 0; i < _pen.Nodes.Count; i++)
        {
            var screen = new Point(ToCanvasX(_pen.Nodes[i].Anchor.X), ToCanvasY(_pen.Nodes[i].Anchor.Y));
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
        if (obj.VectorPath is null || ViewModel is null || !ViewModel.CanSelectObject(obj)) return;
        _nodeEditObject = obj;
        _nodeEditWorkingPath = obj.VectorPath;
        _selectedNodeKeys.Clear();
        _selectedSegment = null;
        if (ViewModel.SelectedObjects.Count != 1 || !ViewModel.SelectedObjects.Contains(obj))
        {
            ViewModel.SelectedObjects.Clear();
            ViewModel.SelectedObjects.Add(obj);
        }
        RedrawSelectionOverlay();
    }

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
        _selectedSegment = null;
        _grabbedNodeKey = null;
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
                // The click also SELECTS the segment, so the segment commands keep a target once the
                // pointer has moved away to the toolbar (hover alone would be gone by then).
                SelectSegment(hit);
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
            else if (_selectedSegment is not null)
            {
                // A click on the shape away from any segment drops the segment selection.
                _selectedSegment = null;
                RedrawSelectionOverlay();
            }
            return;
        }
        _selectedSegment = null;

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
        if (_selectedSegment is { } selectedSeg && IsValidSegment(path, selectedSeg))
            DrawSegmentHoverHighlight(obj, path, selectedSeg, isSelected: true);
        if (showHover && _hoveredSegment is { } hoveredSeg && IsValidSegment(path, hoveredSeg) && !IsSameSegment(_selectedSegment, hoveredSeg))
            DrawSegmentHoverHighlight(obj, path, hoveredSeg);

        // Level of detail. A traced or imported path can carry thousands of nodes; one hit target, one
        // dot and up to two handles per node meant tens of thousands of elements rebuilt on every
        // pointer move of a drag. Nodes that are off screen cannot be touched, and dots closer together
        // than NodeDotMinSpacingPx cannot be told apart or picked individually, so neither is drawn
        // (selected, hovered and open-end nodes always are). On a dense path the Bezier handles are
        // shown for the selected and hovered nodes only, as for every vector editor at that density.
        var visibleNodeCount = 0;
        foreach (var subpath in path.Subpaths)
            foreach (var node in subpath.Nodes)
            {
                var anchor = obj.Transform.Apply(node.Anchor, obj.LocalPivot);
                if (IsNearViewport(ToCanvasX(anchor.X), ToCanvasY(anchor.Y))) visibleNodeCount++;
            }

        var allHandles = visibleNodeCount <= NodeHandleDensityLimit;
        var occupied = new HashSet<long>();
        for (var s = 0; s < path.Subpaths.Count; s++)
        {
            var subpath = path.Subpaths[s];
            for (var n = 0; n < subpath.Nodes.Count; n++)
            {
                var node = subpath.Nodes[n];
                var worldAnchor = obj.Transform.Apply(node.Anchor, obj.LocalPivot);
                var screenAnchor = new Point(ToCanvasX(worldAnchor.X), ToCanvasY(worldAnchor.Y));
                var isSelected = _selectedNodeKeys.Contains((s, n));
                var isHoveredNode = showHover && _hoveredNodeKey == (s, n);
                var isOpenEndpoint = !subpath.IsClosed && (n == 0 || n == subpath.Nodes.Count - 1);

                var mustDraw = isSelected || isHoveredNode || isOpenEndpoint;
                if (!mustDraw)
                {
                    if (!IsNearViewport(screenAnchor.X, screenAnchor.Y)) continue;
                    var cell = ((long)Math.Floor(screenAnchor.X / NodeDotMinSpacingPx) << 32)
                        ^ (long)(uint)(int)Math.Floor(screenAnchor.Y / NodeDotMinSpacingPx);
                    if (!occupied.Add(cell)) continue;
                }

                if (allHandles || isSelected || isHoveredNode)
                {
                    if (node.HandleIn is { } handleIn)
                        DrawHandle(obj, s, n, handleIn, screenAnchor, isOutHandle: false, isHovered: showHover && _hoveredHandleKey == (s, n, false));
                    if (node.HandleOut is { } handleOut)
                        DrawHandle(obj, s, n, handleOut, screenAnchor, isOutHandle: true, isHovered: showHover && _hoveredHandleKey == (s, n, true));
                }

                DrawNodeDot(s, n, screenAnchor, isSelected, node.Type, isOpenEndpoint, isHovered: isHoveredNode);
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
    private static bool IsValidSegment(VectorPath path, VectorPathHitTester.Hit hit) =>
        hit.SubpathIndex >= 0 && hit.SubpathIndex < path.Subpaths.Count
        && hit.SegmentIndex >= 0 && hit.SegmentIndex < path.Subpaths[hit.SubpathIndex].SegmentCount;

    private static bool IsSameSegment(VectorPathHitTester.Hit? a, VectorPathHitTester.Hit b) =>
        a is { } x && x.SubpathIndex == b.SubpathIndex && x.SegmentIndex == b.SegmentIndex;

    /// <summary>The segment the segment commands act on: the clicked (selected) one, else the one
    /// under the pointer. Null when neither exists or it no longer fits the working path.</summary>
    private VectorPathHitTester.Hit? ActiveSegment
    {
        get
        {
            if (_nodeEditWorkingPath is null) return null;
            if (_selectedSegment is { } selected && IsValidSegment(_nodeEditWorkingPath, selected)) return selected;
            if (_hoveredSegment is { } hovered && IsValidSegment(_nodeEditWorkingPath, hovered)) return hovered;
            return null;
        }
    }

    private void SelectSegment(VectorPathHitTester.Hit hit)
    {
        if (_nodeEditWorkingPath is null || !IsValidSegment(_nodeEditWorkingPath, hit)) return;
        _selectedNodeKeys.Clear();
        _selectedSegment = hit;
        _selectedSegmentNodeCount = _nodeEditWorkingPath.Subpaths[hit.SubpathIndex].Nodes.Count;
    }

    /// <summary>After a commit, a selected segment survives only if its contour kept its node count
    /// (a reshape); any topology change (insert, delete, split, join) makes its index meaningless.</summary>
    private void RevalidateSegmentSelection()
    {
        if (_selectedSegment is not { } selected) return;
        if (_nodeEditWorkingPath is null || !IsValidSegment(_nodeEditWorkingPath, selected)
            || _nodeEditWorkingPath.Subpaths[selected.SubpathIndex].Nodes.Count != _selectedSegmentNodeCount)
            _selectedSegment = null;
        if (_hoveredSegment is { } hovered && (_nodeEditWorkingPath is null || !IsValidSegment(_nodeEditWorkingPath, hovered)))
            _hoveredSegment = null;
    }

    /// <summary>Pointer left the canvas: hover visuals go (they describe where the pointer is), the
    /// selected segment stays (it is what the toolbar the pointer is heading to will act on).</summary>
    private void OnDrawCanvasMouseLeaveForNodeEdit(object sender, MouseEventArgs e)
    {
        ClearTrimHover();
        if (!_pen.IsDrawing && _dragMode == DragMode.None) ClearVectorPathPreviewVisuals();
        if (_nodeEditObject is null || _dragMode != DragMode.None || _hoveredSegment is null) return;
        _hoveredSegment = null;
        Cursor = Cursors.Arrow;
        RedrawSelectionOverlay();
    }

    private void DrawSegmentHoverHighlight(SceneObject obj, VectorPath path, VectorPathHitTester.Hit hit, bool isSelected = false)
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
            Opacity = isSelected ? 0.75 : 0.35,
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
    private const double NodeDotMinSpacingPx = 7;
    private const int NodeHandleDensityLimit = 200;

    private bool IsNearViewport(double x, double y) =>
        x >= -24 && y >= -24 && x <= ActualWidth + 24 && y <= ActualHeight + 24;

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
        _selectedSegment = null;
        _grabbedNodeKey = null;
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
            _grabbedNodeKey = key;
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
        _nodeDragStartScreen = screen;
        _nodeDragMoved = false;
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
        // applied directly in UpdateNodeEditDrag). A multi-node drag snaps the GRABBED node and moves
        // the rest of the selection by the same delta, so the selection keeps its own shape; every
        // selected node is excluded from the candidates (they all move).
        _nodeDragSnapCandidates = [];
        if (!tag.IsHandle && _selectedNodeKeys.Contains((tag.Subpath, tag.Node)) && _preferences.SnapToObjects)
        {
            _nodeDragSnapCandidates = SnapCandidateBuilder.FromVectorPath(
                _nodeDragOriginalPath, WorldOf, _selectedNodeKeys);
            if (ViewModel is not null)
            {
                var layers = ViewModel.Layers.ToDictionary(layer => layer.Id);
                _nodeDragSnapCandidates.AddRange(SnapCandidateBuilder.FromSceneObjects(
                    ViewModel.Objects.Where(obj => obj.Id != _nodeEditObject.Id), layers));
            }
        }
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
        _nodeDragStartScreen = Mouse.GetPosition(DrawCanvas);
        _nodeDragMoved = false;
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
        if (!_nodeDragMoved)
        {
            if (Distance(screen, _nodeDragStartScreen) < NodeDragThresholdPx) return;
            _nodeDragMoved = true;
        }
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
        if (!_nodeDragMoved)
        {
            if (Distance(screen, _nodeDragStartScreen) < NodeDragThresholdPx) return;
            _nodeDragMoved = true;
        }
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
            // Alt breaks the tangency (the node becomes a cusp); a Smooth node beside a straight
            // segment slides its handle along the line (see VectorPathEditor.MoveHandle).
            var breakSymmetry = Keyboard.Modifiers.HasFlag(ModifierKeys.Alt);
            var moved = VectorPathEditor.MoveHandle(subpath, handle.Node, handle.IsOutHandle, currentLocal, breakSymmetry);
            updated = _nodeDragOriginalPath.ReplaceSubpath(handle.Subpath, moved);
        }
        else
        {
            var shiftHeld = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
            // Object/path snapping only applies to the default (unconstrained) drag — combining it
            // with Shift's angle-lock would be ambiguous about which one wins, so Shift always means
            // "constrain angle", never "and also snap". The GRABBED node is the one that snaps; the
            // rest of a multi-node selection follows with the same delta.
            if (!shiftHeld && _grabbedNodeKey is { } grabbed && _selectedNodeKeys.Contains(grabbed))
            {
                // Preserve the pointer-to-anchor offset from mouse-down. Snapping the pointer itself
                // would leave the actual node a few pixels away from the reported target whenever
                // the user grabbed inside its hit circle rather than at its exact centre.
                var (subpathIndex, nodeIndex) = grabbed;
                var originalAnchor = _nodeDragOriginalPath.Subpaths[subpathIndex].Nodes[nodeIndex].Anchor;
                var anchorWorld = obj.Transform.Apply(originalAnchor, obj.LocalPivot);
                var proposedAnchor = new Position(
                    anchorWorld.X + currentWorld.X - _nodeDragStartWorld.X,
                    anchorWorld.Y + currentWorld.Y - _nodeDragStartWorld.Y, 0);
                var snappedAnchor = ApplyNodeSnap(proposedAnchor);
                currentWorld = new Position(
                    currentWorld.X + snappedAnchor.X - proposedAnchor.X,
                    currentWorld.Y + snappedAnchor.Y - proposedAnchor.Y, 0);
                currentLocal = obj.Transform.Inverse(currentWorld, obj.LocalPivot);
            }
            else
                _activeSnapTarget = null;

            var startLocal = obj.Transform.Inverse(_nodeDragStartWorld, obj.LocalPivot);
            var dx = currentLocal.X - startLocal.X;
            var dy = currentLocal.Y - startLocal.Y;
            if (shiftHeld)
            {
                // Constrain in WORLD space (the screen's eighth-turns), then map the constrained
                // point back into the object's local frame. Constraining the local delta would lock
                // to the wrong angles on a rotated, flipped or non-uniformly scaled object.
                var (worldDx, worldDy) = ConstrainToNearestEighthTurn(
                    currentWorld.X - _nodeDragStartWorld.X, currentWorld.Y - _nodeDragStartWorld.Y);
                var constrainedLocal = obj.Transform.Inverse(
                    new Position(_nodeDragStartWorld.X + worldDx, _nodeDragStartWorld.Y + worldDy, 0), obj.LocalPivot);
                dx = constrainedLocal.X - startLocal.X;
                dy = constrainedLocal.Y - startLocal.Y;
            }
            updated = TranslateSelectedNodes(_nodeDragOriginalPath, dx, dy);
            Cursor = IsDraggingEndpointOntoJoinTarget(updated) ? Cursors.UpArrow : Cursors.Hand;
        }

        _nodeEditWorkingPath = updated;
        RenderVectorPathLive(obj, updated);
        RedrawSelectionOverlay();
    }

    /// <summary>Moves the selected nodes (anchor and handles together) by a local delta, then lets
    /// each Smooth neighbour that sits beside a straight segment keep that line as its tangent
    /// (VectorPathEditor.ReconcileSmoothNeighbours), so a moved node never leaves a lying Smooth flag.</summary>
    private VectorPath TranslateSelectedNodes(VectorPath source, double dx, double dy)
    {
        var subpaths = source.Subpaths.ToList();
        for (var s = 0; s < subpaths.Count; s++)
        {
            var nodes = subpaths[s].Nodes.ToList();
            var moved = new List<int>();
            for (var n = 0; n < nodes.Count; n++)
            {
                if (!_selectedNodeKeys.Contains((s, n))) continue;
                nodes[n] = nodes[n].Translated(dx, dy);
                moved.Add(n);
            }
            if (moved.Count > 0)
                subpaths[s] = VectorPathEditor.ReconcileSmoothNeighbours(subpaths[s] with { Nodes = nodes }, moved);
        }
        return source with { Subpaths = subpaths };
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
        if (originalSubpath.IsClosed || originalSubpath.Nodes.Count < 3) return false;
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
        // Nastavení: snapping to other objects and to the grid are switched independently.
        IReadOnlyList<SnapCandidate> candidates = _preferences.SnapToObjects ? _nodeDragSnapCandidates : [];
        double? gridStep = _preferences.SnapToGrid ? _preferences.ResolveGridStepMm(_scale) : null;
        var result = SnapEngine.Resolve(currentWorld, candidates, toleranceWorld, gridStep);
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
        double? gridStep = _preferences.SnapToGrid ? _preferences.ResolveGridStepMm(_scale) : null;
        var result = SnapEngine.Resolve(currentWorld, [], toleranceWorld, gridStep);
        _activeSnapTarget = result.Target;
        return result.Point;
    }

    private void BeginGestureSnapping(IEnumerable<SceneObject> excludedObjects)
    {
        _gestureSnapCandidates = [];
        if (ViewModel is null) return;
        var excluded = excludedObjects.Select(item => item.Id).ToHashSet();
        var layers = ViewModel.Layers.ToDictionary(layer => layer.Id);
        _gestureSnapCandidates = SnapCandidateBuilder.FromSceneObjects(
            ViewModel.Objects.Where(item => !excluded.Contains(item.Id)), layers);
    }

    private Position ApplyGestureSnap(Position point)
    {
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            _activeSnapTarget = null;
            return point;
        }

        IReadOnlyList<SnapCandidate> candidates = _preferences.SnapToObjects ? _gestureSnapCandidates : [];
        double? gridStep = _preferences.SnapToGrid ? _preferences.ResolveGridStepMm(_scale) : null;
        var result = SnapEngine.Resolve(point, candidates, SnapToleranceScreenPx / _scale, gridStep);
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
        // The drag is drawn from the working path (see VectorPathRenderSource), a frozen cubic geometry
        // built from the nodes. Re-flattening every node into LocalShapes on each pointer move, only for
        // FinishNodeEditDrag to restore the original shapes before commit, cost tens of milliseconds on a
        // path with thousands of nodes and fed nothing: snapping uses candidates built at drag start and
        // the commit flattens the final path once. The session still pins the pre-drag shapes.
        _nodeDragSession ??= new VectorPathDragSession(obj.LocalShapes);
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

            if (TryCloseByEndpointJoin(out var closedPath, out var refused))
            {
                CommitNodeEdit(closedPath);
            }
            else if (refused)
            {
                // Dropping an end on the other end of a contour too short to close would leave a
                // line traced twice: the drag is rolled back and the reason shown.
                _nodeEditWorkingPath = _nodeDragOriginalPath;
                ShowCanvasNotice(CloseNeedsThreeNodesMessage);
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
    /// onto its OTHER endpoint closes the path instead of leaving the node sitting on top of it. The
    /// dragged end lands exactly on the far end and the two MERGE into one node (VectorPathEditor.Close
    /// drops the coincident duplicate), so the shape the operator dragged is what gets closed — no
    /// return to the pre-drag position, no extra closing segment. A contour that would close with
    /// fewer than three distinct nodes is refused (<paramref name="refused"/>). Screen-space
    /// tolerance, matching the path tool's own close-hover threshold.</summary>
    private bool TryCloseByEndpointJoin(out VectorPath closedPath, out bool refused)
    {
        closedPath = VectorPath.Empty;
        refused = false;
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
        if (Distance(draggedScreen, otherScreen) <= CloseHoverThresholdPx)
        {
            var landed = PlaceNodeOn(draggedWorkingSubpath, nodeIndex, originalSubpath.Nodes[otherIndex].Anchor);
            if (!VectorPathEditor.CanClose(landed))
            {
                refused = true;
                return false;
            }
            closedPath = _nodeEditWorkingPath.ReplaceSubpath(subpathIndex, VectorPathEditor.Close(landed));
            _selectedNodeKeys.Clear(); // merging the two ends shifts the node indices
            return true;
        }

        // Not its own far end: another open subpath of the SAME object may have an endpoint here.
        // The dragged end snaps onto that endpoint (it becomes the join node) and the two contours
        // become one subpath.
        var bestScreenDistance = double.MaxValue;
        (int Index, bool AtStart)? best = null;
        for (var other = 0; other < _nodeDragOriginalPath.Subpaths.Count; other++)
        {
            var candidate = _nodeDragOriginalPath.Subpaths[other];
            if (other == subpathIndex || candidate.IsClosed || candidate.Nodes.Count < 2) continue;
            foreach (var atStart in new[] { true, false })
            {
                var anchor = candidate.Nodes[atStart ? 0 : candidate.Nodes.Count - 1].Anchor;
                var world = obj.Transform.Apply(anchor, obj.LocalPivot);
                var screenPoint = new Point(ToCanvasX(world.X), ToCanvasY(world.Y));
                var distance = Distance(draggedScreen, screenPoint);
                if (distance <= CloseHoverThresholdPx && distance < bestScreenDistance)
                {
                    bestScreenDistance = distance;
                    best = (other, atStart);
                }
            }
        }
        if (best is not { } target) return false;

        // The dragged end lands exactly on the target end and the two merge there: the shape the
        // operator dragged is kept, and the target contour does not move at all.
        var targetSubpath = _nodeEditWorkingPath.Subpaths[target.Index];
        var targetAnchor = targetSubpath.Nodes[target.AtStart ? 0 : targetSubpath.Nodes.Count - 1].Anchor;
        var landedPath = _nodeEditWorkingPath.ReplaceSubpath(subpathIndex, PlaceNodeOn(draggedWorkingSubpath, nodeIndex, targetAnchor));
        closedPath = VectorPathEditor.JoinSubpathsAtEndpoints(
            landedPath, target.Index, target.AtStart, subpathIndex, nodeIndex == 0, EndpointJoinMode.Merge);
        _selectedNodeKeys.Clear(); // subpath indices shift when two contours merge
        return true;
    }

    private static VectorSubpath PlaceNodeOn(VectorSubpath subpath, int nodeIndex, Position anchor)
    {
        var nodes = subpath.Nodes.ToList();
        var node = nodes[nodeIndex];
        nodes[nodeIndex] = node.Translated(anchor.X - node.Anchor.X, anchor.Y - node.Anchor.Y);
        return subpath with { Nodes = nodes };
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

        var replacement = ViewModel.Objects.FirstOrDefault(o => o.Id == _nodeEditObject.Id &&
            o.IsVectorPath && ViewModel.CanSelectObject(o));
        if (replacement is null)
        {
            ExitNodeEditMode();
            return;
        }

        _nodeEditObject = replacement;
        _nodeEditWorkingPath = replacement.VectorPath;
        _selectedNodeKeys.Clear();
        _selectedSegment = null;
        _grabbedNodeKey = null;
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
        RevalidateSegmentSelection();
        DropStaleNodeSelection();
        if (_nodeEditObject is null) ExitNodeEditMode();
        else RedrawSelectionOverlay();
    }

    /// <summary>Node keys are positional; any selected key that no longer points at a node of the
    /// committed path is dropped rather than left to fault the overlay or a later command.</summary>
    private void DropStaleNodeSelection()
    {
        if (_nodeEditWorkingPath is null) { _selectedNodeKeys.Clear(); return; }
        var path = _nodeEditWorkingPath;
        _selectedNodeKeys.RemoveWhere(key => key.Subpath >= path.Subpaths.Count || key.Node >= path.Subpaths[key.Subpath].Nodes.Count);
    }

    /// <summary>Public — the node-edit context toolbar (NodeEditToolbar.xaml) calls this directly,
    /// the same way CanvasViewControls calls SceneCanvas.ZoomIn/SetZoomPercent: this is view state
    /// with no ViewModel equivalent, not a command that belongs on SceneViewModel.</summary>
    public void DeleteSelectedNodes() => DeleteSelectedNodes(refit: true);

    /// <summary>Delete (refit on) / Ctrl+Delete (refit off, Inkscape): removes the selected nodes.
    /// With refit, the node is kept if one cubic cannot approximate the old curve within
    /// VectorPathEditor.RefitToleranceMm. Ctrl+Delete explicitly skips refitting.</summary>
    public void DeleteSelectedNodes(bool refit)
    {
        if (_nodeEditObject is null || _nodeEditWorkingPath is null || ViewModel is null || _selectedNodeKeys.Count == 0) return;

        var worstDeviation = 0.0;
        var subpaths = _nodeEditWorkingPath.Subpaths.ToList();
        // Remove higher subpath indexes first so deleting one contour cannot shift the indexes of
        // other selected contours. A path must never retain a 0/1-node open contour or a 0/1/2-node
        // closed contour: those cannot produce a matching LocalShape or reliable laser output.
        foreach (var group in _selectedNodeKeys.GroupBy(key => key.Subpath).OrderByDescending(group => group.Key))
        {
            var subpath = subpaths[group.Key];
            var selectedIndexes = group.Select(key => key.Node).Distinct().ToList();
            var minimum = subpath.IsClosed ? 3 : 2;
            // Every node selected: the operator asked for the contour to go. Anything less must not
            // silently take the whole contour with it, so removal stops at the minimum node count.
            if (selectedIndexes.Count >= subpath.Nodes.Count)
            {
                subpaths.RemoveAt(group.Key);
                continue;
            }
            // Highest node index first so removing one does not shift the indices still queued.
            foreach (var nodeIndex in selectedIndexes.OrderByDescending(i => i))
            {
                if (subpath.Nodes.Count <= minimum) break;
                subpath = VectorPathEditor.RemoveNode(subpath, nodeIndex, refit, out var deviation);
                worstDeviation = Math.Max(worstDeviation, deviation);
            }
            subpaths[group.Key] = subpath;
        }

        _selectedNodeKeys.Clear();
        if (subpaths.Count == 0)
        {
            // The last contour is gone. Use the scene's regular Delete command so object removal,
            // selection cleanup and undo all follow the same path as whole-object deletion.
            var item = _nodeEditObject;
            ExitNodeEditMode();
            ViewModel.SelectedObjects.ReplaceWith([item]);
            ViewModel.DeleteCommand.Execute(null);
            return;
        }
        var remaining = _nodeEditWorkingPath with { Subpaths = subpaths };
        if (VectorPathsStructurallyEqual(remaining, _nodeEditWorkingPath))
        {
            if (refit && worstDeviation > VectorPathEditor.RefitToleranceMm)
                ShowCanvasNotice($"Uzel zůstal zachován: refit by odchýlil cestu o {worstDeviation.ToString("0.##", System.Globalization.CultureInfo.GetCultureInfo("cs-CZ"))} mm. Ctrl+Delete odstraní uzel bez refitu.");
            else
                ViewModel.ReportVectorOperationRejected("Cesta musí zůstat alespoň se dvěma (otevřená) nebo třemi (uzavřená) uzly.");
            RedrawSelectionOverlay();
            return;
        }
        CommitNodeEdit(remaining);
    }

    /// <summary>Arrow-key nudge for the current node selection (LIGHTBURN_VECTOR_PARITY.md §9) — moves
    /// only the selected nodes by (dx, dy) in the object's LOCAL space and commits one
    /// ReplaceObjectsCommand per key press, the same one-gesture-one-undo-step contract every other
    /// committed node edit already uses. Each press is its own undo step (matching the existing
    /// whole-object Nudge()'s convention below; coalescing a held key into one transaction is
    /// explicitly optional per spec and not implemented here).</summary>
    private void NudgeSelectedNodes(Key arrowKey, double stepMm)
    {
        if (_nodeEditWorkingPath is null || _nodeEditObject is null || _selectedNodeKeys.Count == 0) return;
        var worldDx = arrowKey switch { Key.Left => -stepMm, Key.Right => stepMm, _ => 0 };
        var worldDy = arrowKey switch { Key.Up => stepMm, Key.Down => -stepMm, _ => 0 };
        if (worldDx == 0 && worldDy == 0) return;

        // The arrow keys move along the document axes, but the nodes live in the object's local
        // frame: a rotated, flipped or scaled object would otherwise step in the wrong direction and
        // by the wrong distance. Map the world step through the inverse of the object's transform.
        var obj = _nodeEditObject;
        var localOrigin = obj.Transform.Inverse(new Position(0, 0, 0), obj.LocalPivot);
        var localStep = obj.Transform.Inverse(new Position(worldDx, worldDy, 0), obj.LocalPivot);
        var dx = localStep.X - localOrigin.X;
        var dy = localStep.Y - localOrigin.Y;

        var moved = TranslateSelectedNodes(_nodeEditWorkingPath, dx, dy);
        CommitNodeEdit(moved);
    }

    /// <summary>Sets every selected node to the same Corner/Smooth type in one gesture (toolbar, menu,
    /// Shift+S). Smooth is neighbour-aware — see VectorPathEditor.SetNodeType: a node without handles
    /// gets real tangent handles, a node beside a straight segment keeps that line straight.</summary>
    public void ConvertSelectedNodes(VectorNodeType type) =>
        ApplyToSelectedNodes((subpath, index) => VectorPathEditor.SetNodeType(subpath, index, type));

    /// <summary>Shift+Y: both handles collinear and equally long.</summary>
    public void MakeSelectedNodesSymmetric() => ApplyToSelectedNodes(VectorPathEditor.MakeNodeSymmetric);

    /// <summary>Shift+A: handles regenerated from the neighbours (Inkscape auto-smooth).</summary>
    public void AutoSmoothSelectedNodes() => ApplyToSelectedNodes(VectorPathEditor.AutoSmoothNode);

    /// <summary>One node-type command over the whole selection as ONE undo step; nothing is committed
    /// when the command changes nothing (no empty history entries).</summary>
    private void ApplyToSelectedNodes(Func<VectorSubpath, int, VectorSubpath> edit)
    {
        if (_nodeEditWorkingPath is null || _selectedNodeKeys.Count == 0) return;

        var subpaths = _nodeEditWorkingPath.Subpaths.ToList();
        foreach (var group in _selectedNodeKeys.GroupBy(key => key.Subpath))
        {
            if (group.Key >= subpaths.Count) continue;
            var subpath = subpaths[group.Key];
            foreach (var key in group.OrderBy(key => key.Node))
                if (key.Node < subpath.Nodes.Count) subpath = edit(subpath, key.Node);
            subpaths[group.Key] = subpath;
        }

        var updated = _nodeEditWorkingPath with { Subpaths = subpaths };
        if (VectorPathsStructurallyEqual(updated, _nodeEditWorkingPath)) return;
        CommitNodeEdit(updated);
    }

    /// <summary>Closes or reopens whichever subpath the current node selection belongs to (or, with
    /// nothing selected, the path's only subpath — the common case for a hand-drawn shape). Disabled
    /// from the toolbar (see IsSelectedSubpathClosed) whenever that is ambiguous: a selection spanning
    /// more than one subpath, or no selection on a multi-subpath path. Closing a contour with fewer
    /// than three distinct nodes is refused with the reason (CanToggleSelectedSubpathClosed already
    /// disables the button for it).</summary>
    public void ToggleSelectedSubpathClosed()
    {
        if (_nodeEditWorkingPath is null || ResolveContextSubpathIndex() is not { } index) return;
        var subpath = _nodeEditWorkingPath.Subpaths[index];
        if (!subpath.IsClosed && !VectorPathEditor.CanClose(subpath))
        {
            ShowCanvasNotice(CloseNeedsThreeNodesMessage);
            return;
        }
        var toggled = subpath.IsClosed ? VectorPathEditor.Open(subpath) : VectorPathEditor.Close(subpath);
        if (toggled.Nodes.Count != subpath.Nodes.Count) _selectedNodeKeys.Clear(); // a merged duplicate end shifts indices
        CommitNodeEdit(_nodeEditWorkingPath.ReplaceSubpath(index, toggled));
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
            // Locked, hidden, layer-locked objects and derived offset outlines are not join targets.
            if (!ViewModel.CanSelectObject(candidate) || candidate.OffsetLink is not null) continue;
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
    public void JoinSelectedEndpointToCandidate() => JoinSelectedEndpointToCandidate(EndpointJoinMode.Auto);

    private void JoinSelectedEndpointToCandidate(EndpointJoinMode mode)
    {
        if (ViewModel is null || _nodeEditObject is null || _nodeEditWorkingPath is null) return;
        if (_selectedNodeKeys.Count != 1) return;
        var (_, nodeIndex) = _selectedNodeKeys.Single();
        var draggedAtStart = nodeIndex == 0;

        var candidate = FindCrossObjectJoinCandidate();
        if (candidate is not { } join) return;

        var draggedObject = _nodeEditObject;
        if (ViewModel.JoinObjectEndpoints(draggedObject, draggedAtStart, join.Target, join.TargetAtStart, mode))
        {
            var merged = ViewModel.SelectedObjects.Count == 1 ? ViewModel.SelectedObjects[0] : null;
            if (merged is { IsVectorPath: true }) EnterNodeEditMode(merged);
            else ExitNodeEditMode();
        }
    }

    /// <summary>The two selected open ends of the edited path, when exactly two are selected and both
    /// are ends of open contours (of the same contour or of two different ones).</summary>
    private ((int Subpath, int Node) First, (int Subpath, int Node) Second)? SelectedEndpointPair()
    {
        if (_nodeEditWorkingPath is null || _selectedNodeKeys.Count != 2) return null;
        var keys = _selectedNodeKeys.OrderBy(key => key.Subpath).ThenBy(key => key.Node).ToList();
        foreach (var (s, n) in keys)
        {
            if (s >= _nodeEditWorkingPath.Subpaths.Count) return null;
            var subpath = _nodeEditWorkingPath.Subpaths[s];
            if (subpath.IsClosed || subpath.Nodes.Count < 2 || (n != 0 && n != subpath.Nodes.Count - 1)) return null;
        }
        if (keys[0].Subpath == keys[1].Subpath && keys[0].Node == keys[1].Node) return null;
        return (keys[0], keys[1]);
    }

    /// <summary>Whether Join (Shift+J) / Join with segment can act now: two selected open ends of the
    /// edited path (two contours, or the two ends of one contour that may close), or one selected end
    /// with another path's open end in reach.</summary>
    private bool EvaluateCanJoinSelectedNodes()
    {
        if (_nodeEditWorkingPath is null) return false;
        if (SelectedEndpointPair() is { } pair)
            return pair.First.Subpath != pair.Second.Subpath
                || VectorPathEditor.CanClose(_nodeEditWorkingPath.Subpaths[pair.First.Subpath]);
        return _selectedNodeKeys.Count == 1 && FindCrossObjectJoinCandidate() is not null;
    }

    /// <summary>Join nodes (Shift+J, mode Auto: ends that touch merge, ends apart get a straight
    /// segment) and Join with segment (always a segment): two selected open ends of this path become
    /// one contour (or close their contour), one selected end joins the nearest open end of another
    /// path. One undo step; never drops an anchor or moves geometry beyond the merge tolerance.</summary>
    public void JoinSelectedNodes(EndpointJoinMode mode)
    {
        if (ViewModel is null || _nodeEditWorkingPath is null) return;
        if (SelectedEndpointPair() is { } pair)
        {
            var path = _nodeEditWorkingPath;
            VectorPath joined;
            if (pair.First.Subpath == pair.Second.Subpath)
            {
                var subpath = path.Subpaths[pair.First.Subpath];
                var closed = VectorPathEditor.JoinOwnEnds(subpath, mode);
                if (!closed.IsClosed)
                {
                    ShowCanvasNotice(CloseNeedsThreeNodesMessage);
                    return;
                }
                joined = path.ReplaceSubpath(pair.First.Subpath, closed);
            }
            else
            {
                joined = VectorPathEditor.JoinSubpathsAtEndpoints(path,
                    pair.First.Subpath, pair.First.Node == 0, pair.Second.Subpath, pair.Second.Node == 0, mode);
            }
            _selectedNodeKeys.Clear();
            CommitNodeEdit(joined);
            return;
        }

        if (_selectedNodeKeys.Count == 1) JoinSelectedEndpointToCandidate(mode);
    }

    /// <summary>Delete-key path when no node is selected (LIGHTBURN_VECTOR_PARITY.md §21/§64): the
    /// selected segment, else whatever segment the pointer currently sits over, is deleted, opening a
    /// closed subpath or splitting/shrinking an open one (see VectorPathEditor.DeleteSegment). Does
    /// nothing if neither exists.</summary>
    private void DeleteHoveredSegment(Point screen)
    {
        if (_nodeEditObject is null || _nodeEditWorkingPath is null) return;
        if (_selectedSegment is { } selected && IsValidSegment(_nodeEditWorkingPath, selected))
        {
            DeleteSegment(selected);
            return;
        }
        var hit = HitTestNearestSegment(_nodeEditObject, _nodeEditWorkingPath, screen, SegmentInsertTolerancePx);
        if (hit is not { } segmentHit) return;
        DeleteSegment(segmentHit);
    }

    private void DeleteSegment(VectorPathHitTester.Hit hit)
    {
        if (_nodeEditWorkingPath is null || !IsValidSegment(_nodeEditWorkingPath, hit)) return;
        _selectedNodeKeys.Clear();
        _selectedSegment = null;
        _hoveredSegment = null;
        CommitNodeEdit(VectorPathEditor.DeleteSegment(_nodeEditWorkingPath, hit.SubpathIndex, hit.SegmentIndex));
    }

    // ------------------------------------------------------------------
    // Trim / Extend (LIGHTBURN_VECTOR_PARITY.md §31/§32). The Trim tool (scissors) and node edit
    // (Shift+T / Shift+E, menus) share one model: VectorPathTrim cuts the clicked segment exactly at
    // its nearest crossings with other visible geometry (this path's other segments included) and
    // removes only the clicked piece; Extend runs an open end along its tangent to the nearest
    // crossing. Every click is one undo step.
    // ------------------------------------------------------------------

    /// <summary>Lengthens any extension search; no laser bed is near this size.</summary>
    private const double ExtendReachMm = 10000;

    private readonly record struct TrimTarget(SceneObject Object, VectorPath Path, VectorPathHitTester.Hit Hit);

    /// <summary>The nearest trimmable segment under the pointer: an editable path, or a plain outline
    /// as it WOULD convert (the conversion only happens when a click trims).</summary>
    private TrimTarget? FindTrimTarget(Point screen)
    {
        if (ViewModel is null) return null;
        TrimTarget? best = null;
        var pointerWorld = new Position(ToWorldX(screen.X), ToWorldY(screen.Y), 0);
        var reach = SegmentInsertTolerancePx / _scale;
        foreach (var item in ViewModel.Objects)
        {
            if (item.IsLocked || item.IsRaster || !ViewModel.CanSelectObject(item)) continue;
            // Cheap bounds rejection first: building a plain outline's candidate path is not free.
            var bounds = item.WorldBounds();
            if (pointerWorld.X < bounds.MinX - reach || pointerWorld.X > bounds.MaxX + reach
                || pointerWorld.Y < bounds.MinY - reach || pointerWorld.Y > bounds.MaxY + reach) continue;
            var path = item.VectorPath ?? BuildTrimCandidatePath(item);
            if (path is null) continue;
            if (HitTestNearestSegment(item, path, screen, SegmentInsertTolerancePx) is not { } hit) continue;
            if (best is null || hit.Distance < best.Value.Hit.Distance) best = new TrimTarget(item, path, hit);
        }
        return best;
    }

    /// <summary>Every visible segment in the scene except the one being trimmed, in world space:
    /// that path's own other segments, other editable paths as Bézier segments, plain outlines as
    /// their polylines. Locked objects still cut (they are only read); hidden ones do not.</summary>
    private List<Lasero.Core.Scene.BezierSegment> CollectTrimCutters(SceneObject target, VectorPath targetPath, (int Subpath, int Segment)? exclude)
    {
        var cutters = new List<Lasero.Core.Scene.BezierSegment>();
        if (ViewModel is null) return cutters;
        cutters.AddRange(VectorPathTrim.WorldSegments(targetPath, local => target.Transform.Apply(local, target.LocalPivot), exclude));

        var visibleLayers = ViewModel.Layers.Where(layer => layer.IsVisible).ToList();
        bool ShapeVisible(ImportedShape shape) =>
            visibleLayers.Any(layer => layer.Id == shape.LayerId) ||
            (shape.LayerId == Guid.Empty || ViewModel.Layers.All(layer => layer.Id != shape.LayerId))
                && visibleLayers.Any(layer => layer.Color.IsApproximately(shape.LayerColor));

        foreach (var item in ViewModel.Objects)
        {
            if (ReferenceEquals(item, target) || item.Id == target.Id || !item.IsVisible || item.IsRaster) continue;
            if (item.VectorPath is not null && item.LocalShapes.All(ShapeVisible) && item.GetWorldVectorPath() is { } world)
            {
                cutters.AddRange(VectorPathTrim.WorldSegments(world, point => point));
                continue;
            }
            foreach (var shape in item.GetWorldShapes())
                if (ShapeVisible(shape)) cutters.AddRange(VectorPathTrim.PolylineSegments(shape.Points, shape.IsClosed));
        }
        return cutters;
    }

    private (double From, double To, IReadOnlyList<double> Cuts) TrimPiece(SceneObject obj, VectorPath path, VectorPathHitTester.Hit hit)
    {
        var (a, b) = path.Subpaths[hit.SubpathIndex].Segment(hit.SegmentIndex);
        var world = Lasero.Core.Scene.BezierSegment.FromNodes(a, b).Map(local => obj.Transform.Apply(local, obj.LocalPivot));
        var cuts = VectorPathTrim.CutParameters(world, CollectTrimCutters(obj, path, (hit.SubpathIndex, hit.SegmentIndex)));
        var (from, to) = VectorPathTrim.PieceAround(hit.T, cuts);
        return (from, to, cuts);
    }

    /// <summary>Trim tool click: removes the piece of the clicked segment between its nearest
    /// crossings (or its own nodes). A plain outline is converted to an editable path and trimmed
    /// in ONE undo step. Shift+click extends the nearest open end instead.</summary>
    private void TrimPathAt(Point screen)
    {
        if (ViewModel is null) return;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            ExtendPathAt(screen);
            return;
        }
        if (FindTrimTarget(screen) is not { } target) return;

        var source = target.Object;
        ViewModel.SelectedObjects.Clear();
        ViewModel.SelectedObjects.Add(source);
        ClearTrimHover();

        try
        {
            if (source.VectorPath is { } editablePath)
            {
                var (_, _, cuts) = TrimPiece(source, editablePath, target.Hit);
                var trimmed = VectorPathTrim.TrimSegment(editablePath, target.Hit.SubpathIndex, target.Hit.SegmentIndex, target.Hit.T, cuts);
                if (trimmed.Subpaths.Count == 0)
                {
                    if (ViewModel.DeleteCommand.CanExecute(null)) ViewModel.DeleteCommand.Execute(null);
                    return;
                }
                ViewModel.CommitVectorPathEdit(source, trimmed);
                return;
            }

            // A plain outline: conversion to curves and the cut are ONE undo step.
            ViewModel.ConvertAndEditVectorPath(source, (converted, convertedPath) =>
            {
                if (HitTestNearestSegment(converted, convertedPath, screen, SegmentInsertTolerancePx) is not { } h) return null;
                var (_, _, cuts) = TrimPiece(converted, convertedPath, h);
                return VectorPathTrim.TrimSegment(convertedPath, h.SubpathIndex, h.SegmentIndex, h.T, cuts);
            }, out var deleteRequested);
            if (deleteRequested && ViewModel.DeleteCommand.CanExecute(null)) ViewModel.DeleteCommand.Execute(null);
        }
        catch (ArgumentException exception)
        {
            ViewModel.ReportVectorOperationRejected($"Cestu se nepodařilo oříznout. {exception.Message}");
        }
    }

    /// <summary>Trim tool Shift+click: the open end nearest the pointer (within the close-hover
    /// tolerance) of a visible, unlocked path is extended to the nearest crossing along its tangent.</summary>
    private void ExtendPathAt(Point screen)
    {
        if (ViewModel is null) return;
        (SceneObject Object, VectorPath Path, int Subpath, bool AtStart)? best = null;
        var bestDistance = double.MaxValue;
        foreach (var item in ViewModel.Objects)
        {
            if (item.IsLocked || item.IsRaster || !ViewModel.CanSelectObject(item)) continue;
            var path = item.VectorPath ?? BuildTrimCandidatePath(item);
            if (path is null) continue;
            for (var s = 0; s < path.Subpaths.Count; s++)
            {
                var subpath = path.Subpaths[s];
                if (subpath.IsClosed || subpath.Nodes.Count < 2) continue;
                foreach (var atStart in new[] { true, false })
                {
                    var world = item.Transform.Apply(subpath.Nodes[atStart ? 0 : subpath.Nodes.Count - 1].Anchor, item.LocalPivot);
                    var distance = Distance(new Point(ToCanvasX(world.X), ToCanvasY(world.Y)), screen);
                    if (distance <= CloseHoverThresholdPx && distance < bestDistance)
                    {
                        bestDistance = distance;
                        best = (item, path, s, atStart);
                    }
                }
            }
        }
        if (best is not { } end)
        {
            ShowCanvasNotice(ExtendNeedsOpenEndMessage);
            return;
        }

        var source = end.Object;
        VectorPath? Extend(SceneObject obj, VectorPath path) =>
            VectorPathTrim.TryExtend(path, end.Subpath, end.AtStart,
                local => obj.Transform.Apply(local, obj.LocalPivot),
                world => obj.Transform.Inverse(world, obj.LocalPivot),
                CollectTrimCutters(obj, path, null), ExtendReachMm, out var extended) ? extended : null;

        if (source.VectorPath is { } editable)
        {
            if (Extend(source, editable) is { } extended) ViewModel.CommitVectorPathEdit(source, extended);
            else ShowCanvasNotice(ExtendNothingAheadMessage);
            return;
        }
        if (ViewModel.ConvertAndEditVectorPath(source, Extend, out _) is null) ShowCanvasNotice(ExtendNothingAheadMessage);
    }

    private const string ExtendNeedsOpenEndMessage = "Prodloužit lze jen otevřený konec dráhy. Shift+klik je potřeba umístit na koncový uzel.";
    private const string ExtendNothingAheadMessage = "Ve směru konce dráhy nic neleží, není k čemu ji prodloužit.";

    /// <summary>Node edit Shift+T / menu "Oříznout": trims the active segment (selected, else
    /// hovered) of the edited path at the clicked/hovered point.</summary>
    public void TrimActiveSegment()
    {
        if (_nodeEditObject is null || _nodeEditWorkingPath is null || ActiveSegment is not { } hit) return;
        var (_, _, cuts) = TrimPiece(_nodeEditObject, _nodeEditWorkingPath, hit);
        var trimmed = VectorPathTrim.TrimSegment(_nodeEditWorkingPath, hit.SubpathIndex, hit.SegmentIndex, hit.T, cuts);
        _selectedNodeKeys.Clear();
        _selectedSegment = null;
        _hoveredSegment = null;
        if (trimmed.Subpaths.Count == 0)
        {
            var item = _nodeEditObject;
            ExitNodeEditMode();
            ViewModel?.SelectedObjects.ReplaceWith([item]);
            if (ViewModel?.DeleteCommand.CanExecute(null) == true) ViewModel.DeleteCommand.Execute(null);
            return;
        }
        CommitNodeEdit(trimmed);
    }

    /// <summary>Node edit Shift+E / menu "Prodloužit": extends the single selected open end.</summary>
    public void ExtendSelectedEndpoint()
    {
        if (_nodeEditObject is null || _nodeEditWorkingPath is null || _selectedNodeKeys.Count != 1) return;
        var (s, n) = _selectedNodeKeys.Single();
        var subpath = _nodeEditWorkingPath.Subpaths[s];
        if (subpath.IsClosed || (n != 0 && n != subpath.Nodes.Count - 1))
        {
            ShowCanvasNotice(ExtendNeedsOpenEndMessage);
            return;
        }
        var obj = _nodeEditObject;
        if (!VectorPathTrim.TryExtend(_nodeEditWorkingPath, s, n == 0,
                local => obj.Transform.Apply(local, obj.LocalPivot),
                world => obj.Transform.Inverse(world, obj.LocalPivot),
                CollectTrimCutters(obj, _nodeEditWorkingPath, null), ExtendReachMm, out var extended))
        {
            ShowCanvasNotice(ExtendNothingAheadMessage);
            return;
        }
        if (extended.Subpaths[s].Nodes.Count != subpath.Nodes.Count) _selectedNodeKeys.Clear();
        CommitNodeEdit(extended);
    }

    // --- Trim tool hover preview (§31 "highlight affected segment ... preview before destructive
    // operation"): the piece a click would remove is drawn in the danger colour. Cut parameters are
    // cached per hovered segment, so a pointer move along one segment costs one hit test.
    private readonly List<FrameworkElement> _trimPreviewVisuals = [];
    // Keyed by the scene's command revision too: any edit or undo can move a cutter.
    private (SceneObject Object, int Subpath, int Segment, int Revision, IReadOnlyList<double> Cuts)? _trimHoverCache;

    private void UpdateTrimHover(Point screen)
    {
        ClearTrimHoverVisuals();
        if (ViewModel?.ActiveTool != DesignerTool.TrimPath || _dragMode != DragMode.None) return;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) || FindTrimTarget(screen) is not { } target) return;

        var hit = target.Hit;
        IReadOnlyList<double> cuts;
        var revision = ViewModel.CommandRevision;
        if (_trimHoverCache is { } cache && ReferenceEquals(cache.Object, target.Object) && cache.Revision == revision
            && cache.Subpath == hit.SubpathIndex && cache.Segment == hit.SegmentIndex)
        {
            cuts = cache.Cuts;
        }
        else
        {
            cuts = TrimPiece(target.Object, target.Path, hit).Cuts;
            _trimHoverCache = (target.Object, hit.SubpathIndex, hit.SegmentIndex, revision, cuts);
        }

        var (from, to) = VectorPathTrim.PieceAround(hit.T, cuts);
        var (a, b) = target.Path.Subpaths[hit.SubpathIndex].Segment(hit.SegmentIndex);
        var segment = Lasero.Core.Scene.BezierSegment.FromNodes(a, b);
        var poly = new Polyline
        {
            Stroke = (Brush)FindResource("Brush.Danger"),
            StrokeThickness = 3,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            IsHitTestVisible = false,
        };
        const int steps = 48;
        for (var i = 0; i <= steps; i++)
        {
            var world = target.Object.Transform.Apply(segment.Evaluate(from + (to - from) * i / steps), target.Object.LocalPivot);
            poly.Points.Add(new Point(ToCanvasX(world.X), ToCanvasY(world.Y)));
        }
        DrawCanvas.Children.Add(poly);
        _trimPreviewVisuals.Add(poly);
    }

    private void ClearTrimHoverVisuals()
    {
        foreach (var visual in _trimPreviewVisuals) DrawCanvas.Children.Remove(visual);
        _trimPreviewVisuals.Clear();
    }

    private void ClearTrimHover()
    {
        ClearTrimHoverVisuals();
        _trimHoverCache = null;
    }

    private static VectorPath? BuildTrimCandidatePath(SceneObject item)
    {
        // Same conversion the commit uses (ellipses as Bezier nodes, short contours skipped with
        // their shapes), so the hit-tested path and the committed path cannot disagree.
        if (item.IsText) return null;
        return SceneViewModel.BuildCurveConversion(item)?.VectorPath;
    }

    // ------------------------------------------------------------------
    // Priority 1 — segment-scoped commands the model layer (VectorPathEditor) already implements but
    // this file previously exposed only implicitly (line→curve via reshaping a segment by hand;
    // curve→line and insert-at-exact-midpoint had NO UI path at all — see
    // docs/reference/NODE_EDIT_PARITY_AUDIT_2026-09-16.md, "Curve↔line is not user-reachable"). All
    // act on the ACTIVE segment: the one the operator clicked (selected — it survives the pointer
    // travelling to the toolbar), else the one currently hovered. Public so NodeEditToolbar's buttons
    // and OnCanvasKeyDown's Shift+L/Shift+C/Shift+M/Shift+T shortcuts can call them directly, the same
    // way DeleteSelectedNodes/BreakSelectedNode already are.
    // ------------------------------------------------------------------

    public void ConvertActiveSegmentToLine()
    {
        if (_nodeEditWorkingPath is null || ActiveSegment is not { } hit) return;
        var subpath = _nodeEditWorkingPath.Subpaths[hit.SubpathIndex];
        var updated = VectorPathEditor.ConvertSegmentToLine(subpath, hit.SegmentIndex);
        CommitNodeEdit(_nodeEditWorkingPath.ReplaceSubpath(hit.SubpathIndex, updated));
    }

    public void ConvertActiveSegmentToCurve()
    {
        if (_nodeEditWorkingPath is null || ActiveSegment is not { } hit) return;
        var subpath = _nodeEditWorkingPath.Subpaths[hit.SubpathIndex];
        var updated = VectorPathEditor.ConvertSegmentToCurve(subpath, hit.SegmentIndex);
        CommitNodeEdit(_nodeEditWorkingPath.ReplaceSubpath(hit.SubpathIndex, updated));
    }

    /// <summary>Inserts a node at the EXACT curve-parameter midpoint (t = 0.5) of the active segment —
    /// unlike the double-click gesture (HandleNodeEditCanvasMouseDown), which inserts at the nearest
    /// approximate point under the pointer, this is deliberately the precise midpoint every time,
    /// matching LightBurn/Inkscape's own "Insert midpoint" command semantics.</summary>
    public void InsertNodeAtActiveSegmentMidpoint()
    {
        if (_nodeEditWorkingPath is null || ActiveSegment is not { } hit) return;
        var subpath = _nodeEditWorkingPath.Subpaths[hit.SubpathIndex];
        var updated = VectorPathEditor.InsertNode(subpath, hit.SegmentIndex, 0.5);
        // Node identity here is purely positional (Subpath, Node) index pairs — inserting a node
        // shifts every later index in this subpath, so an outstanding selection elsewhere in it would
        // silently point at the wrong node after the redraw if left alone. Clearing it matches every
        // other topology-changing operation (Delete/Break/Reverse) already does for the same reason.
        _selectedNodeKeys.Clear();
        CommitNodeEdit(_nodeEditWorkingPath.ReplaceSubpath(hit.SubpathIndex, updated));
    }

    /// <summary>Toolbar/shortcut delete of the active segment — no pointer coordinate needed, since a
    /// toolbar click or keyboard shortcut has none of its own over the canvas.</summary>
    public void DeleteActiveSegment()
    {
        if (ActiveSegment is { } hit) DeleteSegment(hit);
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

    private const string ConvertToCurvesForNodesMessage =
        "Pro úpravu uzlů je potřeba tvar nejdřív převést na křivky (Ctrl+Shift+C).";

    private const string NoJoinTargetMessage =
        "Spojit lze dva vybrané otevřené konce, nebo jeden konec, ke kterému je blízko otevřený konec jiné dráhy.";

    /// <summary>Node-edit Shift+letter commands (see OnCanvasKeyDown). True when the key was one of
    /// them and had something to act on; false lets the key travel on unchanged.</summary>
    private bool HandleNodeEditShiftKey(Key key)
    {
        var hasNodes = _selectedNodeKeys.Count > 0;
        var hasSegment = ActiveSegment is not null;
        switch (key)
        {
            case Key.S when hasNodes: ConvertSelectedNodes(VectorNodeType.Smooth); return true;
            case Key.Y when hasNodes: MakeSelectedNodesSymmetric(); return true;
            case Key.A when hasNodes: AutoSmoothSelectedNodes(); return true;
            case Key.B when _selectedNodeKeys.Count == 1: BreakSelectedNode(); return true;
            case Key.J when hasNodes:
                if (CanJoinSelectedNodes) JoinSelectedNodes(EndpointJoinMode.Auto);
                else ShowCanvasNotice(NoJoinTargetMessage);
                return true;
            case Key.E when _selectedNodeKeys.Count == 1: ExtendSelectedEndpoint(); return true;
            case Key.T when hasSegment: TrimActiveSegment(); return true;
            case Key.L when hasSegment: ConvertActiveSegmentToLine(); return true;
            case Key.C when hasSegment: ConvertActiveSegmentToCurve(); return true;
            case Key.M when hasSegment: InsertNodeAtActiveSegmentMidpoint(); return true;
            default: return false;
        }
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

    /// <summary>Null when there is no active segment (none selected by a click, none hovered) —
    /// NodeEditToolbar's Convert-to-Line/Convert-to-Curve/Insert-Midpoint/Trim/Delete-Segment buttons
    /// all target that segment (see the "Priority 1" block above), so they show and hide together via
    /// this one property. True when that segment already has no handles on either end (straight); the
    /// VectorPathEditor.ConvertSegmentToCurve's own "already curved, no-op" note.</summary>
    private static readonly DependencyPropertyKey IsActiveSegmentStraightPropertyKey = DependencyProperty.RegisterReadOnly(
        nameof(IsActiveSegmentStraight), typeof(bool?), typeof(SceneCanvas), new PropertyMetadata(null));
    public static readonly DependencyProperty IsActiveSegmentStraightProperty = IsActiveSegmentStraightPropertyKey.DependencyProperty;
    public bool? IsActiveSegmentStraight => (bool?)GetValue(IsActiveSegmentStraightProperty);

    /// <summary>False when the open/close button would be refused: the resolved contour is open but
    /// has fewer than three distinct nodes (VectorPathEditor.CanClose).</summary>
    private static readonly DependencyPropertyKey CanToggleSelectedSubpathClosedPropertyKey = DependencyProperty.RegisterReadOnly(
        nameof(CanToggleSelectedSubpathClosed), typeof(bool), typeof(SceneCanvas), new PropertyMetadata(false));
    public static readonly DependencyProperty CanToggleSelectedSubpathClosedProperty = CanToggleSelectedSubpathClosedPropertyKey.DependencyProperty;
    public bool CanToggleSelectedSubpathClosed => (bool)GetValue(CanToggleSelectedSubpathClosedProperty);

    /// <summary>The selection is about joining: one open end, or two open ends of this path. Shows the
    /// Join buttons; <see cref="CanJoinSelectedNodes"/> says whether they can act right now.</summary>
    private static readonly DependencyPropertyKey IsJoinContextPropertyKey = DependencyProperty.RegisterReadOnly(
        nameof(IsJoinContext), typeof(bool), typeof(SceneCanvas), new PropertyMetadata(false));
    public static readonly DependencyProperty IsJoinContextProperty = IsJoinContextPropertyKey.DependencyProperty;
    public bool IsJoinContext => (bool)GetValue(IsJoinContextProperty);

    /// <summary>A short operator-facing sentence about the last canvas action (a refused close, a
    /// delete that changed the curve more than the refit tolerance, an extend with nothing ahead).
    /// Shown by CanvasHintOverlay in place of the regular hint until dismissed or the next press.</summary>
    private static readonly DependencyPropertyKey CanvasNoticePropertyKey = DependencyProperty.RegisterReadOnly(
        nameof(CanvasNotice), typeof(string), typeof(SceneCanvas), new PropertyMetadata(null));
    public static readonly DependencyProperty CanvasNoticeProperty = CanvasNoticePropertyKey.DependencyProperty;
    public string? CanvasNotice => (string?)GetValue(CanvasNoticeProperty);

    private void ShowCanvasNotice(string message)
    {
        Serilog.Log.Information("Canvas notice: {Notice}", message);
        SetValue(CanvasNoticePropertyKey, message);
    }

    public void ClearCanvasNotice()
    {
        if (CanvasNotice is not null) SetValue(CanvasNoticePropertyKey, null);
    }

    /// <summary>True when the current (single-node) selection is an open subpath's endpoint — drives
    /// the toolbar/context-menu's Join button group and the "open endpoint" context-menu variant.</summary>
    private static readonly DependencyPropertyKey IsSelectedNodeOpenEndpointPropertyKey = DependencyProperty.RegisterReadOnly(
        nameof(IsSelectedNodeOpenEndpoint), typeof(bool), typeof(SceneCanvas), new PropertyMetadata(false));
    public static readonly DependencyProperty IsSelectedNodeOpenEndpointProperty = IsSelectedNodeOpenEndpointPropertyKey.DependencyProperty;
    public bool IsSelectedNodeOpenEndpoint => (bool)GetValue(IsSelectedNodeOpenEndpointProperty);

    /// <summary>True when Join / Join with segment can act now — two selected open ends of this path,
    /// or one selected end with another path's open end within join tolerance (see
    /// CanJoinSelectedNodes()). The Join buttons/menu rows are visible whenever IsJoinContext but only
    /// ENABLED when this is also true, so their tooltip can explain why instead of the action
    /// seeming not to exist.</summary>
    private static readonly DependencyPropertyKey CanJoinSelectedNodesPropertyKey = DependencyProperty.RegisterReadOnly(
        nameof(CanJoinSelectedNodes), typeof(bool), typeof(SceneCanvas), new PropertyMetadata(false));
    public static readonly DependencyProperty CanJoinSelectedNodesProperty = CanJoinSelectedNodesPropertyKey.DependencyProperty;
    public bool CanJoinSelectedNodes => (bool)GetValue(CanJoinSelectedNodesProperty);

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
        var canToggle = false;
        if (_nodeEditWorkingPath is not null && ResolveContextSubpathIndex() is { } index && index < _nodeEditWorkingPath.Subpaths.Count)
        {
            var resolved = _nodeEditWorkingPath.Subpaths[index];
            closed = resolved.IsClosed;
            canToggle = resolved.IsClosed || VectorPathEditor.CanClose(resolved);
        }
        SetValue(IsSelectedSubpathClosedPropertyKey, closed);
        SetValue(CanToggleSelectedSubpathClosedPropertyKey, canToggle);

        bool? straight = null;
        if (_nodeEditWorkingPath is not null && ActiveSegment is { } hit)
        {
            var (a, b) = _nodeEditWorkingPath.Subpaths[hit.SubpathIndex].Segment(hit.SegmentIndex);
            straight = VectorSubpath.IsStraightSegment(a, b);
        }
        SetValue(IsActiveSegmentStraightPropertyKey, straight);

        var isOpenEndpoint = false;
        if (_nodeEditWorkingPath is not null && _selectedNodeKeys.Count == 1)
        {
            var (s, n) = _selectedNodeKeys.Single();
            if (s < _nodeEditWorkingPath.Subpaths.Count)
            {
                var subpath = _nodeEditWorkingPath.Subpaths[s];
                isOpenEndpoint = !subpath.IsClosed && subpath.Nodes.Count > 0 && (n == 0 || n == subpath.Nodes.Count - 1);
            }
        }
        SetValue(IsSelectedNodeOpenEndpointPropertyKey, isOpenEndpoint);
        SetValue(IsJoinContextPropertyKey, isOpenEndpoint || SelectedEndpointPair() is not null);
        SetValue(CanJoinSelectedNodesPropertyKey, EvaluateCanJoinSelectedNodes());
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
