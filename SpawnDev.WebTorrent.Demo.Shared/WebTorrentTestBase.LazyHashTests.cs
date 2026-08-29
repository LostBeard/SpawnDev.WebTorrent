using SpawnDev.UnitTesting;
using SpawnDev.WebTorrent;

namespace SpawnDev.WebTorrent.Demo.Shared;

/// <summary>
/// Lazy-Hash Torrents: add a torrent from a web-seed URL with the infohash UNKNOWN at add time, download from the
/// seed computing piece hashes as they arrive, and finalize the real infohash on completion. The correctness gate
/// is that the finalized infohash is BYTE-IDENTICAL to creating the torrent eagerly from the same URL/bytes
/// (TorrentCreator.CreateFromUrlAsync) — same name/length/piece-length/piece-hashes → same SHA-1 info hash.
/// </summary>
public abstract partial class WebTorrentTestBase
{
    // The hub /hf web seed serves the same small ONNX model used by the HuggingFaceProxy tests, with byte-range
    // support — the exact shape a model download has.
    private static string LazyHubUrl => $"{HubBaseUrl}/hf/{HfRepoId}/{HfFilePath}";

    [TestMethod(Timeout = 300000, RetryCount = 2, Category = "HeavyModel")]
    public async Task LazyHash_AddUrl_Downloads_FinalizesInfohashMatchesEager()
    {
        var url = LazyHubUrl;

        // Reference: build the torrent EAGERLY (download + hash the whole file) → the infohash we must reproduce.
        string expectedInfohash;
        using (var ehttp = new HttpClient())
        {
            using var ects = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            // Eager reference uses the SAME piece length the lazy path picks (the larger web-seed floor) so the
            // infohashes are comparable.
            var (_, eagerMeta) = await TorrentCreator.CreateFromUrlAsync(url,
                new TorrentCreatorOptions { PieceLength = Torrent.WebSeedMinPieceLength }, ects.Token);
            expectedInfohash = eagerMeta.InfoHash;
            if (string.IsNullOrEmpty(expectedInfohash)) throw new Exception("eager CreateFromUrlAsync produced no infohash");
        }

        var client = new WebTorrentClient();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(4));

            // Lazy add: infohash NOT known up front — only the URL.
            var t = await client.AddAsync(url, ct: cts.Token);
            if (!t.LazyHash) throw new Exception("expected a Lazy-Hash torrent (LazyHash=true) when adding an http(s) URL");
            if (!string.IsNullOrEmpty(t.InfoHash)) throw new Exception($"lazy torrent must have empty infohash at add, got '{t.InfoHash}'");
            if (t.Files == null || t.Files.Length != 1 || t.Files[0].Length <= 0)
                throw new Exception("lazy shell did not resolve a single non-empty file from the web seed");

            // Download the whole file from the web seed (computes every piece hash).
            var file = t.Files[0];
            var all = await file.ReadAsync(0, (int)file.Length, cts.Token);
            if (all == null || all.Length != file.Length)
                throw new Exception($"read {all?.Length ?? 0} of {file.Length} bytes from the lazy web seed");

            // CheckDone → FinalizeLazyHash runs when all pieces are present; wait for it to exit lazy mode.
            for (int i = 0; i < 300 && t.LazyHash; i++) await Task.Delay(50, cts.Token);
            if (t.LazyHash) throw new Exception("lazy torrent did not finalize (still LazyHash) after a full download");

            if (!string.Equals(t.InfoHash, expectedInfohash, StringComparison.OrdinalIgnoreCase))
                throw new Exception($"lazy-finalized infohash '{t.InfoHash}' != eager infohash '{expectedInfohash}' " +
                    "— the lazy shell/finalize is not byte-identical to eager creation");
            if (string.IsNullOrEmpty(t.WireInfoHashHex))
                throw new Exception("finalized torrent has no WireInfoHashHex — not identifiable/seedable");

            Console.WriteLine($"[LazyHash] {file.Name} ({file.Length} B): lazy infohash == eager == {t.InfoHash}");
        }
        catch (UnsupportedTestException) { throw; }
        catch (Exception ex) when (ex.Message.Contains("No connection") || ex.Message.Contains("network")
            || ex.Message.Contains("magnet") || ex.Message.Contains("preparing") || ex is TimeoutException)
        {
            throw new UnsupportedTestException($"hub/network unavailable: {ex.Message}");
        }
        finally { await client.DisposeAsync(); }
    }

    /// <summary>
    /// Random-access read WHILE downloading — the whole point of making it a torrent vs a dumb HTTP stream. Right
    /// after a lazy add (before the file is fully down), read a TAIL range; the read must drive ONLY the covering
    /// pieces to fetch (via HTTP Range from the web seed) and return the correct bytes — matching a direct Range
    /// GET of the same span. Mirrors HuggingFaceProxy_TailSeekRead but for a Lazy-Hash torrent.
    /// </summary>
    [TestMethod(Timeout = 300000, RetryCount = 2, Category = "HeavyModel")]
    public async Task LazyHash_RandomAccessRead_MidDownload_MatchesDirectRange()
    {
        var url = LazyHubUrl;
        var client = new WebTorrentClient();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(4));
            var t = await client.AddAsync(url, ct: cts.Token);
            if (!t.LazyHash) throw new Exception("expected a Lazy-Hash torrent");
            var file = t.Files[0];
            if (file.Length < 8192) throw new Exception($"file too small ({file.Length}) for a meaningful tail-seek");

            // Read a 4 KiB TAIL range — well before the whole file is downloaded. This must NOT require the whole
            // file; it fetches only the covering (tail) pieces via the web seed's Range support.
            int chunk = (int)Math.Min(4096, file.Length);
            long tailOffset = file.Length - chunk;
            var got = await file.ReadAsync(tailOffset, chunk, cts.Token);
            if (got == null || got.Length != chunk) throw new Exception($"tail read returned {got?.Length ?? 0} of {chunk}");
            if (t.Done) throw new Exception("the whole file finished downloading before the tail read returned — not a true mid-download random-access test");

            // Oracle: a direct HTTP Range GET of the same span.
            byte[] expected;
            using (var http = new HttpClient())
            using (var req = new HttpRequestMessage(HttpMethod.Get, url))
            {
                req.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(tailOffset, tailOffset + chunk - 1);
                using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseContentRead, cts.Token);
                resp.EnsureSuccessStatusCode();
                expected = await resp.Content.ReadAsByteArrayAsync(cts.Token);
            }
            if (expected.Length != chunk) throw new UnsupportedTestException($"web seed ignored Range (returned {expected.Length}, not {chunk}) — needs a range-capable seed");
            if (!got.SequenceEqual(expected))
                throw new Exception("mid-download tail read bytes != direct Range GET — random-access read is wrong");

            Console.WriteLine($"[LazyHash] random-access tail read ({chunk} B @ {tailOffset}) mid-download matches direct Range GET");
        }
        catch (UnsupportedTestException) { throw; }
        catch (Exception ex) when (ex.Message.Contains("No connection") || ex.Message.Contains("network")
            || ex.Message.Contains("magnet") || ex.Message.Contains("preparing") || ex is TimeoutException)
        {
            throw new UnsupportedTestException($"hub/network unavailable: {ex.Message}");
        }
        finally { await client.DisposeAsync(); }
    }

    /// <summary>
    /// THE re-download fix. Download a lazy torrent fully on an OPFS-backed client (persists pieces + the finalized
    /// .torrent to OPFS, keyed by the provisional URL id), then simulate a page reload with a FRESH client over the
    /// SAME OPFS: RestoreFromStorageAsync must bring the torrent back COMPLETE (every piece present from OPFS,
    /// Done=true) with ZERO re-download — and it must appear in client.Torrents (so the /cache page lists it). This
    /// is exactly the lifecycle no test covered, and the literal bug TJ hit (re-download every refresh, absent from
    /// the Model Cache page). Browser-only (OPFS).
    /// </summary>
    [TestMethod(Timeout = 300000, RetryCount = 2, Category = "HeavyModel")]
    public async Task LazyHash_DownloadThenReopen_RestoresComplete_NoRedownload()
    {
        var fs = Client.AsyncFileSystem;
        if (fs == null) throw new UnsupportedTestException("no OPFS (desktop runtime) — lazy persistence is browser-only");
        var url = LazyHubUrl;
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(4));

        string infohash; long fileLen; int pieceCount;

        // Session 1: a fresh OPFS-backed client downloads the whole file and finalizes → pieces + .torrent in OPFS.
        var client1 = new WebTorrentClient(new WebTorrentClientOptions { AsyncFileSystem = fs });
        try
        {
            var t1 = await client1.AddAsync(url, ct: cts.Token);
            var f1 = t1.Files[0];
            var bytes1 = await f1.ReadAsync(0, (int)f1.Length, cts.Token);
            if (bytes1 == null || bytes1.Length != f1.Length) throw new Exception($"session1 downloaded {bytes1?.Length ?? 0}/{f1.Length}");
            for (int i = 0; i < 300 && t1.LazyHash; i++) await Task.Delay(50, cts.Token);
            if (t1.LazyHash) throw new Exception("session1 did not finalize");
            infohash = t1.InfoHash; fileLen = f1.Length; pieceCount = t1.PieceCount;
        }
        catch (UnsupportedTestException) { throw; }
        catch (Exception ex) when (ex.Message.Contains("No connection") || ex.Message.Contains("network")
            || ex.Message.Contains("preparing") || ex is TimeoutException)
        { throw new UnsupportedTestException($"hub/network unavailable: {ex.Message}"); }
        finally { await client1.DisposeAsync(); }

        // Session 2 (= page reload): a NEW client over the SAME OPFS restores from storage. No web seed needed.
        var client2 = new WebTorrentClient(new WebTorrentClientOptions { AsyncFileSystem = fs });
        try
        {
            await client2.RestoreFromStorageAsync();

            var t2 = client2.Torrents.FirstOrDefault(t =>
                string.Equals(t.InfoHash, infohash, StringComparison.OrdinalIgnoreCase));
            if (t2 == null)
                throw new Exception($"reload did NOT restore the torrent (infohash {infohash}) — the /cache page would be empty and it would re-download");

            // The fix: every piece restored from OPFS, Done immediately — NO re-download.
            if (!t2.Done || t2.CompletedPieces != pieceCount)
                throw new Exception($"restored only {t2.CompletedPieces}/{pieceCount} pieces (Done={t2.Done}) — the missing pieces would be RE-DOWNLOADED");
            if (t2.Files[0].Length != fileLen)
                throw new Exception($"restored length {t2.Files[0].Length} != {fileLen}");

            // Bytes are served straight from the persisted pieces.
            var head = await t2.Files[0].ReadAsync(0, 4096, cts.Token);
            if (head == null || head.Length != 4096) throw new Exception("restored read returned no data");

            Console.WriteLine($"[LazyHash] reopened: {t2.CompletedPieces}/{pieceCount} pieces restored from OPFS, ZERO re-download, listed on /cache (infohash {infohash})");
        }
        finally { await client2.DisposeAsync(); }
    }

    /// <summary>
    /// Deterministic zero-copy SPAN coalescing: downloading the whole file over the browser zero-copy web-seed path
    /// must issue a few large (~1 MiB) JS.Fetch GETs, NOT one per 16 KiB piece. Uses an OPFS-backed client so the
    /// zero-copy path (JS.Fetch → SubtleCrypto → OPFS) is taken; browser-only (the zero-copy span path is the
    /// no-.NET-copy browser arm). Proves the per-piece GET storm that made delivery slow is collapsed.
    /// </summary>
    [TestMethod(Timeout = 300000, RetryCount = 2, Category = "HeavyModel")]
    public async Task LazyHash_Coalescing_ZeroCopySpans_FewLargeGets()
    {
        var fs = Client.AsyncFileSystem;
        if (fs == null) throw new UnsupportedTestException("no OPFS (desktop runtime) — the zero-copy span path is browser-only");
        var url = LazyHubUrl;

        // Reference infohash (eager) — also proves the zero-copy SubtleCrypto compute matches eager .NET hashing.
        string expectedInfohash;
        using (var ects = new CancellationTokenSource(TimeSpan.FromMinutes(2)))
            expectedInfohash = (await TorrentCreator.CreateFromUrlAsync(url,
                new TorrentCreatorOptions { PieceLength = Torrent.WebSeedMinPieceLength }, ects.Token)).metadata.InfoHash;

        var client = new WebTorrentClient(new WebTorrentClientOptions { AsyncFileSystem = fs });
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(4));
            WebConn.FetchRangeCount = 0;
            Torrent.ZcSpanCount = 0; Torrent.ZcSpanMaxPieces = 0; Torrent.ZcSpanPiecesTotal = 0; Torrent.ZcSpanLog.Clear();

            var t = await client.AddAsync(url, ct: cts.Token);
            var f = t.Files[0];
            var all = await f.ReadAsync(0, (int)f.Length, cts.Token);
            if (all == null || all.Length != f.Length) throw new Exception($"downloaded {all?.Length ?? 0}/{f.Length}");

            // Finalize must produce the SAME infohash as eager — i.e. SubtleCrypto compute (JS-side) == eager hashing.
            for (int i = 0; i < 300 && t.LazyHash; i++) await Task.Delay(50, cts.Token);
            if (!string.Equals(t.InfoHash, expectedInfohash, StringComparison.OrdinalIgnoreCase))
                throw new Exception($"zero-copy finalized infohash '{t.InfoHash}' != eager '{expectedInfohash}' — SubtleCrypto compute is wrong");

            int gets = WebConn.FetchRangeCount;
            int pieceCount = t.PieceCount;
            int spanAvg = Torrent.ZcSpanCount > 0 ? (int)(Torrent.ZcSpanPiecesTotal / Torrent.ZcSpanCount) : 0;
            string diag = $"{gets} GETs, {pieceCount} pieces, {Torrent.ZcSpanCount} spans, avg {spanAvg}/span, max {Torrent.ZcSpanMaxPieces}/span";
            // The span path fetches a whole piece's bytes in ONE Range GET, so GETs ≈ pieceCount — NOT the per-16
            // KiB-BLOCK storm the .NET block path would issue (pieceCount × blocks-per-piece). With the large
            // web-seed piece size, pieceCount itself is small, so this is few GETs in absolute terms.
            if (gets > pieceCount + 24)
                throw new Exception($"GET storm: {diag} for {f.Length} B (expected ~1 GET/piece)");

            Console.WriteLine($"[LazyHash] zero-copy GETs: {diag} / {f.Length} B");
        }
        catch (UnsupportedTestException) { throw; }
        catch (Exception ex) when (ex.Message.Contains("No connection") || ex.Message.Contains("network")
            || ex.Message.Contains("preparing") || ex is TimeoutException)
        {
            throw new UnsupportedTestException($"hub/network unavailable: {ex.Message}");
        }
        finally { await client.DisposeAsync(); }
    }

    /// <summary>
    /// SPEED — the real production case (Rule 5: test the full case, not a 10 MB subset). Download a ~0.5 GB GGUF
    /// model from the hub web seed via lazy-hash and assert it finishes well under 60s. The hub is on a 1 Gb LAN and
    /// the WAN is 500 Mb symmetric fiber, so this is bandwidth-bound: a 0.5 GB model must NOT take minutes — that
    /// would mean per-piece overhead (OPFS writes / digests / interop) is dominating, not the link. Measures the
    /// pure background DOWNLOAD (waits for Done), not the model load. Browser-only (OPFS zero-copy path).
    /// </summary>
    [TestMethod(Timeout = 240000, RetryCount = 1, Category = "HeavyModel")]
    public async Task LazyHash_LargeModel_DownloadsFastFromHub()
    {
        var fs = Client.AsyncFileSystem;
        if (fs == null) throw new UnsupportedTestException("no OPFS (desktop runtime) — the zero-copy download path is browser-only");
        // ~0.5 GB Q8_0 GGUF — the actual model the /ai-chat demo serves.
        var url = $"{HubBaseUrl}/hf/Qwen/Qwen2.5-0.5B-Instruct-GGUF/qwen2.5-0.5b-instruct-q8_0.gguf";

        var client = new WebTorrentClient(new WebTorrentClientOptions { AsyncFileSystem = fs });
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));

            var t = await client.AddAsync(url, ct: cts.Token);
            var f = t.Files[0];
            if (f.Length < 200L * 1024 * 1024)
                throw new Exception($"expected a ~0.5 GB model, got {f.Length} B — wrong URL or hub cold");

            // The whole file is selected (deselect=false), so it downloads in the background. Time it to Done.
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (!t.Done && sw.Elapsed < TimeSpan.FromSeconds(150))
                await Task.Delay(200, cts.Token);
            sw.Stop();

            double mb = f.Length / 1024.0 / 1024.0;
            double mbps = mb / sw.Elapsed.TotalSeconds;
            if (!t.Done)
                throw new Exception($"download did NOT finish: {t.CompletedPieces}/{t.PieceCount} pieces ({mb:F0} MB) in {sw.Elapsed.TotalSeconds:F0}s = {mbps:F1} MB/s");
            if (sw.Elapsed.TotalSeconds > 60)
                throw new Exception($"TOO SLOW: {mb:F0} MB in {sw.Elapsed.TotalSeconds:F1}s = {mbps:F1} MB/s ({t.PieceCount} pieces @ {t.PieceLength / 1024}KiB) — must be < 60s on LAN/fiber");

            Console.WriteLine($"[LazyHash] LARGE MODEL: {mb:F0} MB in {sw.Elapsed.TotalSeconds:F1}s = {mbps:F1} MB/s ({t.PieceCount} pieces @ {t.PieceLength / 1024}KiB)");
        }
        catch (UnsupportedTestException) { throw; }
        catch (Exception ex) when (ex.Message.Contains("No connection") || ex.Message.Contains("network")
            || ex.Message.Contains("preparing") || ex is TimeoutException)
        {
            throw new UnsupportedTestException($"hub/network unavailable: {ex.Message}");
        }
        finally { await client.DisposeAsync(); }
    }

    /// <summary>
    /// Lazy-Hash + DESELECT: the one combination nothing covered. ILGPU.ML's model inspector adds a hub model
    /// by URL (so: a Lazy-Hash torrent) with <c>Deselect = true</c> and reads only structure, expecting the
    /// weights never to be fetched. Deselect-on-MAGNET is covered by
    /// <c>HuggingFaceProxy_DeselectedRead_DownloadsOnlyTouchedPieces</c> and lazy random-access is covered by
    /// <c>LazyHash_RandomAccessRead_MidDownload_MatchesDirectRange</c>, but the INTERSECTION - deselect on a
    /// lazy torrent, whose piece hashes are zeroed placeholders and whose piece length is chosen by
    /// <see cref="Torrent.LazyPieceLength"/> rather than by a real .torrent - had no test at all. That gap is
    /// what let a 100%-download surface in a consumer while this suite stayed green.
    /// </summary>
    /// <remarks>
    /// ⚠️ The budget is derived from the pieces the READ ACTUALLY SPANS, never a flat piece count. A flat
    /// "3 pieces" budget is VACUOUS the moment the piece length is large relative to the file - at the 4 MiB
    /// web-seed floor a 10 MB model is 3 pieces, so "3 pieces" is 123% of the file and the assertion can never
    /// fail. (That is exactly the shape of the ILGPU.ML assertion this was written to explain.) Deriving from
    /// the span keeps the test meaningful at ANY geometry, and it refuses to run rather than pass vacuously if
    /// the geometry ever makes the claim unprovable.
    /// </remarks>
    [TestMethod(Timeout = 300000, RetryCount = 2, Category = "HeavyModel")]
    public async Task LazyHash_DeselectedRead_DoesNotFetchBeyondTheReadSpan()
    {
        var url = LazyHubUrl;
        var client = new WebTorrentClient();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(4));

            var t = await client.AddAsync(url, new AddTorrentOptions { Deselect = true }, cts.Token);
            if (!t.LazyHash) throw new Exception("expected a Lazy-Hash torrent when adding an http(s) URL");
            if (t.Files == null || t.Files.Length != 1 || t.Files[0].Length <= 0)
                throw new Exception("lazy shell did not resolve a single non-empty file from the web seed");

            var file = t.Files[0];
            int chunk = (int)Math.Min(4096, file.Length);

            // Pieces this read touches, plus ONE piece of slack for read-ahead / boundary overlap. Over-fetch
            // at piece boundaries is inherent to torrents and expected - the claim under test is only that a
            // deselected read does not pull the REST of the file.
            long pieceLen = t.PieceLength;
            if (pieceLen <= 0) throw new Exception($"torrent reported PieceLength={pieceLen}");
            long spanned = ((chunk - 1) / pieceLen) + 1;
            long budget = (spanned + 1) * pieceLen;

            // Refuse to assert something this geometry cannot express, rather than passing vacuously.
            if (budget >= file.Length)
                throw new UnsupportedTestException(
                    $"geometry cannot express the claim: pieceLength={pieceLen} over a {file.Length}-byte file " +
                    $"means the {spanned}-piece read span + 1 slack piece ({budget} bytes) already covers the whole " +
                    "file. Needs a larger model or a smaller piece length for this assertion to mean anything.");

            var head = await file.ReadAsync(0, chunk, cts.Token);
            if (head == null || head.Length != chunk)
                throw new Exception($"deselected read returned {head?.Length ?? 0} bytes; expected {chunk}");

            // Oracle: the bytes must be RIGHT, not merely few. A deselected read that returns zeros would
            // otherwise satisfy a bytes-downloaded assertion perfectly.
            byte[] expected;
            using (var http = new HttpClient())
            using (var req = new HttpRequestMessage(HttpMethod.Get, url))
            {
                req.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, chunk - 1);
                using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseContentRead, cts.Token);
                resp.EnsureSuccessStatusCode();
                expected = await resp.Content.ReadAsByteArrayAsync(cts.Token);
            }
            if (expected.Length != chunk)
                throw new UnsupportedTestException($"web seed ignored Range (returned {expected.Length}, not {chunk}) - needs a range-capable seed");
            if (!head.SequenceEqual(expected))
                throw new Exception("deselected head read bytes != direct Range GET - the on-demand read is wrong");

            long downloaded = t.Downloaded;
            if (downloaded > budget)
                throw new Exception(
                    $"deselected read downloaded {downloaded} bytes of a {file.Length}-byte file, over a budget of " +
                    $"{budget} ({spanned} spanned piece(s) + 1 slack, pieceLength={pieceLen}) - deselect did not " +
                    "suppress the whole-file fetch, so structure inspection would pull weights");

            Console.WriteLine($"[LazyHash] deselected {chunk} B head read: downloaded {downloaded} of {file.Length} B "
                            + $"(budget {budget}, pieceLength={pieceLen}), bytes match a direct Range GET");
        }
        catch (UnsupportedTestException) { throw; }
        catch (Exception ex) when (ex.Message.Contains("No connection") || ex.Message.Contains("network")
            || ex.Message.Contains("preparing") || ex is TimeoutException)
        {
            throw new UnsupportedTestException($"hub/network unavailable: {ex.Message}");
        }
        finally { await client.DisposeAsync(); }
    }
}
