using SpawnDev.AsyncFileSystem;
using SpawnDev.BlazorJS.JSObjects;
using SpawnDev.BlazorJS.Toolbox;

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

    // Per-piece File-handle cache (browser/OPFS). A streaming read pulls a piece in many 64 KiB chunks;
    // ReadFile (getFile) re-navigates the OPFS handle each call, so caching the File — an immutable snapshot
    // of a COMPLETED piece — collapses N getFiles per piece into one. Small LRU so concurrent streams (a
    // player reading the front AND range-requesting the tail moov) don't evict each other every chunk.
    // Invalidated per-index on Put. Single-threaded WASM, so a lost interleave race just costs a redundant getFile.
    private readonly Dictionary<int, BlazorJS.JSObjects.File> _fileCache = new();
    private readonly Queue<int> _fileCacheOrder = new();
    private const int FileCacheMax = 4;

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
    /// Read a byte SLICE of a piece as a JS <see cref="Uint8Array"/> WITHOUT reading the whole piece into
    /// memory. Gets the OPFS file as a <see cref="BlazorJS.JSObjects.File"/> (a Blob — just a handle) and
    /// <c>slice()</c>s the requested range: the browser materializes ONLY that range from disk, and the
    /// bytes stay JS-side (zero-copy). This is what makes streaming a 64 KiB chunk out of a 4 MB piece cost
    /// 64 KiB, not 4 MB (the old GetUint8ArrayAsync(index) read the entire piece every chunk). Browser/OPFS
    /// only; the caller owns + disposes the returned Uint8Array.
    /// </summary>
    public async Task<Uint8Array?> GetUint8ArrayAsync(int index, int offset, int length, CancellationToken ct = default)
    {
        if (_browserFs == null) return null;
        await EnsureInitializedAsync();
        var file = await GetPieceFileAsync(index);                 // cached File (Blob) handle — NOT the data
        if (file == null) return null;
        long actualLen = Math.Min(length, file.Size - offset);
        // Return null (NOT an empty Uint8Array) for an out-of-range / short read, matching the byte[]
        // GetAsync(index, offset, length) sibling. An empty-but-non-null slice made the zero-copy read loop
        // (Torrent.ReadFileUint8ArrayAsync) advance 0 bytes and spin FOREVER (got==0 → resultPos stuck);
        // returning null routes it to its fail-loud "data not in store" throw instead of hanging.
        if (actualLen <= 0) return null;
        using var slice = file.Slice(offset, offset + actualLen);  // lazy Blob slice — no copy
        using var ab = await slice.ArrayBuffer();                  // reads ONLY this range from OPFS
        return new Uint8Array(ab);
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
        InvalidateFileCache(index);                                             // the piece changed — drop its cached File handle
    }

    private async Task EnsureInitializedAsync()
    {
        if (_initialized) return;
        _initialized = true;
        if (!await _fs.DirectoryExists(_basePath))
            await _fs.CreateDirectory(_basePath);
    }

    /// <summary>
    /// Get the OPFS <see cref="BlazorJS.JSObjects.File"/> (Blob handle) for a piece, cached so consecutive
    /// slice reads of the same piece don't each pay a getFile. Returns null if the piece file doesn't exist.
    /// The returned File is owned by the cache — callers slice it but must NOT dispose it.
    /// </summary>
    private async Task<BlazorJS.JSObjects.File?> GetPieceFileAsync(int index)
    {
        if (_fileCache.TryGetValue(index, out var hit)) return hit;
        var path = $"{_basePath}/piece_{index}";
        if (!await _fs.FileExists(path)) return null;
        var file = await _browserFs!.ReadFile(path);
        if (_fileCache.TryGetValue(index, out var raced)) { file.Dispose(); return raced; } // lost the race
        _fileCache[index] = file;
        _fileCacheOrder.Enqueue(index);
        while (_fileCacheOrder.Count > FileCacheMax)
        {
            var evict = _fileCacheOrder.Dequeue();
            if (evict != index && _fileCache.Remove(evict, out var old)) old.Dispose();
        }
        return file;
    }

    private void InvalidateFileCache(int index)
    {
        if (_fileCache.Remove(index, out var f)) f.Dispose();
    }

    public async Task PutAsync(int index, ReadOnlyMemory<byte> data, CancellationToken ct = default)
    {
        if (_browserFs == null)
            throw new InvalidOperationException("PutAsync requires a browser file system (OPFS).");
        using var heapView = HeapView.Create(data);
        using var uint8ArrayCopy = heapView.To<Uint8Array>();
        await EnsureInitializedAsync();
        await _browserFs.Write($"{_basePath}/piece_{index}", (TypedArray)uint8ArrayCopy);
        if (_cachedIndex == index) { _cachedIndex = -1; _cachedFull = null; }   // invalidate stale read cache
        InvalidateFileCache(index);                                             // the piece changed — drop its cached File handle
    }

    public async Task<byte[]?> GetAsync(int index, CancellationToken ct = default)
    {
        await EnsureInitializedAsync();
        var path = $"{_basePath}/piece_{index}";
        if (!await _fs.FileExists(path)) return null;
        return await _fs.ReadBytes(path);
    }

    /// <summary>
    /// True if a piece is already stored — a metadata-only existence check (one OPFS <c>FileExists</c>),
    /// NO whole-piece read and NO JS↔.NET byte copy. Restore-on-reload previously called
    /// <see cref="GetAsync(int, CancellationToken)"/> per piece JUST to test existence, dragging the ENTIRE
    /// model through the .NET heap on every page load (e.g. ~2.5 GB for SD-Turbo) and re-reading every
    /// cross-session piece file (the source of the OPFS re-access errors that forced a wipe + full
    /// re-download). Use this to populate the restored bitfield without touching the bytes.
    /// </summary>
    public async Task<bool> PieceExistsAsync(int index, CancellationToken ct = default)
    {
        await EnsureInitializedAsync();
        return await _fs.FileExists($"{_basePath}/piece_{index}");
    }

    public async Task<byte[]?> GetAsync(int index, int offset, int length, CancellationToken ct = default)
    {
        // Browser/OPFS: slice the file so only the requested range is read from disk — never the whole
        // piece. A streaming parser reads a 4 MB piece in many small chunks; reading the whole piece per
        // chunk amplified OPFS reads 16-64x (the band-aid below was a memory cache for exactly that).
        if (_browserFs != null)
        {
            await EnsureInitializedAsync();
            var file = await GetPieceFileAsync(index);
            if (file == null) return null;
            long actualLength = Math.Min(length, file.Size - offset);
            if (actualLength <= 0) return null;
            using var slice = file.Slice(offset, offset + actualLength);
            using var ab = await slice.ArrayBuffer();
            return ab.ReadBytes();
        }

        // Desktop / non-browser AsyncFS has no Blob/slice — read the whole piece (cached) then copy.
        byte[]? full;
        if (_cachedIndex == index && _cachedFull != null) full = _cachedFull;        // slice from the cached piece — no FS re-read
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
        InvalidateFileCache(index);
        await EnsureInitializedAsync();
        var path = $"{_basePath}/piece_{index}";
        if (await _fs.FileExists(path))
            await _fs.Remove(path);
    }

    public async Task ClearAsync(CancellationToken ct = default)
    {
        foreach (var f in _fileCache.Values) f.Dispose();
        _fileCache.Clear();
        _fileCacheOrder.Clear();
        if (await _fs.DirectoryExists(_basePath))
            await _fs.Remove(_basePath, recursive: true);
        await _fs.CreateDirectory(_basePath);
    }

    public ValueTask DisposeAsync()
    {
        foreach (var f in _fileCache.Values) f.Dispose();
        _fileCache.Clear();
        _fileCacheOrder.Clear();
        return ValueTask.CompletedTask;
    }
}
