using System.Collections;

namespace SpawnDev.WebTorrent;

/// <summary>
/// Thread-safe list wrapper. All operations are synchronized via internal lock.
/// Used for Wire.Requests and Wire.PeerRequests which are accessed from multiple threads
/// (download engine, WebRTC callbacks, web seed HTTP callbacks, UI timer).
/// </summary>
public class SynchronizedList<T> : IEnumerable<T>
{
    private readonly List<T> _list = new();
    private readonly object _lock = new();

    public int Count { get { lock (_lock) return _list.Count; } }

    public void Add(T item) { lock (_lock) _list.Add(item); }

    public bool Remove(T item) { lock (_lock) return _list.Remove(item); }

    public void RemoveAt(int index) { lock (_lock) _list.RemoveAt(index); }

    public void Clear() { lock (_lock) _list.Clear(); }

    public T this[int index]
    {
        get { lock (_lock) return _list[index]; }
        set { lock (_lock) _list[index] = value; }
    }

    public T? FirstOrDefault(Func<T, bool> predicate)
    {
        lock (_lock) return _list.FirstOrDefault(predicate);
    }

    public T[] ToArray() { lock (_lock) return _list.ToArray(); }

    public IEnumerator<T> GetEnumerator()
    {
        // Return snapshot iterator so enumeration is safe
        T[] snapshot;
        lock (_lock) snapshot = _list.ToArray();
        return ((IEnumerable<T>)snapshot).GetEnumerator();
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
