namespace Lasero.Core.Scene.Commands;

/// <summary>Deletes one or more objects. Captures each target's original index at construction time so
/// Undo reinserts every object at its original z-order position rather than just appending them back.
/// Linked offset outputs whose source is deleted do not survive as hidden zombies: a live output is
/// detached (keeps its geometry as an ordinary object), a stale suspended one is deleted with its source.
/// All of it is part of this single command, so one Undo restores everything.</summary>
public sealed class DeleteObjectsCommand : ISceneCommand
{
    private readonly SceneDocument _scene;
    private readonly List<(SceneObject Object, int Index)> _targets;
    private readonly List<(SceneObject Object, OffsetLink Link)> _detached = [];
    private readonly List<(Lasero.Core.Layers.LayerSettings Layer, int Index)> _removedLayers = [];

    public DeleteObjectsCommand(SceneDocument scene, IEnumerable<SceneObject> targets)
    {
        _scene = scene;
        var requested = targets
            .Select(o => (Object: o, Index: scene.Objects.IndexOf(o)))
            .Where(t => t.Index >= 0)
            .ToList();

        var deletedIds = requested.Select(t => t.Object.Id).ToHashSet();
        var requestedObjects = requested.Select(t => t.Object).ToHashSet();
        foreach (var candidate in scene.Objects)
        {
            if (requestedObjects.Contains(candidate) || candidate.OffsetLink is not { } link) continue;
            if (!link.SourceIds.Any(deletedIds.Contains)) continue;
            if (link.IsSuspended) requested.Add((candidate, scene.Objects.IndexOf(candidate)));
            else _detached.Add((candidate, link));
        }

        _targets = requested.OrderBy(t => t.Index).ToList();
    }

    public void Do()
    {
        foreach (var (obj, _) in _targets)
            _scene.Objects.Remove(obj);
        foreach (var (obj, _) in _detached)
            obj.OffsetLink = null;

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
        foreach (var (obj, link) in _detached)
            obj.OffsetLink = link;
    }

    private static bool UsesLayer(SceneObject item, Lasero.Core.Layers.LayerSettings layer) =>
        (layer.IsRaster && item.IsRaster && item.LocalShapes.All(shape => shape.LayerId == Guid.Empty)) ||
        item.LocalShapes.Any(shape => shape.LayerId != Guid.Empty
            ? shape.LayerId == layer.Id
            : shape.LayerColor.IsApproximately(layer.Color));
}
