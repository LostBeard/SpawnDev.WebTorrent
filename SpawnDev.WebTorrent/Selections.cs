using System.Collections;

namespace SpawnDev.WebTorrent;

/// <summary>
/// A piece range selection with priority and notification.
/// Direct 1:1 port of selections.js SelectionItem from JS WebTorrent.
/// </summary>
public class SelectionItem
{
    public int From { get; set; }
    public int To { get; set; }
    public int Offset { get; set; }
    public int Priority { get; set; }
    public Action? Notify { get; set; }
    public bool IsStreamSelection { get; set; }
}

/// <summary>
/// Manages piece selections for download prioritization.
/// Direct 1:1 port of selections.js Selections from JS WebTorrent.
/// </summary>
public class Selections : IEnumerable<SelectionItem>
{
    private readonly List<SelectionItem> _items = new();

    public int Length => _items.Count;

    public SelectionItem? Get(int index)
        => index >= 0 && index < _items.Count ? _items[index] : null;

    public void Swap(int i, int j)
        => (_items[i], _items[j]) = (_items[j], _items[i]);

    public void Clear() => _items.Clear();

    public void Sort(Comparison<SelectionItem>? sortFn = null)
    {
        sortFn ??= (a, b) => a.From - b.From;
        _items.Sort(sortFn);
    }

    public void Insert(SelectionItem newItem)
    {
        if (newItem.From > newItem.To)
            throw new ArgumentException("Invalid interval");
        if (!newItem.IsStreamSelection)
            Concatenate(newItem);
        _items.Add(newItem);
    }

    public void Concatenate(SelectionItem newItem)
    {
        for (int i = 0; i < _items.Count; i++)
        {
            var existing = _items[i];
            if (existing.IsStreamSelection) continue;

            if (IsLowerIntersecting(newItem, existing))
                newItem.From = existing.From;
            else if (IsUpperIntersecting(newItem, existing))
                newItem.To = existing.To;
            else if (IsInsideExisting(newItem, existing))
            {
                newItem.From = existing.From;
                newItem.To = existing.To;
            }
            else if (IsCoveringExisting(newItem, existing))
                continue;
            else
                continue;

            MergePriorityAndNotify(newItem, existing);
        }
        Remove(newItem);
    }

    public void Remove(SelectionItem item)
    {
        for (int i = 0; i < _items.Count; i++)
        {
            var existing = _items[i];
            if (existing.IsStreamSelection != item.IsStreamSelection) continue;

            if (existing.IsStreamSelection)
            {
                if (existing.From == item.From && existing.To == item.To)
                {
                    _items.RemoveAt(i);
                    break; // stream selections: remove one at a time
                }
            }
            else
            {
                if (IsLowerIntersecting(item, existing))
                {
                    existing.To = Math.Max(item.From - 1, 0);
                }
                else if (IsUpperIntersecting(item, existing))
                {
                    existing.From = item.To + 1;
                }
                else if (IsInsideExisting(item, existing))
                {
                    var replacements = new List<SelectionItem>();
                    var start = new SelectionItem
                    {
                        From = existing.From,
                        To = Math.Max(item.From - 1, 0),
                        Priority = existing.Priority,
                        Notify = existing.Notify,
                        IsStreamSelection = existing.IsStreamSelection
                    };
                    if (start.To - start.From >= 0 && item.From != 0)
                        replacements.Add(start);
                    var end = new SelectionItem
                    {
                        From = item.To + 1,
                        To = existing.To,
                        Priority = existing.Priority,
                        Notify = existing.Notify,
                        IsStreamSelection = existing.IsStreamSelection
                    };
                    if (end.To - end.From >= 0)
                        replacements.Add(end);
                    _items.RemoveAt(i);
                    _items.InsertRange(i, replacements);
                    i = i - 1 + replacements.Count;
                }
                else if (IsCoveringExisting(item, existing))
                {
                    _items.RemoveAt(i);
                    i--;
                }
            }
        }
    }

    private static void MergePriorityAndNotify(SelectionItem newItem, SelectionItem existing)
    {
        if ((existing.Priority) > (newItem.Priority))
            newItem.Priority = existing.Priority;

        if (newItem.Notify != null && existing.Notify != null)
        {
            var oldNotify = newItem.Notify;
            newItem.Notify = () => { oldNotify(); existing.Notify?.Invoke(); };
        }
        else
        {
            newItem.Notify ??= existing.Notify;
        }
    }

    // --- Interval intersection helpers (match JS exactly) ---

    public static bool IsLowerIntersecting(SelectionItem newItem, SelectionItem existing)
        => newItem.From <= existing.To + 1 && newItem.From > existing.From && newItem.To > existing.To;

    public static bool IsUpperIntersecting(SelectionItem newItem, SelectionItem existing)
        => newItem.To >= existing.From - 1 && newItem.To < existing.To && newItem.From < existing.From;

    public static bool IsInsideExisting(SelectionItem newItem, SelectionItem existing)
    {
        int existingSize = existing.To - existing.From;
        int newSize = newItem.To - newItem.From;
        return newItem.From >= existing.From && newItem.To <= existing.To && newSize < existingSize;
    }

    public static bool IsCoveringExisting(SelectionItem newItem, SelectionItem existing)
        => newItem.From <= existing.From && newItem.To >= existing.To;

    public IEnumerator<SelectionItem> GetEnumerator()
    {
        for (int i = 0; i < _items.Count; i++)
            yield return _items[i];
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
