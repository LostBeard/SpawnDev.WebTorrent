using SpawnDev.AsyncFileSystem;
using SpawnDev.SpawnJS.JSObjects;

namespace SpawnDev.WebTorrent.Storage;

/// <summary>
/// Persistent chunk store that writes a torrent's CONTENT FILES as actual files and folders, the way a
/// desktop torrent client does, instead of one file per piece.
/// </summary>
/// <remarks>
/// <para>
/// 🔴 WHY THIS EXISTS - MEASURED, not assumed. <see cref="AsyncFSChunkStore"/> stores every piece as its
/// own file, so a 2375 MB model is 681 of them and every read pays a per-file open. <c>OpfsLayoutProbe</c>
/// (dedicated worker, warm, 2026-09-09) put that at 22-33x the cost of the same bytes in one file:
/// </para>
/// <code>
///   128 x 65536 B: pieces  346 ms | one file  15 ms | 22.50x
///   681 x 65536 B: pieces 1733 ms | one file  66 ms | 26.18x
///  1362 x 65536 B: pieces 3336 ms | one file 100 ms | 33.27x
/// </code>
/// <para>
/// The per-piece tax is <c>createSyncAccessHandle</c> (1.66 ms) plus <c>getFileHandle</c> (0.46 ms) plus
/// <c>close</c> (0.28 ms). This store pays each of those ONCE PER CONTENT FILE for the life of the store,
/// which is why the one-file column above is flat while the piece column scales with piece count - and why
/// the ratio grows the bigger the model gets. The reads themselves were never the problem: 256 MB in 27 ms
/// (9.5 GB/s) at the real 4 MB piece size.
/// </para>
/// <para>
/// ⚠️ THE BITFIELD MUST BE PERSISTED HERE, and that is the one thing the piece layout got for free.
/// <c>WebTorrentClient.RestoreFromStorageAsync</c> rebuilds a restored torrent's bitfield by asking
/// whether each piece FILE exists. Under this layout a piece has no file of its own - its bytes are
/// interleaved into shared content files - so there is nothing to test and the bitfield is the only record
/// of what is actually present. See <see cref="LoadBitfieldAsync"/>.
/// </para>
/// <para>
/// ⚠️ ORDERING IS A CORRECTNESS PROPERTY. A piece's bytes are flushed to their content file BEFORE its bit
/// is set, and the bitfield is flushed after. A bitfield that under-reports costs a re-download; one that
/// over-reports makes the torrent advertise pieces it cannot serve, which is the failure
/// <c>SeedFromMetadataAsync</c> already documents. Only ever lose bits, never invent them.
/// </para>
/// <para>
/// ⚠️ Sync access handles need a DEDICATED worker (measured: a shared worker has no
/// <c>createSyncAccessHandle</c> at all). Where they are unavailable this falls back to
/// <c>createWritable</c> for writes and <c>getFile()+slice()</c> for reads - still one open per CONTENT
/// FILE rather than one per piece, so the layout wins there too, just by less.
/// </para>
/// </remarks>
public sealed class AsyncFSFileStore : IJSChunkStore
{
    private readonly IAsyncFS _fs;
    private readonly IAsyncBrowserFileSystem? _browserFs;
    private readonly string _basePath;
    private readonly TorrentFileInfo[] _files;
    private readonly long _totalLength;
    private readonly int _pieceCount;
    private bool _initialized;

    /// <summary>Open sync access handles, one per CONTENT FILE - the whole point of this store.</summary>
    private readonly Dictionary<string, FileSystemSyncAccessHandle> _syncHandles = new();
    /// <summary>Blob handles for the fallback read path, one per content file.</summary>
    private readonly Dictionary<string, SpawnDev.SpawnJS.JSObjects.File> _blobCache = new();
    /// <summary>Set once a sync handle cannot be created, so the failed open is paid at most once.</summary>
    private bool _syncUnavailable;

    /// <summary>
    /// Force the <c>createWritable</c> / Blob fallback even where sync access handles exist. Diagnostic.
    /// </summary>
    /// <remarks>
    /// 🔴 THIS EXISTS BECAUSE THE FALLBACK IS THE PATH WE CANNOT SEE. Sync access handles are
    /// DEDICATED-worker only, so the fallback runs precisely where a SHARED worker runs - and a shared
    /// worker's console never reaches the page. Two real defects lived there undetected (a file sized by
    /// how much had arrived rather than its true length, and a cached Blob snapshot going stale mid-read)
    /// while every gate passed, because every gate ran with <c>?worker=dedicated</c> to be able to read the
    /// logs at all. That is a gate selecting the configuration that hides the bug.
    /// <para>
    /// Setting this reproduces the fallback in a DEDICATED worker, where the console is visible and a
    /// Playwright gate can assert on it. Never set it in production: it gives up the sync path's speed.
    /// </para>
    /// </remarks>
    public static bool ForceWritableFallback;

    /// <summary>Which pieces this store holds. The only record - see the class remarks.</summary>
    private bool[] _bitfield = System.Array.Empty<bool>();
    private bool _bitfieldDirty;

    /// <inheritdoc/>
    public int ChunkLength { get; }

    /// <summary>Pieces written and flushed since construction. See <see cref="AsyncFSChunkStore"/>.</summary>
    public long PiecesStored { get; private set; }

    /// <summary>
    /// New store over a torrent's own files.
    /// </summary>
    /// <param name="fs">Backing filesystem (OPFS in the browser).</param>
    /// <param name="basePath">Store root. Content files land under <c>{basePath}/files/</c>.</param>
    /// <param name="chunkLength">Torrent piece length.</param>
    /// <param name="files">The torrent's files, with their offsets into the concatenated stream.</param>
    /// <param name="totalLength">Sum of every file's length.</param>
    public AsyncFSFileStore(IAsyncFS fs, string basePath, int chunkLength, TorrentFileInfo[] files, long totalLength)
    {
        // 🔴 BROWSER ONLY, ENFORCED. This store writes pieces at OFFSETS inside shared content files. The
        // generic IAsyncFS has no ranged write - `Write(path, bytes)` REPLACES the file - so a non-browser
        // filesystem here would silently destroy every piece already in a file each time a new one landed.
        // Failing at construction is the only honest option; the desktop equivalent is FileChunkStore.
        if (fs is not IAsyncBrowserFileSystem)
            throw new NotSupportedException(
                $"AsyncFSFileStore needs an IAsyncBrowserFileSystem (got {fs.GetType().Name}). It writes "
                + "pieces at offsets inside shared content files, and IAsyncFS.Write replaces a file rather "
                + "than writing a range - which would destroy the pieces already stored in it.");
        if (files == null || files.Length == 0)
            throw new ArgumentException(
                "AsyncFSFileStore writes a torrent's own files, so it cannot be built before the file list is "
                + "known. Construct it from metadata, not from a magnet with no info dictionary yet.",
                nameof(files));

        _fs = fs;
        _browserFs = fs as IAsyncBrowserFileSystem;
        _basePath = basePath.TrimEnd('/');
        ChunkLength = chunkLength;
        _files = files;
        _totalLength = totalLength;
        _pieceCount = chunkLength > 0 ? (int)((totalLength + chunkLength - 1) / chunkLength) : 0;
    }

    /// <summary>Where a content file lives, relative to the filesystem root.</summary>
    private string PathOf(TorrentFileInfo f)
    {
        // ⚠️ The torrent's own path, sanitised only enough to be safe. A torrent path is attacker-supplied:
        // "../" segments would escape the store, and an absolute path would leave it entirely.
        var raw = (f.Path ?? f.Name ?? "").Replace('\\', '/');
        var parts = raw.Split('/', StringSplitOptions.RemoveEmptyEntries)
                       .Where(p => p != "." && p != "..")
                       .ToArray();
        if (parts.Length == 0) parts = new[] { "file" };
        return $"{_basePath}/files/{string.Join('/', parts)}";
    }

    private async Task EnsureInitializedAsync()
    {
        if (_initialized) return;
        _initialized = true;

        if (_bitfield.Length != _pieceCount) _bitfield = new bool[_pieceCount];
        await LoadBitfieldAsync().ConfigureAwait(false);

        foreach (var f in _files)
        {
            var path = PathOf(f);
            var dir = path[..path.LastIndexOf('/')];
            if (!await _fs.DirectoryExists(dir).ConfigureAwait(false))
                await _fs.CreateDirectory(dir).ConfigureAwait(false);
        }
    }

    // ── Piece <-> file mapping ─────────────────────────────────────────────────────────────────────────
    // ⚠️ The same arithmetic WebConn already does to turn a piece request into per-file HTTP Range GETs
    // (WebConn.HandleRequestAsync). It is written once more here because there it produces URLs and here it
    // produces file handles; the SHAPE - walk Files[], intersect with the global range, translate to a
    // file-relative offset - is deliberately identical, so a bug found in one is findable in the other.

    /// <summary>Split a global byte range into per-file spans, in order.</summary>
    private IEnumerable<(TorrentFileInfo File, long FileOffset, int Length, int BufferOffset)>
        Spans(long globalOffset, int length)
    {
        long end = globalOffset + length - 1;
        int bufPos = 0;
        foreach (var f in _files)
        {
            long fStart = f.Offset;
            long fEnd = f.Offset + f.Length - 1;
            if (fStart > end || fEnd < globalOffset) continue;

            long overlapStart = Math.Max(fStart, globalOffset);
            long overlapEnd = Math.Min(fEnd, end);
            int span = (int)(overlapEnd - overlapStart + 1);
            yield return (f, overlapStart - fStart, span, bufPos);
            bufPos += span;
        }
    }

    /// <summary>Global byte offset of a piece, and how long that piece is.</summary>
    private (long Offset, int Length) PieceRange(int index)
    {
        long offset = (long)index * ChunkLength;
        int length = (int)Math.Min(ChunkLength, _totalLength - offset);
        return (offset, length);
    }

    // ── Handles ────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Open (or reuse) the sync access handle for one content file, or null when unavailable.</summary>
    private async Task<FileSystemSyncAccessHandle?> GetSyncHandleAsync(TorrentFileInfo f, bool create)
    {
        if (ForceWritableFallback || _syncUnavailable || _browserFs == null) return null;
        var path = PathOf(f);
        if (_syncHandles.TryGetValue(path, out var open)) return open;
        try
        {
            var handle = await _browserFs.GetFileHandle(path).ConfigureAwait(false);
            if (handle == null)
            {
                if (!create) return null;
                // Create it at its final length so a later ranged read of an unwritten region returns
                // zeros rather than a short read, and so GetSize() means what it says.
                await _fs.Write(path, System.Array.Empty<byte>()).ConfigureAwait(false);
                handle = await _browserFs.GetFileHandle(path).ConfigureAwait(false);
                if (handle == null) return null;
            }
            var sync = await handle.CreateSyncAccessHandle().ConfigureAwait(false);
            handle.Dispose();
            if (sync == null) return null;
            if (create && sync.GetSize() != f.Length) sync.Truncate(f.Length);
            _syncHandles[path] = sync;
            return sync;
        }
        catch (Exception ex)
        {
            // 🔴 SAY WHY, ONCE. The usual cause is not an error at all: createSyncAccessHandle() exists
            // only in a DEDICATED worker, so a shared-worker host takes the slower path by design. Silently
            // is exactly how that stayed invisible in AsyncFSChunkStore.
            _syncUnavailable = true;
            Console.WriteLine("[AsyncFSFileStore] sync access handles are unavailable here, falling back to "
                + "createWritable/getFile per content file. createSyncAccessHandle() requires a DEDICATED "
                + "worker. Reason: " + ex.Message);
            return null;
        }
    }

    /// <summary>Close the sync handle for one content file, releasing its exclusive lock.</summary>
    private void CloseSyncHandle(string path)
    {
        if (!_syncHandles.Remove(path, out var h)) return;
        try { h.Flush(); } catch { }
        try { h.Close(); } catch { }
        try { h.Dispose(); } catch { }
    }

    private void DropBlob(string path)
    {
        if (!_blobCache.Remove(path, out var b)) return;
        try { b.Dispose(); } catch { }
    }

    // ── IChunkStore ────────────────────────────────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public async Task PutAsync(int index, ReadOnlyMemory<byte> data, CancellationToken ct = default)
    {
        await EnsureInitializedAsync().ConfigureAwait(false);
        var (offset, length) = PieceRange(index);
        if (data.Length < length)
            throw new ArgumentException(
                $"piece {index} needs {length} bytes but only {data.Length} were supplied; storing it short "
                + "would set a bitfield bit for data that is not there.", nameof(data));

        foreach (var (file, fileOffset, span, bufPos) in Spans(offset, length))
        {
            ct.ThrowIfCancellationRequested();
            var path = PathOf(file);
            var sync = await GetSyncHandleAsync(file, create: true).ConfigureAwait(false);
            if (sync != null)
            {
                // Bulk bytes stay JS-side: one reusable JS buffer, no byte[] marshalling per write.
                using var buf = new Uint8Array(span);
                buf.WriteBytes(data.Slice(bufPos, span).ToArray());
                sync.Write(buf, new FileSystemSyncReadWriteOptions { At = fileOffset });
                sync.Flush();
            }
            else
            {
                await WriteViaWritableAsync(file, fileOffset, data.Slice(bufPos, span)).ConfigureAwait(false);
            }
            // 🔴 THE SNAPSHOT IS *NOT* DROPPED HERE, and that reversal is the difference between a model
            // loading in seconds and in ten minutes. Dropping eagerly on every write means that while the
            // torrent is still downloading, the next read must re-acquire the file with getFile() - which
            // is precisely the per-read cost this layout exists to remove (MEASURED 483 ms per piece under
            // the old piece layout against 0.6 ms once per content file). Warm, nothing notices, which is
            // why every gate passed. DOWNLOADING - the case that actually matters, because the demo loads
            // a model while its remaining pieces arrive - it turned a load into 626 SECONDS on a real run,
            // and hands-free simply timed out and went back to listening.
            //
            // ⚠️ SAFE BECAUSE OF HOW A SNAPSHOT FAILS, not because staleness is tolerable. The browser
            // INVALIDATES a File snapshot when its file changes, so a stale one throws rather than
            // returning old bytes - ReadViaBlobAsync catches exactly that and re-acquires once. And a
            // piece is written once and never rewritten, so a snapshot that is still valid cannot be
            // serving superseded data for a region; an unwritten region is refused by the bitfield before
            // any read reaches here.
        }

        // Bytes are flushed above BEFORE the bit is set - see the class remarks on ordering.
        MarkStored(index);
        if (_bitfieldDirty && PiecesStored % 64 == 0) await SaveBitfieldAsync().ConfigureAwait(false);
    }

    /// <summary>Fallback write for hosts with no sync access handles (not a dedicated worker).</summary>
    /// <remarks>
    /// ⚠️ Deliberately <c>keepExistingData: true</c>. Without it <c>createWritable</c> starts from an EMPTY
    /// file, so writing piece 500 of a torrent would destroy pieces 0-499. Slow, but it must be correct.
    /// </remarks>
    private async Task WriteViaWritableAsync(TorrentFileInfo file, long fileOffset, ReadOnlyMemory<byte> data)
    {
        using var buf = new Uint8Array(data.Length);
        buf.WriteBytes(data.ToArray());
        await WriteViewViaWritableAsync(file, fileOffset, buf).ConfigureAwait(false);
    }

    /// <summary>Fallback write from a JS buffer - no managed copy. See <see cref="WriteViaWritableAsync"/>.</summary>
    private async Task WriteViewViaWritableAsync(TorrentFileInfo file, long fileOffset, Uint8Array data)
    {
        var path = PathOf(file);
        var handle = await _browserFs!.GetFileHandle(path).ConfigureAwait(false);
        if (handle == null)
        {
            await _fs.Write(path, System.Array.Empty<byte>()).ConfigureAwait(false);
            handle = await _browserFs.GetFileHandle(path).ConfigureAwait(false);
            if (handle == null) throw new IOException($"could not create {path}");
        }
        try
        {
            var writable = await handle.CreateWritable(new FileSystemCreateWritableOptions { KeepExistingData = true })
                .ConfigureAwait(false);
            try
            {
                // 🔴 THE FILE IS GIVEN ITS FINAL LENGTH, exactly as the sync path does, and for a reason that
                // took a user-visible crash to find. GetSyncHandleAsync creates at full length "so GetSize()
                // means what it says"; this path did not, so in a SHARED worker - which has no sync access
                // handles at all, and is the default - a file's size was however many bytes had arrived so
                // far. Under the ContentFiles layout a consumer reads the content file DIRECTLY rather than
                // through the piece API, so a half-downloaded model is not detectably incomplete: it is
                // simply a shorter file.
                //
                // MEASURED on the demo: whisper-tiny's encoder_model.onnx sat in OPFS at 29,360,128 bytes -
                // EXACTLY 7 x 4 MiB, against a true length of 32,909,539. Seven of its eight pieces had
                // arrived. The ONNX parser read to the end of what was there and threw
                // `Unknown wire type: 6` - protobuf wire types stop at 5 - which surfaced to the user as
                // "Transcription failed: POST /api/transcribe -> 500". Nothing in that message points here.
                //
                // ⚠️ Truncate() GROWS as well as shrinks (it sets the length), and the region it adds reads
                // as zeros - so an unwritten span now reads as zeros at the right OFFSET instead of moving
                // every later byte, which is what made the size lie in the first place.
                // ⚠️ NO getFile() HERE. A FileSystemWritableFileStream holds a lock on the file, so asking
                // the same handle for a snapshot while it is open is a hazard in its own right - and the
                // query bought nothing: Truncate SETS the length, so calling it when the length is already
                // right is a no-op. Once per file per session is enough.
                if (_lengthEstablished.Add(path))
                    await writable.Truncate((ulong)file.Length).ConfigureAwait(false);
                await writable.Seek((ulong)fileOffset).ConfigureAwait(false);
                await writable.Write(data).ConfigureAwait(false);
            }
            finally { await writable.Close().ConfigureAwait(false); writable.Dispose(); }
        }
        finally { handle.Dispose(); }
    }

    /// <summary>Files whose final length has been established, so it is set once rather than per write.</summary>
    private readonly HashSet<string> _lengthEstablished = new();

    /// <summary>Files already reported as having gone stale mid-read, so it is said once, not per read.</summary>
    private readonly HashSet<string> _staleReported = new();

    /// <inheritdoc/>
    public bool SupportsUint8Array => _browserFs != null;

    /// <inheritdoc/>
    /// <remarks>
    /// ⚠️ THE DOWNLOAD HOT PATH. <c>Torrent.ProcessSpanPieceAsync</c> verifies a piece in JS (SubtleCrypto
    /// over a <see cref="Uint8Array"/>) and hands it straight here, so the bytes of a multi-GB model never
    /// enter the managed heap. The alternative branch is <c>PutAsync(p, pieceUa.ReadBytes())</c>, which
    /// would copy every 4 MB piece across - see <see cref="IJSChunkStore"/> for why this is an interface.
    /// </remarks>
    public async Task PutUint8ArrayAsync(int index, Uint8Array data, CancellationToken ct = default)
    {
        await EnsureInitializedAsync().ConfigureAwait(false);
        if (_browserFs == null)
            throw new InvalidOperationException("PutUint8ArrayAsync requires a browser file system (OPFS).");

        var (offset, length) = PieceRange(index);
        if (data.Length < length)
            throw new ArgumentException(
                $"piece {index} needs {length} bytes but the buffer holds {data.Length}; storing it short "
                + "would set a bitfield bit for data that is not there.", nameof(data));

        foreach (var (file, fileOffset, span, bufPos) in Spans(offset, length))
        {
            ct.ThrowIfCancellationRequested();
            var path = PathOf(file);
            // A view over the SAME buffer - no copy, and nothing to dispose but the view itself.
            using var view = data.SubArray(bufPos, bufPos + span);
            var sync = await GetSyncHandleAsync(file, create: true).ConfigureAwait(false);
            if (sync != null)
            {
                sync.Write(view, new FileSystemSyncReadWriteOptions { At = fileOffset });
                sync.Flush();
            }
            else
            {
                await WriteViewViaWritableAsync(file, fileOffset, view).ConfigureAwait(false);
            }
            // Not dropped - see the note on the byte[] write path above.
        }

        MarkStored(index);
        if (_bitfieldDirty && PiecesStored % 64 == 0) await SaveBitfieldAsync().ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<string> DescribePieceAsync(int index, CancellationToken ct = default)
    {
        try
        {
            await EnsureInitializedAsync().ConfigureAwait(false);
            if (index < 0 || index >= _pieceCount)
                return $"piece {index} is outside this torrent's {_pieceCount} pieces";
            if (!_bitfield[index])
                return $"the bitfield at {BitfieldPath} does not claim piece {index}";

            var (offset, length) = PieceRange(index);
            var parts = new List<string>();
            foreach (var (file, fileOffset, span, _) in Spans(offset, length))
            {
                var path = PathOf(file);
                long size = -1;
                try
                {
                    if (_browserFs != null)
                    {
                        using var blob = await _browserFs.ReadFile(path).ConfigureAwait(false);
                        size = blob?.Size ?? -1;
                    }
                }
                catch { size = -1; }
                parts.Add(size < 0
                    ? $"{path}: MISSING (needs [{fileOffset}, {fileOffset + span}) of {file.Length})"
                    : $"{path}: {size} bytes, needs [{fileOffset}, {fileOffset + span})"
                      + (size < fileOffset + span ? " - TOO SHORT" : ""));
            }
            return string.Join("; ", parts);
        }
        catch (Exception ex) { return $"could not inspect piece {index}: {ex.Message}"; }
    }

    /// <summary>Record that a piece is stored. Called only AFTER its bytes are flushed.</summary>
    private void MarkStored(int index)
    {
        if (index < 0 || index >= _bitfield.Length || _bitfield[index]) return;
        _bitfield[index] = true;
        _bitfieldDirty = true;
        PiecesStored++;
    }

    /// <inheritdoc/>
    public async Task<byte[]?> GetAsync(int index, CancellationToken ct = default)
    {
        var (_, length) = PieceRange(index);
        return await GetAsync(index, 0, length, ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<byte[]?> GetAsync(int index, int offset, int length, CancellationToken ct = default)
    {
        using var ua = await GetUint8ArrayAsync(index, offset, length, ct).ConfigureAwait(false);
        return ua?.ReadBytes();
    }

    /// <inheritdoc/>
    public async Task<Uint8Array?> GetUint8ArrayAsync(int index, CancellationToken ct = default)
    {
        var (_, length) = PieceRange(index);
        return await GetUint8ArrayAsync(index, 0, length, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Read part of a piece as a JS Uint8Array, without copying through the .NET heap.
    /// </summary>
    /// <remarks>
    /// ⚠️ THE HOT PATH. The model loader streams gigabytes through here, so the bytes must stay in JS end
    /// to end (global rule 4). The caller owns the returned array and must dispose it.
    /// </remarks>
    public async Task<Uint8Array?> GetUint8ArrayAsync(int index, int offset, int length, CancellationToken ct = default)
    {
        await EnsureInitializedAsync().ConfigureAwait(false);
        if (index < 0 || index >= _pieceCount) return null;
        if (!_bitfield[index]) return null;

        var (pieceOffset, pieceLength) = PieceRange(index);
        if (offset >= pieceLength) return null;
        int want = (int)Math.Min(length, pieceLength - offset);
        if (want <= 0) return null;

        var result = new Uint8Array(want);
        int filled = 0;
        foreach (var (file, fileOffset, span, bufPos) in Spans(pieceOffset + offset, want))
        {
            ct.ThrowIfCancellationRequested();
            var sync = await GetSyncHandleAsync(file, create: false).ConfigureAwait(false);
            if (sync != null)
            {
                using var chunk = new Uint8Array(span);
                var read = sync.Read(chunk, new FileSystemSyncReadWriteOptions { At = fileOffset });
                if (read != span) { result.Dispose(); return null; }
                result.Set(chunk, bufPos);
                filled += span;
                continue;
            }

            var slice = await ReadViaBlobAsync(file, fileOffset, span).ConfigureAwait(false);
            if (slice == null) { result.Dispose(); return null; }
            using (slice) result.Set(slice, bufPos);
            filled += span;
        }
        if (filled != want) { result.Dispose(); return null; }
        return result;
    }

    /// <summary>Fallback read: one cached Blob per CONTENT FILE, sliced per range.</summary>
    /// <remarks>
    /// ⚠️ Still a large win over the piece layout even though it is the slow path: the piece store paid
    /// ~40 ms of <c>getFile</c> per PIECE (681 of them on a 2375 MB model), where this pays it once per
    /// content file and slices the cached Blob thereafter.
    /// </remarks>
    private async Task<Uint8Array?> ReadViaBlobAsync(TorrentFileInfo file, long fileOffset, int length)
    {
        // ⚠️ No whole-file read here on purpose. Reading a multi-GB content file into a byte[] to serve a
        // 4 MB range is exactly the copy this store exists to avoid; the constructor guarantees _browserFs.
        var path = PathOf(file);

        // 🔴 A CACHED Blob GOES STALE THE MOMENT THE FILE IS WRITTEN, and this store's whole purpose is to
        // be read WHILE the torrent is still writing. `getFile()` returns a SNAPSHOT; once the underlying
        // file changes, every read from that snapshot throws
        //   "The requested file could not be read, typically due to permission problems that have occurred
        //    after a reference to a file was acquired"
        // - a NotReadableError, whose text names permissions and has nothing to do with them.
        //
        // `DropBlob` on the write side is not sufficient by itself: the read is a sequence of awaits
        // (Slice -> ArrayBuffer), so a write can land BETWEEN acquiring the snapshot and using it, and the
        // entry that gets dropped is the one the in-flight read is already holding. MEASURED: a model load
        // racing its own download failed this way with the demo reporting
        // "POST /api/transcribe -> 500".
        //
        // So: re-acquire once and retry. A stale snapshot is expected here, not exceptional - it means a
        // piece landed while we were reading, which is the normal case for a model that starts loading
        // before its download finishes.
        // 🔴 A SNAPSHOT IS ONLY CACHED ONCE THE FILE HAS STOPPED CHANGING. `getFile()` returns a snapshot
        // that the browser INVALIDATES the moment its file is written, and this store's whole purpose is to
        // be read WHILE the torrent writes. Caching one across writes therefore does not save a read - it
        // guarantees that every read throws, is caught, re-acquires and redoes the work. MEASURED on a real
        // shared-worker load: a continuous stream of "snapshot went stale mid-read", one per read, and
        // "[model-load] 626.0s parse 100%" - ten minutes, against 21 s for the same model on the sync path.
        //
        // ⚠️ AN EXCEPTION PER READ IS NOT A SLOW PATH, IT IS A BROKEN ONE. Acquiring the handle fresh costs
        // ~0.7 ms (MEASURED: 681 getFile calls in 468 ms), which is the price of a read, not a catastrophe.
        // Caching only pays once nothing can invalidate it.
        var stable = PiecesStored >= _pieceCount;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            if (!stable) DropBlob(path);
            if (!_blobCache.TryGetValue(path, out var blob))
            {
                blob = await _browserFs!.ReadFile(path).ConfigureAwait(false);
                if (blob == null) return null;
                // Only keep it when the file can no longer change under us.
                if (stable) _blobCache[path] = blob;
            }
            if (fileOffset + length > blob.Size)
            {
                // Short only because the snapshot predates a write that has since extended the file - a
                // fresh one may well cover the range. Never treat the first look as the final answer.
                if (attempt == 0) { DropBlob(path); continue; }
                return null;
            }
            try
            {
                using var sliced = blob.Slice(fileOffset, fileOffset + length);
                using var ab = await sliced.ArrayBuffer().ConfigureAwait(false);
                return new Uint8Array(ab);
            }
            catch (Exception ex) when (attempt == 0)
            {
                // ⚠️ Reported ONCE per file, not per read. While the file is still being written this can
                // fire on every read, and a message per read is how a diagnostic becomes the cost it is
                // meant to be measuring.
                if (_staleReported.Add(path))
                    Console.WriteLine("[filestore] " + path + ": snapshot went stale mid-read - a piece "
                        + "landed while reading; re-reading (" + ex.GetType().Name + "). Reported once per file.");
                DropBlob(path);
            }
            finally
            {
                // ⚠️ Only ours to dispose when it was NOT cached; a cached one is owned by _blobCache and
                // disposing it here would hand the next read a dead handle.
                if (!stable) { try { blob.Dispose(); } catch { } }
            }
        }
        return null;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// ⚠️ A piece cannot be individually removed under this layout - its bytes share files with its
    /// neighbours. Clearing the bit is the honest equivalent: the store stops claiming the piece and it is
    /// re-fetched and overwritten in place, which is what removing it was for.
    /// </remarks>
    public async Task RemoveAsync(int index, CancellationToken ct = default)
    {
        await EnsureInitializedAsync().ConfigureAwait(false);
        if (index < 0 || index >= _bitfield.Length || !_bitfield[index]) return;
        _bitfield[index] = false;
        _bitfieldDirty = true;
        await SaveBitfieldAsync().ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task ClearAsync(CancellationToken ct = default)
    {
        foreach (var path in _syncHandles.Keys.ToList()) CloseSyncHandle(path);
        foreach (var path in _blobCache.Keys.ToList()) DropBlob(path);
        try { if (await _fs.DirectoryExists(_basePath).ConfigureAwait(false)) await _fs.Remove(_basePath, recursive: true).ConfigureAwait(false); }
        catch { }
        _bitfield = new bool[_pieceCount];
        _bitfieldDirty = false;
        _initialized = false;
    }

    // ── Bitfield persistence ───────────────────────────────────────────────────────────────────────────

    private string BitfieldPath => $"{_basePath}/bitfield.bin";

    /// <summary>Which pieces this store holds, as restored from disk. Empty before initialisation.</summary>
    /// <remarks>
    /// 🔴 The piece layout gets this for free by testing whether each piece FILE exists. Here a piece has
    /// no file of its own, so this IS the record - and a wrong one is worse than none. See the class
    /// remarks on ordering.
    /// </remarks>
    public async Task<bool[]> LoadBitfieldAsync()
    {
        if (_bitfield.Length != _pieceCount) _bitfield = new bool[_pieceCount];
        try
        {
            if (!await _fs.FileExists(BitfieldPath).ConfigureAwait(false)) return _bitfield;
            var packed = await _fs.ReadBytes(BitfieldPath).ConfigureAwait(false);
            if (packed == null) return _bitfield;
            for (int i = 0; i < _pieceCount; i++)
            {
                int b = i >> 3;
                if (b >= packed.Length) break;
                _bitfield[i] = (packed[b] & (1 << (7 - (i & 7)))) != 0;
            }
        }
        catch (Exception ex)
        {
            // An unreadable bitfield means "we have nothing", never "we have everything".
            Console.WriteLine($"[AsyncFSFileStore] could not read {BitfieldPath} ({ex.Message}); treating the "
                + "store as empty so its pieces are re-fetched rather than claimed without data.");
            _bitfield = new bool[_pieceCount];
        }
        return _bitfield;
    }

    /// <summary>Persist which pieces this store holds.</summary>
    public async Task SaveBitfieldAsync()
    {
        if (_pieceCount == 0) return;
        var packed = new byte[(_pieceCount + 7) / 8];
        for (int i = 0; i < _pieceCount; i++)
            if (_bitfield[i]) packed[i >> 3] |= (byte)(1 << (7 - (i & 7)));
        try
        {
            if (!await _fs.DirectoryExists(_basePath).ConfigureAwait(false))
                await _fs.CreateDirectory(_basePath).ConfigureAwait(false);
            await _fs.Write(BitfieldPath, packed).ConfigureAwait(false);
            _bitfieldDirty = false;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[AsyncFSFileStore] could not write {BitfieldPath} ({ex.Message}); this "
                + "torrent will re-download on the next load.");
        }
    }

    /// <summary>Whether this store holds the piece. Metadata only - reads the bitfield, not the bytes.</summary>
    public async Task<bool> PieceExistsAsync(int index, CancellationToken ct = default)
    {
        await EnsureInitializedAsync().ConfigureAwait(false);
        return index >= 0 && index < _bitfield.Length && _bitfield[index];
    }

    // ── Migration from the piece layout ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Copy an existing piece-per-file cache into this store, locally, instead of re-downloading it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// 🔴 WHY THIS EXISTS. The two layouts deliberately use separate roots, so switching
    /// <c>StorageLayout</c> otherwise orphans every cached torrent - for a 7.1 GB model that is a 7.1 GB
    /// re-download to gain a faster load, which is a bad trade for the user who most needs the faster load.
    /// This is an OPFS-to-OPFS copy at local speed instead.
    /// </para>
    /// <para>
    /// ⚠️ Zero managed copies: each piece moves as a JS <see cref="Uint8Array"/> from the old store
    /// straight into this one. A migration that read pieces into <c>byte[]</c> would pull the whole model
    /// through the WASM heap, which is the cost this whole change exists to remove.
    /// </para>
    /// <para>
    /// ⚠️ Only pieces the SOURCE actually holds are copied, and the bitfield is written at the end. A
    /// migration interrupted half way therefore leaves a store that under-reports (the copied pieces are
    /// re-fetched) rather than one that claims bytes it does not have.
    /// </para>
    /// </remarks>
    /// <param name="source">A store over the old <c>webtorrent/{key}</c> directory.</param>
    /// <param name="pieceCount">Pieces in the torrent.</param>
    /// <param name="progress">Called with (copied, total) as it goes.</param>
    /// <param name="ct">Cancellation. Cancelling leaves the store consistent, just incomplete.</param>
    /// <returns>
    /// How many pieces were copied, and whether EVERY piece the source held is now present here - the
    /// precondition for deleting the source. Reported from this loop rather than re-derived afterwards
    /// because the caller would otherwise ask both stores about every piece a second time, and on the
    /// piece layout each of those questions is two OPFS metadata calls.
    /// </returns>
    public async Task<(int Copied, bool SourceFullyCovered)> MigrateFromAsync(IJSChunkStore source,
        int pieceCount, Action<int, int>? progress = null, CancellationToken ct = default)
    {
        await EnsureInitializedAsync().ConfigureAwait(false);
        if (!source.SupportsUint8Array)
            throw new NotSupportedException(
                "the source store cannot hand back Uint8Arrays, so migrating from it would copy every piece "
                + "through the .NET heap - which is the cost this layout exists to remove.");

        int copied = 0;
        bool covered = true;
        try
        {
            for (int i = 0; i < pieceCount && i < _bitfield.Length; i++)
            {
                ct.ThrowIfCancellationRequested();
                if (_bitfield[i]) { progress?.Invoke(copied, pieceCount); continue; }   // already here
                if (!await source.PieceExistsAsync(i, ct).ConfigureAwait(false)) continue;

                using var ua = await source.GetUint8ArrayAsync(i, ct).ConfigureAwait(false);
                if (ua == null) { covered = false; continue; } // source lost it - leave the bit clear
                await PutUint8ArrayAsync(i, ua, ct).ConfigureAwait(false);
                if (!_bitfield[i]) covered = false;            // the write did not take
                copied++;
                progress?.Invoke(copied, pieceCount);
            }
        }
        catch (OperationCanceledException)
        {
            covered = false;                                   // an interrupted pass has not covered anything
            throw;
        }
        finally
        {
            // Even on cancellation, persist what really landed - those pieces are on disk either way.
            await SaveBitfieldAsync().ConfigureAwait(false);
        }
        return (copied, covered);
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (_bitfieldDirty) await SaveBitfieldAsync().ConfigureAwait(false);
        foreach (var path in _syncHandles.Keys.ToList()) CloseSyncHandle(path);
        foreach (var path in _blobCache.Keys.ToList()) DropBlob(path);
    }
}
