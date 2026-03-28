using SpawnDev.UnitTesting;
using SpawnDev.WebTorrent.Storage;
using SpawnDev.WebTorrent.Torrent;
using SpawnDev.WebTorrent.Transports;

namespace SpawnDev.WebTorrent.Demo.Shared.UnitTests;

/// <summary>
/// End-to-end download pipeline tests.
/// Tests the full path: WebSeedConnection → DownloadCoordinator → PieceManager → ChunkStore.
/// These are the tests that should catch download bugs BEFORE TJ ever sees the demo.
/// </summary>
public abstract partial class WebTorrentTestBase
{
    // ═══════════════════════════════════════════════════════════
    //  WebSeedConnection — URL Construction
    // ═══════════════════════════════════════════════════════════

    [TestMethod]
    public async Task WebSeed_UrlConstruction_SingleFile()
    {
        var data = new byte[32768];
        var (_, metadata) = TorrentCreator.CreateFromBytes("test file.bin", data,
            new TorrentCreatorOptions { PieceLength = 16384 });

        // Verify file name in metadata has a space
        if (metadata.Files[0].Path != "test file.bin")
            throw new Exception($"File path: '{metadata.Files[0].Path}'");

        // Verify the web seed can be constructed without errors
        var seed = new WebSeedConnection(new HttpClient { Timeout = TimeSpan.FromSeconds(2) },
            "https://example.com/files", metadata);

        // Try to download — will fail (example.com) but verifies no crash in URL construction
        var result = await seed.DownloadPieceAsync(0);
        // result is null (expected — example.com doesn't serve torrents)

        Console.WriteLine("[Download] URL construction: no crash with spaces in filename");
    }

    // ═══════════════════════════════════════════════════════════
    //  Full Pipeline — Real Web Seed Download (Big Buck Bunny)
    // ═══════════════════════════════════════════════════════════

    [TestMethod(Timeout = 60000)]
    public async Task Download_WebSeed_FirstPiece_BigBuckBunny()
    {
        // Fetch the .torrent file
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        byte[] torrentBytes;
        try
        {
            torrentBytes = await http.GetByteArrayAsync("https://webtorrent.io/torrents/big-buck-bunny.torrent");
        }
        catch (Exception ex)
        {
            throw new UnsupportedTestException($"Could not fetch .torrent: {ex.Message}");
        }

        var metadata = TorrentParser.Parse(torrentBytes);
        Console.WriteLine($"[Download] Metadata: {metadata.Name}, {metadata.TotalLength:N0} bytes, {metadata.PieceCount} pieces, pieceLen={metadata.PieceLength}");
        Console.WriteLine($"[Download] Web seeds: {string.Join(", ", metadata.UrlList)}");
        Console.WriteLine($"[Download] Files: {string.Join(", ", metadata.Files.Select(f => $"{f.Path} ({f.Length:N0})"))}");

        // Create the full pipeline
        await using var store = new MemoryChunkStore(metadata.PieceLength);
        var pm = new PieceManager(metadata, store);

        // Create WebSeedConnection with proper URL
        var wsUrl = metadata.UrlList.Length > 0 ? metadata.UrlList[0] : null;
        if (wsUrl == null) throw new UnsupportedTestException("No web seed URL in .torrent");

        var seedHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        var seed = new WebSeedConnection(seedHttp, wsUrl.TrimEnd('/'), metadata);
        var seedLogs = new List<string>();
        seed.OnLog += (msg) =>
        {
            seedLogs.Add(msg);
            Console.WriteLine($"[Download] {msg}");
        };

        // Download piece 0
        Console.WriteLine("[Download] Downloading piece 0 via web seed...");
        var pieceData = await seed.DownloadPieceAsync(0);

        if (pieceData == null)
        {
            Console.WriteLine("[Download] FAILED — pieceData is null");
            foreach (var log in seedLogs)
                Console.WriteLine($"  LOG: {log}");
            throw new Exception($"Web seed returned null for piece 0. Logs: {string.Join(" | ", seedLogs)}");
        }

        Console.WriteLine($"[Download] Got {pieceData.Length} bytes for piece 0");

        // Verify via PieceManager
        var ok = await pm.ReceiveCompletePieceAsync(0, pieceData);
        if (!ok) throw new Exception("Piece 0 hash verification failed");

        Console.WriteLine($"[Download] Piece 0 verified! Progress: {pm.Progress:P1}");

        if (pm.CompletedCount != 1)
            throw new Exception($"Expected 1 completed piece, got {pm.CompletedCount}");
    }

    // ═══════════════════════════════════════════════════════════
    //  Full Pipeline — DownloadCoordinator with Web Seeds
    // ═══════════════════════════════════════════════════════════

    [TestMethod(Timeout = 120000)]
    public async Task Download_Coordinator_WebSeed_ThreePieces()
    {
        // Fetch .torrent
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        byte[] torrentBytes;
        try
        {
            torrentBytes = await http.GetByteArrayAsync("https://webtorrent.io/torrents/big-buck-bunny.torrent");
        }
        catch (Exception ex)
        {
            throw new UnsupportedTestException($"Could not fetch .torrent: {ex.Message}");
        }

        var metadata = TorrentParser.Parse(torrentBytes);

        // Create full pipeline
        await using var store = new MemoryChunkStore(metadata.PieceLength);
        var pm = new PieceManager(metadata, store);
        var coordinator = new DownloadCoordinator(pm, metadata);

        var logs = new List<string>();
        coordinator.OnLog += (msg) =>
        {
            logs.Add(msg);
            Console.WriteLine($"[Coordinator] {msg}");
        };
        coordinator.OnError += (ex) => Console.WriteLine($"[Coordinator] ERROR: {ex.Message}");

        var piecesCompleted = new List<int>();
        coordinator.OnPieceComplete += (idx) =>
        {
            piecesCompleted.Add(idx);
            Console.WriteLine($"[Coordinator] Piece {idx} complete ({piecesCompleted.Count} total)");
        };

        // Add web seed
        var wsUrl = metadata.UrlList.Length > 0 ? metadata.UrlList[0] : null;
        if (wsUrl == null) throw new UnsupportedTestException("No web seed URL");
        coordinator.AddWebSeed(new HttpClient { Timeout = TimeSpan.FromSeconds(30) }, wsUrl.TrimEnd('/'));

        // Start download
        coordinator.Start();

        // Wait for at least 3 pieces
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (piecesCompleted.Count < 3 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(500);
        }

        coordinator.Stop();

        Console.WriteLine($"[Coordinator] Final: {piecesCompleted.Count} pieces, logs: {logs.Count}");

        if (piecesCompleted.Count < 3)
        {
            Console.WriteLine("[Coordinator] FAILED — did not complete 3 pieces");
            foreach (var log in logs.TakeLast(20))
                Console.WriteLine($"  LOG: {log}");
            throw new Exception($"Expected at least 3 pieces, got {piecesCompleted.Count}. Last logs: {string.Join(" | ", logs.TakeLast(5))}");
        }

        Console.WriteLine($"[Coordinator] SUCCESS: {piecesCompleted.Count} pieces verified via web seed");
    }

    // ═══════════════════════════════════════════════════════════
    //  Full Pipeline — TorrentSwarm.AddWebSeed + StartDownload
    // ═══════════════════════════════════════════════════════════

    [TestMethod(Timeout = 120000)]
    public async Task Download_Swarm_WebSeed_EndToEnd()
    {
        // Fetch .torrent
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        byte[] torrentBytes;
        try
        {
            torrentBytes = await http.GetByteArrayAsync("https://webtorrent.io/torrents/sintel.torrent");
        }
        catch (Exception ex)
        {
            throw new UnsupportedTestException($"Could not fetch .torrent: {ex.Message}");
        }

        var metadata = TorrentParser.Parse(torrentBytes);
        Console.WriteLine($"[Swarm] {metadata.Name}: {metadata.TotalLength:N0} bytes, {metadata.PieceCount} pieces");

        // Create client and swarm
        await using var client = new WebTorrentClient();
        var swarm = await client.AddAsync(metadata);

        var logs = new List<string>();
        var piecesVerified = 0;

        swarm.OnLog += (msg) =>
        {
            logs.Add(msg);
            Console.WriteLine($"[Swarm] {msg}");
        };
        swarm.OnPieceVerified += (_) => Interlocked.Increment(ref piecesVerified);
        swarm.OnError += (ex) => Console.WriteLine($"[Swarm] ERROR: {ex.Message}");

        // Add web seed and start
        var wsUrl = metadata.UrlList.Length > 0 ? metadata.UrlList[0] : null;
        if (wsUrl == null) throw new UnsupportedTestException("No web seed URL");

        swarm.AddWebSeed(wsUrl.TrimEnd('/'));
        swarm.StartDownload();

        // Wait for at least 3 pieces
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (piecesVerified < 3 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(500);
        }

        swarm.StopDownload();

        Console.WriteLine($"[Swarm] Final: {piecesVerified} verified pieces");

        if (piecesVerified < 3)
        {
            Console.WriteLine("[Swarm] FAILED");
            foreach (var log in logs.TakeLast(20))
                Console.WriteLine($"  LOG: {log}");
            throw new Exception($"Expected at least 3 verified pieces, got {piecesVerified}. Last logs: {string.Join(" | ", logs.TakeLast(5))}");
        }

        Console.WriteLine($"[Swarm] SUCCESS: {piecesVerified} pieces downloaded and verified via web seed");
    }

    // ═══════════════════════════════════════════════════════════
    //  Demo Page Simulation — exact flow of Torrents.razor
    // ═══════════════════════════════════════════════════════════

    [TestMethod(Timeout = 120000)]
    public async Task Download_DemoPageFlow_BigBuckBunny()
    {
        var magnetUri = "magnet:?xt=urn:btih:dd8255ecdc7ca55fb0bbf81323d87062db1f6d1c&dn=Big+Buck+Bunny&ws=https%3A%2F%2Fwebtorrent.io%2Ftorrents%2F&xs=https%3A%2F%2Fwebtorrent.io%2Ftorrents%2Fbig-buck-bunny.torrent";

        await using var client = new WebTorrentClient();
        var swarm = await client.AddAsync(magnetUri);

        // Parse xs= and ws= (same as Torrents.razor)
        string? torrentUrl = null;
        var webSeedUrls = new List<string>();
        foreach (var part in magnetUri.Split('&'))
        {
            var p = part.Contains('?') ? part.Split('?').Last() : part;
            var eq = p.IndexOf('=');
            if (eq < 0) continue;
            var k = p[..eq];
            var v = Uri.UnescapeDataString(p[(eq + 1)..].Replace('+', ' '));
            if (k == "xs") torrentUrl = v;
            if (k == "ws") webSeedUrls.Add(v);
        }

        if (torrentUrl == null) throw new UnsupportedTestException("No xs= URL");

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        byte[] torrentBytes;
        try { torrentBytes = await http.GetByteArrayAsync(torrentUrl); }
        catch (Exception ex) { throw new UnsupportedTestException($"Fetch failed: {ex.Message}"); }

        var metadata = TorrentParser.Parse(torrentBytes);
        if (!metadata.InfoHash.SequenceEqual(swarm.InfoHash))
            throw new Exception("Info hash mismatch");

        foreach (var ws in metadata.UrlList)
            if (!webSeedUrls.Contains(ws)) webSeedUrls.Add(ws);

        Console.WriteLine($"[DemoFlow] {metadata.Name}, {metadata.TotalLength:N0} bytes, {metadata.PieceCount} pieces, {webSeedUrls.Count} seeds");

        swarm.SetMetadata(metadata);
        foreach (var ws in webSeedUrls) swarm.AddWebSeed(ws.TrimEnd('/'));

        int piecesVerified = 0;
        swarm.OnPieceVerified += (_) => Interlocked.Increment(ref piecesVerified);
        swarm.OnLog += (msg) => Console.WriteLine($"[DemoFlow] {msg}");

        swarm.StartDownload();

        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (piecesVerified < 3 && DateTime.UtcNow < deadline)
            await Task.Delay(500);

        swarm.StopDownload();

        if (piecesVerified < 3)
            throw new Exception($"Demo flow failed: {piecesVerified} pieces");

        Console.WriteLine($"[DemoFlow] SUCCESS — {piecesVerified} pieces via demo flow");
    }
}
