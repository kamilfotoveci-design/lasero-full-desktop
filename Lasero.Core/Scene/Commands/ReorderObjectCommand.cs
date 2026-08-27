namespace Lasero.Core.Scene.Commands;

/// <summary>Bring-to-front / send-to-back / step forward/backward — Objects' list order IS z-order
/// (last = topmost), so this is just ObservableCollection.Move, captured for undo.</summary>
public sealed class ReorderObjectCommand : ISceneCommand
{
    private readonly SceneDocument _scene;
    private readonly int _fromIndex;
    private readonly int _toIndex;

    public ReorderObjectCommand(SceneDocument scene, SceneObject target, int toIndex)
    {
        _scene = scene;
        _fromIndex = scene.Objects.IndexOf(target);
        _toIndex = toIndex;
    }

    public void Do()
    {
        if (_fromIndex != _toIndex) _scene.Objects.Move(_fromIndex, _toIndex);
    }

    public void Undo()
    {
        if (_fromIndex != _toIndex) _scene.Objects.Move(_toIndex, _fromIndex);
    }
}
