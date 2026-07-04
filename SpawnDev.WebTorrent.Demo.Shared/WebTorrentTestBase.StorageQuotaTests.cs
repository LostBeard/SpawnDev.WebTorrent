using SpawnDev.UnitTesting;
using SpawnDev.WebTorrent;
using SpawnDev.WebTorrent.Storage;

namespace SpawnDev.WebTorrent.Demo.Shared;

/// <summary>
/// The storage-quota RUNAWAY regression (2026-07-04). A browser model download stores each verified piece to OPFS.
/// If a store WRITE throws — the browser's QuotaExceededError when the origin is out of space — the old policy left
/// the piece unflagged for the picker, which re-requested it, which re-fetched + re-stored + threw again: an infinite
/// hot loop (169 identical range GETs observed live on the 1.5B model) that also, before the AsyncFileSystem
/// abort-on-throw fix, leaked an OPFS swap file per attempt. The fix: classify the failure. Quota (unrecoverable) →
/// pause the torrent + raise OnError. A span that fails repeatedly for any other reason → back off, and after a strike
/// cap, also pause. A transient failure that clears → recover with no false pause.
///
/// Fault is injected at the REAL store (AsyncFSChunkStore.PutUint8ArrayAsync) so the whole production download loop
/// runs; only the write outcome is forced. Browser-only (the zero-copy OPFS store path is the browser arm).
/// </summary>
public abstract partial class WebTorrentTestBase
{
    [TestMethod(Timeout = 120000, RetryCount = 1, Category = "HeavyModel")]
    public async Task StorageQuota_WriteThrows_PausesTorrent_NoRunawayRefetch()
    {
        var fs = Client.AsyncFileSystem;
        if (fs == null) throw new UnsupportedTestException("no OPFS (desktop runtime) — the zero-copy store path is browser-only");
        // A many-piece model so a runaway (169 identical GETs) would be unmistakable; quota pauses after the first
        // few spans, so despite the size the test transfers only ~tens of MB before pausing.
        var url = $"{HubBaseUrl}/hf/Qwen/Qwen2.5-0.5B-Instruct-GGUF/qwen2.5-0.5b-instruct-q8_0.gguf";

        AsyncFSChunkStore.TestThrowQuotaForever = false;
        AsyncFSChunkStore.TestThrowTransientOnPutCount = 0;

        var client = new WebTorrentClient(new WebTorrentClientOptions { AsyncFileSystem = fs });
        int errorCount = 0; string? lastError = null;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(1));
            WebConn.FetchRangeCount = 0;

            // Add PAUSED so we can subscribe OnError before a single piece is requested (no race with the first
            // store-throw). Arm the quota fault, then Resume to start the download: every piece write now throws
            // QuotaExceededError.
            var t = await client.AddAsync(url, new AddTorrentOptions { Paused = true }, cts.Token);
            t.OnError += msg => { errorCount++; lastError = msg; };
            if (t.PieceCount < 2) throw new UnsupportedTestException($"file too small ({t.PieceCount} pieces) to distinguish bounded vs runaway");
            AsyncFSChunkStore.TestThrowQuotaForever = true;
            t.Resume();

            // The download begins storing pieces → the first store throws quota → the torrent must PAUSE.
            bool paused = false;
            for (int i = 0; i < 300 && !paused; i++) { paused = t.Paused; if (!paused) await Task.Delay(100, cts.Token); }
            if (!paused)
                throw new Exception($"torrent did NOT pause after quota write failures (Paused=false, {WebConn.FetchRangeCount} GETs) — the runaway is not contained");
            if (!t.StorageQuotaExceeded)
                throw new Exception("paused but StorageQuotaExceeded=false — quota was not classified");
            if (errorCount == 0)
                throw new Exception("paused but OnError never fired — the app gets no signal to free storage");

            // THE runaway assertion: once paused, GETs must STOP. Sample, wait, resample — a hot loop would keep
            // climbing (169+ identical GETs). A bounded fix holds steady (± a few in-flight spans already dispatched).
            int getsAtPause = WebConn.FetchRangeCount;
            await Task.Delay(2500, cts.Token);
            int getsAfter = WebConn.FetchRangeCount;
            int delta = getsAfter - getsAtPause;
            if (delta > ZeroCopyMaxSpansHeadroom)
                throw new Exception($"RUNAWAY: {delta} more GETs in 2.5s AFTER pause ({getsAtPause}→{getsAfter}) — the picker is still re-requesting");

            // And resume clears the quota flag (the app's signal it freed space) so the torrent can retry.
            t.Resume();
            if (t.StorageQuotaExceeded || t.Paused)
                throw new Exception("Resume did not clear StorageQuotaExceeded/Paused");

            Console.WriteLine($"[StorageQuota] quota write-throw → paused after {getsAtPause} GETs, +{delta} in 2.5s (bounded), OnError×{errorCount}: {lastError}");
        }
        catch (UnsupportedTestException) { throw; }
        catch (Exception ex) when (ex.Message.Contains("No connection") || ex.Message.Contains("network")
            || ex.Message.Contains("preparing") || ex is TimeoutException)
        {
            throw new UnsupportedTestException($"hub/network unavailable: {ex.Message}");
        }
        finally
        {
            AsyncFSChunkStore.TestThrowQuotaForever = false;
            AsyncFSChunkStore.TestThrowTransientOnPutCount = 0;
            await client.DisposeAsync();
        }
    }

    [TestMethod(Timeout = 180000, RetryCount = 1, Category = "HeavyModel")]
    public async Task StorageQuota_TransientWriteFault_RecoversWithoutPausing()
    {
        var fs = Client.AsyncFileSystem;
        if (fs == null) throw new UnsupportedTestException("no OPFS (desktop runtime) — the zero-copy store path is browser-only");
        var url = LazyHubUrl;

        AsyncFSChunkStore.TestThrowQuotaForever = false;
        AsyncFSChunkStore.TestThrowTransientOnPutCount = 0;

        var client = new WebTorrentClient(new WebTorrentClientOptions { AsyncFileSystem = fs });
        int errorCount = 0;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));

            // A couple of NON-quota store faults (network hiccup class): the span must back off and RETRY, and the
            // download must still complete — with NO pause and NO OnError (transient is recoverable, not fatal).
            AsyncFSChunkStore.TestThrowTransientOnPutCount = 2;

            var t = await client.AddAsync(url, ct: cts.Token);
            t.OnError += _ => errorCount++;
            var f = t.Files[0];

            var all = await f.ReadAsync(0, (int)f.Length, cts.Token);
            if (all == null || all.Length != f.Length)
                throw new Exception($"transient faults did not recover: read {all?.Length ?? 0}/{f.Length} bytes");
            if (t.Paused)
                throw new Exception("torrent PAUSED on a transient (recoverable) store fault — should have backed off and retried");
            if (errorCount != 0)
                throw new Exception($"OnError fired {errorCount}× on a transient fault — should be warning-only, not fatal");
            if (AsyncFSChunkStore.TestThrowTransientOnPutCount != 0)
                throw new UnsupportedTestException("injected faults never consumed — file may be too small; inconclusive");

            Console.WriteLine($"[StorageQuota] transient store faults recovered: {f.Length} B downloaded intact, no pause, no OnError");
        }
        catch (UnsupportedTestException) { throw; }
        catch (Exception ex) when (ex.Message.Contains("No connection") || ex.Message.Contains("network")
            || ex.Message.Contains("preparing") || ex is TimeoutException)
        {
            throw new UnsupportedTestException($"hub/network unavailable: {ex.Message}");
        }
        finally
        {
            AsyncFSChunkStore.TestThrowQuotaForever = false;
            AsyncFSChunkStore.TestThrowTransientOnPutCount = 0;
            await client.DisposeAsync();
        }
    }

    // A handful of in-flight spans may already have a GET dispatched at the instant the torrent pauses; allow that
    // small settling delta but nothing resembling a re-request loop.
    private const int ZeroCopyMaxSpansHeadroom = 6;
}
