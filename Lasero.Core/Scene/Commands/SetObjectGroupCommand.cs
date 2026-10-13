namespace Lasero.Core.Scene.Commands;

/// <summary>Changes membership without replacing or flattening geometry. All members retain their
/// object identity, processing layer, editable source and position across Undo and Redo.</summary>
public sealed class SetObjectGroupCommand : ISceneCommand
{
    private readonly IReadOnlyList<(SceneObject Object, Guid? Before, IReadOnlyList<Guid> NestedBefore)> _members;
    private readonly Guid? _after;

    public SetObjectGroupCommand(IEnumerable<SceneObject> members, Guid? after)
    {
        _members = members.Distinct()
            .Select(item => (item, item.GroupId, (IReadOnlyList<Guid>)item.NestedGroupIds.ToArray()))
            .ToList();
        _after = after;
    }

    public void Do()
    {
        foreach (var (item, before, nestedBefore) in _members)
        {
            if (_after is { } groupId)
            {
                item.NestedGroupIds = before is { } previous
                    ? nestedBefore.Append(previous).ToArray()
                    : nestedBefore.ToArray();
                item.GroupId = groupId;
            }
            else
            {
                item.NestedGroupIds = nestedBefore.Take(Math.Max(0, nestedBefore.Count - 1)).ToArray();
                item.GroupId = nestedBefore.Count > 0 ? nestedBefore[^1] : null;
            }
        }
    }

    public void Undo()
    {
        foreach (var (item, before, nestedBefore) in _members)
        {
            item.NestedGroupIds = nestedBefore.ToArray();
            item.GroupId = before;
        }
    }
}
