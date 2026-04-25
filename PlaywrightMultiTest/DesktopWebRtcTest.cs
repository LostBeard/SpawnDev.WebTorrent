using System.Net;
using System.Security.Cryptography;
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

    [Test, Timeout(30000)]
    public async Task Desktop_TcpListenerOption_AcceptsInboundLeech()
    {
        // Locks the WebTorrentClientOptions.TcpListenPort + EnsureTcpListenerAsync
        // surface added in 3.1.7+ (the path that closes seed-C# / leech-mainline
        // interop). Two clients on loopback: A seeds with the listener auto-started;
        // B leeches by manually constructing a TcpPeer.ConnectAsync to A's
        // kernel-assigned port. Verifies the listener accepts the inbound peer,
        // routes the BT handshake to the matching torrent by info_hash, and serves
        // the full payload byte-identical.

        // Deterministic 64 KiB payload (4 pieces of 16 KiB each).
        var payload = new byte[65536];
        for (int i = 0; i < payload.Length; i++)
            payload[i] = (byte)((i * 31 + 7) & 0xFF);
        var expectedHash = Convert.ToHexString(SHA256.HashData(payload));

        // ----- A: seeder + auto-started TCP listener on a kernel-assigned port -----
        await using var seeder = new WebTorrentClient(new WebTorrentClientOptions
        {
            // Disable peer-discovery sources so the only path B can use is
            // direct TCP connect. Makes the test deterministic and fast.
            EnableTrackers = false,
            EnableDht = false,
            EnableLsd = false,
            EnableUtPex = false,
            DefaultTrackers = Array.Empty<string>(),
            TcpListenPort = 0,
            TcpListenAddress = IPAddress.Loopback,
        });
        // Wait for the listener to bind (constructor fired EnsureTcpListenerAsync
        // fire-and-forget; idempotent re-call returns once it's ready).
        await seeder.EnsureTcpListenerAsync(0, IPAddress.Loopback);
        Assert.That(seeder.TcpListener, Is.Not.Null, "TcpListener should be set after EnsureTcpListenerAsync");
        var listenPort = seeder.TcpListener!.LocalEndPoint.Port;
        Assert.That(listenPort, Is.GreaterThan(0), "Kernel-assigned port should be > 0");

        var seedTorrent = await seeder.SeedAsync("tcp-listener-test.bin", payload,
            new TorrentCreatorOptions
            {
                PieceLength = 16384,
                MetaVersion = 2,
                Hybrid = true,
            });
        Assert.That(seedTorrent.Done, Is.True, "Seeder torrent should be done");

        // ----- B: leecher with no peer-discovery; manually connect to A's port -----
        await using var leecher = new WebTorrentClient(new WebTorrentClientOptions
        {
            EnableTrackers = false,
            EnableDht = false,
            EnableLsd = false,
            EnableUtPex = false,
            DefaultTrackers = Array.Empty<string>(),
        });

        // Add the same .torrent metadata to the leecher (no payload data).
        var (torrentBytes, _) = TorrentCreator.CreateFromBytes("tcp-listener-test.bin", payload,
            new TorrentCreatorOptions
            {
                PieceLength = 16384,
                MetaVersion = 2,
                Hybrid = true,
            });
        var dlTmp = Path.Combine(Path.GetTempPath(), "tcplistener_test_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dlTmp);
        var dlTorrent = leecher.Add(torrentBytes, new AddTorrentOptions { Path = dlTmp });

        // Direct TCP dial-in to the seeder's listener.
        var tcpPeer = new TcpPeer(initiator: true);
        await tcpPeer.ConnectAsync($"127.0.0.1:{listenPort}");
        Assert.That(tcpPeer.Connected, Is.True, "TcpPeer.ConnectAsync should establish a connection to the listener");
        dlTorrent.AddPeer(tcpPeer);

        // Wait for the leecher to finish downloading.
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (!dlTorrent.Done && DateTime.UtcNow < deadline)
            await Task.Delay(200);

        Assert.That(dlTorrent.Done, Is.True,
            $"Leecher torrent should be Done within 20s (progress={dlTorrent.Progress:P1}, downloaded={dlTorrent.Downloaded}, " +
            $"listenerAccepted={seeder.TcpListener.AcceptedCount}, listenerRejected={seeder.TcpListener.RejectedCount})");

        var dlFile = dlTorrent.Files?.FirstOrDefault();
        Assert.That(dlFile, Is.Not.Null, "Leecher should have a file");
        var actualBytes = await dlFile!.ReadAsync(0, (int)dlFile.Length);
        Assert.That(actualBytes.Length, Is.EqualTo(payload.Length));
        var actualHash = Convert.ToHexString(SHA256.HashData(actualBytes));
        Assert.That(actualHash, Is.EqualTo(expectedHash),
            "Leecher's downloaded bytes must SHA-256-match the seeder's payload");

        Assert.That(seeder.TcpListener.AcceptedCount, Is.GreaterThanOrEqualTo(1),
            "Listener should have accepted at least one inbound peer");
    }
}
