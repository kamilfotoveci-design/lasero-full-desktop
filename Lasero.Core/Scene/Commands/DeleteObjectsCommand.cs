namespace Lasero.Core.Scene.Commands;

/// <summary>Deletes one or more objects. Captures each target's original index at construction time so
/// Undo reinserts every object at its original z-order position rather than just appending them back.
/// Linked offset outputs whose source is deleted do not survive as hidden zombies: a live output is
/// detached (keeps its geometry as an ordinary object), a stale suspended one is deleted with its source.
/// Layers the deletion leaves empty go too (see EmptiedLayerPruner).
/// All of it is part of this single command, so one Undo restores everything.</summary>
public sealed class DeleteObjectsCommand : ISceneCommand
{
    private readonly SceneDocument _scene;
    private readonly List<(SceneObject Object, int Index)> _targets;
    private readonly List<(SceneObject Object, OffsetLink Link)> _detached = [];
    private readonly EmptiedLayerPruner _layers;

    public DeleteObjectsCommand(SceneDocument scene, IEnumerable<SceneObject> targets)
    {
        _scene = scene;
        _layers = new EmptiedLayerPruner(scene);
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
        _layers.CaptureBefore();
        foreach (var (obj, _) in _targets)
            _scene.Objects.Remove(obj);
        foreach (var (obj, _) in _detached)
            obj.OffsetLink = null;
        _layers.PruneAfter();
    }

    public void Undo()
    {
        _layers.Restore();

        // Ascending index order: each insert lands at its recorded position because every
        // earlier (lower-index) reinsertion has already shifted the list back into shape.
        foreach (var (obj, index) in _targets)
            _scene.Objects.Insert(Math.Min(index, _scene.Objects.Count), obj);
        foreach (var (obj, link) in _detached)
            obj.OffsetLink = link;
    }
}
