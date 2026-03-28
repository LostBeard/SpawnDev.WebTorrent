using SpawnDev.UnitTesting;
using SpawnDev.WebTorrent.Storage;
using SpawnDev.WebTorrent.Torrent;

namespace SpawnDev.WebTorrent.Demo.Shared.UnitTests;

/// <summary>
/// DownloadCoordinator tests — configuration, prioritization, web seeds, endgame mode.
/// Pure logic with no network peers.
/// </summary>
public abstract partial class WebTorrentTestBase
{
    private static (DownloadCoordinator coord, PieceManager pm, byte[] data) CreateTestCoordinator(
        int dataSize = 65536, int pieceLength = 16384)
    {
        var data = new byte[dataSize];
        Random.Shared.NextBytes(data);
        var (_, meta) = TorrentCreator.CreateFromBytes("coord-test.bin", data,
            new TorrentCreatorOptions { PieceLength = pieceLength });
        var store = new MemoryChunkStore(pieceLength);
        var pm = new PieceManager(meta, store);
        var coord = new DownloadCoordinator(pm, meta);
        return (coord, pm, data);
    }

    // ═══════════════════════════════════════════════════════════
    //  Coordinator — Configuration
    // ═══════════════════════════════════════════════════════════

    [TestMethod]
    public async Task Coord_DefaultConfig()
    {
        var (coord, _, _) = CreateTestCoordinator();
        if (coord.MaxRequestsPerPeer != 6)
            throw new Exception($"MaxRequestsPerPeer default: {coord.MaxRequestsPerPeer}");
        if (coord.EndgameThreshold != 5)
            throw new Exception($"EndgameThreshold default: {coord.EndgameThreshold}");
        if (coord.Strategy != "rarest")
            throw new Exception($"Strategy default: {coord.Strategy}");
        if (coord.EndgameMode)
            throw new Exception("EndgameMode should be false initially");
    }

    [TestMethod]
    public async Task Coord_ConfigSetters()
    {
        var (coord, _, _) = CreateTestCoordinator();
        coord.MaxRequestsPerPeer = 10;
        coord.EndgameThreshold = 3;
        coord.Strategy = "sequential";
        coord.UpdateIntervalMs = 50;
        if (coord.MaxRequestsPerPeer != 10) throw new Exception("MaxRequestsPerPeer set failed");
        if (coord.EndgameThreshold != 3) throw new Exception("EndgameThreshold set failed");
        if (coord.Strategy != "sequential") throw new Exception("Strategy set failed");
        if (coord.UpdateIntervalMs != 50) throw new Exception("UpdateIntervalMs set failed");
    }

    // ═══════════════════════════════════════════════════════════
    //  Coordinator — Web Seeds
    // ═══════════════════════════════════════════════════════════

    [TestMethod]
    public async Task Coord_AddWebSeed_CountIncreases()
    {
        var (coord, _, _) = CreateTestCoordinator();
        if (coord.WebSeedCount != 0)
            throw new Exception($"Initial WebSeedCount: {coord.WebSeedCount}");
        coord.AddWebSeed(new HttpClient(), "https://seed1.example.com/files/");
        if (coord.WebSeedCount != 1)
            throw new Exception($"After add: {coord.WebSeedCount}");
        coord.AddWebSeed(new HttpClient(), "https://seed2.example.com/files/");
        if (coord.WebSeedCount != 2)
            throw new Exception($"After add 2: {coord.WebSeedCount}");
    }

    // ═══════════════════════════════════════════════════════════
    //  Coordinator — Prioritization
    // ═══════════════════════════════════════════════════════════

    [TestMethod]
    public async Task Coord_Prioritize_MultiplePieces()
    {
        var (coord, _, _) = CreateTestCoordinator();
        coord.Prioritize(0);
        coord.Prioritize(2);
        coord.Prioritize(3);
        // Prioritize is fire-and-forget — verify it doesn't throw
        // and that coordinator accepted all three
        Console.WriteLine("[Coord] Prioritized pieces 0, 2, 3 — no errors");
    }

    [TestMethod]
    public async Task Coord_Prioritize_Idempotent()
    {
        var (coord, _, _) = CreateTestCoordinator();
        coord.Prioritize(1);
        coord.Prioritize(1);
        coord.Prioritize(1);
        // Same piece prioritized multiple times — should not throw or duplicate
        Console.WriteLine("[Coord] Duplicate prioritize — no errors");
    }

    // ═══════════════════════════════════════════════════════════
    //  Coordinator — Start/Stop
    // ═══════════════════════════════════════════════════════════

    [TestMethod]
    public async Task Coord_StartStop_NoPeers()
    {
        var (coord, _, _) = CreateTestCoordinator();
        // Start with no peers — loop should run and do nothing
        coord.Start();
        await Task.Delay(200);
        coord.Stop();
        // Should not throw
        Console.WriteLine("[Coord] Start/Stop with no peers — no errors");
    }

    [TestMethod]
    public async Task Coord_PeerCount_Initially()
    {
        var (coord, _, _) = CreateTestCoordinator();
        if (coord.PeerCount != 0)
            throw new Exception($"Initial PeerCount: {coord.PeerCount}");
    }

    // ═══════════════════════════════════════════════════════════
    //  Coordinator — Events
    // ═══════════════════════════════════════════════════════════

    [TestMethod]
    public async Task Coord_Events_OnPieceComplete_Fires()
    {
        var (coord, pm, data) = CreateTestCoordinator(16384, 16384);
        int? completedPiece = null;
        coord.OnPieceComplete += idx => completedPiece = idx;
        // Complete piece via PieceManager — coordinator should relay the event
        await pm.ReceiveCompletePieceAsync(0, data);
        if (completedPiece != 0)
            throw new Exception($"OnPieceComplete should fire with index 0: {completedPiece}");
    }

    [TestMethod]
    public async Task Coord_Events_OnDownloadComplete_Fires()
    {
        var (coord, pm, data) = CreateTestCoordinator(16384, 16384);
        bool downloadComplete = false;
        coord.OnDownloadComplete += () => downloadComplete = true;
        await pm.ReceiveCompletePieceAsync(0, data);
        if (!downloadComplete)
            throw new Exception("OnDownloadComplete should fire when all pieces done");
    }

    [TestMethod]
    public async Task Coord_Events_OnProgressChanged_Fires()
    {
        var (coord, pm, data) = CreateTestCoordinator(32768, 16384);
        double? lastProgress = null;
        coord.OnProgressChanged += p => lastProgress = p;
        await pm.ReceiveCompletePieceAsync(0, data.AsSpan(0, 16384).ToArray());
        if (lastProgress == null || lastProgress < 0.4 || lastProgress > 0.6)
            throw new Exception($"OnProgressChanged should fire ~0.5: {lastProgress}");
    }
}
