namespace Lasero.Core.Scene.Commands;

/// <summary>Deletes one or more objects. Captures each target's original index at construction time so
/// Undo reinserts every object at its original z-order position rather than just appending them back.</summary>
public sealed class DeleteObjectsCommand : ISceneCommand
{
    private readonly SceneDocument _scene;
    private readonly List<(SceneObject Object, int Index)> _targets;
    private readonly List<(Lasero.Core.Layers.LayerSettings Layer, int Index)> _removedLayers = [];

    public DeleteObjectsCommand(SceneDocument scene, IEnumerable<SceneObject> targets)
    {
        _scene = scene;
        _targets = targets
            .Select(o => (Object: o, Index: scene.Objects.IndexOf(o)))
            .Where(t => t.Index >= 0)
            .OrderBy(t => t.Index)
            .ToList();
    }

    public void Do()
    {
        foreach (var (obj, _) in _targets)
            _scene.Objects.Remove(obj);

        _removedLayers.Clear();
        foreach (var layer in _scene.Layers.ToList())
        {
            if (!_targets.Any(target => UsesLayer(target.Object, layer)) ||
                _scene.Objects.Any(item => UsesLayer(item, layer))) continue;

            _removedLayers.Add((layer, _scene.Layers.IndexOf(layer)));
            _scene.Layers.Remove(layer);
        }
    }

    public void Undo()
    {
        foreach (var (layer, index) in _removedLayers.OrderBy(item => item.Index))
            _scene.Layers.Insert(Math.Min(index, _scene.Layers.Count), layer);

        // Ascending index order: each insert lands at its recorded position because every
        // earlier (lower-index) reinsertion has already shifted the list back into shape.
        foreach (var (obj, index) in _targets)
            _scene.Objects.Insert(Math.Min(index, _scene.Objects.Count), obj);
    }

    private static bool UsesLayer(SceneObject item, Lasero.Core.Layers.LayerSettings layer) =>
        (layer.IsRaster && item.IsRaster && item.LocalShapes.All(shape => shape.LayerId == Guid.Empty)) ||
        item.LocalShapes.Any(shape => shape.LayerId != Guid.Empty
            ? shape.LayerId == layer.Id
            : shape.LayerColor.IsApproximately(layer.Color));
}
