namespace Lasero.Core.Scene.Commands;

/// <summary>Sets the complete stacking order (Objects[0] is the back) as one undo step. Used by the
/// z-order commands for a multi-selection or a group, where several objects move at once.</summary>
public sealed class ReorderObjectsCommand : ISceneCommand
{
    private readonly SceneDocument _scene;
    private readonly IReadOnlyList<SceneObject> _before;
    private readonly IReadOnlyList<SceneObject> _after;

    public ReorderObjectsCommand(SceneDocument scene, IReadOnlyList<SceneObject> newOrder)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(newOrder);
        _scene = scene;
        _before = scene.Objects.ToList();
        _after = newOrder.ToList();
        if (_after.Count != _before.Count || !_after.ToHashSet().SetEquals(_before))
            throw new ArgumentException("The new order must contain exactly the objects of the scene.", nameof(newOrder));
    }

    public bool ChangesAnything => !_before.SequenceEqual(_after);

    public void Do() => Apply(_after);

    public void Undo() => Apply(_before);

    private void Apply(IReadOnlyList<SceneObject> order)
    {
        for (var index = 0; index < order.Count; index++)
        {
            var current = _scene.Objects.IndexOf(order[index]);
            if (current >= 0 && current != index) _scene.Objects.Move(current, index);
        }
    }
}
