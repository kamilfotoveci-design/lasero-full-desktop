namespace Lasero.Core.Scene.Commands;

/// <summary>Deletes one or more objects. Captures each target's original index at construction time so
/// Undo reinserts every object at its original z-order position rather than just appending them back.</summary>
public sealed class DeleteObjectsCommand : ISceneCommand
{
    private readonly SceneDocument _scene;
    private readonly List<(SceneObject Object, int Index)> _targets;

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
    }

    public void Undo()
    {
        // Ascending index order: each insert lands at its recorded position because every
        // earlier (lower-index) reinsertion has already shifted the list back into shape.
        foreach (var (obj, index) in _targets)
            _scene.Objects.Insert(Math.Min(index, _scene.Objects.Count), obj);
    }
}
