using System.Collections.ObjectModel;

namespace Quartermaster.Gui.Shared;

internal static class CollectionUpdates
{
    // Keep item sources and surviving row instances intact so edits do not reset the list.
    public static void Synchronize<T>(ObservableCollection<T> collection, IReadOnlyList<T> desired) where T : class
    {
        var retained = desired.ToHashSet();
        for (var index = collection.Count - 1; index >= 0; index--)
            if (!retained.Contains(collection[index])) collection.RemoveAt(index);
        for (var index = 0; index < desired.Count; index++)
        {
            var item = desired[index];
            if (index < collection.Count && ReferenceEquals(collection[index], item)) continue;
            var previous = collection.IndexOf(item);
            if (previous >= 0) collection.Move(previous, index);
            else collection.Insert(index, item);
        }
    }
}
