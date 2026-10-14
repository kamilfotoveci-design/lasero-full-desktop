namespace Lasero.Core.Scene.Commands;

/// <summary>Locks or unlocks objects as one undo step. Each object's previous state is captured, so
/// undoing "Odemknout vše" relocks exactly the objects that were locked before.</summary>
public sealed class SetObjectsLockedCommand : ISceneCommand
{
    private readonly IReadOnlyList<(SceneObject Object, bool Before)> _targets;
    private readonly bool _locked;

    public SetObjectsLockedCommand(IEnumerable<SceneObject> targets, bool locked)
    {
        ArgumentNullException.ThrowIfNull(targets);
        _targets = targets.Distinct().Select(item => (item, item.IsLocked)).ToList();
        _locked = locked;
    }

    /// <summary>False when every target already has the requested state; such a command must not be
    /// pushed, or it would be an undo step that does nothing.</summary>
    public bool ChangesAnything => _targets.Any(target => target.Before != _locked);

    public void Do()
    {
        foreach (var (item, _) in _targets) item.IsLocked = _locked;
    }

    public void Undo()
    {
        foreach (var (item, before) in _targets) item.IsLocked = before;
    }
}
