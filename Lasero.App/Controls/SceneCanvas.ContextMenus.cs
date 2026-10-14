using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Lasero.App.Components;
using Lasero.App.ViewModels;
using Lasero.Core.GCode;
using Lasero.Core.Scene;

namespace Lasero.App.Controls;

/// <summary>
/// Right-click / Menu key / Shift+F10 on the design canvas. One entry point decides WHAT was clicked
/// (node, segment, object, selection, empty canvas, or empty canvas while editing nodes), the pure
/// <see cref="CanvasContextMenuBuilder"/> decides which rows that target deserves, and this file only
/// turns rows into WPF and rows back into the commands that already exist. The menu is built in code
/// because a ContextMenu is its own visual tree and does not inherit this control's DataContext.
/// </summary>
public partial class SceneCanvas
{
    /// <summary>Opens the file picker that adds SVG, image or G-code to the design. Owned by the shell
    /// (GCodeViewModel.LoadFileCommand); the canvas only offers it on an empty-canvas right-click.</summary>
    public static readonly DependencyProperty ImportCommandProperty = DependencyProperty.Register(
        nameof(ImportCommand), typeof(ICommand), typeof(SceneCanvas), new PropertyMetadata(null));

    public ICommand? ImportCommand
    {
        get => (ICommand?)GetValue(ImportCommandProperty);
        set => SetValue(ImportCommandProperty, value);
    }

    /// <summary>A menu never opens mid-gesture, while a path is being drawn, or over the inline text
    /// editor (which keeps its own clipboard menu).</summary>
    private bool CanOpenContextMenu =>
        ViewModel is not null && _dragMode == DragMode.None && !_pathToolDrawing && _inlineTextEditor is null;

    // ------------------------------------------------------------------
    // Entry points
    // ------------------------------------------------------------------

    /// <summary>Right-click on an object's own outline. Routed through the same resolution as a click on
    /// empty canvas so node-edit mode still sees a click on its own outline as a segment click.</summary>
    private void OnObjectMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!CanOpenContextMenu || sender is not Path { Tag: SceneObject obj }) return;
        e.Handled = true;
        Focus();
        ShowContextMenuAt(e.GetPosition(DrawCanvas), obj);
    }

    /// <summary>Right-click that no object outline, resize grip or node claimed: a filled object's
    /// interior, a segment while editing nodes, or nothing at all.</summary>
    private void OnCanvasBackgroundRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!CanOpenContextMenu) return;
        e.Handled = true;
        Focus();
        ShowContextMenuAt(e.GetPosition(DrawCanvas), null);
    }

    /// <summary>The Menu key and Shift+F10 raise ContextMenuOpening with no cursor position. The mouse
    /// path is fully handled on button-down, so a mouse-initiated event is only swallowed here so that
    /// nothing else opens a second menu on button-up.</summary>
    private void OnCanvasContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (e.OriginalSource is System.Windows.Controls.Primitives.TextBoxBase) return;
        e.Handled = true;
        // (-1, -1) is the keyboard sentinel; a mouse event on a monitor left of the primary one can
        // legitimately report a negative X, so both coordinates have to be checked.
        if (e.CursorLeft != -1 || e.CursorTop != -1 || !CanOpenContextMenu) return;
        ShowKeyboardContextMenu();
    }

    // ------------------------------------------------------------------
    // Target resolution
    // ------------------------------------------------------------------

    private void ShowContextMenuAt(Point screen, SceneObject? directHit)
    {
        if (ViewModel is not { } scene) return;
        var lockedHit = directHit is not null && scene.IsUnlockableObject(directHit) ? directHit : null;
        if (directHit is not null && !scene.CanSelectObject(directHit)) directHit = null;

        if (_nodeEditObject is not null && _nodeEditWorkingPath is not null)
        {
            if (HitTestNearestSegment(_nodeEditObject, _nodeEditWorkingPath, screen, SegmentInsertTolerancePx) is { } segment)
                OpenSegmentMenu(segment, screen);
            else
                OpenNodeEditCanvasMenu(screen);
            return;
        }

        var target = directHit ?? HitTestScene(screen).Select(candidate => candidate.Object).FirstOrDefault();
        if (target is not null)
        {
            // Right-click inside the current selection keeps it (so the menu acts on all of it); a
            // click on anything else selects it first, like every editor does, and a member of a group
            // selects its whole group, exactly as a left click would.
            if (!scene.SelectedObjects.Contains(target))
                scene.SelectedObjects.ReplaceWith(scene.SelectionUnitFor(target));
            OpenObjectMenu(screen);
            return;
        }

        // Empty space inside the selection's bounding box still means "the selection" (a thin
        // outline is hard to hit exactly); anywhere else is the canvas itself and keeps the selection.
        if (scene.SelectedObjects.Count > 0 && SelectionScreenBounds() is { } bounds && bounds.Contains(screen))
        {
            OpenObjectMenu(screen);
            return;
        }

        // A locked object cannot be selected, but it must never be out of reach: offer to unlock it.
        lockedHit ??= HitTestLockedObject(screen);
        if (lockedHit is not null)
        {
            OpenLockedObjectMenu(screen, lockedHit);
            return;
        }

        OpenEmptyCanvasMenu(screen);
    }

    /// <summary>The topmost locked object under the pointer that unlocking would make selectable again.</summary>
    private SceneObject? HitTestLockedObject(Point screen)
    {
        if (ViewModel is not { } scene) return null;
        var pointer = new Lasero.Core.Grbl.Position(ToWorldX(screen.X), ToWorldY(screen.Y), 0);
        return SceneHitTester.HitTest(
                scene.Objects,
                pointer,
                _scale,
                (obj, shape) => obj.IsRaster ||
                    scene.LayerModeFor(shape.LayerId, shape.LayerColor) is Lasero.Core.Layers.LayerMode.Fill or Lasero.Core.Layers.LayerMode.FillAndCut,
                (obj, _) => scene.IsUnlockableObject(obj),
                _preferences.ClickTolerancePx)
            .Select(candidate => candidate.Object)
            .FirstOrDefault();
    }

    private void OpenLockedObjectMenu(Point at, SceneObject locked)
    {
        if (ViewModel is not { } scene) return;
        var state = CanvasContextMenuStates.ForEmptyCanvas(
            scene, ImportCommand?.CanExecute(null) == true, WorkAreaWidthMm > 0 && WorkAreaHeightMm > 0);
        OpenMenu(CanvasContextMenuBuilder.ForLockedObject(state), at, row =>
        {
            if (row.Action == ContextAction.UnlockObject) scene.UnlockObject(locked);
            else RunObjectAction(row);
        });
    }

    private void ShowKeyboardContextMenu()
    {
        if (ViewModel is not { } scene) return;
        var anchor = KeyboardAnchor();

        if (_nodeEditObject is not null && _nodeEditWorkingPath is not null)
        {
            if (_selectedNodeKeys.Count > 0)
            {
                var (subpath, node) = _selectedNodeKeys.OrderBy(key => key.Subpath).ThenBy(key => key.Node).First();
                if (subpath < _nodeEditWorkingPath.Subpaths.Count && node < _nodeEditWorkingPath.Subpaths[subpath].Nodes.Count)
                {
                    var world = _nodeEditObject.Transform.Apply(_nodeEditWorkingPath.Subpaths[subpath].Nodes[node].Anchor, _nodeEditObject.LocalPivot);
                    OpenNodeMenu(ClampToCanvas(new Point(ToCanvasX(world.X), ToCanvasY(world.Y))));
                    return;
                }
            }
            OpenNodeEditCanvasMenu(anchor);
            return;
        }

        if (scene.SelectedObjects.Count > 0)
        {
            var at = SelectionScreenBounds() is { } bounds
                ? ClampToCanvas(new Point(bounds.Left + bounds.Width / 2, bounds.Top + bounds.Height / 2))
                : anchor;
            OpenObjectMenu(at);
            return;
        }

        OpenEmptyCanvasMenu(anchor);
    }

    /// <summary>Where a keyboard-invoked menu opens when nothing on the canvas dictates it: under the
    /// pointer if it is over the canvas, otherwise the middle of the view.</summary>
    private Point KeyboardAnchor()
    {
        var mouse = Mouse.GetPosition(DrawCanvas);
        return mouse.X >= 0 && mouse.Y >= 0 && mouse.X <= DrawCanvas.ActualWidth && mouse.Y <= DrawCanvas.ActualHeight
            ? mouse
            : new Point(DrawCanvas.ActualWidth / 2, DrawCanvas.ActualHeight / 2);
    }

    private Point ClampToCanvas(Point point) => new(
        Math.Max(0, Math.Min(point.X, DrawCanvas.ActualWidth)),
        Math.Max(0, Math.Min(point.Y, DrawCanvas.ActualHeight)));

    private Rect? SelectionScreenBounds()
    {
        if (ViewModel is null || ViewModel.SelectedObjects.Count == 0) return null;
        var box = BoundingBox2D.Empty;
        foreach (var obj in ViewModel.SelectedObjects) box = Union(box, obj.WorldBounds());
        var left = ToCanvasX(box.MinX);
        var right = ToCanvasX(box.MaxX);
        var top = ToCanvasY(box.MaxY);
        var bottom = ToCanvasY(box.MinY);
        return new Rect(new Point(Math.Min(left, right), Math.Min(top, bottom)), new Point(Math.Max(left, right), Math.Max(top, bottom)));
    }

    // ------------------------------------------------------------------
    // Menus
    // ------------------------------------------------------------------

    private void OpenObjectMenu(Point at)
    {
        if (ViewModel is not { } scene) return;
        var rows = CanvasContextMenuBuilder.ForObject(CanvasContextMenuStates.ForSelection(scene, CurrentReach()));
        OpenMenu(rows, at, item => RunObjectAction(item));
    }

    /// <summary>Raised by "Zobrazit vlastnosti". The selection bar (which owns the property fields)
    /// listens through its TargetCanvas and moves keyboard focus to the first field that applies.</summary>
    public event Action? PropertiesRequested;

    /// <summary>The job preview ("Náhled") is a window of its own that draws the toolpath and never talks to the
    /// machine; the canvas reaches it through the shell view model it inherits as its data context.</summary>
    private ICommand? PreviewCommand => (DataContext as MainViewModel)?.GCode.OpenPreviewCommand;

    private CanvasContextMenuStates.Reach CurrentReach() => new()
    {
        HasPreview = PreviewCommand is not null,
        PreviewReason = PreviewCommand?.CanExecute(null) == false
            ? "Náhled je k dispozici, jakmile je na plátně něco, co se vyrobí."
            : null,
        HasProperties = PropertiesRequested is not null,
    };

    private void OpenEmptyCanvasMenu(Point at)
    {
        if (ViewModel is not { } scene) return;
        var state = CanvasContextMenuStates.ForEmptyCanvas(
            scene, ImportCommand?.CanExecute(null) == true, WorkAreaWidthMm > 0 && WorkAreaHeightMm > 0);
        OpenMenu(CanvasContextMenuBuilder.ForEmptyCanvas(state), at, item => RunObjectAction(item));
    }

    private void OpenNodeEditCanvasMenu(Point at)
    {
        if (_nodeEditWorkingPath is null) return;
        var state = new NodeEditCanvasContextState
        {
            NodeCount = _nodeEditWorkingPath.Subpaths.Sum(subpath => subpath.Nodes.Count),
            SelectedNodeCount = _selectedNodeKeys.Count,
        };
        OpenMenu(CanvasContextMenuBuilder.ForNodeEditCanvas(state), at, item => RunObjectAction(item));
    }

    /// <summary>Right-click on a node dot: it becomes the selection unless it is already part of it, so
    /// a right-click inside a multi-node selection acts on all of those nodes.</summary>
    private void ShowNodeContextMenuFor(int subpathIndex, int nodeIndex, Point at)
    {
        var key = (subpathIndex, nodeIndex);
        if (!_selectedNodeKeys.Contains(key))
        {
            _selectedNodeKeys.Clear();
            _selectedNodeKeys.Add(key);
            RedrawSelectionOverlay();
        }
        OpenNodeMenu(at);
    }

    private void OpenNodeMenu(Point at)
    {
        if (_nodeEditWorkingPath is null || _selectedNodeKeys.Count == 0) return;
        var path = _nodeEditWorkingPath;
        var nodes = _selectedNodeKeys
            .Where(key => key.Subpath < path.Subpaths.Count && key.Node < path.Subpaths[key.Subpath].Nodes.Count)
            .Select(key => (key.Subpath, key.Node, Data: path.Subpaths[key.Subpath].Nodes[key.Node]))
            .ToList();
        if (nodes.Count == 0) return;

        var types = nodes.Select(node => node.Data.Type).Distinct().ToList();
        var canBreak = false;
        if (nodes.Count == 1)
        {
            var subpath = path.Subpaths[nodes[0].Subpath];
            canBreak = subpath.IsClosed || (nodes[0].Node != 0 && nodes[0].Node != subpath.Nodes.Count - 1);
        }

        var isOpenEndpoint = false;
        if (nodes.Count == 1)
        {
            var only = path.Subpaths[nodes[0].Subpath];
            isOpenEndpoint = !only.IsClosed && (nodes[0].Node == 0 || nodes[0].Node == only.Nodes.Count - 1);
        }

        var canClose = false;
        if (ResolveContextSubpathIndex() is { } resolved)
        {
            // The same rule as the toolbar, the pen and the endpoint drag: three distinct nodes.
            canClose = VectorPathEditor.CanClose(path.Subpaths[resolved]);
        }

        var state = new NodeContextState
        {
            SelectedNodeCount = nodes.Count,
            AllSmooth = types.Count == 1 ? types[0] == VectorNodeType.Smooth : null,
            CanBreak = canBreak,
            CanClosePath = canClose,
            IsOpenEndpoint = isOpenEndpoint,
            IsEndpointPair = SelectedEndpointPair() is not null,
            CanJoin = EvaluateCanJoinSelectedNodes(),
        };
        OpenMenu(CanvasContextMenuBuilder.ForNode(state), at, item => RunObjectAction(item));
    }

    private void OpenSegmentMenu(VectorPathHitTester.Hit hit, Point at)
    {
        if (_nodeEditWorkingPath is null) return;
        var (a, b) = _nodeEditWorkingPath.Subpaths[hit.SubpathIndex].Segment(hit.SegmentIndex);
        _hoveredSegment = hit;
        RedrawSelectionOverlay();
        var state = new SegmentContextState { IsStraight = VectorSubpath.IsStraightSegment(a, b) };
        OpenMenu(CanvasContextMenuBuilder.ForSegment(state), at, item => RunSegmentAction(item, hit));
    }

    // ------------------------------------------------------------------
    // Rendering rows
    // ------------------------------------------------------------------

    private void OpenMenu(IReadOnlyList<ContextMenuItemModel> rows, Point at, Action<ContextMenuItemModel> run)
    {
        if (rows.Count == 0) return;
        var menu = BuildContextMenu(rows, run);
        menu.PlacementTarget = DrawCanvas;
        menu.Placement = PlacementMode.Relative;
        menu.HorizontalOffset = at.X;
        menu.VerticalOffset = at.Y;
        menu.IsOpen = true;
    }

    /// <summary>Rows to WPF, without opening anything. Internal so a test can render the result.</summary>
    internal ContextMenu BuildContextMenu(IReadOnlyList<ContextMenuItemModel> rows, Action<ContextMenuItemModel> run)
    {
        var menu = new ContextMenu();
        foreach (var row in rows) menu.Items.Add(CreateMenuElement(row, run));
        return menu;
    }

    private object CreateMenuElement(ContextMenuItemModel row, Action<ContextMenuItemModel> run)
    {
        if (row.IsSeparator) return new Separator();

        var item = new MenuItem
        {
            Style = (Style)Resources["CanvasMenuItem"],
            Header = row.Header,
            InputGestureText = row.Gesture ?? string.Empty,
            IsChecked = row.IsChecked,
            IsEnabled = row.IsEnabled,
        };

        if (row.Icon is not null && TryFindResource(row.Icon) is Geometry geometry)
        {
            item.Icon = new IconGlyph
            {
                IconData = geometry,
                Width = 16,
                Height = 16,
                Foreground = row.Action == ContextAction.Delete || row.Action == ContextAction.NodeDelete || row.Action == ContextAction.SegmentDelete
                    ? (Brush)FindResource("Brush.Danger")
                    : (Brush)FindResource("Brush.TextSecondary"),
            };
        }

        if (row.Reason is not null)
        {
            item.ToolTip = row.Reason;
            ToolTipService.SetShowOnDisabled(item, true);
        }

        if (row.Kind == ContextMenuItemKind.Submenu)
        {
            foreach (var child in row.Children) item.Items.Add(CreateMenuElement(child, run));
        }
        else
        {
            item.Click += (_, _) => run(row);
        }
        return item;
    }

    // ------------------------------------------------------------------
    // Rows back to existing behaviour
    // ------------------------------------------------------------------

    /// <summary>"Převést na dráhu" (Ctrl+Shift+C), the same action as the menu row: text becomes ordinary
    /// contours, a primitive or an imported outline becomes editable curves and opens node editing.
    /// False when the selection has nothing to convert, so the key can fall through.</summary>
    public bool ConvertSelectionToPath()
    {
        if (ViewModel is not { } scene) return false;
        if (scene.CanConvertSelectedTextToPath)
        {
            Run(scene.ConvertSelectedTextToPathCommand);
            return true;
        }
        if (scene.CanConvertSelectionToCurves && scene.ConvertSelectedObjectToCurves() is { } converted)
        {
            EnterNodeEditMode(converted);
            return true;
        }
        return false;
    }

    private static void Run(ICommand command, object? parameter = null)
    {
        if (command.CanExecute(parameter)) command.Execute(parameter);
    }

    private void RunObjectAction(ContextMenuItemModel row)
    {
        if (ViewModel is not { } scene) return;
        switch (row.Action)
        {
            case ContextAction.Cut: Run(scene.CutCommand); break;
            case ContextAction.Copy: Run(scene.CopyCommand); break;
            case ContextAction.Paste: Run(scene.PasteCommand); break;
            case ContextAction.Duplicate: Run(scene.DuplicateCommand); break;
            case ContextAction.Delete: Run(scene.DeleteCommand); break;
            case ContextAction.SelectAll: Run(scene.SelectAllCommand); break;
            case ContextAction.DeselectAll: scene.SelectedObjects.Clear(); break;

            case ContextAction.EditNodes:
                if (scene.Selected is { IsVectorPath: true, IsLocked: false } path) EnterNodeEditMode(path);
                break;
            case ContextAction.ConvertToCurves:
                if (scene.ConvertSelectedObjectToCurves() is { } converted) EnterNodeEditMode(converted);
                break;
            case ContextAction.EditText:
                if (scene.Selected is { IsText: true, IsLocked: false } text) BeginInlineTextEdit(text);
                break;
            case ContextAction.ConvertTextToPath: Run(scene.ConvertSelectedTextToPathCommand); break;
            case ContextAction.ConvertToBitmap: Run(scene.ConvertSelectionToBitmapCommand); break;
            case ContextAction.Preview: if (PreviewCommand is { } preview) Run(preview); break;
            case ContextAction.ShowProperties: PropertiesRequested?.Invoke(); break;
            case ContextAction.Group: Run(scene.GroupSelectionCommand); break;
            case ContextAction.Ungroup: Run(scene.UngroupSelectionCommand); break;
            case ContextAction.BreakApart: Run(scene.BreakApartSelectionCommand); break;
            case ContextAction.ToggleLock: Run(scene.ToggleLockCommand); break;
            case ContextAction.UnlockAll: Run(scene.UnlockAllCommand); break;
            case ContextAction.Combine: Run(scene.CombinePathsCommand); break;
            case ContextAction.SplitPath: Run(scene.SplitPathCommand); break;

            case ContextAction.Unite: Run(scene.UniteSelectionCommand); break;
            case ContextAction.Subtract: Run(scene.SubtractSelectionCommand); break;
            case ContextAction.Intersect: Run(scene.IntersectSelectionCommand); break;
            case ContextAction.Exclude: Run(scene.ExcludeSelectionCommand); break;
            case ContextAction.Offset: Run(scene.OffsetSelectionCommand); break;

            case ContextAction.EditBitmap: Run(scene.EditSelectedRasterCommand); break;
            case ContextAction.TraceBitmap: Run(scene.TraceSelectedRasterCommand); break;
            case ContextAction.RemoveBackground: Run(scene.RemoveSelectedBackgroundCommand); break;
            case ContextAction.RestoreBackground: Run(scene.RestoreSelectedBackgroundCommand); break;

            case ContextAction.BringToFront: Run(scene.BringToFrontCommand); break;
            case ContextAction.BringForward: Run(scene.BringForwardCommand); break;
            case ContextAction.SendBackward: Run(scene.SendBackwardCommand); break;
            case ContextAction.SendToBack: Run(scene.SendToBackCommand); break;
            case ContextAction.AlignLeft: Run(scene.AlignLeftCommand); break;
            case ContextAction.AlignCenterHorizontal: Run(scene.AlignCenterHorizontalCommand); break;
            case ContextAction.AlignCenterBoth: Run(scene.AlignCenterBothCommand); break;
            case ContextAction.AlignCenterWorkArea: Run(scene.CenterOnWorkAreaCommand); break;
            case ContextAction.AlignRight: Run(scene.AlignRightCommand); break;
            case ContextAction.AlignTop: Run(scene.AlignTopCommand); break;
            case ContextAction.AlignMiddle: Run(scene.AlignMiddleCommand); break;
            case ContextAction.AlignBottom: Run(scene.AlignBottomCommand); break;
            case ContextAction.DistributeCentersHorizontally: Run(scene.DistributeCentersHorizontallyCommand); break;
            case ContextAction.DistributeCentersVertically: Run(scene.DistributeCentersVerticallyCommand); break;
            case ContextAction.DistributeSpacingHorizontally: Run(scene.DistributeSpacingHorizontallyCommand); break;
            case ContextAction.DistributeSpacingVertically: Run(scene.DistributeSpacingVerticallyCommand); break;
            case ContextAction.RotateLeft: Run(scene.RotateLeftCommand); break;
            case ContextAction.RotateRight: Run(scene.RotateRightCommand); break;
            case ContextAction.FlipHorizontal: Run(scene.FlipHorizontalCommand); break;
            case ContextAction.FlipVertical: Run(scene.FlipVerticalCommand); break;
            case ContextAction.AssignToLayer:
                if (row.Argument >= 0 && row.Argument < scene.Layers.Count)
                    Run(scene.AssignSelectionToLayerCommand, scene.Layers[row.Argument]);
                break;

            case ContextAction.Import: if (ImportCommand is { } import) Run(import); break;
            case ContextAction.FitView: FitToView(); break;
            case ContextAction.ZoomActual: SetZoomPercent(100); break;
            case ContextAction.CenterBed: CenterWorkArea(); break;

            case ContextAction.NodeSmooth: ConvertSelectedNodes(VectorNodeType.Smooth); break;
            case ContextAction.NodeCorner: ConvertSelectedNodes(VectorNodeType.Corner); break;
            case ContextAction.NodeSymmetric: MakeSelectedNodesSymmetric(); break;
            case ContextAction.NodeAutoSmooth: AutoSmoothSelectedNodes(); break;
            case ContextAction.NodeBreak: BreakSelectedNode(); break;
            case ContextAction.NodeJoin: JoinSelectedNodes(EndpointJoinMode.Auto); break;
            case ContextAction.NodeJoinSegment: JoinSelectedNodes(EndpointJoinMode.Segment); break;
            case ContextAction.NodeExtend: ExtendSelectedEndpoint(); break;
            case ContextAction.NodeClosePath: ToggleSelectedSubpathClosed(); break;
            case ContextAction.NodeDelete: DeleteSelectedNodes(); break;
            case ContextAction.NodeDeleteNoRefit: DeleteSelectedNodes(refit: false); break;
            case ContextAction.SelectAllNodes: SelectAllNodes(); break;
            case ContextAction.DeselectNodes: ClearNodeSelection(); break;
            case ContextAction.ExitNodeEdit: ExitNodeEditMode(); break;
        }
    }

    private void RunSegmentAction(ContextMenuItemModel row, VectorPathHitTester.Hit hit)
    {
        if (_nodeEditWorkingPath is null || hit.SubpathIndex >= _nodeEditWorkingPath.Subpaths.Count) return;
        var path = _nodeEditWorkingPath;
        var subpath = path.Subpaths[hit.SubpathIndex];
        if (hit.SegmentIndex >= subpath.SegmentCount) return;

        switch (row.Action)
        {
            case ContextAction.SegmentToCurve:
                CommitSegmentEdit(path.ReplaceSubpath(hit.SubpathIndex, VectorPathEditor.ConvertSegmentToCurve(subpath, hit.SegmentIndex)));
                break;
            case ContextAction.SegmentToLine:
                CommitSegmentEdit(path.ReplaceSubpath(hit.SubpathIndex, VectorPathEditor.ConvertSegmentToLine(subpath, hit.SegmentIndex)));
                break;
            case ContextAction.SegmentInsertHere:
                // Hit-test parameters are clamped to (0.025, 0.975) for curves but can reach 0 or 1 on
                // a zero-length line; never hand InsertNode an end parameter.
                CommitSegmentEdit(path.ReplaceSubpath(hit.SubpathIndex, VectorPathEditor.InsertNode(subpath, hit.SegmentIndex, Math.Clamp(hit.T, 0.02, 0.98))));
                break;
            case ContextAction.SegmentInsertMidpoint:
                CommitSegmentEdit(path.ReplaceSubpath(hit.SubpathIndex, VectorPathEditor.InsertNode(subpath, hit.SegmentIndex, 0.5)));
                break;
            case ContextAction.SegmentDelete:
                CommitSegmentEdit(VectorPathEditor.DeleteSegment(path, hit.SubpathIndex, hit.SegmentIndex));
                break;
            case ContextAction.SegmentTrim:
                _selectedSegment = hit;
                _selectedSegmentNodeCount = subpath.Nodes.Count;
                TrimActiveSegment();
                break;
        }
    }

    /// <summary>Every segment action is one committed node edit, so it is one undo step like the
    /// toolbar and keyboard equivalents.</summary>
    private void CommitSegmentEdit(VectorPath updated)
    {
        _selectedNodeKeys.Clear();
        _hoveredSegment = null;
        _selectedSegment = null;
        CommitNodeEdit(updated);
    }
}
