using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace Elysium_Cast_IPTV.Models;

/// <summary>
/// ObservableCollection that can be repopulated in bulk with a single change
/// notification. Adding thousands of items one-by-one (e.g. 9000+ movies) raises
/// a CollectionChanged event per item and freezes the bound list; <see
/// cref="Reset"/> fills the backing store directly and notifies once.
/// </summary>
public sealed class BulkObservableCollection<T> : ObservableCollection<T>
{
    public void Reset(IEnumerable<T> items)
    {
        Items.Clear();
        foreach (var item in items) Items.Add(item);
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}
