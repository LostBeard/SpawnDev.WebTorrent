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

    // ── Sync-access read handles (the fast path) ────────────────────────────────────────────────────
    // 🔴 WHY THIS EXISTS. Reading a range via getFile()+slice()+arrayBuffer() measured 23 MB/s cold on
    // OPFS-cached model weights, with getFile alone costing ~40 ms per piece - so loading a 2.4 GB model
    // spent 130 s in this store while the GPU consumed the same bytes at 9.5 GB/s. A sync access handle
    // is opened ONCE per piece and reads a range straight into a JS buffer.
    //
    // ⚠️ IT TAKES AN EXCLUSIVE LOCK on the file, so a handle must be closed before that piece is written
    // and on eviction. It is also worker-only; outside a worker CreateSyncAccessHandle throws, which is
    // why the Blob path is kept and used as the fallback rather than removed.
    private readonly Dictionary<int, SpawnDev.SpawnJS.JSObjects.FileSystemSyncAccessHandle> _syncHandles = new();
    private readonly Queue<int> _syncOrder = new();
    private const int SyncHandleMax = 8;
    /// <summary>Set once a sync handle cannot be created, so the cost is paid at most once per store.</summary>
    private bool _syncUnavailable;

    /// <summary>
    /// The piece directory, resolved ONCE. Looking a piece up by full path re-walks it every time.
    /// </summary>
    /// <remarks>
    /// 🔴 MEASURED: resolving "{_basePath}/piece_N" from the root cost 178 ms per call on a 2.4 GB model
    /// (681 opens = 121.5 s), while createSyncAccessHandle on the resolved handle cost 1.6 ms. Path
    /// resolution splits the path and makes a separate async OPFS call per segment, so every piece paid
    /// the whole walk. Holding the directory turns that into one lookup on an already-open handle.
    /// </remarks>
    private SpawnDev.SpawnJS.JSObjects.FileSystemDirectoryHandle? _pieceDir;
    private bool _pieceDirResolved;

    /// <summary>Open (or reuse) a sync access handle for a piece, or null when unavailable.</summary>
    private async Task<SpawnDev.SpawnJS.JSObjects.FileSystemSyncAccessHandle?> GetSyncHandleAsync(int index)
    {
        if (_syncUnavailable || _browserFs == null) return null;
        if (_syncHandles.TryGetValue(index, out var open)) return open;
        try
        {
            long tOpen = TraceReadTiming ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
            // Resolve the piece DIRECTORY once, then look the file up on it - one OPFS call per piece
            // instead of a full path walk. See _pieceDir.
            if (!_pieceDirResolved)
            {
                _pieceDirResolved = true;
                try { _pieceDir = await _browserFs.GetDirectoryHandle(_basePath); }
                catch { _pieceDir = null; }
            }
            SpawnDev.SpawnJS.JSObjects.FileSystemFileHandle? handle;
            if (_pieceDir != null)
            {
                try { handle = await _pieceDir.GetFileHandle($"piece_{index}", false); }
                catch { handle = null; }
            }
            else handle = await _browserFs.GetFileHandle($"{_basePath}/piece_{index}");
            if (TraceReadTiming) SyncResolveMs += System.Diagnostics.Stopwatch.GetElapsedTime(tOpen).TotalMilliseconds;
            if (handle == null) return null;
            long tCreate = TraceReadTiming ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
            var sync = await handle.CreateSyncAccessHandle();
            if (TraceReadTiming) SyncCreateMs += System.Diagnostics.Stopwatch.GetElapsedTime(tCreate).TotalMilliseconds;
            handle.Dispose();
            if (TraceReadTiming)
            {
                SyncOpenMs += System.Diagnostics.Stopwatch.GetElapsedTime(tOpen).TotalMilliseconds;
                SyncOpens++;
            }
            if (sync == null) return null;

            _syncHandles[index] = sync;
            _syncOrder.Enqueue(index);
            while (_syncOrder.Count > SyncHandleMax)
            {
                var evict = _syncOrder.Dequeue();
                if (evict != index) CloseSyncHandle(evict);
            }
            return sync;
        }
        catch
        {
            // Not a worker, the file is locked, or the browser lacks it. Fall back permanently rather
            // than paying a failed open on every read.
            _syncUnavailable = true;
            return null;
        }
    }

    /// <summary>Close and forget the sync handle for a piece, releasing its exclusive lock.</summary>
    private void CloseSyncHandle(int index)
    {
        if (!_syncHandles.Remove(index, out var h)) return;
        try { h.Close(); } catch { /* already closed or the file is gone */ }
        try { h.Dispose(); } catch { }
    }

    /// <summary>Close every open sync handle - required before writes and on teardown.</summary>
    private void CloseAllSyncHandles()
    {
        foreach (var idx in _syncHandles.Keys.ToList()) CloseSyncHandle(idx);
        _syncOrder.Clear();
        try { _pieceDir?.Dispose(); } catch { }
        _pieceDir = null;
        _pieceDirResolved = false;
    }
    private readonly Queue<int> _fileCacheOrder = new();
    private const int FileCacheMax = 4;

    public int ChunkLength { get; }

    /// <summary>
    /// Pieces this store has successfully written and verified since construction. Zero on a store whose
    /// torrent claims completed pieces means the download path set bitfield bits WITHOUT ever storing them -
    /// a distinction that "no file at &lt;path&gt;" alone cannot make, and one that sent a real investigation
    /// looking for a lost file that had never been written.
    /// </summary>
    public int PiecesWritten { get; private set; }

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
    /// <summary>
    /// Set true to time the pieces of a ranged read. Off by default; the counters below are only
    /// meaningful while it is on.
    /// </summary>
    /// <remarks>
    /// 🔴 ADDED TO FIND WHERE A MODEL LOAD GOES. Reading a 6.87 GB GGUF through this store measured
    /// 20.7 MB/s while the GPU accepted the same bytes at 9.8 GB/s, so ~99.8% of a model load is this
    /// read path. Reading the SAME piece files directly through IAsyncFS measures 707 MB/s, so the gap
    /// is in how a range is fetched here, not in OPFS itself. These counters say which call it is.
    /// </remarks>
    public static bool TraceReadTiming { get; set; }

    /// <summary>Milliseconds spent obtaining the piece's File handle (cache hit or getFile).</summary>
    public static double ReadHandleMs;
    /// <summary>Milliseconds spent in Blob.slice() - expected to be near zero (it is lazy).</summary>
    public static double ReadSliceMs;
    /// <summary>Milliseconds spent in Blob.arrayBuffer() - the call that actually reads bytes.</summary>
    public static double ReadArrayBufferMs;
    /// <summary>Milliseconds spent wrapping the ArrayBuffer as a Uint8Array.</summary>
    public static double ReadWrapMs;
    /// <summary>Bytes returned, and how many ranged reads were made.</summary>
    public static long ReadBytes;
    /// <summary>Number of ranged reads.</summary>
    public static long ReadCalls;
    /// <summary>How many of those reads had to fetch a File handle rather than reuse a cached one.</summary>
    public static long ReadHandleMisses;
    /// <summary>Milliseconds spent in sync-access-handle reads (the fast path).</summary>
    public static double ReadSyncMs;
    /// <summary>How many reads took the sync-access fast path.</summary>
    public static long ReadSyncCalls;
    /// <summary>Milliseconds spent OPENING sync access handles.</summary>
    public static double SyncOpenMs;
    /// <summary>How many sync access handles were opened.</summary>
    public static long SyncOpens;
    /// <summary>Milliseconds resolving the path to a FileSystemFileHandle.</summary>
    public static double SyncResolveMs;
    /// <summary>Milliseconds in createSyncAccessHandle() itself.</summary>
    public static double SyncCreateMs;

    /// <summary>Zero the ranged-read counters.</summary>
    public static void ResetReadTiming()
    {
        ReadHandleMs = ReadSliceMs = ReadArrayBufferMs = ReadWrapMs = ReadSyncMs = 0;
        ReadBytes = ReadCalls = ReadHandleMisses = ReadSyncCalls = SyncOpens = 0;
        SyncOpenMs = SyncResolveMs = SyncCreateMs = 0;
    }

    public async Task<Uint8Array?> GetUint8ArrayAsync(int index, int offset, int length, CancellationToken ct = default)
    {
        if (_browserFs == null) return null;
        await EnsureInitializedAsync();
        bool trace = TraceReadTiming;

        // ── FAST PATH FIRST, and it must not call getFile() at all ──────────────────────────────────
        // 🔴 MEASURED THE HARD WAY. Trying the sync handle AFTER GetPieceFileAsync kept the fast read but
        // still paid the Blob open - and made it FOUR TIMES WORSE (40 ms -> 175 ms per open, 723 opens =
        // 127 s), because a sync access handle holds an EXCLUSIVE LOCK and getFile() on the same file then
        // contends with it. The two must never both be used for a piece. GetSize() gives the length the
        // Blob was only being opened to provide.
        var syncHandle = await GetSyncHandleAsync(index);
        if (syncHandle != null)
        {
            long tF = trace ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
            long size;
            try { size = syncHandle.GetSize(); }
            catch { CloseSyncHandle(index); size = -1; }
            if (size >= 0)
            {
                long want = Math.Min(length, size - offset);
                if (want <= 0) return null;
                var fast = new Uint8Array((int)want);
                try
                {
                    var read = syncHandle.Read(fast, new SpawnDev.SpawnJS.JSObjects.FileSystemSyncReadWriteOptions { At = offset });
                    if (read == want)
                    {
                        if (trace)
                        {
                            ReadSyncMs += System.Diagnostics.Stopwatch.GetElapsedTime(tF).TotalMilliseconds;
                            ReadBytes += want;
                            ReadCalls++;
                            ReadSyncCalls++;
                        }
                        return fast;
                    }
                    fast.Dispose();   // short read - fall through to the Blob path rather than lie
                }
                catch
                {
                    fast.Dispose();
                    CloseSyncHandle(index);
                    _syncUnavailable = true;
                }
            }
        }

        // ── FALLBACK: getFile + slice + arrayBuffer (no worker, locked file, or an unexpected short read)
        long t0 = trace ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
        bool cached = trace && _fileCache.ContainsKey(index);
        var file = await GetPieceFileAsync(index);                 // cached File (Blob) handle — NOT the data
        if (trace)
        {
            ReadHandleMs += System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
            if (!cached) ReadHandleMisses++;
        }
        if (file == null) return null;
        long actualLen = Math.Min(length, file.Size - offset);
        // Return null (NOT an empty Uint8Array) for an out-of-range / short read, matching the byte[]
        // GetAsync(index, offset, length) sibling. An empty-but-non-null slice made the zero-copy read loop
        // (Torrent.ReadFileUint8ArrayAsync) advance 0 bytes and spin FOREVER (got==0 → resultPos stuck);
        // returning null routes it to its fail-loud "data not in store" throw instead of hanging.
        if (actualLen <= 0) return null;

        long tS = trace ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
        using var slice = file.Slice(offset, offset + actualLen);  // lazy Blob slice — no copy
        if (trace) ReadSliceMs += System.Diagnostics.Stopwatch.GetElapsedTime(tS).TotalMilliseconds;

        long tA = trace ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
        using var ab = await slice.ArrayBuffer();                  // reads ONLY this range from OPFS
        if (trace) ReadArrayBufferMs += System.Diagnostics.Stopwatch.GetElapsedTime(tA).TotalMilliseconds;

        long tW = trace ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
        var u8 = new Uint8Array(ab);
        if (trace)
        {
            ReadWrapMs += System.Diagnostics.Stopwatch.GetElapsedTime(tW).TotalMilliseconds;
            ReadBytes += actualLen;
            ReadCalls++;
        }
        return u8;
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
        // 🔴 RELEASE THE EXCLUSIVE LOCK FIRST. A sync access handle blocks any write to its file, so
        // closing it after the write would be too late - the write itself would fail.
        CloseSyncHandle(index);
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
        PiecesWritten++;
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
        CloseSyncHandle(index);
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
        PiecesWritten++;
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
                // ⚠️ ASK WHETHER THE DIRECTORY EXISTS BEFORE LISTING IT. GetFiles on a missing directory
                // throws, and the message that surfaced was a bare "Arg_NullReferenceException" - which reads
                // as a bug in this diagnostic rather than as the fact it was actually reporting: that NOTHING
                // was ever written under this key, not even the directory.
                string siblings;
                if (!await _fs.DirectoryExists(_basePath))
                {
                    siblings = $"the store directory {_basePath} DOES NOT EXIST - no piece was ever written "
                             + "under this key (the directory is created on the first write)";
                }
                else
                {
                    try
                    {
                        var files = (await _fs.GetFiles(_basePath)).ToList();
                        siblings = files.Count == 0
                            ? "the store directory is EMPTY - nothing was ever written here"
                            : $"{files.Count} file(s) present: {string.Join(", ", files.Take(8))}";
                    }
                    catch (Exception ex) { siblings = $"could not list {_basePath}: {ex.GetType().Name}: {ex.Message}"; }
                }
                return $"no file at {path}; {siblings}; this store has written {PiecesWritten} piece(s) since it was created";
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
        CloseAllSyncHandles();
        foreach (var f in _fileCache.Values) f.Dispose();
        _fileCache.Clear();
        _fileCacheOrder.Clear();
        if (await _fs.DirectoryExists(_basePath))
            await _fs.Remove(_basePath, recursive: true);
        await _fs.CreateDirectory(_basePath);
    }

    public ValueTask DisposeAsync()
    {
        CloseAllSyncHandles();
        foreach (var f in _fileCache.Values) f.Dispose();
        _fileCache.Clear();
        _fileCacheOrder.Clear();
        return ValueTask.CompletedTask;
    }
}
