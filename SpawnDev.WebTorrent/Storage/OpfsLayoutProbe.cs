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
        double PieceResolveMs,
        double PieceCreateMs,
        double PieceReadMs,
        double PieceCloseMs,
        double FileResolveMs,
        double FileCreateMs,
        double FileReadMs,
        double FileCloseMs)
    {
        /// <summary>Total bytes read by each pass.</summary>
        public long TotalBytes => (long)EntryCount * EntryBytes;
        /// <summary>Whole piece-layout pass.</summary>
        public double PieceTotalMs => PieceResolveMs + PieceCreateMs + PieceReadMs + PieceCloseMs;
        /// <summary>Whole one-file pass.</summary>
        public double FileTotalMs => FileResolveMs + FileCreateMs + FileReadMs + FileCloseMs;
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
        // ⚠️ SEPARATE DIRECTORIES, deliberately. Putting whole.bin beside N piece files would make the
        // one-file pass pay the very directory-size cost this is trying to attribute, and the comparison
        // would report no difference for the wrong reason.
        var pieceDirPath = $"{ProbeRoot}/pieces-{count}-{bytes}";
        var wholeDirPath = $"{ProbeRoot}/whole-{count}-{bytes}";
        await fs.CreateDirectory(pieceDirPath);
        await fs.CreateDirectory(wholeDirPath);

        var pieceDir = await browserFs.GetDirectoryHandle(pieceDirPath)
            ?? throw new InvalidOperationException($"could not open {pieceDirPath} after creating it");
        var wholeDir = await browserFs.GetDirectoryHandle(wholeDirPath)
            ?? throw new InvalidOperationException($"could not open {wholeDirPath} after creating it");

        // One JS-side buffer for every write and every read. Bulk bytes never touch the .NET heap
        // (the byte[] Write overload marshals a copy per call), and reusing it keeps allocation out of
        // the timings - allocation is measured separately in the real load and was not the cost.
        using var payload = new Uint8Array(bytes);
        using var readBuf = new Uint8Array(bytes);

        try
        {
            // ── Write the piece layout ──────────────────────────────────────────────────────────────
            log?.Invoke($"[layout-probe] writing {count} x {bytes} B piece files...");
            for (int i = 0; i < count; i++)
            {
                ct.ThrowIfCancellationRequested();
                using var h = await pieceDir.GetFileHandle($"piece_{i}", create: true);
                var sync = await h.CreateSyncAccessHandle()
                    ?? throw new NotSupportedException(
                        "createSyncAccessHandle() returned null - the probe must run in a WORKER, because "
                        + "that is the API the production read path uses.");
                try { sync.Write(payload, new FileSystemSyncReadWriteOptions { At = 0 }); sync.Flush(); }
                finally { sync.Close(); sync.Dispose(); }
            }

            // ── Write the same bytes as one file ────────────────────────────────────────────────────
            log?.Invoke($"[layout-probe] writing 1 x {(long)count * bytes} B file...");
            {
                using var h = await wholeDir.GetFileHandle("whole.bin", create: true);
                var sync = await h.CreateSyncAccessHandle()!;
                try
                {
                    for (int i = 0; i < count; i++)
                        sync.Write(payload, new FileSystemSyncReadWriteOptions { At = (long)i * bytes });
                    sync.Flush();
                }
                finally { sync.Close(); sync.Dispose(); }
            }

            // ── Read the piece layout, exactly as AsyncFSChunkStore does ────────────────────────────
            double pResolve = 0, pCreate = 0, pRead = 0, pClose = 0;
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
                    var read = sync.Read(readBuf, new FileSystemSyncReadWriteOptions { At = 0 });
                    pRead += System.Diagnostics.Stopwatch.GetElapsedTime(t2).TotalMilliseconds;
                    if (read != bytes)
                        throw new InvalidOperationException(
                            $"piece_{i} read {read} of {bytes} bytes - the probe wrote a file it cannot read "
                            + "back, so its timings would describe nothing.");

                    // Mirror the store's LRU: hold `held` locks open while looking the next entry up.
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

            // ── Read the same bytes from one file: one resolve, one open, N ranged reads ────────────
            double fResolve, fCreate, fRead = 0, fClose;
            {
                var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                var h = await wholeDir.GetFileHandle("whole.bin", create: false);
                fResolve = System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMilliseconds;

                var t1 = System.Diagnostics.Stopwatch.GetTimestamp();
                var sync = await h!.CreateSyncAccessHandle();
                fCreate = System.Diagnostics.Stopwatch.GetElapsedTime(t1).TotalMilliseconds;
                h.Dispose();

                try
                {
                    for (int i = 0; i < count; i++)
                    {
                        ct.ThrowIfCancellationRequested();
                        var t2 = System.Diagnostics.Stopwatch.GetTimestamp();
                        var read = sync.Read(readBuf, new FileSystemSyncReadWriteOptions { At = (long)i * bytes });
                        fRead += System.Diagnostics.Stopwatch.GetElapsedTime(t2).TotalMilliseconds;
                        if (read != bytes)
                            throw new InvalidOperationException(
                                $"whole.bin read {read} of {bytes} bytes at offset {(long)i * bytes} - the "
                                + "probe wrote a file it cannot read back.");
                    }
                }
                finally
                {
                    var t3 = System.Diagnostics.Stopwatch.GetTimestamp();
                    sync.Close();
                    fClose = System.Diagnostics.Stopwatch.GetElapsedTime(t3).TotalMilliseconds;
                    sync.Dispose();
                }
            }

            return new LayoutMeasurement(count, bytes, held,
                pResolve, pCreate, pRead, pClose,
                fResolve, fCreate, fRead, fClose);
        }
        finally
        {
            pieceDir.Dispose();
            wholeDir.Dispose();
        }
    }

    /// <summary>Format one measurement as the lines a console reader needs to draw a conclusion.</summary>
    public static IEnumerable<string> Describe(LayoutMeasurement m)
    {
        yield return $"[layout-probe] {m.EntryCount} entries x {m.EntryBytes} B "
                   + $"({m.TotalBytes / 1048576.0:F1} MB), {m.HeldHandles} handle(s) held open";
        yield return $"[layout-probe]   piece-per-file: {m.PieceTotalMs,9:F0} ms total "
                   + $"= resolve {m.PieceResolveMs:F0} + create {m.PieceCreateMs:F0} "
                   + $"+ read {m.PieceReadMs:F0} + close {m.PieceCloseMs:F0}";
        yield return $"[layout-probe]   one file      : {m.FileTotalMs,9:F0} ms total "
                   + $"= resolve {m.FileResolveMs:F1} + create {m.FileCreateMs:F1} "
                   + $"+ read {m.FileReadMs:F0} + close {m.FileCloseMs:F1}";
        yield return $"[layout-probe]   getFileHandle : {m.PieceResolvePerEntryMs:F2} ms per entry "
                   + $"(the real qwen3:4b load measured 172 ms per entry at 681 entries)";
        var ratio = m.FileTotalMs > 0 ? m.PieceTotalMs / m.FileTotalMs : 0;
        yield return $"[layout-probe]   VERDICT       : pieces cost {ratio:F2}x the one-file layout";
    }
}
