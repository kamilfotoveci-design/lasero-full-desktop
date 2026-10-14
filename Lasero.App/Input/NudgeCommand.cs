using Lasero.Core.Scene;
using Lasero.Core.Scene.Commands;

namespace Lasero.App.Input;

/// <summary>
/// One keyboard nudge of a selection, as one undo step. Holding an arrow key auto-repeats, and a
/// command per repeat made Ctrl+Z walk back one millimetre at a time; the canvas instead replaces its
/// own newest step with <see cref="Extended"/> while the same selection keeps being nudged within
/// <see cref="CoalesceWindow"/>. The undo target stays the transforms from before the first key press.
/// </summary>
public sealed class NudgeCommand : ISceneCommand
{
    /// <summary>How soon after the previous nudge the next one still belongs to the same undo step.</summary>
    public static readonly TimeSpan CoalesceWindow = TimeSpan.FromMilliseconds(600);

    private readonly IReadOnlyList<(SceneObject Target, ObjectTransform Before, ObjectTransform After)> _items;

    private NudgeCommand(IReadOnlyList<(SceneObject, ObjectTransform, ObjectTransform)> items) => _items = items;

    public IReadOnlyList<SceneObject> Targets => _items.Select(item => item.Target).ToList();

    public static NudgeCommand Create(IEnumerable<SceneObject> targets, double dx, double dy) =>
        new(targets
            .Select(obj => (obj, obj.Transform, obj.Transform with { X = obj.Transform.X + dx, Y = obj.Transform.Y + dy }))
            .ToList());

    /// <summary>The same step carried further by one more nudge; the original "before" is kept.</summary>
    public NudgeCommand Extended(double dx, double dy) =>
        new(_items
            .Select(item => (item.Target, item.Before, item.After with { X = item.After.X + dx, Y = item.After.Y + dy }))
            .ToList());

    /// <summary>True when <paramref name="targets"/> is exactly the set this command moves.</summary>
    public bool MovesSameObjects(IReadOnlyCollection<SceneObject> targets) =>
        targets.Count == _items.Count && _items.All(item => targets.Contains(item.Target));

    public void Do()
    {
        foreach (var item in _items) item.Target.Transform = item.After;
    }

    public void Undo()
    {
        for (var i = _items.Count - 1; i >= 0; i--) _items[i].Target.Transform = _items[i].Before;
    }
}
