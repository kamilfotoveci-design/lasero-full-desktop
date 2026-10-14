namespace Lasero.Core.Scene;

/// <summary>
/// Stacking-order moves for a selection of any size. Objects[0] is the back of the stack. The
/// selection keeps its own relative order, and a group moves as one block: its members end up next to
/// each other, and an unselected group is stepped over as a whole rather than split by a one-step move.
/// Pure; the caller commits the returned order with ReorderObjectsCommand.
/// </summary>
public static class ZOrderPlanner
{
    public static IReadOnlyList<SceneObject> BringToFront(IReadOnlyList<SceneObject> order, IReadOnlySet<SceneObject> selected) =>
        order.Where(item => !selected.Contains(item)).Concat(order.Where(selected.Contains)).ToList();

    public static IReadOnlyList<SceneObject> SendToBack(IReadOnlyList<SceneObject> order, IReadOnlySet<SceneObject> selected) =>
        order.Where(selected.Contains).Concat(order.Where(item => !selected.Contains(item))).ToList();

    /// <summary>Every selected block moves above the unit directly above it. <paramref name="unitKey"/>
    /// identifies what moves together (a group id, or the object's own id).</summary>
    public static IReadOnlyList<SceneObject> BringForward(
        IReadOnlyList<SceneObject> order, IReadOnlySet<SceneObject> selected, Func<SceneObject, Guid> unitKey)
    {
        var units = Units(order, selected, unitKey);
        for (var index = units.Count - 2; index >= 0; index--)
            if (units[index].Selected && !units[index + 1].Selected)
                (units[index], units[index + 1]) = (units[index + 1], units[index]);
        return units.SelectMany(unit => unit.Members).ToList();
    }

    public static IReadOnlyList<SceneObject> SendBackward(
        IReadOnlyList<SceneObject> order, IReadOnlySet<SceneObject> selected, Func<SceneObject, Guid> unitKey)
    {
        var units = Units(order, selected, unitKey);
        for (var index = 1; index < units.Count; index++)
            if (units[index].Selected && !units[index - 1].Selected)
                (units[index], units[index - 1]) = (units[index - 1], units[index]);
        return units.SelectMany(unit => unit.Members).ToList();
    }

    /// <summary>True when some unselected object lies above the lowest selected one, i.e. bringing the
    /// selection forward would change anything.</summary>
    public static bool CanBringForward(IReadOnlyList<SceneObject> order, IReadOnlySet<SceneObject> selected)
    {
        var seenSelected = false;
        foreach (var item in order)
        {
            if (selected.Contains(item)) seenSelected = true;
            else if (seenSelected) return true;
        }
        return false;
    }

    public static bool CanSendBackward(IReadOnlyList<SceneObject> order, IReadOnlySet<SceneObject> selected)
    {
        var seenUnselected = false;
        foreach (var item in order)
        {
            if (!selected.Contains(item)) seenUnselected = true;
            else if (seenUnselected) return true;
        }
        return false;
    }

    private static List<(bool Selected, List<SceneObject> Members)> Units(
        IReadOnlyList<SceneObject> order, IReadOnlySet<SceneObject> selected, Func<SceneObject, Guid> unitKey)
    {
        var units = new List<(bool Selected, List<SceneObject> Members)>();
        Guid? previousKey = null;
        var previousSelected = false;
        foreach (var item in order)
        {
            var isSelected = selected.Contains(item);
            var key = unitKey(item);
            if (units.Count > 0 && previousKey == key && previousSelected == isSelected)
                units[^1].Members.Add(item);
            else
                units.Add((isSelected, [item]));
            previousKey = key;
            previousSelected = isSelected;
        }
        return units;
    }
}
