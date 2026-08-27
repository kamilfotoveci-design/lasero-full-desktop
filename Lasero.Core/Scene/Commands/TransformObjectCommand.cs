namespace Lasero.Core.Scene.Commands;

/// <summary>Move, rotate, and scale are all the same shape of edit — capture the transform before/after
/// one drag gesture (not one per MouseMove) and push a single command on MouseUp.</summary>
public sealed class TransformObjectCommand : ISceneCommand
{
    private readonly SceneObject _target;
    private readonly ObjectTransform _before;
    private readonly ObjectTransform _after;

    public TransformObjectCommand(SceneObject target, ObjectTransform before, ObjectTransform after)
    {
        _target = target;
        _before = before;
        _after = after;
    }

    public void Do() => _target.Transform = _after;
    public void Undo() => _target.Transform = _before;
}
