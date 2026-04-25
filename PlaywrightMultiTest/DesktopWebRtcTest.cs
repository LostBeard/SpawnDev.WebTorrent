using SpawnDev.WebTorrent;

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
        // Local in-process tracker - verifies the desktop seed path is functional
        // without a live-infra dependency on hub.spawndev.com.
        await using var tracker = await LocalTrackerFixture.StartAsync();
        var client = new WebTorrentClient();
        WebTorrentClient.VerboseLogging = true;

        var data = new byte[16384];
        for (int i = 0; i < data.Length; i++) data[i] = (byte)(i % 256);

        var swarm = await client.SeedAsync("desktop-test.bin", data,
            new TorrentCreatorOptions
            {
                PieceLength = 16384,
                Trackers = new[] { tracker.WsAnnounceUrl },
            });

        Console.WriteLine($"InfoHash: {swarm.InfoHashHex}");
        Console.WriteLine($"MagnetURI: {swarm.ComputedMagnetUri}");
        Console.WriteLine($"Ready: {swarm.Ready}");
        Console.WriteLine($"Done: {swarm.Done}");
        Console.WriteLine($"PeerCount: {swarm.PeerCount}");
        Console.WriteLine($"HasMetadata: {swarm.HasMetadata}");

        // Verify basic seeder state
        Assert.That(swarm.Ready, Is.True, "Swarm should be ready");
        Assert.That(swarm.Done, Is.True, "Swarm should be done (all pieces seeded)");
        Assert.That(swarm.HasMetadata, Is.True, "Swarm should have metadata");
        Assert.That(swarm.InfoHashHex, Is.Not.Empty, "InfoHash should not be empty");
        Assert.That(swarm.ComputedMagnetUri, Does.Contain("xt=urn:btih:"), "MagnetURI should be valid");
        Assert.That(swarm.ComputedMagnetUri, Does.Contain("127.0.0.1"), "MagnetURI should contain the local tracker address");

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
        // In-process local tracker - deterministic and doesn't depend on hub.spawndev.com.
        // Previously used wss://hub.spawndev.com:44365/announce which 403's desktop C#
        // clients when the hub has an Origin allowlist set (browser-only abuse protection).
        // SpawnDev.RTC 1.1.6-rc.2 fixes the allowlist to bypass empty-Origin, but local
        // tracker is still the right choice for a unit test: no internet, no flake, CI-safe.
        await using var tracker = await LocalTrackerFixture.StartAsync();
        WebTorrentClient.VerboseLogging = true;

        // Seeder
        var seeder = new WebTorrentClient();
        var data = new byte[32768];
        for (int i = 0; i < data.Length; i++) data[i] = (byte)((i * 7 + 13) % 256);

        var seederSwarm = await seeder.SeedAsync("two-client-test.bin", data,
            new TorrentCreatorOptions
            {
                PieceLength = 16384,
                Trackers = new[] { tracker.WsAnnounceUrl },
            });
        Console.WriteLine($"Seeder: InfoHash={seederSwarm.InfoHashHex}, Ready={seederSwarm.Ready}");

        // Downloader — add via magnet
        var downloader = new WebTorrentClient();
        var dlSwarm = downloader.Add(seederSwarm.ComputedMagnetUri);
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

    [Test, Timeout(180000), Retry(3)]
    public async Task Desktop_Download_Sintel_PeersOnly()
    {
        WebTorrentClient.VerboseLogging = true;
        var client = new WebTorrentClient();

        var magnet = "magnet:?xt=urn:btih:08ada5a7a6183aae1e09d831df6748d566095a10&dn=Sintel" +
            "&tr=wss%3A%2F%2Ftracker.openwebtorrent.com" +
            "&ws=https%3A%2F%2Fwebtorrent.io%2Ftorrents%2F";

        var swarm = client.Add(magnet, new AddTorrentOptions { DisableWebSeeds = true });
        Console.WriteLine($"[Test] Added Sintel.");

        // Wait for metadata — 60s, ICE can take time
        var metaTimeout = DateTime.UtcNow.AddSeconds(60);
        while (!swarm.HasMetadata && DateTime.UtcNow < metaTimeout)
            await Task.Delay(500);

        if (!swarm.HasMetadata)
        {
            await client.DisposeAsync();
            Assert.Fail($"No metadata after 60s. Peers={swarm.PeerCount}");
            return;
        }

        Console.WriteLine($"[Test] Metadata: {swarm.Name}, {swarm.PieceCount} pieces");
        Console.WriteLine($"[Test] Peers={swarm.PeerCount}");

        // Wait for download — 90s
        var dlTimeout = DateTime.UtcNow.AddSeconds(90);
        while (swarm.Downloaded == 0 && DateTime.UtcNow < dlTimeout)
        {
            await Task.Delay(2000);
            Console.WriteLine($"[Test] Peers={swarm.PeerCount}, Downloaded={swarm.Downloaded}, Progress={swarm.Progress:P1}");
        }

        var downloaded = swarm.Downloaded;
        var peers = swarm.PeerCount;
        await client.DisposeAsync();
        WebTorrentClient.VerboseLogging = false;

        if (downloaded == 0)
            Assert.Fail($"Downloaded 0 bytes. Peers={peers}. Check [DL] logs.");

        Console.WriteLine($"[Test] SUCCESS: Downloaded {downloaded} bytes from {peers} peers");
    }
}
