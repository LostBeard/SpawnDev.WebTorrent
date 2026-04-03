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
    [TestMethod(Timeout = 60000)]
    public async Task ControlledSwarm_SeedDownload_MultipleFiles()
    {
        // Create a multi-file scenario: seed two different torrents simultaneously
        var data1 = new byte[32768];
        var data2 = new byte[49152];
        Random.Shared.NextBytes(data1);
        Random.Shared.NextBytes(data2);

        await using var client = new WebTorrentClient(crypto: Client!.Crypto);

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
