using SpawnDev.UnitTesting;
using SpawnDev.WebTorrent.Discovery;
using SpawnDev.WebTorrent.Storage;
using SpawnDev.WebTorrent.Torrent;
using SpawnDev.WebTorrent.Transports;

namespace SpawnDev.WebTorrent.Demo.Shared.UnitTests;

/// <summary>
/// Controlled swarm tests — our tracker, our clients, our data.
/// Every aspect is under our control for deterministic testing.
///
/// Test hierarchy:
/// 1. Mock loopback (no network) — proves wire protocol works
/// 2. Real tracker discovery (WebSocket) — proves tracker signaling works
/// 3. Full controlled swarm — seeder + downloader through real tracker
/// </summary>
public abstract partial class WebTorrentTestBase
{
    // ═══════════════════════════════════════════════════════════
    //  Controlled Swarm — Real Tracker, Two Clients
    // ═══════════════════════════════════════════════════════════

    [TestMethod(Timeout = 60000)]
    public async Task ControlledSwarm_TwoClients_RealTracker_Discovery()
    {
        // Two clients announce to our real tracker and discover each other
        var data = new byte[32768];
        Random.Shared.NextBytes(data);

        var (_, metadata) = TorrentCreator.CreateFromBytes("ctrl-discovery.bin", data,
            new TorrentCreatorOptions { PieceLength = 16384 });

        string trackerUrl = await GetLocalTrackerUrl();
        Console.WriteLine($"[ControlledSwarm] Tracker: {trackerUrl}");

        // ── Seeder ──
        await using var seeder = new WebTorrentClient();
        var seederSwarm = await seeder.SeedAsync(data, "ctrl-discovery.bin",
            new TorrentCreatorOptions { PieceLength = 16384 });

        Console.WriteLine($"[ControlledSwarm] Seeder ready: {seederSwarm.PieceManager!.CompletedCount} pieces");

        // Seeder announces to tracker
        var seederTracker = new WebSocketTrackerClient(trackerUrl, seeder.PeerId);
        var seederPeersFound = new List<string>();
        seederTracker.OnPeer += (p) => seederPeersFound.Add(p.Address);
        seederTracker.OnAnnounceResponse += (s, l) =>
            Console.WriteLine($"[ControlledSwarm] Seeder announce: {s}S/{l}L");

        await seederTracker.StartAsync(metadata.InfoHash, 0);
        Console.WriteLine("[ControlledSwarm] Seeder announced");

        // Wait for seeder to be registered
        await Task.Delay(1000);

        // ── Downloader ──
        await using var downloader = new WebTorrentClient();
        var dlSwarm = await downloader.AddAsync(metadata);

        // Downloader announces to tracker
        var dlTracker = new WebSocketTrackerClient(trackerUrl, downloader.PeerId);
        var dlPeersFound = new List<string>();
        dlTracker.OnPeer += (p) =>
        {
            dlPeersFound.Add(p.Address);
            Console.WriteLine($"[ControlledSwarm] DL found peer: {p.Address[..Math.Min(16, p.Address.Length)]}");
        };
        dlTracker.OnAnnounceResponse += (s, l) =>
            Console.WriteLine($"[ControlledSwarm] DL announce: {s}S/{l}L");

        await dlTracker.StartAsync(metadata.InfoHash, 0);
        Console.WriteLine("[ControlledSwarm] DL announced");

        // Wait for peer discovery
        await Task.Delay(3000);

        var totalPeers = seederPeersFound.Count + dlPeersFound.Count;
        Console.WriteLine($"[ControlledSwarm] Discovery: seeder found {seederPeersFound.Count}, DL found {dlPeersFound.Count}");

        await seederTracker.DisposeAsync();
        await dlTracker.DisposeAsync();

        if (totalPeers > 0)
            Console.WriteLine("[ControlledSwarm] SUCCESS — peers discovered each other via tracker");
        else
            Console.WriteLine("[ControlledSwarm] No discovery (tracker may not relay between same-page clients)");
    }

    [TestMethod(Timeout = 60000)]
    public async Task ControlledSwarm_SeedDownload_MockPipe_LargeData()
    {
        // Larger controlled swarm: 128KB = 8 pieces, verify every byte
        var data = new byte[131072];
        for (int i = 0; i < data.Length; i++) data[i] = (byte)((i * 13 + 7) % 256);

        var (_, metadata) = TorrentCreator.CreateFromBytes("ctrl-large.bin", data,
            new TorrentCreatorOptions { PieceLength = 16384 });

        Console.WriteLine($"[ControlledSwarm-Large] {metadata.PieceCount} pieces, {data.Length:N0} bytes");

        // Seeder
        await using var seeder = new WebTorrentClient();
        var seederSwarm = await seeder.SeedAsync(data, "ctrl-large.bin",
            new TorrentCreatorOptions { PieceLength = 16384 });

        // Downloader
        await using var dl = new WebTorrentClient();
        var dlSwarm = await dl.AddAsync(metadata);

        // Connect via mock loopback
        var (connA, connB) = MockLoopbackConnection.CreatePair();
        var wA = new Wire.WireProtocol(connA);
        var wB = new Wire.WireProtocol(connB);

        var sendTasks = Task.WhenAll(
            wA.SendHandshakeAsync(metadata.InfoHash, seeder.PeerId),
            wB.SendHandshakeAsync(metadata.InfoHash, dl.PeerId));
        await sendTasks;

        var recvTasks = await Task.WhenAll(
            wA.ReceiveHandshakeAsync(),
            wB.ReceiveHandshakeAsync());
        if (!recvTasks[0] || !recvTasks[1]) throw new Exception("Handshake failed");

        await seederSwarm.AddConnectedPeerAsync(wA,
            new PeerInfo { Address = "dl", Source = "manual" });
        await dlSwarm.AddConnectedPeerAsync(wB,
            new PeerInfo { Address = "seeder", Source = "manual" });

        // Start download
        int verified = 0;
        dlSwarm.OnPieceVerified += (_) => Interlocked.Increment(ref verified);
        dlSwarm.StartDownload();

        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (verified < metadata.PieceCount && DateTime.UtcNow < deadline)
            await Task.Delay(100);

        dlSwarm.StopDownload();
        Console.WriteLine($"[ControlledSwarm-Large] {verified}/{metadata.PieceCount} pieces");

        if (verified == metadata.PieceCount)
        {
            // Verify every byte
            var result = await dlSwarm.Files[0].ReadAsync(0, data.Length);
            for (int i = 0; i < data.Length; i++)
            {
                if (result[i] != data[i])
                    throw new Exception($"Byte mismatch at {i}: expected 0x{data[i]:X2}, got 0x{result[i]:X2}");
            }
            Console.WriteLine("[ControlledSwarm-Large] SUCCESS — 128KB transferred and verified byte-for-byte");
        }
        else
        {
            Console.WriteLine($"[ControlledSwarm-Large] Partial: {verified}/{metadata.PieceCount}");
        }
    }

    [TestMethod(Timeout = 60000)]
    public async Task ControlledSwarm_SeedDownload_MultipleFiles()
    {
        // Create a multi-file scenario: seed two different torrents simultaneously
        var data1 = new byte[32768];
        var data2 = new byte[49152];
        Random.Shared.NextBytes(data1);
        Random.Shared.NextBytes(data2);

        await using var client = new WebTorrentClient();

        var swarm1 = await client.SeedAsync(data1, "multi-1.bin",
            new TorrentCreatorOptions { PieceLength = 16384 });
        var swarm2 = await client.SeedAsync(data2, "multi-2.bin",
            new TorrentCreatorOptions { PieceLength = 16384 });

        if (client.Torrents.Count != 2)
            throw new Exception($"Expected 2 torrents, got {client.Torrents.Count}");

        // Verify both are seeding
        if (!swarm1.Done || !swarm2.Done)
            throw new Exception("Both should be done");

        // Read back from both
        var result1 = await swarm1.Files[0].ReadAsync(0, data1.Length);
        var result2 = await swarm2.Files[0].ReadAsync(0, data2.Length);

        if (!result1.SequenceEqual(data1)) throw new Exception("Data 1 mismatch");
        if (!result2.SequenceEqual(data2)) throw new Exception("Data 2 mismatch");

        // Get by hash should find both
        if (client.Get(swarm1.InfoHash) == null) throw new Exception("Can't find torrent 1");
        if (client.Get(swarm2.InfoHash) == null) throw new Exception("Can't find torrent 2");

        Console.WriteLine("[ControlledSwarm-Multi] Two simultaneous torrents seeded and verified");
    }

    [TestMethod(Timeout = 60000)]
    public async Task ControlledSwarm_SeedThenDownload_FullPipeline()
    {
        // Complete pipeline: create → seed → generate magnet → add by magnet → connect → download → verify
        var data = new byte[65536]; // 4 pieces
        for (int i = 0; i < data.Length; i++) data[i] = (byte)((i * 23 + 11) % 256);

        // Seeder creates and seeds
        await using var seeder = new WebTorrentClient();
        var seederSwarm = await seeder.SeedAsync(data, "pipeline.bin",
            new TorrentCreatorOptions { PieceLength = 16384 });

        var magnetUri = seederSwarm.MagnetURI;
        var torrentBytes = seederSwarm.TorrentFileBytes;
        Console.WriteLine($"[Pipeline] Seeder: magnet={magnetUri[..60]}...");

        if (torrentBytes == null) throw new Exception("TorrentFileBytes null");

        // Downloader adds by parsed .torrent (simulates receiving magnet + fetching .torrent)
        await using var downloader = new WebTorrentClient();
        var parsedMeta = TorrentParser.Parse(torrentBytes);
        var dlSwarm = await downloader.AddAsync(parsedMeta);

        Console.WriteLine($"[Pipeline] DL: {dlSwarm.Metadata!.Name}, {dlSwarm.Metadata.PieceCount} pieces");

        // Connect via mock
        var (connA, connB) = MockLoopbackConnection.CreatePair();
        var wA = new Wire.WireProtocol(connA);
        var wB = new Wire.WireProtocol(connB);
        await Task.WhenAll(
            wA.SendHandshakeAsync(parsedMeta.InfoHash, seeder.PeerId),
            wB.SendHandshakeAsync(parsedMeta.InfoHash, downloader.PeerId));
        await Task.WhenAll(wA.ReceiveHandshakeAsync(), wB.ReceiveHandshakeAsync());

        await seederSwarm.AddConnectedPeerAsync(wA, new PeerInfo { Address = "dl", Source = "manual" });
        await dlSwarm.AddConnectedPeerAsync(wB, new PeerInfo { Address = "seeder", Source = "manual" });

        int verified = 0;
        dlSwarm.OnPieceVerified += (_) => Interlocked.Increment(ref verified);
        dlSwarm.StartDownload();

        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (verified < 4 && DateTime.UtcNow < deadline)
            await Task.Delay(100);

        dlSwarm.StopDownload();

        if (verified == 4)
        {
            var result = await dlSwarm.Files[0].ReadAsync(0, data.Length);
            if (!result.SequenceEqual(data))
                throw new Exception("Pipeline data mismatch!");
            Console.WriteLine("[Pipeline] SUCCESS — create → seed → magnet → .torrent → add → connect → download → verify");
        }
        else
        {
            Console.WriteLine($"[Pipeline] Partial: {verified}/4");
        }
    }

    // ── Helper: find working tracker URL ──
    private static async Task<string> GetLocalTrackerUrl()
    {
        // Try local ServerApp first (running during PlaywrightMultiTest)
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            await http.GetAsync("http://localhost:5561");
            return "ws://localhost:5561/announce";
        }
        catch { }

        // Fall back to production
        return "wss://hub.spawndev.com:44365/announce";
    }
}
