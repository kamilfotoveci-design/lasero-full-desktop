namespace Lasero.Core.Scene.Commands;

using Lasero.Core.Layers;

/// <summary>
/// Replaces a set of scene objects with another set as one undoable edit. Grouping and
/// ungrouping use this command so the original z-order is restored exactly by Undo.
/// </summary>
public sealed class ReplaceObjectsCommand : ISceneCommand
{
    private readonly SceneDocument _scene;
    private readonly List<(SceneObject Object, int Index)> _removed;
    private readonly IReadOnlyList<SceneObject> _added;
    private readonly IReadOnlyList<LayerSettings> _candidateLayers;
    private IReadOnlyList<LayerSettings> _addedLayers = [];
    private readonly int _insertIndex;
    private readonly IReadOnlyList<LayerSettings> _layersToRemove;
    private List<(LayerSettings Layer, int Index)> _removedLayers = [];

    public ReplaceObjectsCommand(
        SceneDocument scene,
        IEnumerable<SceneObject> removed,
        IEnumerable<SceneObject> added,
        IEnumerable<LayerSettings>? candidateLayers = null,
        IEnumerable<LayerSettings>? layersToRemove = null)
    {
        _scene = scene;
        _removed = removed
            .Select(item => (Object: item, Index: scene.Objects.IndexOf(item)))
            .Where(item => item.Index >= 0)
            .OrderBy(item => item.Index)
            .ToList();
        _added = added.ToList();
        _candidateLayers = candidateLayers?.ToList() ?? [];
        _layersToRemove = layersToRemove?.ToList() ?? [];
        // A replacement represents the complete selected stack, so it must retain the z-position
        // of the topmost removed object. Inserting at the first removed index can put a united vector
        // behind an unselected filled object and make the result appear to have disappeared.
        _insertIndex = _removed.Count == 0
            ? scene.Objects.Count
            : _removed[^1].Index - (_removed.Count - 1);
    }

    public void Do()
    {
        _addedLayers = _scene.EnsureLayers(_candidateLayers, _added);

        foreach (var (item, _) in _removed)
            _scene.Objects.Remove(item);

        for (var index = 0; index < _added.Count; index++)
            _scene.Objects.Insert(Math.Min(_insertIndex + index, _scene.Objects.Count), _added[index]);

        // Layers that only the removed objects used (a traced bitmap's operation) leave with them.
        _removedLayers = _layersToRemove
            .Select(layer => (Layer: layer, Index: _scene.Layers.IndexOf(layer)))
            .Where(item => item.Index >= 0)
            .OrderBy(item => item.Index)
            .ToList();
        foreach (var (layer, _) in _removedLayers)
            _scene.Layers.Remove(layer);
    }

    public void Undo()
    {
        foreach (var item in _added)
            _scene.Objects.Remove(item);

        foreach (var layer in _addedLayers)
            _scene.Layers.Remove(layer);

        foreach (var (layer, index) in _removedLayers)
            _scene.Layers.Insert(Math.Min(index, _scene.Layers.Count), layer);

        foreach (var (item, index) in _removed)
            _scene.Objects.Insert(Math.Min(index, _scene.Objects.Count), item);
    }
}
