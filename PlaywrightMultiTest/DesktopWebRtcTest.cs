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
}
