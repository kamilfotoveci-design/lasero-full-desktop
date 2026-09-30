namespace Lasero.Core.Scene.Commands;

/// <summary>Adds new objects to the scene, each directly above the object it derives from, as one
/// undoable edit. Nothing existing is removed or moved relative to its neighbours, so Undo restores
/// the scene exactly. Used by Offset Path when the original shape is kept.</summary>
public sealed class InsertObjectsAboveCommand : ISceneCommand
{
    private readonly SceneDocument _scene;
    private readonly IReadOnlyList<(SceneObject Anchor, SceneObject Added)> _items;

    public InsertObjectsAboveCommand(SceneDocument scene, IEnumerable<(SceneObject Anchor, SceneObject Added)> items)
    {
        _scene = scene;
        _items = items.ToList();
    }

    public void Do()
    {
        foreach (var (anchor, added) in _items)
        {
            var index = _scene.Objects.IndexOf(anchor);
            _scene.Objects.Insert(index < 0 ? _scene.Objects.Count : index + 1, added);
        }
    }

    public void Undo()
    {
        foreach (var (_, added) in _items)
            _scene.Objects.Remove(added);
    }
}
