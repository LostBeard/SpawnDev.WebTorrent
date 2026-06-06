using SpawnDev.AsyncFileSystem;
using SpawnDev.BlazorJS.JSObjects;

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
    private readonly IAsyncBrowserFileSystem? _browserFs;
    private readonly string _basePath;
    private bool _initialized;
    // Single-piece read cache. A streaming parser reads a piece in many small chunks; GetAsync(index,
    // offset, length) reads the WHOLE piece file each call. Caching the last whole-piece read makes
    // consecutive chunk-reads of the same piece slice from memory — one OPFS read per piece, not per chunk
    // (the SD-Turbo model load was re-reading each piece 16-64x from OPFS). Single reader per store
    // (sequential stream read), so no lock needed in WASM.
    private int _cachedIndex = -1;
    private byte[]? _cachedFull;

    public int ChunkLength { get; }

    /// <summary>Whether this store supports zero-copy Uint8Array reads (browser OPFS).</summary>
    public bool SupportsUint8Array => _browserFs != null;

    /// <summary>
    /// Create a persistent chunk store.
    /// </summary>
    /// <param name="fs">The async file system (OPFS in browser, native on desktop).</param>
    /// <param name="basePath">Directory path for this torrent's pieces.</param>
    /// <param name="chunkLength">Standard piece length in bytes.</param>
    public AsyncFSChunkStore(IAsyncFS fs, string basePath, int chunkLength)
    {
        _fs = fs;
        _browserFs = fs as IAsyncBrowserFileSystem;
        _basePath = basePath;
        ChunkLength = chunkLength;
    }

    /// <summary>
    /// Read a chunk as a JS Uint8Array without copying through .NET byte[].
    /// Only available when the backing FS is a browser file system (OPFS).
    /// The caller owns the returned Uint8Array and must dispose it.
    /// </summary>
    public async Task<Uint8Array?> GetUint8ArrayAsync(int index, CancellationToken ct = default)
    {
        if (_browserFs == null) return null;
        await EnsureInitializedAsync();
        var path = $"{_basePath}/piece_{index}";
        if (!await _fs.FileExists(path)) return null;
        return await _browserFs.ReadUint8Array(path);
    }

    /// <summary>
    /// Write a chunk from a JS <see cref="Uint8Array"/> to OPFS WITHOUT copying through a .NET byte[].
    /// The zero-copy download counterpart to <see cref="GetUint8ArrayAsync"/>: a web-seed piece fetched as a
    /// JS Uint8Array can be hashed (SubtleCrypto) and stored here, never entering the .NET heap. Only valid
    /// on a browser file system (OPFS). The caller owns/disposes the passed Uint8Array.
    /// </summary>
    public async Task PutUint8ArrayAsync(int index, Uint8Array data, CancellationToken ct = default)
    {
        if (_browserFs == null)
            throw new InvalidOperationException("PutUint8ArrayAsync requires a browser file system (OPFS).");
        await EnsureInitializedAsync();
        await _browserFs.Write($"{_basePath}/piece_{index}", (TypedArray)data);
        if (_cachedIndex == index) { _cachedIndex = -1; _cachedFull = null; }   // invalidate stale read cache
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
        if (_cachedIndex == index) { _cachedIndex = -1; _cachedFull = null; }   // invalidate stale read cache
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
        byte[]? full;
        if (_cachedIndex == index && _cachedFull != null) full = _cachedFull;        // slice from the cached piece — no OPFS re-read
        else { full = await GetAsync(index, ct); _cachedIndex = index; _cachedFull = full; }
        if (full == null) return null;
        if (offset == 0 && length == full.Length) return full;
        int actualLen = Math.Min(length, full.Length - offset);
        if (actualLen <= 0) return null;
        var result = new byte[actualLen];
        System.Array.Copy(full, offset, result, 0, actualLen);
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
