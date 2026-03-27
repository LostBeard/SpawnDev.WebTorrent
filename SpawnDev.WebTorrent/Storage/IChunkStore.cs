namespace SpawnDev.WebTorrent.Storage;

/// <summary>
/// Abstract chunk storage backend. Implementations:
/// - MemoryChunkStore: in-memory (browser, testing)
/// - FileChunkStore: filesystem-based (desktop)
/// - IndexedDbChunkStore: browser IndexedDB (persistent browser storage)
///
/// Follows the abstract-chunk-store pattern from the Node.js ecosystem.
/// Each chunk has a fixed size (piece length) and is addressed by index.
/// </summary>
public interface IChunkStore : IAsyncDisposable
{
    /// <summary>Fixed chunk size in bytes (torrent piece length).</summary>
    int ChunkLength { get; }

    /// <summary>Store a chunk at the given index.</summary>
    Task PutAsync(int index, ReadOnlyMemory<byte> data, CancellationToken ct = default);

    /// <summary>Retrieve a chunk by index. Returns null if not stored.</summary>
    Task<byte[]?> GetAsync(int index, CancellationToken ct = default);

    /// <summary>Retrieve a portion of a chunk (offset + length within the chunk).</summary>
    Task<byte[]?> GetAsync(int index, int offset, int length, CancellationToken ct = default);

    /// <summary>Remove a chunk from storage.</summary>
    Task RemoveAsync(int index, CancellationToken ct = default);

    /// <summary>Remove all stored chunks.</summary>
    Task ClearAsync(CancellationToken ct = default);
}

/// <summary>
/// Simple in-memory chunk store. Fast, no persistence.
/// </summary>
public class MemoryChunkStore : IChunkStore
{
    private readonly Dictionary<int, byte[]> _chunks = new();
    public int ChunkLength { get; }

    public MemoryChunkStore(int chunkLength)
    {
        ChunkLength = chunkLength;
    }

    public Task PutAsync(int index, ReadOnlyMemory<byte> data, CancellationToken ct = default)
    {
        _chunks[index] = data.ToArray();
        return Task.CompletedTask;
    }

    public Task<byte[]?> GetAsync(int index, CancellationToken ct = default)
    {
        return Task.FromResult(_chunks.TryGetValue(index, out var data) ? data : null);
    }

    public Task<byte[]?> GetAsync(int index, int offset, int length, CancellationToken ct = default)
    {
        if (!_chunks.TryGetValue(index, out var data)) return Task.FromResult<byte[]?>(null);
        if (offset + length > data.Length) return Task.FromResult<byte[]?>(null);
        var result = new byte[length];
        Array.Copy(data, offset, result, 0, length);
        return Task.FromResult<byte[]?>(result);
    }

    public Task RemoveAsync(int index, CancellationToken ct = default)
    {
        _chunks.Remove(index);
        return Task.CompletedTask;
    }

    public Task ClearAsync(CancellationToken ct = default)
    {
        _chunks.Clear();
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        _chunks.Clear();
        return ValueTask.CompletedTask;
    }
}
