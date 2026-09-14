using SpawnDev.UnitTesting;
using SpawnDev.WebTorrent.Storage;

namespace SpawnDev.WebTorrent.Demo.Shared;

/// <summary>
/// Drives <see cref="OpfsLayoutProbe"/> so the piece-per-file vs one-file decision rests on current
/// numbers instead of an old summary.
/// </summary>
/// <remarks>
/// <para>
/// 🔴 THE MEASUREMENT THAT CHOSE THE CONTENT-FILE LAYOUT WAS TAKEN AT 64 KiB ENTRIES. At that size a
/// per-file open (~2.4 ms) swamps everything, so "pieces cost 22-33x one file" was really "opens cost
/// more than reads". Production pieces are 1-4 MiB, where there are 16-64x FEWER opens for the same
/// bytes, so that ratio is not transferable and must not be quoted as if it were.
/// </para>
/// <para>
/// ⭐ THE SWEEP IS A CONTROLLED EXPERIMENT: every config moves the SAME total bytes and varies only the
/// entry count, across a 64x range. Cost that tracks entry count is per-OPEN; cost that stays flat is
/// per-BYTE. Comparing configs of different total size could not separate the two, which is why the old
/// numbers could not settle this.
/// </para>
/// <para>
/// ⚠️ SYNC HANDLES NEED A DEDICATED WORKER, and the PMT browser lane runs in the page, so this run
/// reports <c>sync UNAVAILABLE</c> and measures the BLOB path only. That is not a throwaway: the Blob
/// path is exactly what a window or shared-worker host takes, and it is the case that decides whether
/// chunked files can serve every scope. The sync half needs a dedicated-worker lane and is NOT covered
/// here - do not read these numbers as covering it.
/// </para>
/// </remarks>
public abstract partial class WebTorrentTestBase
{
    /// <summary>
    /// Reports the layout cost curve through the exception message. A browser <c>Console.WriteLine</c>
    /// never reaches the PMT log, so the numbers have to ride out on a failure.
    /// </summary>
    [TestMethod(Timeout = 600000)]
    public async Task LayoutProbe_EntryCountSweep_ReportsCostCurve()
    {
        if (!OperatingSystem.IsBrowser())
            throw new UnsupportedTestException("OpfsLayoutProbe measures OPFS handle cost (browser only)");

        var fs = Client.AsyncFileSystem
            ?? throw new UnsupportedTestException("no AsyncFileSystem on the shared client");

        // Constant 64 MiB per config; entry count varies 64x against entry size. HeldHandles = 8 mirrors
        // AsyncFSChunkStore's real LRU, so this measures the store's actual behaviour, not an ideal one.
        var configs = new (int EntryCount, int EntryBytes, int HeldHandles)[]
        {
            (1024, 65536,   8),   //  64 KiB entries - the size the original decision was measured at
            ( 256, 262144,  8),   // 256 KiB
            (  64, 1048576, 8),   //   1 MiB - the piece size the verify profile was measured at
            (  16, 4194304, 8),   //   4 MiB - production piece size
        };

        var lines = new List<string>();
        var results = await OpfsLayoutProbe.MeasureAsync(fs, configs, log: s => lines.Add(s));

        var report = new List<string>();
        foreach (var m in results) report.AddRange(OpfsLayoutProbe.Describe(m));
        string Report() => "LAYOUT-PROBE || " + string.Join(" || ", report);

        if (results.Count != configs.Length)
            throw new Exception($"expected {configs.Length} measurements, got {results.Count} || {Report()}");

        // The instrument must actually distinguish the two layouts. The one-file pass reuses ONE getFile
        // while the piece pass pays one PER ENTRY, so at 1024 entries that gap is the whole point of the
        // probe. If it ever vanishes, the probe has stopped measuring what it claims and every conclusion
        // drawn from it is void - including the ones recorded above.
        var many = results[0];
        if (many.FileBlobOpenMs <= 0 || many.PieceBlobOpenMs <= 0)
            throw new Exception($"probe recorded no Blob open cost at all || {Report()}");
        if (many.PieceBlobOpenMs <= many.FileBlobOpenMs * 10)
            throw new Exception(
                $"piece-per-file getFile ({many.PieceBlobOpenMs:F0} ms over {many.EntryCount} entries) is not "
                + $"meaningfully above the one-file getFile ({many.FileBlobOpenMs:F1} ms, reused) - the probe "
                + $"is no longer separating the layouts || {Report()}");

        // Every config moves the same bytes, so read time falling as entries grow is the READ-SIZE effect
        // (measured 84 MB/s at 64 KiB against 985 MB/s at 4 MiB, and the one-file column shows the same
        // curve - so it is the read size, not the layout). Pinning it stops that finding silently rotting.
        var fewest = results[^1];
        if (fewest.FileBlobReadMs >= many.FileBlobReadMs)
            throw new Exception(
                $"reading {many.TotalBytes / 1048576.0:F0} MB in {fewest.EntryBytes} B reads took "
                + $"{fewest.FileBlobReadMs:F0} ms, no better than {many.EntryBytes} B reads at "
                + $"{many.FileBlobReadMs:F0} ms - the read-size effect this rests on is gone || {Report()}");
    }
}
