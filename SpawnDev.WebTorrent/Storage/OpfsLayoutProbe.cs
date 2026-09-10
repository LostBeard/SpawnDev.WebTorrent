using SpawnDev.AsyncFileSystem;
using SpawnDev.SpawnJS.JSObjects;

namespace SpawnDev.WebTorrent.Storage;

/// <summary>
/// What the piece-per-file OPFS layout costs, measured against the same bytes laid out as one file.
/// </summary>
/// <remarks>
/// <para>
/// 🔴 THIS EXISTS TO SETTLE A DESIGN DECISION WITH NUMBERS. <see cref="AsyncFSChunkStore"/> stores every
/// torrent piece as its own OPFS file, so a 2375 MB model is 681 files. Instrumenting a real model load
/// (qwen3:4b, 681 pieces) measured the read itself at 1051 ms across 1337 reads - roughly 2.2 GB/s, faster
/// than the GPU needs - while OPENING those 681 piece files cost 121,571 ms, of which 117,192 ms was
/// <c>getFileHandle</c> alone. That is ~99% of model load time spent locating files, and it is the whole
/// reason a cached model still takes minutes to load.
/// </para>
/// <para>
/// ⚠️ THE OBSERVED COST IS NOT A CONSTANT PER OPEN, which is why this probe sweeps entry count rather
/// than measuring one layout. The same code path costs ~0.9 ms per open on qwen3:0.6b (~150 piece files)
/// and ~172 ms per open on qwen3:4b (681 piece files) - a 190x jump for 4.5x the files. If directory size
/// is the driver, one file per torrent CONTENT file removes the problem outright; if it is a fixed cost
/// per open, it does too, but for a different reason (681 opens become 1). This says which, and whether
/// holding exclusive sync locks on other entries is what makes the lookup expensive.
/// </para>
/// <para>
/// ⚠️ WORKER ONLY. <c>createSyncAccessHandle()</c> throws outside a worker, and that is the API the
/// production read path uses, so a window-scope run would measure a path production does not take.
/// <see cref="MeasureAsync"/> reports that rather than silently measuring something else.
/// </para>
/// </remarks>
public static class OpfsLayoutProbe
{
    /// <summary>Where the probe writes. Under <c>webtorrent/</c> so it shares the store's quota and is
    /// obvious in an OPFS listing; removed again by <see cref="MeasureAsync"/>.</summary>
    public const string ProbeRoot = "webtorrent/_layout-probe";

    /// <summary>One layout comparison at one entry count.</summary>
    /// <param name="EntryCount">Files in the piece-layout directory (== reads performed by both layouts).</param>
    /// <param name="EntryBytes">Bytes per entry / per read.</param>
    /// <param name="HeldHandles">Sync handles kept open during the piece pass, mirroring the store's LRU.
    /// 0 closes each handle before opening the next.</param>
    public sealed record LayoutMeasurement(
        int EntryCount,
        int EntryBytes,
        int HeldHandles,
        bool SyncAvailable,
        double PieceResolveMs,
        double PieceCreateMs,
        double PieceReadMs,
        double PieceCloseMs,
        double FileResolveMs,
        double FileCreateMs,
        double FileReadMs,
        double FileCloseMs,
        double PieceBlobOpenMs,
        double PieceBlobReadMs,
        double FileBlobOpenMs,
        double FileBlobReadMs)
    {
        /// <summary>Total bytes read by each pass.</summary>
        public long TotalBytes => (long)EntryCount * EntryBytes;
        /// <summary>Whole piece-layout pass over sync access handles. Zero when sync is unavailable.</summary>
        public double PieceTotalMs => PieceResolveMs + PieceCreateMs + PieceReadMs + PieceCloseMs;
        /// <summary>Whole one-file pass over a sync access handle. Zero when sync is unavailable.</summary>
        public double FileTotalMs => FileResolveMs + FileCreateMs + FileReadMs + FileCloseMs;
        /// <summary>Whole piece-layout pass over the Blob fallback - one getFile PER PIECE.</summary>
        public double PieceBlobTotalMs => PieceBlobOpenMs + PieceBlobReadMs;
        /// <summary>Whole one-file pass over the Blob fallback - one getFile for the WHOLE file.</summary>
        public double FileBlobTotalMs => FileBlobOpenMs + FileBlobReadMs;
        /// <summary>Milliseconds of <c>getFileHandle</c> per entry - the number the real load blames.</summary>
        public double PieceResolvePerEntryMs => EntryCount > 0 ? PieceResolveMs / EntryCount : 0;
    }

    /// <summary>
    /// Lay the same bytes out both ways at each requested size and time reading every entry back.
    /// </summary>
    /// <param name="fs">The OPFS filesystem. Must also be an <see cref="IAsyncBrowserFileSystem"/>.</param>
    /// <param name="configs">(entryCount, entryBytes, heldHandles) triples to measure, in order.</param>
    /// <param name="log">Per-step progress, so a long run is not silent.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>One measurement per config.</returns>
    /// <exception cref="NotSupportedException">Not a browser filesystem, or not running in a worker.</exception>
    public static async Task<List<LayoutMeasurement>> MeasureAsync(
        IAsyncFS fs,
        IReadOnlyList<(int EntryCount, int EntryBytes, int HeldHandles)> configs,
        Action<string>? log = null,
        CancellationToken ct = default)
    {
        if (fs is not IAsyncBrowserFileSystem browserFs)
            throw new NotSupportedException(
                $"OpfsLayoutProbe needs an IAsyncBrowserFileSystem (got {fs.GetType().Name}); the cost it "
                + "measures is OPFS handle resolution and does not exist on other filesystems.");

        var results = new List<LayoutMeasurement>();
        try
        {
            foreach (var (count, bytes, held) in configs)
            {
                ct.ThrowIfCancellationRequested();
                results.Add(await MeasureOneAsync(fs, browserFs, count, bytes, held, log, ct));
            }
        }
        finally
        {
            // Hundreds of MB in the same store models cache into would eat the user's quota.
            try { if (await fs.DirectoryExists(ProbeRoot)) await fs.Remove(ProbeRoot, recursive: true); }
            catch (Exception ex) { log?.Invoke($"[layout-probe] cleanup failed: {ex.Message}"); }
        }
        return results;
    }

    private static async Task<LayoutMeasurement> MeasureOneAsync(
        IAsyncFS fs, IAsyncBrowserFileSystem browserFs,
        int count, int bytes, int held, Action<string>? log, CancellationToken ct)
    {
        // WARNING: SEPARATE DIRECTORIES, deliberately. Putting whole.bin beside N piece files would make
        // the one-file pass pay the very directory-size cost this is trying to attribute, and the
        // comparison would report no difference for the wrong reason.
        var pieceDirPath = $"{ProbeRoot}/pieces-{count}-{bytes}";
        var wholeDirPath = $"{ProbeRoot}/whole-{count}-{bytes}";
        await fs.CreateDirectory(pieceDirPath);
        await fs.CreateDirectory(wholeDirPath);

        var pieceDir = await browserFs.GetDirectoryHandle(pieceDirPath)
            ?? throw new InvalidOperationException($"could not open {pieceDirPath} after creating it");
        var wholeDir = await browserFs.GetDirectoryHandle(wholeDirPath)
            ?? throw new InvalidOperationException($"could not open {wholeDirPath} after creating it");

        // One JS-side buffer for every write and every read. Bulk bytes never touch the .NET heap (the
        // byte[] Write overload marshals a copy per call), and reusing it keeps allocation out of the
        // timings - allocation is measured separately in the real load and was not the cost.
        using var payload = new Uint8Array(bytes);
        using var readBuf = new Uint8Array(bytes);

        try
        {
            // -- Is the sync API here at all? ---------------------------------------------------------
            // THIS DECIDES WHAT THE RUN CAN MEASURE, so it is established once, up front, rather than
            // discovered as an exception half way through. createSyncAccessHandle() exists ONLY in a
            // DEDICATED worker: in a shared worker or on the main thread it is undefined, and BOTH OPFS
            // stores then fall back to getFile()+slice()+arrayBuffer(). An earlier version of this probe
            // THREW there - so the one context whose cost most needed measuring, the shared worker a
            // normal visitor actually gets, was the one context it refused to run in.
            bool syncAvailable;
            {
                using var probeHandle = await wholeDir.GetFileHandle("_synccheck", create: true);
                try
                {
                    var sync = await probeHandle.CreateSyncAccessHandle();
                    syncAvailable = sync != null;
                    if (sync != null) { sync.Close(); sync.Dispose(); }
                }
                catch { syncAvailable = false; }
                try { await wholeDir.RemoveEntry("_synccheck"); } catch { }
            }
            log?.Invoke($"[layout-probe] {count} x {bytes} B: sync access handles "
                + (syncAvailable ? "AVAILABLE (dedicated worker)" : "UNAVAILABLE - Blob path only"));

            // -- Lay the bytes out both ways ----------------------------------------------------------
            log?.Invoke($"[layout-probe] writing {count} x {bytes} B piece files...");
            for (int i = 0; i < count; i++)
            {
                ct.ThrowIfCancellationRequested();
                using var h = await pieceDir.GetFileHandle($"piece_{i}", create: true);
                await WriteAsync(h, 0, payload, syncAvailable);
            }

            log?.Invoke($"[layout-probe] writing 1 x {(long)count * bytes} B file...");
            {
                using var h = await wholeDir.GetFileHandle("whole.bin", create: true);
                if (syncAvailable)
                {
                    var sync = await h.CreateSyncAccessHandle();
                    try
                    {
                        for (int i = 0; i < count; i++)
                            sync!.Write(payload, new FileSystemSyncReadWriteOptions { At = (long)i * bytes });
                        sync!.Flush();
                    }
                    finally { sync!.Close(); sync.Dispose(); }
                }
                else
                {
                    // WARNING: ONE writable for all N writes. Opening a writable per write would dominate
                    // the setup cost and, with keepExistingData, copy the whole file every time.
                    var w = await h.CreateWritable(new FileSystemCreateWritableOptions { KeepExistingData = true });
                    try
                    {
                        for (int i = 0; i < count; i++)
                        {
                            await w.Seek((ulong)((long)i * bytes));
                            await w.Write(payload);
                        }
                    }
                    finally { await w.Close(); w.Dispose(); }
                }
            }

            // -- Pass 1: sync access handles, the production fast path (dedicated worker only) ---------
            double pResolve = 0, pCreate = 0, pRead = 0, pClose = 0;
            double fResolve = 0, fCreate = 0, fRead = 0, fClose = 0;
            if (syncAvailable)
            {
                var openHandles = new Queue<FileSystemSyncAccessHandle>();
                try
                {
                    for (int i = 0; i < count; i++)
                    {
                        ct.ThrowIfCancellationRequested();

                        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                        var h = await pieceDir.GetFileHandle($"piece_{i}", create: false);
                        pResolve += System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMilliseconds;

                        var t1 = System.Diagnostics.Stopwatch.GetTimestamp();
                        var sync = await h!.CreateSyncAccessHandle();
                        pCreate += System.Diagnostics.Stopwatch.GetElapsedTime(t1).TotalMilliseconds;
                        h.Dispose();

                        var t2 = System.Diagnostics.Stopwatch.GetTimestamp();
                        var read = sync!.Read(readBuf, new FileSystemSyncReadWriteOptions { At = 0 });
                        pRead += System.Diagnostics.Stopwatch.GetElapsedTime(t2).TotalMilliseconds;
                        if (read != bytes)
                            throw new InvalidOperationException(
                                $"piece_{i} read {read} of {bytes} bytes - the probe wrote a file it cannot "
                                + "read back, so its timings would describe nothing.");

                        // Mirror the store LRU: hold `held` locks open while looking the next entry up.
                        openHandles.Enqueue(sync);
                        while (openHandles.Count > held)
                        {
                            var evict = openHandles.Dequeue();
                            var t3 = System.Diagnostics.Stopwatch.GetTimestamp();
                            evict.Close();
                            pClose += System.Diagnostics.Stopwatch.GetElapsedTime(t3).TotalMilliseconds;
                            evict.Dispose();
                        }
                    }
                }
                finally
                {
                    while (openHandles.Count > 0)
                    {
                        var h = openHandles.Dequeue();
                        try { h.Close(); } catch { }
                        try { h.Dispose(); } catch { }
                    }
                }

                var s0 = System.Diagnostics.Stopwatch.GetTimestamp();
                var wh = await wholeDir.GetFileHandle("whole.bin", create: false);
                fResolve = System.Diagnostics.Stopwatch.GetElapsedTime(s0).TotalMilliseconds;

                var s1 = System.Diagnostics.Stopwatch.GetTimestamp();
                var wsync = await wh!.CreateSyncAccessHandle();
                fCreate = System.Diagnostics.Stopwatch.GetElapsedTime(s1).TotalMilliseconds;
                wh.Dispose();
                try
                {
                    for (int i = 0; i < count; i++)
                    {
                        ct.ThrowIfCancellationRequested();
                        var s2 = System.Diagnostics.Stopwatch.GetTimestamp();
                        var read = wsync!.Read(readBuf, new FileSystemSyncReadWriteOptions { At = (long)i * bytes });
                        fRead += System.Diagnostics.Stopwatch.GetElapsedTime(s2).TotalMilliseconds;
                        if (read != bytes)
                            throw new InvalidOperationException(
                                $"whole.bin read {read} of {bytes} bytes at offset {(long)i * bytes} - the "
                                + "probe wrote a file it cannot read back.");
                    }
                }
                finally
                {
                    var s3 = System.Diagnostics.Stopwatch.GetTimestamp();
                    wsync!.Close();
                    fClose = System.Diagnostics.Stopwatch.GetElapsedTime(s3).TotalMilliseconds;
                    wsync.Dispose();
                }
            }

            // -- Pass 2: the Blob fallback, which is what a SHARED worker is stuck with ----------------
            // THE ASYMMETRY HERE IS THE FINDING. The piece layout must getFile() per PIECE, because a File
            // is an immutable snapshot of one entry. The content layout opens ONE File for the whole file
            // and slices it per read, which is exactly what AsyncFSFileStore's fallback does - so the
            // content layout shrinks the shared-worker penalty as well as the dedicated-worker one.
            double pbOpen = 0, pbRead = 0, fbOpen = 0, fbRead = 0;
            for (int i = 0; i < count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                var h = await pieceDir.GetFileHandle($"piece_{i}", create: false);
                using var file = await h!.GetFile();
                pbOpen += System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
                h.Dispose();

                var t1 = System.Diagnostics.Stopwatch.GetTimestamp();
                using var slice = file.Slice(0, bytes);
                using var ab = await slice.ArrayBuffer();
                using var ua = new Uint8Array(ab);
                pbRead += System.Diagnostics.Stopwatch.GetElapsedTime(t1).TotalMilliseconds;
                if (ua.Length != bytes)
                    throw new InvalidOperationException(
                        $"Blob read of piece_{i} gave {ua.Length} of {bytes} B");
            }
            {
                var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                var h = await wholeDir.GetFileHandle("whole.bin", create: false);
                using var file = await h!.GetFile();
                fbOpen = System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
                h.Dispose();

                for (int i = 0; i < count; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    var t1 = System.Diagnostics.Stopwatch.GetTimestamp();
                    using var slice = file.Slice((long)i * bytes, (long)i * bytes + bytes);
                    using var ab = await slice.ArrayBuffer();
                    using var ua = new Uint8Array(ab);
                    fbRead += System.Diagnostics.Stopwatch.GetElapsedTime(t1).TotalMilliseconds;
                    if (ua.Length != bytes)
                        throw new InvalidOperationException(
                            $"Blob read of whole.bin at {(long)i * bytes} gave {ua.Length} of {bytes} B");
                }
            }

            return new LayoutMeasurement(count, bytes, held, syncAvailable,
                pResolve, pCreate, pRead, pClose,
                fResolve, fCreate, fRead, fClose,
                pbOpen, pbRead, fbOpen, fbRead);
        }
        finally
        {
            pieceDir.Dispose();
            wholeDir.Dispose();
        }
    }

    /// <summary>One contention measurement: the same ranged reads, idle and under a concurrent writer.</summary>
    /// <param name="Reads">Ranged reads performed in each pass.</param>
    /// <param name="ReadBytes">Bytes per read.</param>
    /// <param name="IdleReadMs">Total read time with nothing else touching OPFS.</param>
    /// <param name="LoadedReadMs">Total read time while a writer is hammering a different file.</param>
    /// <param name="WritesCompleted">Writes the background writer got through during the loaded pass.</param>
    /// <param name="SyncAvailable">False means the Blob path was measured instead.</param>
    public sealed record ContentionMeasurement(
        int Reads,
        int ReadBytes,
        double IdleReadMs,
        double LoadedReadMs,
        long WritesCompleted,
        bool SyncAvailable)
    {
        /// <summary>How much slower reads are while something else is writing. 1.0 = no effect.</summary>
        public double Ratio => IdleReadMs > 0 ? LoadedReadMs / IdleReadMs : 0;
    }

    /// <summary>
    /// Does a concurrent OPFS writer slow ranged reads on an ALREADY-OPEN handle?
    /// </summary>
    /// <remarks>
    /// <para>
    /// 🔴 WHAT THIS IS FOR. A real model load measured 172 ms per file open while the torrent was still
    /// downloading, against 0.39-0.46 ms for the same code path idle - a 370x gap that directory size,
    /// held locks and entry size were all measured and cleared of. Concurrent writing was the remaining
    /// candidate and was NEVER TESTED, so it stayed a guess and was recorded as one.
    /// </para>
    /// <para>
    /// ⚠️ IT ASKS THE VERSION OF THE QUESTION THAT STILL MATTERS. Under the content-file layout a load
    /// performs ONE open and then hundreds of ranged reads on that open handle, so inflated OPEN cost is
    /// no longer interesting - inflated READ cost is, because the demo loads a model while its remaining
    /// pieces are still arriving. The writer therefore targets a DIFFERENT file: this measures
    /// whole-filesystem interference, not lock contention on the file being read (which is separately
    /// impossible - a sync handle holds that file exclusively).
    /// </para>
    /// <para>
    /// ⚠️ The writer is cooperative, not parallel. WASM is single-threaded, so it only advances when the
    /// read loop awaits - which is exactly how the real download interleaves with the real load, and is
    /// why the result is representative rather than an artificial worst case.
    /// </para>
    /// </remarks>
    /// <param name="fs">The OPFS filesystem.</param>
    /// <param name="reads">Ranged reads per pass.</param>
    /// <param name="readBytes">Bytes per read.</param>
    /// <param name="writeBytes">Size of each background write.</param>
    /// <param name="log">Progress.</param>
    /// <param name="ct">Cancellation.</param>
    public static async Task<ContentionMeasurement> MeasureWriteContentionAsync(
        IAsyncFS fs, int reads = 310, int readBytes = 2 * 1024 * 1024, int writeBytes = 4 * 1024 * 1024,
        Action<string>? log = null, CancellationToken ct = default)
    {
        if (fs is not IAsyncBrowserFileSystem browserFs)
            throw new NotSupportedException(
                $"MeasureWriteContentionAsync needs an IAsyncBrowserFileSystem (got {fs.GetType().Name}).");

        var readDirPath = $"{ProbeRoot}/contend-read";
        var writeDirPath = $"{ProbeRoot}/contend-write";
        await fs.CreateDirectory(readDirPath);
        await fs.CreateDirectory(writeDirPath);
        var readDir = await browserFs.GetDirectoryHandle(readDirPath)
            ?? throw new InvalidOperationException($"could not open {readDirPath}");
        var writeDir = await browserFs.GetDirectoryHandle(writeDirPath)
            ?? throw new InvalidOperationException($"could not open {writeDirPath}");

        long total = (long)reads * readBytes;
        using var readBuf = new Uint8Array(readBytes);
        using var writeBuf = new Uint8Array(writeBytes);

        try
        {
            bool syncAvailable;
            {
                using var probeHandle = await readDir.GetFileHandle("_synccheck", create: true);
                try
                {
                    var sync = await probeHandle.CreateSyncAccessHandle();
                    syncAvailable = sync != null;
                    if (sync != null) { sync.Close(); sync.Dispose(); }
                }
                catch { syncAvailable = false; }
                try { await readDir.RemoveEntry("_synccheck"); } catch { }
            }

            // ── The file being read: one content file, laid out once ─────────────────────────────────
            log?.Invoke($"[contend] writing the {total / 1048576.0:F0} MB file to read from...");
            {
                using var h = await readDir.GetFileHandle("whole.bin", create: true);
                if (syncAvailable)
                {
                    var sync = await h.CreateSyncAccessHandle();
                    try
                    {
                        for (int i = 0; i < reads; i++)
                            sync!.Write(readBuf, new FileSystemSyncReadWriteOptions { At = (long)i * readBytes });
                        sync!.Flush();
                    }
                    finally { sync!.Close(); sync.Dispose(); }
                }
                else
                {
                    var w = await h.CreateWritable(new FileSystemCreateWritableOptions { KeepExistingData = true });
                    try
                    {
                        for (int i = 0; i < reads; i++)
                        {
                            await w.Seek((ulong)((long)i * readBytes));
                            await w.Write(readBuf);
                        }
                    }
                    finally { await w.Close(); w.Dispose(); }
                }
            }

            // ── Pass A: reads with nothing else running ──────────────────────────────────────────────
            log?.Invoke($"[contend] pass A: {reads} x {readBytes} B reads, idle...");
            var idleMs = await ReadPassAsync(readDir, readBuf, reads, readBytes, syncAvailable, ct);

            // ── Pass B: the same reads while a writer hammers a DIFFERENT file ───────────────────────
            log?.Invoke($"[contend] pass B: the same reads while writing {writeBytes} B chunks elsewhere...");
            long writes = 0;
            using var writerStop = new CancellationTokenSource();
            var writer = WriteLoopAsync(writeDir, writeBuf, writeBytes, syncAvailable,
                () => writes++, writerStop.Token);
            double loadedMs;
            try { loadedMs = await ReadPassAsync(readDir, readBuf, reads, readBytes, syncAvailable, ct); }
            finally
            {
                writerStop.Cancel();
                try { await writer; } catch { /* cancelled, or the write path gave out - either is fine */ }
            }

            return new ContentionMeasurement(reads, readBytes, idleMs, loadedMs, writes, syncAvailable);
        }
        finally
        {
            readDir.Dispose();
            writeDir.Dispose();
            try { if (await fs.DirectoryExists(ProbeRoot)) await fs.Remove(ProbeRoot, recursive: true); }
            catch (Exception ex) { log?.Invoke($"[contend] cleanup failed: {ex.Message}"); }
        }
    }

    /// <summary>One pass of ranged reads over a single file, timing only the reads.</summary>
    private static async Task<double> ReadPassAsync(FileSystemDirectoryHandle dir, Uint8Array buf,
        int reads, int readBytes, bool syncAvailable, CancellationToken ct)
    {
        double ms = 0;
        var h = await dir.GetFileHandle("whole.bin", create: false)
            ?? throw new InvalidOperationException("the file to read from is gone");
        if (syncAvailable)
        {
            var sync = await h.CreateSyncAccessHandle();
            h.Dispose();
            try
            {
                for (int i = 0; i < reads; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    var t = System.Diagnostics.Stopwatch.GetTimestamp();
                    var got = sync!.Read(buf, new FileSystemSyncReadWriteOptions { At = (long)i * readBytes });
                    ms += System.Diagnostics.Stopwatch.GetElapsedTime(t).TotalMilliseconds;
                    if (got != readBytes)
                        throw new InvalidOperationException(
                            $"read {got} of {readBytes} B at {(long)i * readBytes} - the probe cannot read "
                            + "back what it wrote, so its timings would describe nothing");
                    // Let anything else pending actually run. Without this the synchronous reads would
                    // never yield and the "under load" pass would measure no contention BY CONSTRUCTION.
                    await Task.Yield();
                }
            }
            finally { sync!.Close(); sync.Dispose(); }
            return ms;
        }

        using var file = await h.GetFile();
        h.Dispose();
        for (int i = 0; i < reads; i++)
        {
            ct.ThrowIfCancellationRequested();
            var t = System.Diagnostics.Stopwatch.GetTimestamp();
            using var slice = file.Slice((long)i * readBytes, (long)i * readBytes + readBytes);
            using var ab = await slice.ArrayBuffer();
            using var ua = new Uint8Array(ab);
            ms += System.Diagnostics.Stopwatch.GetElapsedTime(t).TotalMilliseconds;
            if (ua.Length != readBytes)
                throw new InvalidOperationException($"Blob read gave {ua.Length} of {readBytes} B");
        }
        return ms;
    }

    /// <summary>Write chunks into a rotating set of files until cancelled. The interference source.</summary>
    private static async Task WriteLoopAsync(FileSystemDirectoryHandle dir, Uint8Array buf, int writeBytes,
        bool syncAvailable, Action onWrite, CancellationToken ct)
    {
        int n = 0;
        while (!ct.IsCancellationRequested)
        {
            // A fresh file every few writes, so this exercises entry creation as well as raw writing -
            // a torrent download does both.
            var name = $"chunk_{n % 64}";
            try
            {
                using var h = await dir.GetFileHandle(name, create: true);
                await WriteAsync(h, 0, buf, syncAvailable);
                onWrite();
            }
            catch (OperationCanceledException) { return; }
            catch { return; }            // the filesystem gave out; the read pass still has its numbers
            n++;
            await Task.Yield();
        }
    }

    /// <summary>Write the payload at an offset, via whichever API this context actually has.</summary>
    private static async Task WriteAsync(FileSystemFileHandle h, long at, Uint8Array payload, bool syncAvailable)
    {
        if (syncAvailable)
        {
            var sync = await h.CreateSyncAccessHandle();
            try { sync!.Write(payload, new FileSystemSyncReadWriteOptions { At = at }); sync.Flush(); }
            finally { sync!.Close(); sync.Dispose(); }
            return;
        }
        // WARNING: keepExistingData must stay true - without it createWritable starts from an EMPTY file.
        var w = await h.CreateWritable(new FileSystemCreateWritableOptions { KeepExistingData = true });
        try { await w.Seek((ulong)at); await w.Write(payload); }
        finally { await w.Close(); w.Dispose(); }
    }

    /// <summary>Format one measurement as the lines a console reader needs to draw a conclusion.</summary>
    public static IEnumerable<string> Describe(LayoutMeasurement m)
    {
        yield return $"[layout-probe] {m.EntryCount} entries x {m.EntryBytes} B "
                   + $"({m.TotalBytes / 1048576.0:F1} MB), {m.HeldHandles} handle(s) held, sync "
                   + (m.SyncAvailable ? "AVAILABLE" : "UNAVAILABLE");
        if (m.SyncAvailable)
        {
            yield return $"[layout-probe]   SYNC  piece-per-file: {m.PieceTotalMs,9:F0} ms total "
                       + $"= resolve {m.PieceResolveMs:F0} + create {m.PieceCreateMs:F0} "
                       + $"+ read {m.PieceReadMs:F0} + close {m.PieceCloseMs:F0}";
            yield return $"[layout-probe]   SYNC  one file      : {m.FileTotalMs,9:F0} ms total "
                       + $"= resolve {m.FileResolveMs:F1} + create {m.FileCreateMs:F1} "
                       + $"+ read {m.FileReadMs:F0} + close {m.FileCloseMs:F1}";
            yield return $"[layout-probe]         getFileHandle : {m.PieceResolvePerEntryMs:F2} ms per entry";
        }
        yield return $"[layout-probe]   BLOB  piece-per-file: {m.PieceBlobTotalMs,9:F0} ms total "
                   + $"= getFile {m.PieceBlobOpenMs:F0} (one PER PIECE) + slice/arrayBuffer {m.PieceBlobReadMs:F0}";
        yield return $"[layout-probe]   BLOB  one file      : {m.FileBlobTotalMs,9:F0} ms total "
                   + $"= getFile {m.FileBlobOpenMs:F1} (ONE, reused) + slice/arrayBuffer {m.FileBlobReadMs:F0}";

        if (m.SyncAvailable && m.FileTotalMs > 0)
            yield return $"[layout-probe]   VERDICT sync: pieces cost {m.PieceTotalMs / m.FileTotalMs:F2}x "
                       + "the one-file layout";
        if (m.FileBlobTotalMs > 0)
            yield return $"[layout-probe]   VERDICT blob: pieces cost {m.PieceBlobTotalMs / m.FileBlobTotalMs:F2}x "
                       + "the one-file layout";
        // The number that decides whether a SHARED-worker host (no sync API at all) is still paying for it.
        if (m.SyncAvailable && m.FileTotalMs > 0 && m.FileBlobTotalMs > 0)
            yield return $"[layout-probe]   one-file layout, blob vs sync: "
                       + $"{m.FileBlobTotalMs / m.FileTotalMs:F2}x - this is the whole cost of being in a "
                       + "shared worker once the layout is fixed";
    }
}
