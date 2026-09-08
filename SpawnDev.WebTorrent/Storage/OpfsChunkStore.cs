using SpawnDev.AsyncFileSystem;
using SpawnDev.SpawnJS.JSObjects;
using SpawnDev.SpawnJS.Toolbox;

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
    private readonly Dictionary<int, SpawnDev.SpawnJS.JSObjects.File> _fileCache = new();
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
    /// memory. Gets the OPFS file as a <see cref="SpawnDev.SpawnJS.JSObjects.File"/> (a Blob — just a handle) and
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
    // TEST-ONLY fault injection for the storage-quota runaway regression (2026-07-04). When armed, PutUint8ArrayAsync
    // throws a browser-shaped QuotaExceededError instead of writing, so a unit test can drive the REAL download loop
    // into the store-failure path and prove it PAUSES (quota) / backs-off-then-recovers (transient) instead of
    // hot-looping the same piece forever. Static so a test can arm it without a handle to the internal store; a test
    // MUST reset both in a finally. Inert (single bool check) in production.
    internal static bool TestThrowQuotaForever;
    internal static int TestThrowTransientOnPutCount;
    private const string QuotaMessage = "The operation failed because it would cause the application to exceed its storage quota.";

    public async Task PutUint8ArrayAsync(int index, Uint8Array data, CancellationToken ct = default)
    {
        if (_browserFs == null)
            throw new InvalidOperationException("PutUint8ArrayAsync requires a browser file system (OPFS).");
        if (TestThrowQuotaForever)
            throw new InvalidOperationException(QuotaMessage);
        if (TestThrowTransientOnPutCount > 0)
        {
            TestThrowTransientOnPutCount--;
            throw new IOException($"[test] transient store fault for piece {index}");   // non-quota → backoff+retry path
        }
        await EnsureInitializedAsync();
        var piecePath = $"{_basePath}/piece_{index}";
        var expected = data.Length;
        await _browserFs.Write(piecePath, (TypedArray)data);
        if (_cachedIndex == index) { _cachedIndex = -1; _cachedFull = null; }   // invalidate stale read cache
        InvalidateFileCache(index);                                             // the piece changed — drop its cached File handle

        // 🔴 VERIFY THE SIZE THAT LANDED. This is the path the browser download loop actually uses, so it is
        // the one that matters. There is no atomic rename on OPFS, so an interrupted or partial write leaves
        // a piece file that EXISTS but is short - classically zero bytes. Restore's presence check then
        // accepts it, the bitfield claims the piece, and the read fails much later and far away with
        // "marked as verified but data not in store". An ABSENT piece is handled correctly (re-fetch); a
        // short one is a lie. Metadata only - one File handle, no bytes read, no JS->.NET copy.
        //
        // Thrown as IOException on purpose: the download loop treats that as TRANSIENT and backs off + retries
        // the piece, which is exactly right for a write that did not land. (Quota is a different, non-IO
        // exception and still pauses.)
        using var verify = await _browserFs.ReadFile(piecePath);
        if (verify == null || verify.Size != expected)
        {
            var got = verify == null ? "no handle" : $"{verify.Size} bytes";
            try { await _fs.Remove(piecePath); } catch { }
            InvalidateFileCache(index);
            throw new IOException(
                $"Piece {index} did not persist: wrote {expected} bytes to {piecePath}, file holds {got}. "
                + "The partial file has been removed so the piece is re-fetched rather than trusted.");
        }
    }

    private async Task EnsureInitializedAsync()
    {
        if (_initialized) return;
        _initialized = true;
        if (!await _fs.DirectoryExists(_basePath))
            await _fs.CreateDirectory(_basePath);
    }

    /// <summary>
    /// Get the OPFS <see cref="SpawnDev.SpawnJS.JSObjects.File"/> (Blob handle) for a piece, cached so consecutive
    /// slice reads of the same piece don't each pay a getFile. Returns null if the piece file doesn't exist.
    /// The returned File is owned by the cache — callers slice it but must NOT dispose it.
    /// </summary>
    private async Task<SpawnDev.SpawnJS.JSObjects.File?> GetPieceFileAsync(int index)
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
        using var uint8ArrayCopy = HeapView.CreateCopy(data);
        await EnsureInitializedAsync();
        var piecePath = $"{_basePath}/piece_{index}";
        await _browserFs.Write(piecePath, (TypedArray)uint8ArrayCopy);
        if (_cachedIndex == index) { _cachedIndex = -1; _cachedFull = null; }   // invalidate stale read cache
        InvalidateFileCache(index);                                             // the piece changed — drop its cached File handle

        // 🔴 VERIFY THE SIZE THAT LANDED, and remove the file if it is wrong.
        //
        // There is no atomic rename here, so an interrupted or failed write can leave a piece file that
        // EXISTS but holds fewer bytes than we wrote - classically zero. That file then satisfies restore's
        // presence check, the bitfield claims the piece, and the read fails much later with
        // "marked as verified but data not in store". An ABSENT piece is handled correctly (re-fetch); a
        // short one is a lie. Metadata only - one File handle, no bytes read, no JS->.NET copy - so this
        // stays cheap for a multi-GB model.
        using var verify = await _browserFs.ReadFile(piecePath);
        if (verify == null || verify.Size != data.Length)
        {
            var got = verify == null ? "no handle" : $"{verify.Size} bytes";
            try { await _fs.Remove(piecePath); } catch { }
            InvalidateFileCache(index);
            throw new IOException(
                $"Piece {index} did not persist: wrote {data.Length} bytes to {piecePath}, file holds {got}. "
                + "The partial file has been removed so the piece is re-fetched rather than trusted.");
        }
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
        // 🔴 EXISTENCE IS NOT READABILITY. The read path returns null when the piece file has NO BYTES
        // (GetAsync computes `file.Size - offset <= 0`), so a zero-length piece file passes a bare
        // FileExists and then makes the restored bitfield LIE - the torrent believes it holds a piece it
        // cannot serve, and the failure surfaces far away as "marked as verified but data not in store".
        // Checking Size is still metadata-only - no byte read, no JS->.NET copy - and it warms the very
        // File handle the following read uses, so it costs nothing.
        // ⚠️ A TRANSIENT handle, deliberately NOT GetPieceFileAsync. An OPFS File is a SNAPSHOT of the entry
        // at the moment it was opened, so caching one here - during restore, for every piece - hands the
        // later read a handle that may already be stale, and the read then throws NotFoundError instead of
        // returning a piece that is perfectly present. Restore must observe the store, not mutate its cache.
        var piecePath = $"{_basePath}/piece_{index}";
        if (!await _fs.FileExists(piecePath)) return false;
        if (_browserFs == null) return true;
        using var probe = await _browserFs.ReadFile(piecePath);
        return probe != null && probe.Size > 0;
    }

    /// <summary>
    /// Why a piece is or is not readable, for an error message. Metadata only.
    /// </summary>
    /// <remarks>
    /// A "shouldn't happen" throw that cannot say what it found is a dead end for whoever hits it. This
    /// turns "data not in store" into the actual state: no file, an empty file, or a file of N bytes that
    /// the requested range fell outside of.
    /// </remarks>
    public async Task<string> DescribePieceAsync(int index, CancellationToken ct = default)
    {
        try
        {
            await EnsureInitializedAsync();
            var path = $"{_basePath}/piece_{index}";
            if (!await _fs.FileExists(path))
            {
                // ⚠️ SAY WHAT *IS* THERE. "No file" alone cannot tell "nothing was ever written" from
                // "written under a different name or key", and those have completely different fixes.
                string siblings;
                try
                {
                    var files = (await _fs.GetFiles(_basePath)).ToList();
                    siblings = files.Count == 0
                        ? "the store directory is EMPTY - nothing was ever written here"
                        : $"{files.Count} file(s) present: {string.Join(", ", files.Take(8))}";
                }
                catch (Exception ex) { siblings = $"could not list {_basePath}: {ex.Message}"; }
                return $"no file at {path}; {siblings}";
            }
            if (_browserFs == null) return $"file exists at {path} (size unknown on this file system)";
            var file = await GetPieceFileAsync(index);
            if (file == null) return $"file exists at {path} but no handle could be opened";
            return file.Size == 0
                ? $"file at {path} is EMPTY (0 bytes) - it was created but never written"
                : $"file at {path} is {file.Size} bytes";
        }
        catch (Exception ex) { return $"could not describe piece {index}: {ex.GetType().Name}: {ex.Message}"; }
    }

    public async Task<byte[]?> GetAsync(int index, int offset, int length, CancellationToken ct = default)
    {
        // Browser/OPFS: slice the file so only the requested range is read from disk — never the whole
        // piece. A streaming parser reads a 4 MB piece in many small chunks; reading the whole piece per
        // chunk amplified OPFS reads 16-64x (the band-aid below was a memory cache for exactly that).
        if (_browserFs != null)
        {
            await EnsureInitializedAsync();

            // 🔴 A CACHED OPFS File IS A SNAPSHOT, AND A CONCURRENT WRITER INVALIDATES IT.
            //
            // _browserFs.Write TRUNCATES before it writes, so while any other holder of this store rewrites
            // piece N there is a window where the entry is empty or the snapshot no longer resolves - and
            // reading the Blob then throws NotFoundError ("A requested file or directory could not be found")
            // rather than returning short. MEASURED 2026-09-08: that is one of three failure modes
            // WebTorrent_OpfsReloadPersistence produced on an UNCHANGED library, and the reload case - a second
            // client over the same OPFS store while the first is still live - hits it directly.
            //
            // A stale snapshot is RECOVERABLE: drop the cached handle, re-open, read again. Bounded at one
            // retry so a genuinely missing piece still fails fast and returns null to the caller's
            // "data not in store" path instead of spinning.
            for (int attempt = 0; ; attempt++)
            {
                var file = await GetPieceFileAsync(index);
                if (file == null) return null;
                try
                {
                    long actualLength = Math.Min(length, file.Size - offset);
                    if (actualLength <= 0)
                    {
                        if (attempt == 0) { InvalidateFileCache(index); continue; }   // mid-truncate: re-open once
                        return null;
                    }
                    using var slice = file.Slice(offset, offset + actualLength);
                    using var ab = await slice.ArrayBuffer();
                    return ab.ReadBytes();
                }
                catch when (attempt == 0)
                {
                    InvalidateFileCache(index);   // stale snapshot - re-open the handle and try once more
                }
            }
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
