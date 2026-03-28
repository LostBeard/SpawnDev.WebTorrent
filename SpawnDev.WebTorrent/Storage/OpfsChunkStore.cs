using SpawnDev.AsyncFileSystem;

namespace SpawnDev.WebTorrent.Storage;

/// <summary>
/// Persistent chunk store backed by SpawnDev.AsyncFileSystem.
/// In browser: uses OPFS (Origin Private File System) — survives page reloads.
/// On desktop: uses native file system.
///
/// Each piece is stored as a file: {basePath}/piece_{index}
/// </summary>
public class AsyncFSChunkStore : IChunkStore
{
    private readonly IAsyncFS _fs;
    private readonly string _basePath;
    private bool _initialized;

    public int ChunkLength { get; }

    /// <summary>
    /// Create a persistent chunk store.
    /// </summary>
    /// <param name="fs">The async file system (OPFS in browser, native on desktop).</param>
    /// <param name="basePath">Directory path for this torrent's pieces.</param>
    /// <param name="chunkLength">Standard piece length in bytes.</param>
    public AsyncFSChunkStore(IAsyncFS fs, string basePath, int chunkLength)
    {
        _fs = fs;
        _basePath = basePath;
        ChunkLength = chunkLength;
    }

    private async Task EnsureInitializedAsync()
    {
        if (_initialized) return;
        _initialized = true;
        if (!await _fs.DirectoryExists(_basePath))
            await _fs.CreateDirectory(_basePath);
    }

    public async Task PutAsync(int index, ReadOnlyMemory<byte> data, CancellationToken ct = default)
    {
        await EnsureInitializedAsync();
        await _fs.Write($"{_basePath}/piece_{index}", data.ToArray());
    }

    public async Task<byte[]?> GetAsync(int index, CancellationToken ct = default)
    {
        await EnsureInitializedAsync();
        var path = $"{_basePath}/piece_{index}";
        if (!await _fs.FileExists(path)) return null;
        return await _fs.ReadBytes(path);
    }

    public async Task<byte[]?> GetAsync(int index, int offset, int length, CancellationToken ct = default)
    {
        var full = await GetAsync(index, ct);
        if (full == null) return null;
        if (offset == 0 && length == full.Length) return full;
        int actualLen = Math.Min(length, full.Length - offset);
        if (actualLen <= 0) return null;
        var result = new byte[actualLen];
        Array.Copy(full, offset, result, 0, actualLen);
        return result;
    }

    public async Task RemoveAsync(int index, CancellationToken ct = default)
    {
        await EnsureInitializedAsync();
        var path = $"{_basePath}/piece_{index}";
        if (await _fs.FileExists(path))
            await _fs.Remove(path);
    }

    public async Task ClearAsync(CancellationToken ct = default)
    {
        if (await _fs.DirectoryExists(_basePath))
            await _fs.Remove(_basePath, recursive: true);
        await _fs.CreateDirectory(_basePath);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
