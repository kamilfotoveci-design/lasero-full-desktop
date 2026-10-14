using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Lasero.Core.GCode;
using Lasero.Core.Grbl;
using Lasero.Core.Scene;
using Lasero.Core.Scene.Commands;

namespace Lasero.App.Controls;

/// <summary>
/// Resize and rotate handles for a multi-object selection (LightBurn-style group transform). The maths
/// lives in <see cref="GroupTransform"/>; this file owns the handles, the pointer gesture and its
/// live preview.
///
/// - Handles sit on the world bounding box of the unlocked, visible selected objects, with the same
///   invisible enlarged hit targets, cursors and look as the single-object grips. The padding is screen
///   space interaction geometry only; nothing here reaches a transform or the toolpath.
/// - Dragging assigns each object a transform derived from its immutable start transform (never
///   cumulatively), so the preview is never committed state. The live assignment goes through
///   SceneViewModel.ApplyGesturePreview: one inspector notification per frame, and this class repaints
///   the objects and the overlay once.
/// - Mouse-up commits ONE CompositeSceneCommand of TransformObjectCommand per changed object.
///   Escape restores the start transforms and records nothing.
/// </summary>
public partial class SceneCanvas
{
    private sealed record GroupHandleTag(ResizeHandle Handle);

    private sealed class GroupRotateTag
    {
        public static readonly GroupRotateTag Instance = new();
    }

    private sealed class GroupGesture
    {
        public required IReadOnlyList<(SceneObject Object, ObjectTransform Before)> Items { get; init; }
        public required BoundingBox2D StartBox { get; init; }
        public required ResizeHandle Handle { get; init; }
        public required bool AllowFlip { get; init; }
        public double StartAngleDeg { get; init; }

        /// <summary>Box the overlay frames while the gesture runs: the nominal scaled box while resizing,
        /// the start box turned by <see cref="OverlayRotationDeg"/> while rotating. Using it instead of the
        /// objects' own axis-aligned bounds keeps the frame steady under the pointer.</summary>
        public BoundingBox2D OverlayBox { get; set; }
        public double OverlayRotationDeg { get; set; }
    }

    private GroupGesture? _groupGesture;

    /// <summary>The selected objects a group transform acts on: visible and unlocked. Locked objects are
    /// left exactly where they are, as for move.</summary>
    private List<SceneObject> GroupTransformableObjects()
    {
        var result = new List<SceneObject>();
        if (ViewModel is null) return result;
        foreach (var obj in ViewModel.SelectedObjects)
            if (!obj.IsLocked && IsObjectVisibleOnCanvas(obj))
                result.Add(obj);
        return result;
    }

    /// <summary>When source and its derived offset are selected together, move the source once.
    /// The offset is regenerated from it after the gesture; transforming both would double-move it.</summary>
    private static IReadOnlyList<SceneObject> IndependentTransformTargets(IEnumerable<SceneObject> selection) =>
        Lasero.App.ViewModels.SceneViewModel.IndependentTransformTargets(selection);

    // ------------------------------------------------------------------
    // Overlay
    // ------------------------------------------------------------------

    /// <summary>Frame, 8 resize grips and (for vector-only selections) a rotate grip for 2+ objects.
    /// Falls back to the plain dashed box when every selected object is locked or hidden.</summary>
    private void DrawGroupSelection()
    {
        var movable = GroupTransformableObjects();
        if (movable.Count == 0)
        {
            DrawMultiSelectionBox();
            return;
        }

        var box = _groupGesture?.OverlayBox ?? GroupTransform.BoundsOf(movable);
        if (box.IsEmpty)
        {
            DrawMultiSelectionBox();
            return;
        }

        var rotation = _groupGesture?.OverlayRotationDeg ?? 0;
        var centre = GroupTransform.CentreOf(box);
        Point ScreenOf(ResizeHandle handle) => GroupHandleScreen(box, centre, rotation, handle);

        var outline = new Polygon
        {
            Points = new PointCollection(new[]
            {
                ScreenOf(ResizeHandle.TopLeft), ScreenOf(ResizeHandle.TopRight),
                ScreenOf(ResizeHandle.BottomRight), ScreenOf(ResizeHandle.BottomLeft),
            }),
            Stroke = SelectionBrush,
            StrokeThickness = 1.2,
            Fill = Brushes.Transparent,
            IsHitTestVisible = false,
        };
        ApplyMarchingAnts(outline);
        DrawCanvas.Children.Add(outline);
        _selectionVisuals.Add(outline);

        foreach (var handle in AllResizeHandles)
        {
            var screen = ScreenOf(handle);

            // Invisible target you can actually hit, plus the small visible square (see
            // DrawSingleObjectHandles for why they are two elements).
            var target = new Rectangle
            {
                Width = HandleHitSizePx,
                Height = HandleHitSizePx,
                Fill = Brushes.Transparent,
                Tag = new GroupHandleTag(handle),
                Cursor = CursorForHandle(handle),
            };
            target.MouseLeftButtonDown += OnGroupResizeHandleMouseLeftButtonDown;
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

        var centreScreen = new Point(ToCanvasX(centre.X), ToCanvasY(centre.Y));
        var centreMarker = new Rectangle
        {
            Width = 6,
            Height = 6,
            Fill = SelectionHandleFill,
            Stroke = SelectionBrush,
            StrokeThickness = 1.2,
            IsHitTestVisible = false,
        };
        Canvas.SetLeft(centreMarker, centreScreen.X - 3);
        Canvas.SetTop(centreMarker, centreScreen.Y - 3);
        DrawCanvas.Children.Add(centreMarker);
        _selectionVisuals.Add(centreMarker);

        // A raster cannot rotate (its planner is axis-aligned), so a selection containing one offers no
        // rotate grip, the same rule as the single-object handles.
        if (movable.Any(obj => obj.IsRaster)) return;

        // ResizeHandle.Bottom is the maximum-Y edge, which the canvas draws at the top (see
        // DrawSingleObjectHandles).
        var topScreen = ScreenOf(ResizeHandle.Bottom);
        var dirX = topScreen.X - centreScreen.X;
        var dirY = topScreen.Y - centreScreen.Y;
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
            Tag = GroupRotateTag.Instance,
            Cursor = Lasero.App.Input.RotateCursor.Get(),
        };
        rotateHandle.MouseLeftButtonDown += OnGroupRotateHandleMouseLeftButtonDown;
        Canvas.SetLeft(rotateHandle, rotateScreen.X - (HandleSizePx + 2) / 2);
        Canvas.SetTop(rotateHandle, rotateScreen.Y - (HandleSizePx + 2) / 2);
        DrawCanvas.Children.Add(rotateHandle);
        _selectionVisuals.Add(rotateHandle);
    }

    /// <summary>Canvas position of a group-box handle, the box turned by <paramref name="rotationDeg"/> about its centre.</summary>
    private Point GroupHandleScreen(BoundingBox2D box, Position centre, double rotationDeg, ResizeHandle handle)
    {
        var local = ObjectTransform.HandleLocalPoint(box, centre, handle);
        var (rx, ry) = GroupTransform.RotateVector(local.X - centre.X, local.Y - centre.Y, rotationDeg);
        return new Point(ToCanvasX(centre.X + rx), ToCanvasY(centre.Y + ry));
    }

    // ------------------------------------------------------------------
    // Gesture
    // ------------------------------------------------------------------

    private bool BeginGroupGesture(ResizeHandle handle, bool rotate, Point screen)
    {
        if (ViewModel is null) return false;
        var movable = GroupTransformableObjects();
        if (movable.Count == 0) return false;
        if (rotate && movable.Any(obj => obj.IsRaster)) return false;

        var box = GroupTransform.BoundsOf(movable);
        if (box.IsEmpty) return false;
        BeginGestureSnapping(movable);

        var centre = GroupTransform.CentreOf(box);
        var world = new Position(ToWorldX(screen.X), ToWorldY(screen.Y), 0);
        _groupGesture = new GroupGesture
        {
            Items = IndependentTransformTargets(movable).Select(obj => (obj, obj.Transform)).ToList(),
            StartBox = box,
            Handle = handle,
            // Rasters cannot flip, as in the single-object resize.
            AllowFlip = !movable.Any(obj => obj.IsRaster),
            StartAngleDeg = Math.Atan2(world.Y - centre.Y, world.X - centre.X) * 180.0 / Math.PI,
            OverlayBox = box,
        };

        _dragMode = rotate ? DragMode.Rotate : DragMode.Resize;
        _activeSingleObject = null;
        DrawCanvas.CaptureMouse();
        return true;
    }

    private void OnGroupResizeHandleMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (ViewModel is null || ((Rectangle)sender).Tag is not GroupHandleTag tag) return;
        e.Handled = true;
        Focus();
        BeginGroupGesture(tag.Handle, rotate: false, e.GetPosition(DrawCanvas));
    }

    private void OnGroupRotateHandleMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (ViewModel is null) return;
        e.Handled = true;
        Focus();
        BeginGroupGesture(ResizeHandle.Bottom, rotate: true, e.GetPosition(DrawCanvas));
    }

    /// <summary>Coalesced pointer update while a group resize is dragged.</summary>
    private void UpdateGroupResize(Point screen)
    {
        if (_groupGesture is not { } gesture) return;
        // Same rule as the single resize: the persistent inspector toggle, or Shift for this drag.
        var lockAspect = ViewModel?.LockAspectRatio == true || Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        // The group box is world-axis aligned: snap the edge(s) being dragged, not the pointer.
        var centre = GroupTransform.CentreOf(gesture.StartBox);
        var dragged = ObjectTransform.HandleLocalPoint(gesture.StartBox, centre, gesture.Handle);
        var anchor = new Position(2 * centre.X - dragged.X, 2 * centre.Y - dragged.Y, centre.Z);
        var world = ApplyResizeEdgeSnap(
            new Position(ToWorldX(screen.X), ToWorldY(screen.Y), 0), gesture.Handle, axesSwapped: false,
            lockAspect, anchor,
            mouse =>
            {
                var s = GroupTransform.ComputeScale(gesture.StartBox, gesture.Handle, mouse, lockAspect, gesture.AllowFlip);
                return new Position(
                    anchor.X + (dragged.X - anchor.X) * s.Gx,
                    anchor.Y + (dragged.Y - anchor.Y) * s.Gy, 0);
            });
        var solution = GroupTransform.ComputeScale(
            gesture.StartBox,
            gesture.Handle,
            world,
            lockAspectRatio: lockAspect,
            allowFlip: gesture.AllowFlip);

        gesture.OverlayBox = GroupTransform.ScaleBox(gesture.StartBox, solution.Anchor, solution.Gx, solution.Gy);
        gesture.OverlayRotationDeg = 0;
        ApplyGroupPreview(gesture, (obj, before) =>
            GroupTransform.ScaleAbout(before, obj.LocalPivot, solution.Anchor, solution.Gx, solution.Gy));
    }

    /// <summary>Coalesced pointer update while the group rotate grip is dragged.</summary>
    private void UpdateGroupRotate(Point screen)
    {
        if (_groupGesture is not { } gesture) return;
        var centre = GroupTransform.CentreOf(gesture.StartBox);
        var world = new Position(ToWorldX(screen.X), ToWorldY(screen.Y), 0);
        var angle = Math.Atan2(world.Y - centre.Y, world.X - centre.X) * 180.0 / Math.PI;
        var delta = angle - gesture.StartAngleDeg;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
            delta = Math.Round(delta / 15.0) * 15.0;

        gesture.OverlayBox = gesture.StartBox;
        gesture.OverlayRotationDeg = delta;
        ApplyGroupPreview(gesture, (obj, before) => GroupTransform.RotateAbout(before, obj.LocalPivot, centre, delta));
    }

    private void ApplyGroupPreview(GroupGesture gesture, Func<SceneObject, ObjectTransform, ObjectTransform> after)
    {
        if (ViewModel is null) return;
        ViewModel.ApplyGesturePreview(() =>
        {
            foreach (var (obj, before) in gesture.Items)
                obj.Transform = after(obj, before);
        });

        // The canvas skips per-object repaints while the preview bracket is open; patch every object
        // (cached geometry + one matrix each) and the overlay once.
        foreach (var (obj, _) in gesture.Items)
            UpdateObjectGeometry(obj);
        RedrawSelectionOverlay();
    }

    /// <summary>Mouse-up: one undo transaction for the whole group.</summary>
    private void FinishGroupTransform()
    {
        var gesture = _groupGesture;
        _groupGesture = null;
        ClearResizeSnap();
        if (gesture is null || ViewModel is null) return;

        var commands = new List<ISceneCommand>();
        foreach (var (obj, before) in gesture.Items)
        {
            if (before.Equals(obj.Transform)) continue;
            commands.Add(new TransformObjectCommand(obj, before, obj.Transform));
        }

        if (commands.Count == 1) ViewModel.Execute(commands[0]);
        else if (commands.Count > 1) ViewModel.Execute(new CompositeSceneCommand(commands));

        // Back to the objects' own bounds; also covers a gesture that changed nothing.
        RedrawSelectionOverlay();
    }

    /// <summary>Escape: put every object back exactly where it started and record nothing.</summary>
    private void CancelGroupTransform()
    {
        var gesture = _groupGesture;
        _groupGesture = null;
        ClearResizeSnap();
        if (gesture is null || ViewModel is null) return;

        ViewModel.ApplyGesturePreview(() =>
        {
            foreach (var (obj, before) in gesture.Items)
                obj.Transform = before;
        });
        foreach (var (obj, _) in gesture.Items)
            UpdateObjectGeometry(obj);
    }
}
