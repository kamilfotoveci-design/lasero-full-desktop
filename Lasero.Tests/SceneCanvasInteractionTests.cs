using System.IO;

namespace Lasero.Tests;

public sealed class SceneCanvasInteractionTests
{
    [Fact]
    public void NodeDragCancelsOnLostCaptureAndOwnerWindowDeactivation()
    {
        var root = FindRepositoryRoot();
        var xaml = File.ReadAllText(Path.Combine(root, "Lasero.App", "Controls", "SceneCanvas.xaml"));
        var codeBehind = File.ReadAllText(Path.Combine(root, "Lasero.App", "Controls", "SceneCanvas.xaml.cs")).Replace("\r\n", "\n");

        Assert.Contains("LostMouseCapture=\"OnDrawCanvasLostMouseCapture\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Loaded=\"OnSceneCanvasLoaded\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Unloaded=\"OnSceneCanvasUnloaded\"", xaml, StringComparison.Ordinal);
        Assert.Contains("_ownerWindow.Deactivated += OnOwnerWindowDeactivated", codeBehind, StringComparison.Ordinal);
        Assert.Contains("_ownerWindow.Deactivated -= OnOwnerWindowDeactivated", codeBehind, StringComparison.Ordinal);

        // Neither event may commit the live preview: both route active node/segment drags through
        // CancelActiveInteraction, whose node-edit branch restores the immutable drag snapshot.
        // The handlers must not name the node modes themselves: any live gesture (move, resize, draw,
        // marquee, node drag) has to be rolled back, so each one simply asks for the cancel.
        var lostCapture = codeBehind[
            codeBehind.IndexOf("private void OnDrawCanvasLostMouseCapture", StringComparison.Ordinal)..
            codeBehind.IndexOf("private void CancelActiveInteraction", StringComparison.Ordinal)];
        var deactivated = codeBehind[
            codeBehind.IndexOf("private void OnOwnerWindowDeactivated", StringComparison.Ordinal)..
            codeBehind.IndexOf("private void OnDrawCanvasLostMouseCapture", StringComparison.Ordinal)];
        Assert.Contains("CancelActiveInteraction();", lostCapture, StringComparison.Ordinal);
        Assert.Contains("CancelActiveInteraction();", deactivated, StringComparison.Ordinal);
        Assert.Contains("_dragMode != DragMode.None", lostCapture, StringComparison.Ordinal);
        Assert.Contains("_dragMode != DragMode.None", deactivated, StringComparison.Ordinal);
    }

    [Fact]
    public void PanToolKeepsPanningWithTheLeftButtonHeld()
    {
        var root = FindRepositoryRoot();
        var codeBehind = File.ReadAllText(Path.Combine(root, "Lasero.App", "Controls", "SceneCanvas.xaml.cs")).Replace("\r\n", "\n");
        var mouseMove = codeBehind[
            codeBehind.IndexOf("private void OnDrawCanvasMouseMove", StringComparison.Ordinal)..
            codeBehind.IndexOf("private void OnDrawCanvasMouseLeftButtonUp", StringComparison.Ordinal)];

        // The first move after the press used to end the pan: only Space or the middle button kept it alive.
        Assert.Contains("ViewModel?.ActiveTool == DesignerTool.Pan", mouseMove, StringComparison.Ordinal);
    }

    [Fact]
    public void MiddleClickMidGestureRollsTheGestureBackBeforePanning()
    {
        var root = FindRepositoryRoot();
        var codeBehind = File.ReadAllText(Path.Combine(root, "Lasero.App", "Controls", "SceneCanvas.xaml.cs")).Replace("\r\n", "\n");
        var middleDown = codeBehind[
            codeBehind.IndexOf("private void OnDrawCanvasMouseDown", StringComparison.Ordinal)..
            codeBehind.IndexOf("private void BeginPan", StringComparison.Ordinal)];

        Assert.True(middleDown.IndexOf("CancelActiveInteraction();", StringComparison.Ordinal)
            < middleDown.IndexOf("BeginPan(", StringComparison.Ordinal));
    }

    [Fact]
    public void TextEntryKeepsSpaceFAndArrowsOutOfTheCanvasShortcuts()
    {
        var root = FindRepositoryRoot();
        var codeBehind = File.ReadAllText(Path.Combine(root, "Lasero.App", "Controls", "SceneCanvas.xaml.cs")).Replace("\r\n", "\n");
        var keyDown = codeBehind[
            codeBehind.IndexOf("private void OnCanvasKeyDown", StringComparison.Ordinal)..
            codeBehind.IndexOf("private bool IsTextEntryFocused", StringComparison.Ordinal)];

        Assert.Contains("e.Key == Key.Space && !typing", keyDown, StringComparison.Ordinal);
        Assert.Contains("if (typing) return;", keyDown, StringComparison.Ordinal);
    }

    [Fact]
    public void HeldArrowKeyExtendsOneUndoStepAndKeepsTheOriginalBefore()
    {
        var a = new Lasero.Core.Scene.SceneObject
        {
            LocalShapes = [],
            LocalPivot = new Lasero.Core.Grbl.Position(0, 0, 0),
            LocalBounds = new Lasero.Core.GCode.BoundingBox2D(0, 0, 10, 10),
        };
        var first = Lasero.App.Input.NudgeCommand.Create([a], 1, 0);
        a.Transform = a.Transform with { X = a.Transform.X + 1 };

        var merged = first.Extended(1, 0);
        Assert.True(merged.MovesSameObjects([a]));

        var startX = a.Transform.X - 1;
        merged.Do();
        Assert.Equal(startX + 2, a.Transform.X, 6);
        merged.Undo();
        Assert.Equal(startX, a.Transform.X, 6);
    }

    [Fact]
    public void NodeDragCoalescesPointerUpdatesAndFlushesLatestPointBeforeCommit()
    {
        var root = FindRepositoryRoot();
        var codeBehind = File.ReadAllText(Path.Combine(root, "Lasero.App", "Controls", "SceneCanvas.xaml.cs")).Replace("\r\n", "\n");
        var mouseMove = codeBehind[
            codeBehind.IndexOf("private void OnDrawCanvasMouseMove", StringComparison.Ordinal)..
            codeBehind.IndexOf("private void OnDrawCanvasMouseLeftButtonUp", StringComparison.Ordinal)];
        var updateDispatcher = codeBehind[
            codeBehind.IndexOf("private void ApplyExpensiveDragUpdate", StringComparison.Ordinal)..
            codeBehind.IndexOf("private void FinishResize", StringComparison.Ordinal)];
        var mouseUp = codeBehind[
            codeBehind.IndexOf("private void OnDrawCanvasMouseLeftButtonUp", StringComparison.Ordinal)..
            codeBehind.IndexOf("private void FinishDraw", StringComparison.Ordinal)];

        Assert.Contains("case DragMode.NodeEdit:\n            case DragMode.NodeSegmentDrag:", mouseMove, StringComparison.Ordinal);
        Assert.Contains("ScheduleExpensiveDragUpdate(screen)", mouseMove, StringComparison.Ordinal);
        Assert.Contains("case DragMode.NodeEdit:\n                UpdateNodeEditDrag(screen)", updateDispatcher, StringComparison.Ordinal);
        Assert.Contains("case DragMode.NodeSegmentDrag:\n                UpdateNodeSegmentDrag(screen)", updateDispatcher, StringComparison.Ordinal);
        Assert.Contains("FlushExpensiveDragUpdate();", mouseUp, StringComparison.Ordinal);
    }

    [Fact]
    public void NormalMouseUpReleasesCaptureOnlyAfterNodeDragBecomesIdle()
    {
        var root = FindRepositoryRoot();
        var codeBehind = File.ReadAllText(Path.Combine(root, "Lasero.App", "Controls", "SceneCanvas.xaml.cs")).Replace("\r\n", "\n");
        var vectorTool = File.ReadAllText(Path.Combine(root, "Lasero.App", "Controls", "SceneCanvas.VectorPathTool.cs")).Replace("\r\n", "\n");
        var mouseUp = codeBehind[
            codeBehind.IndexOf("private void OnDrawCanvasMouseLeftButtonUp", StringComparison.Ordinal)..
            codeBehind.IndexOf("private void FinishDraw", StringComparison.Ordinal)];
        var finishDrag = vectorTool[
            vectorTool.IndexOf("private void FinishNodeEditDrag", StringComparison.Ordinal)..
            vectorTool.IndexOf("private void CancelNodeEditDrag", StringComparison.Ordinal)];
        var exitNodeEdit = vectorTool[
            vectorTool.IndexOf("private void ExitNodeEditMode", StringComparison.Ordinal)..
            vectorTool.IndexOf("private void HandleNodeEditCanvasMouseDown", StringComparison.Ordinal)];

        Assert.True(mouseUp.IndexOf("FinishNodeEditDrag();", StringComparison.Ordinal)
            < mouseUp.IndexOf("_dragMode = DragMode.None;", StringComparison.Ordinal));
        Assert.True(mouseUp.IndexOf("_dragMode = DragMode.None;", StringComparison.Ordinal)
            < mouseUp.IndexOf("DrawCanvas.ReleaseMouseCapture();", StringComparison.Ordinal));
        Assert.DoesNotContain("ReleaseMouseCapture", finishDrag, StringComparison.Ordinal);
        Assert.Contains("if (_dragMode is DragMode.NodeEdit or DragMode.NodeSegmentDrag)\n            CancelActiveInteraction();", exitNodeEdit, StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "Lasero.App")))
                return directory.FullName;
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Lasero repository root was not found.");
    }
}
