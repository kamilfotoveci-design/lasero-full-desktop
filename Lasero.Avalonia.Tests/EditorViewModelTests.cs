using System.IO;
using Lasero.Avalonia.Services;
using Lasero.Avalonia.ViewModels;
using Lasero.Core.Layers;
using ReactiveUI.Builder;

namespace Lasero.Avalonia.Tests;

[System.Runtime.Versioning.SupportedOSPlatform("windows")] // EditorViewModel's constructor is windows-only (BackgroundRemovalService).
public sealed class EditorViewModelTests
{
    private static EditorViewModel NewViewModel() =>
        new(new FakeLaserMachine(), new AppSettingsStore(Path.Combine(Path.GetTempPath(), $"lasero-test-{Guid.NewGuid():N}.json")));

    [Fact]
    public void ToolCommandsChangeTheActiveEditorTool()
    {
        TestApp.EnsureInitialized();
        var viewModel = NewViewModel();

        viewModel.ShapeTool.Execute().Subscribe();

        Assert.Equal(EditorTool.Shape, viewModel.ActiveTool);
        Assert.True(viewModel.IsShapeTool);
        Assert.False(viewModel.IsSelectTool);
    }

    [Fact]
    public void ResetViewRestoresFitState()
    {
        TestApp.EnsureInitialized();
        var viewModel = NewViewModel();
        viewModel.Zoom = 2.4;
        viewModel.PanX = 120;
        viewModel.PanY = -45;

        viewModel.ResetView.Execute().Subscribe();

        Assert.Equal(1, viewModel.Zoom);
        Assert.Equal(0, viewModel.PanX);
        Assert.Equal(0, viewModel.PanY);
        Assert.Empty(viewModel.Document.Objects);
    }

    [Fact]
    public void DrawnPrimitiveUsesCoreDocumentAndUndoRedoStack()
    {
        TestApp.EnsureInitialized();
        var viewModel = NewViewModel();

        var result = viewModel.AddPrimitive(EditorTool.Rectangle, 10, 20, 60, 70);

        Assert.True(result.IsSuccess);
        Assert.Single(viewModel.Document.Objects);
        Assert.Single(viewModel.Document.Layers);
        Assert.Same(result.Value, viewModel.SelectedObject);
        Assert.True(viewModel.CanUndo);

        viewModel.Undo.Execute().Subscribe();
        Assert.Empty(viewModel.Document.Objects);
        Assert.True(viewModel.CanRedo);

        viewModel.Redo.Execute().Subscribe();
        Assert.Single(viewModel.Document.Objects);
    }

    [Fact]
    public void SelectShapeActivatesTheShapeToolAndRemembersTheChosenShape()
    {
        TestApp.EnsureInitialized();
        var viewModel = NewViewModel();

        viewModel.SelectShape.Execute(ShapeKind.Star).Subscribe();

        Assert.Equal(EditorTool.Shape, viewModel.ActiveTool);
        Assert.Equal(ShapeKind.Star, viewModel.CurrentShapeTool);

        var result = viewModel.AddPrimitive(EditorTool.Shape, 0, 0, 10, 10);
        Assert.True(result.IsSuccess);
        Assert.Equal("Hvězda", result.Value.Name);
    }

    [Fact]
    public void LayerCommandsReorderAndDeleteOnlyWhenUnused()
    {
        TestApp.EnsureInitialized();
        var viewModel = NewViewModel();

        // The first drawn object creates the only default layer; a second object with no explicit
        // color reuses that same layer (matches AddPrimitive's Document.Layers.FirstOrDefault()
        // fallback) — a second, independent layer only exists once one is added manually, which the
        // Avalonia editor does not yet support (no per-color layer creation ported). So this test
        // covers the one-layer, multi-object case that is actually reachable today.
        var first = viewModel.AddPrimitive(EditorTool.Rectangle, 0, 0, 10, 10).Value;
        viewModel.AddPrimitive(EditorTool.Line, 20, 20, 30, 30);
        Assert.Single(viewModel.Layers);

        var layer = viewModel.Layers[0];
        viewModel.SelectedLayer = layer;
        Assert.False(viewModel.CanReorderLayers); // only one layer exists
        Assert.False(viewModel.CanDeleteSelectedLayer); // used by two objects

        viewModel.Document.Objects.Remove(first);
        viewModel.SelectedLayer = layer; // re-trigger recompute after an object moved out from under it
        Assert.False(viewModel.CanDeleteSelectedLayer); // the line still uses it

        viewModel.Document.Objects.Clear();
        viewModel.SelectedLayer = layer;
        Assert.True(viewModel.CanDeleteSelectedLayer);

        viewModel.DeleteSelectedLayer.Execute().Subscribe();
        Assert.DoesNotContain(layer, viewModel.Layers);
        Assert.Null(viewModel.SelectedLayer);
    }

    [Fact]
    public void MoveSelectedLayerUpAndDownReorderTheStack()
    {
        TestApp.EnsureInitialized();
        var viewModel = NewViewModel();
        var layerA = LayerSettings.CreateDefault(new RgbColor(255, 0, 0), LayerMode.Cut, "Vrstva A");
        var layerB = LayerSettings.CreateDefault(new RgbColor(0, 255, 0), LayerMode.Cut, "Vrstva B");
        viewModel.Document.Layers.Add(layerA);
        viewModel.Document.Layers.Add(layerB);

        viewModel.SelectedLayer = layerA;
        Assert.True(viewModel.CanReorderLayers);
        Assert.False(viewModel.CanMoveSelectedLayerUp);
        Assert.True(viewModel.CanMoveSelectedLayerDown);

        viewModel.MoveSelectedLayerDown.Execute().Subscribe();
        Assert.Equal([layerB, layerA], viewModel.Layers);
        Assert.True(viewModel.CanMoveSelectedLayerUp);
        Assert.False(viewModel.CanMoveSelectedLayerDown);

        viewModel.MoveSelectedLayerUp.Execute().Subscribe();
        Assert.Equal([layerA, layerB], viewModel.Layers);
    }
}
