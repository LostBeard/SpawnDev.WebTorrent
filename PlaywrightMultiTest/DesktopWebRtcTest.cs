using SpawnDev.BlazorJS.Cryptography;
using SpawnDev.WebTorrent;
using SpawnDev.WebTorrent.Torrent;

namespace PlaywrightMultiTest;

/// <summary>
/// Standalone NUnit test — no browser needed.
/// Verifies desktop WebTorrent client can seed, connect to tracker, and be discovered.
/// </summary>
[TestFixture]
public class DesktopWebRtcTest
{
    [Test, Timeout(30000)]
    public async Task Desktop_SeedAndAnnounce_TrackerConnects()
    {
        var crypto = new DotNetCrypto();
        var client = new WebTorrentClient(crypto: crypto);
        WebTorrentClient.VerboseLogging = true;

        var data = new byte[16384];
        for (int i = 0; i < data.Length; i++) data[i] = (byte)(i % 256);

        var swarm = await client.SeedAsync(data, "desktop-test.bin",
            new TorrentCreatorOptions
            {
                PieceLength = 16384,
                Trackers = new[] { "wss://hub.spawndev.com:44365/announce" },
            });

        Console.WriteLine($"InfoHash: {swarm.InfoHashHex}");
        Console.WriteLine($"MagnetURI: {swarm.MagnetURI}");
        Console.WriteLine($"Ready: {swarm.Ready}");
        Console.WriteLine($"Done: {swarm.Done}");
        Console.WriteLine($"PeerCount: {swarm.PeerCount}");
        Console.WriteLine($"HasMetadata: {swarm.HasMetadata}");

        // Verify basic seeder state
        Assert.That(swarm.Ready, Is.True, "Swarm should be ready");
        Assert.That(swarm.Done, Is.True, "Swarm should be done (all pieces seeded)");
        Assert.That(swarm.HasMetadata, Is.True, "Swarm should have metadata");
        Assert.That(swarm.InfoHashHex, Is.Not.Empty, "InfoHash should not be empty");
        Assert.That(swarm.MagnetURI, Does.Contain("xt=urn:btih:"), "MagnetURI should be valid");
        Assert.That(swarm.MagnetURI, Does.Contain("hub.spawndev.com"), "MagnetURI should contain tracker");

        // The tracker connection is now awaited in SetMetadataAsync.
        // If we got here, the tracker WebSocket connected and announced successfully.
        Console.WriteLine("Desktop seed + tracker announce: PASSED");

        await swarm.DisposeAsync();
        await client.DisposeAsync();
        WebTorrentClient.VerboseLogging = false;
    }

    [Test, Timeout(60000)]
    public async Task Desktop_TwoClients_DiscoverViaTracker()
    {
        WebTorrentClient.VerboseLogging = true;
        var crypto = new DotNetCrypto();

        // Seeder
        var seeder = new WebTorrentClient(crypto: crypto);
        var data = new byte[32768];
        for (int i = 0; i < data.Length; i++) data[i] = (byte)((i * 7 + 13) % 256);

        var seederSwarm = await seeder.SeedAsync(data, "two-client-test.bin",
            new TorrentCreatorOptions
            {
                PieceLength = 16384,
                Trackers = new[] { "wss://hub.spawndev.com:44365/announce" },
            });
        Console.WriteLine($"Seeder: InfoHash={seederSwarm.InfoHashHex}, Ready={seederSwarm.Ready}");

        // Downloader — add via magnet
        var downloader = new WebTorrentClient(crypto: crypto);
        var dlSwarm = await downloader.AddAsync(seederSwarm.MagnetURI);
        Console.WriteLine($"Downloader: InfoHash={dlSwarm.InfoHashHex}");

        // Wait for peers to discover each other
        var timeout = DateTime.UtcNow.AddSeconds(30);
        while (seederSwarm.PeerCount == 0 && dlSwarm.PeerCount == 0 && DateTime.UtcNow < timeout)
        {
            await Task.Delay(500);
            Console.WriteLine($"  Seeder peers={seederSwarm.PeerCount}, Downloader peers={dlSwarm.PeerCount}");
        }

        Console.WriteLine($"Final: Seeder peers={seederSwarm.PeerCount}, Downloader peers={dlSwarm.PeerCount}");

        if (seederSwarm.PeerCount == 0 && dlSwarm.PeerCount == 0)
            Assert.Fail("Neither client discovered the other via tracker");

        await dlSwarm.DisposeAsync();
        await downloader.DisposeAsync();
        await seederSwarm.DisposeAsync();
        await seeder.DisposeAsync();
        WebTorrentClient.VerboseLogging = false;
    }

    [Test, Timeout(300000)]
    public async Task Desktop_Download_Sintel_PeersOnly()
    {
        WebTorrentClient.VerboseLogging = true;
        var crypto = new DotNetCrypto();
        var client = new WebTorrentClient(crypto: crypto);

        var magnet = "magnet:?xt=urn:btih:08ada5a7a6183aae1e09d831df6748d566095a10&dn=Sintel" +
            "&tr=wss%3A%2F%2Ftracker.openwebtorrent.com" +
            "&tr=wss%3A%2F%2Fhub.spawndev.com%3A44365%2Fannounce";

        var swarm = await client.AddAsync(magnet, new AddTorrentOptions { DisableWebSeeds = true });
        Console.WriteLine($"[Test] Added Sintel. WebSeedsDisabled={swarm.WebSeedsDisabled}");

        // Wait for metadata — 90s, ICE gathering + connection can take time
        var metaTimeout = DateTime.UtcNow.AddSeconds(90);
        while (!swarm.HasMetadata && DateTime.UtcNow < metaTimeout)
            await Task.Delay(500);

        if (!swarm.HasMetadata)
        {
            await client.DisposeAsync();
            Assert.Fail($"No metadata after 90s. Peers={swarm.PeerCount}");
            return;
        }

        Console.WriteLine($"[Test] Metadata: {swarm.Name}, {swarm.Metadata!.PieceCount} pieces");

        // Wait for piece data — check Progress (pieces completing) not just Downloaded
        var dlTimeout = DateTime.UtcNow.AddSeconds(120);
        double startProgress = swarm.Progress;
        while (swarm.Progress <= startProgress && DateTime.UtcNow < dlTimeout)
        {
            await Task.Delay(3000);
            Console.WriteLine($"[Test] Peers={swarm.PeerCount}, Downloaded={swarm.Downloaded}, Progress={swarm.Progress:P2}, DL={swarm.DownloadSpeed/1024:F0}KB/s");
        }

        var downloaded = swarm.Downloaded;
        var progress = swarm.Progress;
        var peers = swarm.PeerCount;
        await client.DisposeAsync();
        WebTorrentClient.VerboseLogging = false;

        if (progress <= startProgress)
            Assert.Fail($"No progress. Start={startProgress:P2}, End={progress:P2}, Peers={peers}, Downloaded={downloaded}.");

        Console.WriteLine($"[Test] SUCCESS: Progress={progress:P2}, Downloaded={downloaded} bytes from {peers} peers");
    }
}
