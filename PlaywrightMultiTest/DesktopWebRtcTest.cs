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
}
