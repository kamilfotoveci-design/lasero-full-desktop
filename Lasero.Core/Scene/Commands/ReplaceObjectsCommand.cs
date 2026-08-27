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

    public ReplaceObjectsCommand(
        SceneDocument scene,
        IEnumerable<SceneObject> removed,
        IEnumerable<SceneObject> added,
        IEnumerable<LayerSettings>? candidateLayers = null)
    {
        _scene = scene;
        _removed = removed
            .Select(item => (Object: item, Index: scene.Objects.IndexOf(item)))
            .Where(item => item.Index >= 0)
            .OrderBy(item => item.Index)
            .ToList();
        _added = added.ToList();
        _candidateLayers = candidateLayers?.ToList() ?? [];
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
    }

    public void Undo()
    {
        foreach (var item in _added)
            _scene.Objects.Remove(item);

        foreach (var layer in _addedLayers)
            _scene.Layers.Remove(layer);

        foreach (var (item, index) in _removed)
            _scene.Objects.Insert(Math.Min(index, _scene.Objects.Count), item);
    }
}
