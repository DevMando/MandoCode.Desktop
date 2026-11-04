using System.Collections.ObjectModel;

namespace MandoCode.Desktop.Services;

/// <summary>Reconcile keyed rows without resetting the list or recreating unchanged containers.</summary>
public static class StableCollection
{
    public static void Update<T, TKey>(ObservableCollection<T> current, IEnumerable<T> desired,
        Func<T, TKey> key, Func<T, T, bool>? same = null) where TKey : notnull
    {
        var next = desired.ToList();
        var keys = next.Select(key).ToHashSet();
        for (var i = current.Count - 1; i >= 0; i--)
            if (!keys.Contains(key(current[i]))) current.RemoveAt(i);
        for (var i = 0; i < next.Count; i++)
        {
            var index = -1;
            for (var j = i; j < current.Count; j++)
                if (EqualityComparer<TKey>.Default.Equals(key(current[j]), key(next[i]))) { index = j; break; }
            if (index < 0) current.Insert(i, next[i]);
            else
            {
                if (index != i) current.Move(index, i);
                if (!(same?.Invoke(current[i], next[i]) ?? EqualityComparer<T>.Default.Equals(current[i], next[i])))
                    current[i] = next[i];
            }
        }
    }
}
