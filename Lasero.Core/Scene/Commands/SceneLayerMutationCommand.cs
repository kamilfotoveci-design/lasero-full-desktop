using Lasero.Core.Import;
using Lasero.Core.Layers;

namespace Lasero.Core.Scene.Commands;

/// <summary>Undoable layer-list and layer-assignment mutation. Captures complete layer settings and
/// every object's shape-to-layer assignments so color-based merges can be restored exactly.</summary>
public sealed class SceneLayerMutationCommand : ISceneCommand
{
    private readonly SceneDocument _scene;
    private readonly Action _mutation;
    private readonly Func<LayerSettings?>? _captureSelection;
    private readonly Action<LayerSettings?>? _restoreSelection;
    private Snapshot? _before;
    private Snapshot? _after;

    public SceneLayerMutationCommand(
        SceneDocument scene,
        Action mutation,
        Func<LayerSettings?>? captureSelection = null,
        Action<LayerSettings?>? restoreSelection = null)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(mutation);
        _scene = scene;
        _mutation = mutation;
        _captureSelection = captureSelection;
        _restoreSelection = restoreSelection;
    }

    public void Do()
    {
        if (_after is not null)
        {
            Restore(_after);
            return;
        }

        _before = Capture();
        _mutation();
        _after = Capture();
    }

    public void Undo()
    {
        if (_before is not null) Restore(_before);
    }

    private Snapshot Capture() => new(
        _scene.Layers.Select(layer => new LayerState(
            layer, layer.Color, layer.Name, layer.Mode, layer.Speed, layer.Power, layer.Passes,
            layer.FillLineIntervalMm, layer.MaterialLabel, layer.IsEnabled, layer.IsVisible,
            layer.IsLocked, layer.IsRaster)).ToList(),
        _scene.Objects.Select(item => new ObjectState(item, item.LocalShapes.ToList())).ToList(),
        _captureSelection?.Invoke());

    private void Restore(Snapshot snapshot)
    {
        foreach (var state in snapshot.Layers)
        {
            state.Layer.Color = state.Color;
            state.Layer.Name = state.Name;
            state.Layer.Mode = state.Mode;
            state.Layer.Speed = state.Speed;
            state.Layer.Power = state.Power;
            state.Layer.Passes = state.Passes;
            state.Layer.FillLineIntervalMm = state.FillLineIntervalMm;
            state.Layer.MaterialLabel = state.MaterialLabel;
            state.Layer.IsEnabled = state.IsEnabled;
            state.Layer.IsVisible = state.IsVisible;
            state.Layer.IsLocked = state.IsLocked;
            state.Layer.IsRaster = state.IsRaster;
        }

        _scene.Layers.Clear();
        foreach (var state in snapshot.Layers)
            _scene.Layers.Add(state.Layer);

        foreach (var state in snapshot.Objects)
            state.Object.RestoreLocalShapes(state.Shapes);
        _restoreSelection?.Invoke(snapshot.SelectedLayer);
    }

    private sealed record Snapshot(
        IReadOnlyList<LayerState> Layers,
        IReadOnlyList<ObjectState> Objects,
        LayerSettings? SelectedLayer);
    private sealed record ObjectState(SceneObject Object, IReadOnlyList<ImportedShape> Shapes);
    private sealed record LayerState(
        LayerSettings Layer,
        RgbColor Color,
        string Name,
        LayerMode Mode,
        double Speed,
        double Power,
        int Passes,
        double FillLineIntervalMm,
        string? MaterialLabel,
        bool IsEnabled,
        bool IsVisible,
        bool IsLocked,
        bool IsRaster);
}
