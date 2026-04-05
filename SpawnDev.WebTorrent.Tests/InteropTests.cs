using NUnit.Framework;
using SpawnDev.WebTorrent;

namespace SpawnDev.WebTorrent.Tests;

/// <summary>
/// Live integration tests using REAL WebRTC connections, REAL tracker communication,
/// and downloading REAL data from REAL peers. No mocks. No simulated connections.
/// </summary>
[TestFixture]
public class InteropTests
{
    private const int TestTimeoutMs = 120_000; // 2 minutes

    /// <summary>
    /// Connect to the live Sintel public WebTorrent swarm (~8 peers typically online).
    /// Download at least 1 piece from a real JS WebTorrent peer over WebRTC.
    /// This proves the full chain: tracker → WebRTC → wire → metadata → piece download.
    /// </summary>
    [Test, CancelAfter(TestTimeoutMs)]
    public async Task LiveSwarm_Sintel_DownloadsPieces()
    {
        // Sintel magnet with known active WSS trackers — TJ's older client has ~8 peers on this right now
        const string sintelMagnet = "magnet:?xt=urn:btih:08ada5a7a6183aae1e09d831df6748d566095a10&dn=Sintel&tr=wss%3A%2F%2Ftracker.btorrent.xyz&tr=wss%3A%2F%2Ftracker.openwebtorrent.com&tr=wss%3A%2F%2Ftracker.fastcast.nz";

        Console.WriteLine("[Test] Creating C# WebTorrent client for live Sintel swarm...");
        var client = new WebTorrentClient();
        var torrent = client.Add(sintelMagnet, new AddTorrentOptions { DisableWebSeeds = true });

        Console.WriteLine($"[Test] Torrent added. InfoHash: {torrent.InfoHash}");

        // Wait for metadata
        Console.WriteLine("[Test] Waiting for metadata from peers...");
        var metadataTimeout = DateTime.UtcNow.AddSeconds(60);
        while (!torrent.HasMetadata && DateTime.UtcNow < metadataTimeout)
        {
            await Task.Delay(1000);
            Console.WriteLine($"[Test] Peers: {torrent.NumPeers}, HasMetadata: {torrent.HasMetadata}");
        }

        if (!torrent.HasMetadata)
        {
            Assert.Fail($"No metadata after 60s. Peers: {torrent.NumPeers}. " +
                "Tracker connection or WebRTC signaling may have failed.");
            return;
        }

        Console.WriteLine($"[Test] Metadata: {torrent.Name}, {torrent.Pieces.Length} pieces, {torrent.Length} bytes");

        // Wait for at least SOME data to download (proves piece transfer works)
        Console.WriteLine("[Test] Waiting for piece download from WebRTC peers...");
        var downloadTimeout = DateTime.UtcNow.AddSeconds(60);
        while (torrent.Downloaded == 0 && DateTime.UtcNow < downloadTimeout)
        {
            await Task.Delay(1000);
            Console.WriteLine($"[Test] Peers: {torrent.NumPeers}, Downloaded: {torrent.Downloaded}, Progress: {torrent.Progress:P1}");
        }

        Console.WriteLine($"[Test] FINAL: Downloaded={torrent.Downloaded} bytes, Peers={torrent.NumPeers}");

        Assert.That(torrent.Downloaded, Is.GreaterThan(0),
            $"Downloaded 0 bytes from live swarm. Peers: {torrent.NumPeers}. " +
            "Piece transfer over WebRTC is broken.");

        Console.WriteLine($"[Test] SUCCESS: Downloaded {torrent.Downloaded} bytes from live Sintel swarm (WebRTC only, no web seeds)");

        await client.DisposeAsync();
    }
}
