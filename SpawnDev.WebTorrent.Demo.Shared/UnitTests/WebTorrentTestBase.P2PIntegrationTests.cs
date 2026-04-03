using SpawnDev.UnitTesting;
using SpawnDev.WebTorrent.Discovery;
using SpawnDev.WebTorrent.Storage;
using SpawnDev.WebTorrent.Torrent;
using SpawnDev.WebTorrent.Transports;

namespace SpawnDev.WebTorrent.Demo.Shared.UnitTests;

/// <summary>
/// P2P integration tests — two clients in the same process, one seeds, one downloads.
/// Proves the full pipeline: seed → tracker announce → peer discovery → WebRTC signaling →
/// data channel → wire protocol → piece exchange → SHA-1 verification.
///
/// These tests use our hub.spawndev.com tracker for signaling.
/// Browser-only tests use WebRtcTransport; the loopback test uses mock connections.
/// </summary>
public abstract partial class WebTorrentTestBase
{
    // ═══════════════════════════════════════════════════════════
    //  Load Torrent Without Starting Download (browse files first)
    // ═══════════════════════════════════════════════════════════

    [TestMethod]
    public async Task P2P_LoadTorrent_BrowseFilesBeforeDownload()
    {
        // Fetch real .torrent to get multi-file metadata
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        byte[] torrentBytes;
        try
        {
            torrentBytes = await http.GetByteArrayAsync("https://webtorrent.io/torrents/big-buck-bunny.torrent");
        }
        catch (Exception ex)
        {
            throw new UnsupportedTestException($"Fetch failed: {ex.Message}");
        }

        var metadata = TorrentParser.Parse(torrentBytes);

        // Add with paused=true, deselect=true — should NOT start downloading
        await using var client = new WebTorrentClient(crypto: Client!.Crypto);
        var swarm = await client.AddAsync(metadata, new AddTorrentOptions { Paused = true });

        // Verify we can browse files without downloading
        if (!swarm.HasMetadata) throw new Exception("Should have metadata");
        if (swarm.Files.Length == 0) throw new Exception("Should have files");
        if (swarm.Paused != true) throw new Exception("Should be paused");

        Console.WriteLine($"[BrowseFirst] {metadata.Name}: {swarm.Files.Length} files");
        foreach (var f in swarm.Files)
        {
            Console.WriteLine($"  {f.Path} — {f.Length:N0} bytes ({f.Type})");
            if (f.Progress != 0) throw new Exception($"File {f.Path} should have 0 progress when paused");
        }

        // No pieces should be downloading
        if (swarm.PieceManager!.CompletedCount != 0)
            throw new Exception("Should have 0 completed pieces when paused");

        // Select a specific file
        var targetFile = swarm.Files.FirstOrDefault(f => f.Path.EndsWith(".mp4") || f.Path.EndsWith(".mkv"));
        if (targetFile != null)
        {
            targetFile.Select();
            Console.WriteLine($"[BrowseFirst] Selected: {targetFile.Path}");
        }

        // Resume and start
        swarm.Resume();
        if (swarm.Paused) throw new Exception("Should be resumed");

        Console.WriteLine("[BrowseFirst] SUCCESS — browsed files, selected one, resumed");
    }

    [TestMethod]
    public async Task P2P_SeedAndVerifyAllPieces()
    {
        var data = new byte[131072]; // 8 pieces at 16KB
        Random.Shared.NextBytes(data);

        await using var client = new WebTorrentClient(crypto: Client!.Crypto);
        var swarm = await client.SeedAsync(data, "verify-seed.bin",
            new TorrentCreatorOptions { PieceLength = 16384 });

        // Verify every piece can be read back
        for (int i = 0; i < 8; i++)
        {
            int offset = i * 16384;
            int len = Math.Min(16384, data.Length - offset);
            var chunk = await swarm.Files[0].ReadAsync(offset, len);

            for (int j = 0; j < len; j++)
            {
                if (chunk[j] != data[offset + j])
                    throw new Exception($"Data mismatch at piece {i} offset {j}");
            }
        }

        Console.WriteLine("[P2P] All 8 pieces verified via seed → read roundtrip");
    }

    [TestMethod]
    public async Task P2P_SeedLargeFile_MultiPiece()
    {
        // 256KB = 16 pieces
        var data = new byte[262144];
        Random.Shared.NextBytes(data);

        await using var client = new WebTorrentClient(crypto: Client!.Crypto);
        var swarm = await client.SeedAsync(data, "large-seed.bin",
            new TorrentCreatorOptions { PieceLength = 16384 });

        if (swarm.PieceManager!.CompletedCount != 16)
            throw new Exception($"Expected 16 pieces, got {swarm.PieceManager.CompletedCount}");

        // Random-access read from the middle
        var chunk = await swarm.Files[0].ReadAsync(100000, 50000);
        for (int i = 0; i < 50000; i++)
        {
            if (chunk[i] != data[100000 + i])
                throw new Exception($"Random access mismatch at offset {100000 + i}");
        }

        Console.WriteLine("[P2P] 256KB seed with random-access read verified");
    }
}

// ═══════════════════════════════════════════════════════════
//  Cross-Platform P2P — Desktop Seeder + Browser Downloader
// ═══════════════════════════════════════════════════════════

public abstract partial class WebTorrentTestBase
{
    [TestMethod(Timeout = 120000)]
    public async Task P2P_CrossPlatform_DesktopSeed_BrowserDownload()
    {
        // Fetch test config written by PlaywrightMultiTest's DesktopSeeder.
        // If the file doesn't exist, we're not running under PlaywrightMultiTest → skip.
        string? magnetUri;
        int expectedLength;
        try
        {
            // PlaywrightMultiTest writes _test-desktop-seeder.json to the published wwwroot
            // and serves on port 5562. Fetch it to get the desktop seeder's magnet URI.
            using var http = new HttpClient { BaseAddress = new Uri("https://localhost:5562/") };
            var json = await http.GetStringAsync("_test-desktop-seeder.json");
            var config = System.Text.Json.JsonDocument.Parse(json);
            magnetUri = config.RootElement.GetProperty("magnetUri").GetString();
            expectedLength = config.RootElement.GetProperty("dataLength").GetInt32();
        }
        catch
        {
            throw new UnsupportedTestException("Desktop seeder not available — not running under PlaywrightMultiTest");
        }

        if (string.IsNullOrEmpty(magnetUri))
            throw new UnsupportedTestException("Desktop seeder magnet URI is empty");

        Console.WriteLine($"[CrossPlatform] Desktop seeder magnet: {magnetUri[..Math.Min(80, magnetUri.Length)]}...");

        // Generate expected data — same deterministic pattern as DesktopSeeder
        var expected = new byte[expectedLength];
        for (int i = 0; i < expected.Length; i++)
            expected[i] = (byte)((i * 7 + 13) % 256);

        // Download from the desktop seeder via real tracker + WebRTC
        var crypto = Client!.Crypto;
        WebTorrentClient.VerboseLogging = true;
        await using var downloader = new WebTorrentClient(crypto: crypto);
        var swarm = await downloader.AddAsync(magnetUri);

        Console.WriteLine($"[CrossPlatform] Swarm created, InfoHash: {swarm.InfoHashHex}");
        Console.WriteLine($"[CrossPlatform] Tracker URLs in magnet: {magnetUri.Count(c => c == '&')} params");

        // Wait for metadata — desktop seeder sends it via BEP 9 (ut_metadata) after connecting
        var metadataTimeout = DateTime.UtcNow.AddSeconds(45);
        while (swarm.Metadata == null && DateTime.UtcNow < metadataTimeout)
        {
            await Task.Delay(500);
            Console.WriteLine($"[CrossPlatform] Waiting for metadata... peers={swarm.PeerCount} progress={swarm.Progress:P0}");
        }
        WebTorrentClient.VerboseLogging = false;
        if (swarm.Metadata == null)
            throw new Exception($"Metadata never received from desktop seeder (peers={swarm.PeerCount})");
        Console.WriteLine($"[CrossPlatform] Metadata received: {swarm.Metadata.TotalLength} bytes, {swarm.Metadata.PieceCount} pieces");

        // Wait for download to complete
        var downloadTimeout = DateTime.UtcNow.AddSeconds(30);
        while (swarm.Progress < 1.0 && DateTime.UtcNow < downloadTimeout)
            await Task.Delay(200);

        if (swarm.Progress < 1.0)
            throw new Exception($"Download incomplete: {swarm.Progress:P0} ({swarm.Downloaded}/{swarm.Metadata.TotalLength} bytes)");

        Console.WriteLine($"[CrossPlatform] Download complete: {swarm.Downloaded} bytes");

        // Verify data matches
        var store = swarm.Store;
        if (store == null)
            throw new Exception("Store is null after download");

        var actual = new byte[expectedLength];
        int offset = 0;
        for (int i = 0; i < swarm.Metadata.PieceCount; i++)
        {
            var piece = await store.GetAsync(i);
            if (piece == null)
                throw new Exception($"Piece {i} missing after download");
            int len = Math.Min(piece.Length, expectedLength - offset);
            Array.Copy(piece, 0, actual, offset, len);
            offset += len;
        }

        if (!actual.SequenceEqual(expected))
        {
            // Find first mismatch
            for (int i = 0; i < actual.Length; i++)
            {
                if (actual[i] != expected[i])
                    throw new Exception($"Data mismatch at byte {i}: expected 0x{expected[i]:X2}, got 0x{actual[i]:X2}");
            }
        }

        Console.WriteLine("[CrossPlatform] Desktop↔Browser P2P VERIFIED — data matches byte-for-byte");
    }
}

/// <summary>
/// Mock loopback connection pair — two IConnections that pipe data to each other.
/// Used for testing P2P without network.
/// </summary>
internal class MockLoopbackConnection : SpawnDev.WebTorrent.Transports.IConnection
{
    private MockLoopbackConnection? _peer;
    private readonly List<byte> _receiveBuffer = new();
    private readonly SemaphoreSlim _dataAvailable = new(0);

    public string RemoteId { get; set; } = "";
    public string TransportType => "loopback";
    public bool IsConnected { get; private set; } = true;

    public event Action? OnDataAvailable;
    public event Action? OnDisconnected;

    public static (MockLoopbackConnection a, MockLoopbackConnection b) CreatePair()
    {
        var a = new MockLoopbackConnection { RemoteId = "peer-b" };
        var b = new MockLoopbackConnection { RemoteId = "peer-a" };
        a._peer = b;
        b._peer = a;
        return (a, b);
    }

    public Task SendAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default)
    {
        if (_peer == null || !IsConnected) return Task.CompletedTask;

        lock (_peer._receiveBuffer)
        {
            _peer._receiveBuffer.AddRange(data.ToArray());
        }
        _peer._dataAvailable.Release();
        _peer.OnDataAvailable?.Invoke();
        return Task.CompletedTask;
    }

    public async Task<int> ReceiveAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        while (IsConnected)
        {
            await _dataAvailable.WaitAsync(ct);
            lock (_receiveBuffer)
            {
                if (_receiveBuffer.Count == 0) continue; // spurious wake — retry
                int count = Math.Min(buffer.Length, _receiveBuffer.Count);
                for (int i = 0; i < count; i++)
                    buffer.Span[i] = _receiveBuffer[i];
                _receiveBuffer.RemoveRange(0, count);
                // Keep semaphore signaled if there's still data to read
                if (_receiveBuffer.Count > 0)
                    _dataAvailable.Release();
                return count;
            }
        }
        return 0; // disconnected
    }

    public Task CloseAsync()
    {
        IsConnected = false;
        _peer?.OnDisconnected?.Invoke();
        OnDisconnected?.Invoke();
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await CloseAsync();
        _dataAvailable.Dispose();
    }
}

// ═══════════════════════════════════════════════════════════
//  Real WebRTC P2P — Two Clients, Real Tracker, Real Data
// ═══════════════════════════════════════════════════════════

public abstract partial class WebTorrentTestBase
{
    [TestMethod(Timeout = 60000)]
    public async Task P2P_RealWebRTC_SeedAndDownload_ViaTracker()
    {

        // ── Create test data ──
        var data = new byte[32768]; // 2 pieces at 16KB
        for (int i = 0; i < data.Length; i++) data[i] = (byte)((i * 7 + 13) % 256);

        var trackerUrl = "wss://hub.spawndev.com:44365/announce";

        // ── Seeder: create torrent and seed ──
        var crypto = Client!.Crypto;
        var seeder = new WebTorrentClient(crypto: crypto);
        TorrentSwarm? seederSwarm = null;
        try
        {
            seederSwarm = await seeder.SeedAsync(data, "p2p-test.bin",
            new TorrentCreatorOptions
            {
                PieceLength = 16384,
                Trackers = new[] { trackerUrl },
                HashAlgorithm = "SHA-256",
            });
        }
        catch (Exception ex)
        {
            throw new Exception($"Seeder setup failed: {ex.Message}");
        }

        var infoHash = seederSwarm.InfoHash;
        var magnetUri = seederSwarm.MagnetURI;
        Console.WriteLine($"[P2P Test] Seeder: hash={seederSwarm.InfoHashHex}, magnet={magnetUri}");
        Console.WriteLine($"[P2P Test] Seeder: {seederSwarm.PieceManager?.CompletedCount}/{seederSwarm.Metadata?.PieceCount} pieces");

        // ── Downloader: add by magnet, use MemoryChunkStore (no OPFS conflict) ──
        var downloader = new WebTorrentClient(crypto: crypto);
        TorrentSwarm? dlSwarm = null;

        try
        {
            // Parse the torrent bytes to get metadata (so downloader doesn't need peer metadata exchange)
            var metadata = seederSwarm.Metadata!;
            dlSwarm = await downloader.AddAsync(metadata, new AddTorrentOptions
            {
                StoreFactory = (pieceLen) => new MemoryChunkStore(pieceLen),
            });

            Console.WriteLine($"[P2P Test] Downloader: added, {dlSwarm.Files.Length} files");
            Console.WriteLine($"[P2P Test] Downloader metadata AnnounceList: {metadata.AnnounceList?.Length ?? 0} tiers");
            if (metadata.AnnounceList != null)
                foreach (var tier in metadata.AnnounceList)
                    foreach (var t in tier)
                        Console.WriteLine($"[P2P Test]   tracker: {t}");

            // Start download immediately — peers will connect async
            dlSwarm.StartDownload();
            Console.WriteLine($"[P2P Test] Download started, waiting for peers + data...");

            // Wait for completion — give time for tracker + WebRTC + piece exchange
            var done = new TaskCompletionSource<bool>();
            using var cts = new CancellationTokenSource(55000);

            if (dlSwarm.Done)
                done.TrySetResult(true);
            else
                dlSwarm.OnDone += () => done.TrySetResult(true);
            cts.Token.Register(() => done.TrySetResult(false));

            // Log progress
            dlSwarm.OnPieceVerified += (idx) =>
                Console.WriteLine($"[P2P Test] Downloader piece {idx} verified, progress={dlSwarm.Progress:P0}");

            var completed = await done.Task;

            Console.WriteLine($"[P2P Test] Downloader: completed={completed}, peers={dlSwarm.PeerCount}, " +
                $"pieces={dlSwarm.PieceManager?.CompletedCount}/{metadata.PieceCount}");

            if (!completed)
            {
                // Log why it failed
                throw new Exception($"P2P download timed out. Progress: {dlSwarm.Progress:P0}, " +
                    $"peers: {dlSwarm.PeerCount}, " +
                    $"pieces: {dlSwarm.PieceManager?.CompletedCount ?? 0}/{metadata.PieceCount}");
            }

            // Verify downloaded data
            var downloadedData = await dlSwarm.Files[0].GetArrayBufferAsync();
            if (downloadedData.Length != data.Length)
                throw new Exception($"Size mismatch: {downloadedData.Length} vs {data.Length}");

            for (int i = 0; i < data.Length; i++)
            {
                if (downloadedData[i] != data[i])
                    throw new Exception($"Data mismatch at byte {i}");
            }

            Console.WriteLine($"[P2P Test] SUCCESS: {data.Length} bytes transferred via real WebRTC P2P, verified byte-for-byte");
        }
        finally
        {
            if (dlSwarm != null)
                await downloader.RemoveAsync(dlSwarm, destroyStore: true);
            await seeder.RemoveAsync(seederSwarm, destroyStore: true);
        }
    }
}
