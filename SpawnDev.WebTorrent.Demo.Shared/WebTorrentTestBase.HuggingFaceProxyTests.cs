using System.Net.Http.Json;
using SpawnDev.UnitTesting;
using SpawnDev.WebTorrent;

namespace SpawnDev.WebTorrent.Demo.Shared;

/// <summary>
/// Live integration against the SpawnDev HuggingFace proxy hub (hub.spawndev.com). Proves the
/// production model-delivery path: a browser asks the proxy for a magnet, adds it, and resolves
/// metadata WITHOUT any peer in the swarm — because the magnet carries an HTTP exact-source
/// (<c>xs=</c>) URL to the full .torrent. Before the <c>xs=</c> fetch existed, the first client
/// (zero peers, web-seed-only hub) had no ut_metadata source and stalled forever. These tests
/// guard that bootstrap. Requires internet; the hub fetches the model from HuggingFace on the
/// first request, so the magnet GET can be slow on a cold cache (generous timeout).
/// </summary>
public abstract partial class WebTorrentTestBase
{
    // Public hub running SpawnDev.WebTorrent.Server.HuggingFace.
    const string HubBaseUrl = "https://hub.spawndev.com:44365";

    // A small, real ONNX model file. The proxy resolves it to
    // https://huggingface.co/{repo}/resolve/main/{path} upstream, caches + torrents it.
    const string HfRepoId = "onnx-community/mobilenetv3_small_100.lamb_in1k";
    const string HfFilePath = "onnx/model.onnx";

    private sealed record MagnetResult(string MagnetUri, string RepoId, string FilePath, string WebSeed);

    /// <summary>Ask the proxy for a magnet. Blocks server-side until the file is fetched + torrented.</summary>
    private static async Task<string> GetHubMagnetAsync(HttpClient http)
    {
        // 120s: a cold-cache first request makes the hub download + SHA-256 hash the whole file.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        var url = $"{HubBaseUrl}/magnet/{HfRepoId}/{HfFilePath}";
        var result = await http.GetFromJsonAsync<MagnetResult>(url, cts.Token);
        if (result == null || string.IsNullOrEmpty(result.MagnetUri))
            throw new Exception($"Hub returned no magnet for {HfRepoId}/{HfFilePath} ({url})");
        return result.MagnetUri;
    }

    /// <summary>
    /// Core guard: a hub magnet must resolve metadata peer-free via its HTTP exact-source.
    /// This is the test that FAILS without the xs= bootstrap (no peers + web-seed-only hub).
    /// </summary>
    [TestMethod(Timeout = 240000, RetryCount = 2)]
    public async Task HuggingFaceProxy_Magnet_ResolvesMetadataPeerFree()
    {
        using var http = new HttpClient();
        var magnet = await GetHubMagnetAsync(http);

        var client = new WebTorrentClient();
        try
        {
            var torrent = client.Add(magnet);

            // The magnet must carry an HTTP exact-source — that is what enables peer-free metadata.
            if (string.IsNullOrEmpty(torrent.ExactSourceUrl))
                throw new Exception(
                    "hub magnet has no HTTP xs= exact-source; cannot bootstrap metadata peer-free. " +
                    $"magnet={magnet}");

            // No peers should be needed — metadata comes from the xs= .torrent over HTTP.
            var deadline = DateTime.UtcNow.AddSeconds(45);
            while (!torrent.HasMetadata && DateTime.UtcNow < deadline)
                await Task.Delay(500);

            if (!torrent.HasMetadata)
                throw new Exception(
                    $"no metadata after 45s with zero peers (peers={torrent.NumPeers}). " +
                    "The HTTP xs= exact-source .torrent fetch did not resolve metadata.");

            if (torrent.Files == null || torrent.Files.Length < 1)
                throw new Exception("metadata resolved but Files is empty");

            if (torrent.Files[0].Length <= 0)
                throw new Exception($"file length is {torrent.Files[0].Length}; expected > 0");
        }
        finally
        {
            await client.DisposeAsync();
        }
    }

    /// <summary>
    /// ZERO-COPY browser web-seed download: on an OPFS-backed store (browser), a single-file hub torrent
    /// downloads with each piece's bytes staying in JS end to end (browser fetch -> SubtleCrypto 16 KiB-leaf
    /// hashes -> .NET Merkle tree over only the 32-byte hashes -> OPFS write) — the piece data NEVER enters
    /// the .NET/WASM heap. Asserts the zero-copy path actually fired (ZeroCopyPiecesVerified > 0) and the
    /// download completed (so every piece passed SubtleCrypto+Merkle verification). Browser/OPFS-only;
    /// desktop uses the byte[] path (covered by the other HF tests).
    /// </summary>
    [TestMethod(Timeout = 240000, RetryCount = 1)]
    public async Task HuggingFaceProxy_ZeroCopy_BrowserOpfsDownload()
    {
        if (!OperatingSystem.IsBrowser())
            throw new UnsupportedTestException("Zero-copy web-seed download is browser/OPFS-only");

        using var http = new HttpClient();
        var magnet = await GetHubMagnetAsync(http);

        // The shared Client has an OPFS AsyncFileSystem in the browser -> AsyncFSChunkStore
        // (SupportsUint8Array) -> the zero-copy web-seed path fires for this single-file torrent.
        var torrent = Client.Add(magnet);
        try
        {
            var deadline = DateTime.UtcNow.AddSeconds(120);
            while (!torrent.Done && DateTime.UtcNow < deadline)
                await Task.Delay(200);

            if (!torrent.Done)
                throw new Exception(
                    $"zero-copy download did not complete in 120s: Done={torrent.Done} " +
                    $"progress={torrent.Progress:F3} zeroCopyVerified={torrent.ZeroCopyPiecesVerified} " +
                    $"peers={torrent.NumPeers} hasMetadata={torrent.HasMetadata}");

            if (torrent.ZeroCopyPiecesVerified <= 0)
                throw new Exception(
                    "zero-copy path did not fire (ZeroCopyPiecesVerified=0) — store was not OPFS-backed");

            if (torrent.Files == null || torrent.Files.Length != 1 || torrent.Files[0].Length <= 0)
                throw new Exception("expected a single non-empty file");

            // Done == true means every piece passed SubtleCrypto+Merkle verification on the zero-copy path.
            // Read the head back to confirm real content is retrievable from the OPFS store.
            var head = await torrent.Files[0].ReadAsync(0, 64);
            if (head.Length != 64)
                throw new Exception($"head read returned {head.Length} bytes, expected 64");
        }
        finally
        {
            await Client.RemoveAsync(torrent);
        }
    }

    /// <summary>
    /// Keeps the zero-copy verify profiler ALIVE and honest, and records what it measured.
    /// </summary>
    /// <remarks>
    /// 🔴 THIS PROFILER WAS DEAD FOR MONTHS. <c>EnableZcProfiling</c> was hardcoded false and its counters
    /// were accumulated and read NOWHERE, so the "BIG-ARRAY read experiment" it was added to settle could
    /// never be settled. Worse, it could not have answered the question anyway: <c>ZcReadMs</c> summed the
    /// per-leaf <c>Set</c> loop AND the single big <c>ReadBytes</c>, which are exactly the two terms being
    /// compared. This test exists so that never silently happens again.
    /// <para>
    /// ⭐ MEASURED 2026-09-14, hub mobilenetv3 model, 1 MiB pieces (64 leaves), 10 pieces, browser:
    /// </para>
    /// <code>
    ///   v2Wall 82.2 ms | digestFire 45.6 (55.5%) digestWait 10.5 (12.8%) leafSet 16.3 (19.8%)
    ///                  | tree 8.0 (9.7%) marshal 1.5 (1.8%) split 0.1 (0.12%)
    ///                  | remainder 0.1 ms (0.1%)
    /// </code>
    /// <para>
    /// ⭐⭐ THE EXPERIMENT'S ANSWER: the big-array read WORKS - one marshal of the whole hash block costs
    /// 1.5 ms - but the per-leaf <c>allHashes.Set(...)</c> loop costs 16.3 ms, 11x that. The cost is the
    /// CROSSING COUNT, not the per-call marshal. Any further work belongs on the ~64 per-leaf crossings
    /// (digestFire + leafSet = 75% of verify), never on the .NET side.
    /// </para>
    /// <para>
    /// ⚠️ AND IT KILLED A "FIX" I WAS ABOUT TO MAKE. The jagged <c>byte[32]</c> split before the Merkle
    /// tree is real redundant copying, and it is 0.12% of verify. Rewriting a BEP 52 hash path for that
    /// would have been risk for nothing. An operation count is not a cost.
    /// </para>
    /// </remarks>
    [TestMethod(Timeout = 240000, RetryCount = 1)]
    public async Task ZcProfile_RecordsVerifyPhaseSplit_AndAccountsForItsOwnWall()
    {
        if (!OperatingSystem.IsBrowser())
            throw new UnsupportedTestException("Zero-copy verify is browser/OPFS-only");

        using var http = new HttpClient();
        var magnet = await GetHubMagnetAsync(http);

        Torrent.EnableZcProfiling = true;
        Torrent.ResetZcProfile();
        var torrent = Client.Add(magnet);
        try
        {
            var deadline = DateTime.UtcNow.AddSeconds(120);
            while (!torrent.Done && DateTime.UtcNow < deadline)
                await Task.Delay(200);

            var report = Torrent.ZcProfileReport();
            if (!torrent.Done)
                throw new Exception($"download did not complete in 120s: progress={torrent.Progress:F3} || {report}");

            // The profiler must have SEEN something. A green run on an all-zero profile would mean the
            // counters had quietly stopped being written - which is the state this test exists to prevent.
            if (Torrent.ZcV2Pieces == 0 && Torrent.ZcV1Pieces == 0)
                throw new Exception($"the zero-copy verify profiler recorded NOTHING || {report}");

            // This torrent is v2, so the Merkle leaf path is the one that must have run. Asserting the
            // BRANCH (not just "some pieces") stops a v1 fallback from reading as a healthy v2 profile.
            if (torrent.MetaVersion == 2 && Torrent.ZcV2Pieces == 0)
                throw new Exception($"MetaVersion=2 but no piece took the v2 Merkle branch || {report}");

            // An accounting check on the instrument itself: if the phases do not add up to the wall they
            // claim to describe, the split is measuring something other than what ran and every number
            // above it is untrustworthy. 0.1% when this was written; 25% is a loose guard against drift.
            var accounted = Torrent.ZcDigestFireMs + Torrent.ZcDigestWaitMs + Torrent.ZcLeafSetMs
                          + Torrent.ZcMarshalMs + Torrent.ZcSplitMs + Torrent.ZcTreeMs;
            var remainder = Torrent.ZcV2WallMs - accounted;
            if (Torrent.ZcV2WallMs > 0 && Math.Abs(remainder) / Torrent.ZcV2WallMs > 0.25)
                throw new Exception($"verify phase split does not account for its own wall time || {report}");
        }
        finally
        {
            Torrent.EnableZcProfiling = false;
            await Client.RemoveAsync(torrent);
        }
    }

    /// <summary>
    /// AddAsync (documented in README + Docs/huggingface.md) must return a ready torrent once
    /// metadata resolves, then a range read must pull real bytes from the HTTP web seed.
    /// </summary>
    [TestMethod(Timeout = 240000, RetryCount = 2)]
    public async Task HuggingFaceProxy_AddAsync_ReadsBytesFromWebSeed()
    {
        using var http = new HttpClient();
        var magnet = await GetHubMagnetAsync(http);

        var client = new WebTorrentClient();
        try
        {
            using var addCts = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            var torrent = await client.AddAsync(magnet, ct: addCts.Token);

            if (!torrent.HasMetadata)
                throw new Exception("AddAsync returned but torrent has no metadata");
            if (torrent.Files == null || torrent.Files.Length < 1)
                throw new Exception("AddAsync returned but Files is empty");

            // Read the first 4 KiB — served by the hub web seed (zero peers in the swarm).
            var head = await torrent.Files[0].ReadAsync(0, 4096, addCts.Token);
            if (head == null || head.Length == 0)
                throw new Exception("read 0 bytes from web seed");

            // A real ONNX file is non-zero protobuf at offset 0 — guard against a zero-fill read.
            bool anyNonZero = false;
            foreach (var b in head) { if (b != 0) { anyNonZero = true; break; } }
            if (!anyNonZero)
                throw new Exception($"first {head.Length} bytes were all zero — web-seed read returned a hole");
        }
        finally
        {
            await client.DisposeAsync();
        }
    }

    /// <summary>
    /// Critical-piece prioritization guard. A read marks its pieces <c>Critical()</c>; the picker
    /// must fetch those AHEAD of the normal rarest/sequential walk, and <c>Critical()</c> must kick
    /// the request loop immediately. A TAIL-seek read (last piece = last in sequential order) is the
    /// worst case: before the fix, the read stalled ~21s (browser) because nothing kicked requests
    /// and the walk hadn't reached the tail. After the fix it resolves the tail piece directly, so
    /// even over the network + a fresh client it returns well under the old stall. Threshold is
    /// generous (network + cold metadata) but far below the broken behavior.
    /// </summary>
    [TestMethod(Timeout = 240000, RetryCount = 2)]
    public async Task HuggingFaceProxy_TailSeekRead_IsPrioritized()
    {
        using var http = new HttpClient();
        var magnet = await GetHubMagnetAsync(http);

        var client = new WebTorrentClient();
        try
        {
            using var addCts = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            var torrent = await client.AddAsync(magnet, ct: addCts.Token);

            if (torrent.Files == null || torrent.Files.Length < 1 || torrent.Files[0].Length <= 0)
                throw new Exception("AddAsync returned but Files is empty or zero-length");

            var file = torrent.Files[0];
            int chunk = (int)Math.Min(4096, file.Length);
            long tailOffset = file.Length - chunk;

            // Time ONLY the tail read (not the magnet fetch / metadata resolve).
            using var readCts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            var startUtc = DateTime.UtcNow;
            var tail = await file.ReadAsync(tailOffset, chunk, readCts.Token);
            var elapsed = DateTime.UtcNow - startUtc;

            if (tail == null || tail.Length != chunk)
                throw new Exception($"tail read returned {tail?.Length ?? 0} bytes; expected {chunk}");

            bool anyNonZero = false;
            foreach (var b in tail) { if (b != 0) { anyNonZero = true; break; } }
            if (!anyNonZero)
                throw new Exception($"tail {tail.Length} bytes all zero — read returned a hole, not the prioritized tail piece");

            // Before critical-first prioritization, a cold tail read stalled ~21s+ (browser).
            if (elapsed > TimeSpan.FromSeconds(15))
                throw new Exception(
                    $"tail-seek read took {elapsed.TotalSeconds:F1}s (> 15s) — critical-piece prioritization did not fetch the tail piece ahead of the sequential walk");
        }
        finally
        {
            await client.DisposeAsync();
        }
    }

    /// <summary>
    /// On-demand inspect guard. Adding a hub model DESELECTED + a small range read must download
    /// ONLY the touched pieces, not the whole file — this is what lets the Model Inspector read
    /// graph structure from a multi-GB checkpoint without pulling weights. Without the fix,
    /// ReadFileAsync auto-selected the entire file (it saw no selections) and downloaded everything.
    /// Reads the ONNX header and asserts <c>torrent.Downloaded</c> is ~one piece, far below the file.
    /// </summary>
    [TestMethod(Timeout = 240000, RetryCount = 2)]
    public async Task HuggingFaceProxy_DeselectedRead_DownloadsOnlyTouchedPieces()
    {
        using var http = new HttpClient();
        var magnet = await GetHubMagnetAsync(http);

        var client = new WebTorrentClient();
        try
        {
            using var addCts = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            var torrent = await client.AddAsync(magnet, new AddTorrentOptions { Deselect = true }, addCts.Token);

            if (torrent.Files == null || torrent.Files.Length < 1 || torrent.Files[0].Length <= 0)
                throw new Exception("AddAsync returned but Files is empty or zero-length");

            var file = torrent.Files[0];
            int chunk = (int)Math.Min(4096, file.Length);

            // Read the first 4 KiB (ONNX protobuf header). Deselected, so on-demand picking should
            // fetch only the head piece(s) via Critical(), not auto-select + download the whole file.
            var head = await file.ReadAsync(0, chunk, addCts.Token);
            if (head == null || head.Length != chunk)
                throw new Exception($"deselected read returned {head?.Length ?? 0} bytes; expected {chunk}");

            bool anyNonZero = false;
            foreach (var b in head) { if (b != 0) { anyNonZero = true; break; } }
            if (!anyNonZero)
                throw new Exception("deselected read returned an all-zero head — not a real on-demand fetch");

            // The whole point of deselect: do NOT download the whole file. A 4 KiB read at offset 0
            // touches ~1 piece; allow generous slack (3 pieces) for boundary/endgame. This is robust
            // to model size — a multi-MB model is many pieces, so 3 pieces is a tiny fraction.
            long downloaded = torrent.Downloaded;
            long budget = 3L * torrent.PieceLength;
            if (downloaded > budget)
                throw new Exception(
                    $"deselected read downloaded {downloaded} bytes (> {budget} = 3 pieces) of a {file.Length}-byte file " +
                    "— the whole-file auto-select was not suppressed; on-demand inspect would pull weights");
        }
        finally
        {
            await client.DisposeAsync();
        }
    }
}
