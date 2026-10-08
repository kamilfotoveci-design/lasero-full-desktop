using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using Lasero.Core.Scene;

namespace Lasero.App.ViewModels;

/// <summary>
/// The scene selection. An ObservableCollection announces every Add separately, so selecting 900
/// objects (Select All, a marquee, the result of a trace) refreshed the selection overlay and the
/// whole inspector state 900 times, each time over a larger selection. <see cref="ReplaceWith"/>
/// swaps the contents and announces the change once.
/// </summary>
public sealed class SelectionCollection : ObservableCollection<SceneObject>
{
    /// <summary>Makes the selection exactly <paramref name="items"/>, in order, with one notification
    /// (none at all when nothing changes).</summary>
    public void ReplaceWith(IEnumerable<SceneObject> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        var replacement = items.ToList();
        if (replacement.Count == Count && replacement.SequenceEqual(this))
            return;

        CheckReentrancy();
        Items.Clear();
        foreach (var item in replacement) Items.Add(item);
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}
