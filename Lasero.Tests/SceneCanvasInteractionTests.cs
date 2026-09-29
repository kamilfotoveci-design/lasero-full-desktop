using System.IO;

namespace Lasero.Tests;

public sealed class SceneCanvasInteractionTests
{
    [Fact]
    public void NodeDragCancelsOnLostCaptureAndOwnerWindowDeactivation()
    {
        var root = FindRepositoryRoot();
        var xaml = File.ReadAllText(Path.Combine(root, "Lasero.App", "Controls", "SceneCanvas.xaml"));
        var codeBehind = File.ReadAllText(Path.Combine(root, "Lasero.App", "Controls", "SceneCanvas.xaml.cs"));

        Assert.Contains("LostMouseCapture=\"OnDrawCanvasLostMouseCapture\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Loaded=\"OnSceneCanvasLoaded\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Unloaded=\"OnSceneCanvasUnloaded\"", xaml, StringComparison.Ordinal);
        Assert.Contains("_ownerWindow.Deactivated += OnOwnerWindowDeactivated", codeBehind, StringComparison.Ordinal);
        Assert.Contains("_ownerWindow.Deactivated -= OnOwnerWindowDeactivated", codeBehind, StringComparison.Ordinal);

        // Neither event may commit the live preview: both route active node/segment drags through
        // CancelActiveInteraction, whose node-edit branch restores the immutable drag snapshot.
        Assert.Contains("private void OnDrawCanvasLostMouseCapture", codeBehind, StringComparison.Ordinal);
        Assert.Contains("private void OnOwnerWindowDeactivated", codeBehind, StringComparison.Ordinal);
        Assert.Contains("if (_dragMode is DragMode.NodeEdit or DragMode.NodeSegmentDrag)\n            CancelActiveInteraction();", codeBehind, StringComparison.Ordinal);
    }

    [Fact]
    public void NodeDragCoalescesPointerUpdatesAndFlushesLatestPointBeforeCommit()
    {
        var root = FindRepositoryRoot();
        var codeBehind = File.ReadAllText(Path.Combine(root, "Lasero.App", "Controls", "SceneCanvas.xaml.cs"));
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
        var codeBehind = File.ReadAllText(Path.Combine(root, "Lasero.App", "Controls", "SceneCanvas.xaml.cs"));
        var vectorTool = File.ReadAllText(Path.Combine(root, "Lasero.App", "Controls", "SceneCanvas.VectorPathTool.cs"));
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
