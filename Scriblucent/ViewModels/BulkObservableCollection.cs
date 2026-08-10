using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Linq;

namespace Scriblucent.ViewModels;

/// <summary>
/// Supports bulk additions with one Reset notification instead of per-item events.
/// AddRange populates the internal list before notifying observers.
/// </summary>
internal sealed class BulkObservableCollection<T> : ObservableCollection<T>
{
    public BulkObservableCollection()
    {
    }

    public BulkObservableCollection(IEnumerable<T> items)
        : base(items)
    {
    }

    public void AddRange(IEnumerable<T> items)
    {
        if (items is null)
            return;

        var list = items as IList<T> ?? items.ToList();
        if (list.Count == 0)
            return;

        foreach (var item in list)
            Items.Add(item);

        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}
