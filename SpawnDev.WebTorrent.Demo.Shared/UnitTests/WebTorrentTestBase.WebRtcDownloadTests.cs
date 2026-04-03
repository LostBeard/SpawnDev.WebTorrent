using SpawnDev.UnitTesting;

namespace SpawnDev.WebTorrent.Demo.Shared.UnitTests;

/// <summary>
/// WebRTC peer-to-peer download tests.
/// These tests download from REAL peers via WebRTC with web seeds DISABLED.
/// Proves the full pipeline: tracker → WebRTC connect → handshake → bitfield →
/// interested → unchoke → request → piece → verify → store.
/// </summary>
public abstract partial class WebTorrentTestBase
{
    // Sintel magnet with extra wss:// trackers for better peer discovery in browser
    private const string SintelMagnetFull = "magnet:?xt=urn:btih:08ada5a7a6183aae1e09d831df6748d566095a10&dn=Sintel&tr=wss%3A%2F%2Ftracker.btorrent.xyz&tr=wss%3A%2F%2Ftracker.openwebtorrent.com&tr=wss%3A%2F%2Ftracker.fastcast.nz&ws=https%3A%2F%2Fwebtorrent.io%2Ftorrents%2F";

    [TestMethod(Timeout = 120000)]
    public async Task WebRTC_Download_Sintel_PeersOnly_ReceivesPieces()
    {
        var client = Client;
        if (client == null) throw new UnsupportedTestException("WebTorrentClient not available");

        // Disable web seeds — force WebRTC-only download
        var options = new AddTorrentOptions { DisableWebSeeds = true };
        var swarm = await client.AddAsync(SintelMagnetFull, options);

        Console.WriteLine($"[Test] Added Sintel torrent. InfoHash: {swarm.InfoHash}");
        Console.WriteLine($"[Test] WebSeedsDisabled: {swarm.WebSeedsDisabled}");

        if (!swarm.WebSeedsDisabled)
            throw new Exception("DisableWebSeeds should be true");

        // Wait for metadata (via ut_metadata from peers)
        var metadataTimeout = DateTime.UtcNow.AddSeconds(30);
        while (!swarm.HasMetadata && DateTime.UtcNow < metadataTimeout)
            await Task.Delay(500);

        if (!swarm.HasMetadata)
            throw new UnsupportedTestException("Could not get metadata from peers within 30s — no peers available");

        Console.WriteLine($"[Test] Metadata received: {swarm.Name}, {swarm.Metadata!.PieceCount} pieces, {swarm.Length} bytes");
        Console.WriteLine($"[Test] Peers: {swarm.PeerCount}, WebSeeds: {swarm.WebSeedCount}");

        // Verify no web seeds were added
        if (swarm.WebSeedCount > 0)
            throw new Exception($"WebSeedCount should be 0 with DisableWebSeeds=true, got {swarm.WebSeedCount}");

        // Wait for at least 1 piece to download from peers
        var downloadTimeout = DateTime.UtcNow.AddSeconds(60);
        while (swarm.Downloaded == 0 && DateTime.UtcNow < downloadTimeout)
        {
            await Task.Delay(1000);
            Console.WriteLine($"[Test] Peers: {swarm.PeerCount}, Downloaded: {swarm.Downloaded}, Progress: {swarm.Progress:P1}");
        }

        Console.WriteLine($"[Test] FINAL — Peers: {swarm.PeerCount}, Downloaded: {swarm.Downloaded} bytes, Progress: {swarm.Progress:P1}");

        if (swarm.Downloaded == 0)
            throw new Exception("Downloaded 0 bytes from WebRTC peers with web seeds disabled. " +
                $"Peers connected: {swarm.PeerCount}. " +
                "The peer download pipeline is broken — pieces are not being received via WebRTC.");

        Console.WriteLine($"[Test] SUCCESS — Downloaded {swarm.Downloaded} bytes from WebRTC peers (no web seeds)");

        // Clean up
        await client.RemoveAsync(swarm);
    }
}
