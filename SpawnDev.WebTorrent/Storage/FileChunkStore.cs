namespace SpawnDev.WebTorrent.Storage;

/// <summary>
/// File-based chunk store for desktop .NET.
/// Stores each piece as a separate file: {baseDir}/piece_{index:D6}.bin
/// Suitable for persistent torrent storage on disk.
///
/// For browser (Blazor WASM), use an IAsyncFS-backed implementation
/// (SpawnDev.BlazorJS.AsyncFileSystem) or MemoryChunkStore.
/// </summary>
public class FileChunkStore : IChunkStore
{
    private readonly string _baseDir;
    public int ChunkLength { get; }

    public FileChunkStore(string baseDir, int chunkLength)
    {
        _baseDir = baseDir;
        ChunkLength = chunkLength;
        Directory.CreateDirectory(baseDir);
    }

    public async Task PutAsync(int index, ReadOnlyMemory<byte> data, CancellationToken ct = default)
    {
        var path = GetPath(index);
        await File.WriteAllBytesAsync(path, data.ToArray(), ct);
    }

    public async Task<byte[]?> GetAsync(int index, CancellationToken ct = default)
    {
        var path = GetPath(index);
        if (!File.Exists(path)) return null;
        return await File.ReadAllBytesAsync(path, ct);
    }

    public async Task<byte[]?> GetAsync(int index, int offset, int length, CancellationToken ct = default)
    {
        var full = await GetAsync(index, ct);
        if (full == null) return null;
        if (offset + length > full.Length) return null;
        var result = new byte[length];
        Array.Copy(full, offset, result, 0, length);
        return result;
    }

    public Task RemoveAsync(int index, CancellationToken ct = default)
    {
        var path = GetPath(index);
        if (File.Exists(path)) File.Delete(path);
        return Task.CompletedTask;
    }

    public Task ClearAsync(CancellationToken ct = default)
    {
        if (Directory.Exists(_baseDir))
        {
            foreach (var file in Directory.GetFiles(_baseDir, "piece_*.bin"))
                File.Delete(file);
        }
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        // Files persist — nothing to clean up
        return ValueTask.CompletedTask;
    }

    private string GetPath(int index) => Path.Combine(_baseDir, $"piece_{index:D6}.bin");
}
