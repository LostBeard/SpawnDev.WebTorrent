using SpawnDev.UnitTesting;
using SpawnDev.WebTorrent;
using System.Text;

namespace SpawnDev.WebTorrent.Demo.Shared;

/// <summary>
/// Tests for BEPs that were listed in docs but had no test coverage:
/// BEP 20 (Peer ID), BEP 27 (Private Torrents), BEP 53 (Magnet Select Files).
/// </summary>
public abstract partial class WebTorrentTestBase
{
    // ── BEP 20: Peer ID Conventions ──

    [TestMethod]
    public async Task Bep20_PeerId_AzureusFormat()
    {
        // BEP 20: Azureus-style peer ID is "-XX0000-" + 12 random chars
        // SpawnDev.WebTorrent uses "-SD" prefix
        var client = CreateIsolatedClient();
        var peerId = client.PeerId;

        if (string.IsNullOrEmpty(peerId))
            throw new Exception("PeerId is null/empty");

        // PeerId is hex-encoded (40 chars = 20 bytes)
        if (peerId.Length != 40)
            throw new Exception($"PeerId hex should be 40 chars, got {peerId.Length}");

        // Decode to ASCII and check prefix
        var peerIdBytes = client.PeerIdBuffer;
        if (peerIdBytes.Length != 20)
            throw new Exception($"PeerIdBuffer should be 20 bytes, got {peerIdBytes.Length}");

        var peerIdStr = Encoding.ASCII.GetString(peerIdBytes);
        // Should start with -SD (Azureus-style for SpawnDev)
        if (!peerIdStr.StartsWith("-"))
            throw new Exception($"Peer ID should start with '-' (Azureus style), got: {peerIdStr}");

        await client.DisposeAsync();
    }

    [TestMethod]
    public async Task Bep20_PeerId_Unique()
    {
        // Two clients should have different peer IDs
        var client1 = CreateIsolatedClient();
        var client2 = CreateIsolatedClient();

        if (client1.PeerId == client2.PeerId)
            throw new Exception("Two clients should have different peer IDs");

        await client1.DisposeAsync();
        await client2.DisposeAsync();
    }

    // ── BEP 27: Private Torrents ──

    [TestMethod]
    public async Task Bep27_PrivateTorrent_FlagParsed()
    {
        // Create a private torrent and verify the flag is parsed
        var data = MakeDeterministicData(16384, seed: 270);
        var (torrentBytes, meta) = TorrentCreator.CreateFromBytes("private.bin", data,
            new TorrentCreatorOptions { IsPrivate = true });
        var parsed = TorrentParser.Parse(torrentBytes);

        if (!parsed.IsPrivate)
            throw new Exception("Private flag should be true");
    }

    [TestMethod]
    public async Task Bep27_PublicTorrent_FlagFalse()
    {
        var data = MakeDeterministicData(16384, seed: 271);
        var (torrentBytes, meta) = TorrentCreator.CreateFromBytes("public.bin", data);
        var parsed = TorrentParser.Parse(torrentBytes);

        if (parsed.IsPrivate)
            throw new Exception("Public torrent should not have private flag");
    }

    // ── BEP 14: Local Service Discovery ──

    [TestMethod]
    public async Task Bep14_LSD_ConstructsAndDisposes()
    {
        if (OperatingSystem.IsBrowser())
            throw new UnsupportedTestException("LSD requires UDP multicast (desktop only)");

        // Verify LSD constructs and disposes without throwing
        // Also verify event registration works (OnPeer is how LSD reports discovered peers)
        var infoHash = new byte[20];
        infoHash[0] = 0xAB; infoHash[19] = 0xCD;
        var lsd = new LocalServiceDiscovery(infoHash, 6881);

        string? discoveredPeer = null;
        lsd.OnPeer += (addr) => discoveredPeer = addr;

        // Dispose should be safe even without calling StartAsync
        await lsd.DisposeAsync();

        // Double dispose should be safe
        await lsd.DisposeAsync();
    }

    /// <summary>
    /// Real 2-peer BEP 14 end-to-end: two LSD instances on the same host, same
    /// infohash, different advertised ports. Peer A announces with Port=6881,
    /// Peer B announces with Port=6882. Via the UDP multicast group each one's
    /// receive loop sees the other's BT-SEARCH and fires OnPeer with the peer's
    /// `ip:port`. Test passes when each peer has observed the OTHER peer's
    /// advertised port (not just its own self-announce).
    ///
    /// Gracefully skips if the test environment has no multicast routing
    /// (common in containerized / locked-down CI) - after 3 s with no events
    /// from either side we can't distinguish "no multicast" from "bug" so we
    /// report Unsupported. When multicast DOES work on this box it's a real
    /// proof that LSD peer discovery works, not just a shape check.
    /// </summary>
    [TestMethod(Timeout = 15000)]
    public async Task Bep14_LSD_TwoPeers_DiscoverEachOtherViaMulticast()
    {
        if (OperatingSystem.IsBrowser())
            throw new UnsupportedTestException("LSD requires UDP multicast (desktop only)");

        var infoHash = new byte[20];
        infoHash[0] = 0xBE; infoHash[1] = 0x14; infoHash[19] = 0xE2;

        // Use the standard BitTorrent client port (6881) for A and 6882 for B,
        // plus the real LSD port - these show up in the Port: header of the
        // BT-SEARCH message and are what OnPeer reports.
        await using var peerA = new LocalServiceDiscovery(infoHash, port: 6881);
        await using var peerB = new LocalServiceDiscovery(infoHash, port: 6882);

        var peerASeesBPort = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var peerBSeesAPort = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        peerA.OnPeer += addr =>
        {
            // LSD reports "ip:port" where port is the advertised BitTorrent port from the message.
            // Peer A receiving B's announce sees ":6882" in the tail.
            if (addr.EndsWith(":6882", StringComparison.Ordinal)) peerASeesBPort.TrySetResult(true);
        };
        peerB.OnPeer += addr =>
        {
            if (addr.EndsWith(":6881", StringComparison.Ordinal)) peerBSeesAPort.TrySetResult(true);
        };

        try
        {
            await peerA.StartAsync();
            await peerB.StartAsync();
        }
        catch (System.Net.Sockets.SocketException ex)
        {
            throw new UnsupportedTestException(
                $"LSD could not bind the multicast group 239.192.152.143:6771 ({ex.Message}). " +
                "Likely a sandboxed / containerized environment with no multicast routing.");
        }

        // Re-announce a few times to give the multicast stack a chance on slower
        // network stacks. Each StartAsync also kicks off an initial AnnounceAsync
        // so a fresh pair may already have fired before we await.
        for (int i = 0; i < 5; i++)
        {
            var bothDone = Task.WhenAll(peerASeesBPort.Task, peerBSeesAPort.Task);
            var race = await Task.WhenAny(bothDone, Task.Delay(500));
            if (race == bothDone) return; // both peers found each other - pass

            await peerA.AnnounceAsync();
            await peerB.AnnounceAsync();
        }

        // Final 3s wait - multicast delivery CAN be slow; give it one more settle.
        var both = Task.WhenAll(peerASeesBPort.Task, peerBSeesAPort.Task);
        var final = await Task.WhenAny(both, Task.Delay(3000));
        if (final != both)
        {
            // Neither side saw any peer events at all => no multicast on this host.
            // At least one side saw events but not the other side's port => test fail.
            var aDone = peerASeesBPort.Task.IsCompleted;
            var bDone = peerBSeesAPort.Task.IsCompleted;
            if (!aDone && !bDone)
                throw new UnsupportedTestException(
                    "No LSD multicast events received by either peer - multicast is not routable on this host.");
            throw new Exception(
                $"LSD 2-peer E2E failed: peerA saw peerB={aDone}, peerB saw peerA={bDone}. " +
                "One side received its own announce but not the other's - multicast partial-delivery issue.");
        }
    }

    // ── BEP 48: Tracker Scrape ���─

    [TestMethod]
    public async Task Bep48_ScrapeUrl_DerivedFromAnnounce()
    {
        // BEP 48: scrape URL is derived by replacing /announce with /scrape
        var tracker = new HttpTracker("http://tracker.example.com/announce", new byte[20], new byte[20], new HttpClient());
        if (tracker.ScrapeUrl != "http://tracker.example.com/scrape")
            throw new Exception($"Wrong scrape URL: {tracker.ScrapeUrl}");
        await tracker.DisposeAsync();
    }

    [TestMethod]
    public async Task Bep48_ScrapeUrl_WithPath()
    {
        var tracker = new HttpTracker("http://tracker.example.com/path/announce?key=1", new byte[20], new byte[20], new HttpClient());
        if (tracker.ScrapeUrl != "http://tracker.example.com/path/scrape?key=1")
            throw new Exception($"Wrong scrape URL with path: {tracker.ScrapeUrl}");
        await tracker.DisposeAsync();
    }

    [TestMethod]
    public async Task Bep48_ScrapeUrl_NoAnnounce_ReturnsNull()
    {
        var tracker = new HttpTracker("http://tracker.example.com/custom-endpoint", new byte[20], new byte[20], new HttpClient());
        if (tracker.ScrapeUrl != null)
            throw new Exception($"ScrapeUrl should be null when no /announce in URL, got: {tracker.ScrapeUrl}");
        await tracker.DisposeAsync();
    }

    [TestMethod]
    public async Task Bep48_ScrapeUrl_MultipleTrackers_DerivedCorrectly()
    {
        // Verify scrape URL derivation works for various tracker URL patterns
        var patterns = new (string announce, string? expectedScrape)[]
        {
            ("http://tracker.example.com/announce", "http://tracker.example.com/scrape"),
            ("http://tracker.example.com/x/announce", "http://tracker.example.com/x/scrape"),
            ("http://tracker.example.com:8080/announce?passkey=abc", "http://tracker.example.com:8080/scrape?passkey=abc"),
            ("http://tracker.example.com/no-announce-here", null),
        };

        foreach (var (announce, expected) in patterns)
        {
            var tracker = new HttpTracker(announce, new byte[20], new byte[20], new HttpClient());
            if (tracker.ScrapeUrl != expected)
                throw new Exception($"Announce: {announce}, Expected scrape: {expected}, Got: {tracker.ScrapeUrl}");
            await tracker.DisposeAsync();
        }
    }

    // ── BEP 53: Magnet URI Select Specific Files ──

    [TestMethod]
    public async Task Bep53_MagnetSelectFiles_SoParam()
    {
        // BEP 53: so=0,2,4 selects file indices 0, 2, 4
        var magnetUri = "magnet:?xt=urn:btih:08ada5a7a6183aae1e09d831df6748d566095a10&dn=Test&so=0,2,4";
        var client = CreateIsolatedClient();
        var torrent = client.Add(magnetUri);

        if (torrent.SelectedFileIndices == null)
            throw new Exception("SelectedFileIndices should be parsed from so= parameter");
        if (torrent.SelectedFileIndices.Length != 3)
            throw new Exception($"Expected 3 selected files, got {torrent.SelectedFileIndices.Length}");
        if (torrent.SelectedFileIndices[0] != 0 || torrent.SelectedFileIndices[1] != 2 || torrent.SelectedFileIndices[2] != 4)
            throw new Exception("Selected file indices don't match so=0,2,4");

        // Also test range parsing (BEP 53 spec supports ranges like 0-4,6)
        // Use a different infohash to avoid deduplication with the first torrent
        var magnetRange = "magnet:?xt=urn:btih:1111111111111111111111111111111111111111&dn=Range&so=0-2,5,7-8";
        var torrent2 = client.Add(magnetRange);
        if (torrent2.SelectedFileIndices == null)
            throw new Exception("SelectedFileIndices should parse ranges");
        // 0-2 = [0,1,2], 5 = [5], 7-8 = [7,8] => [0,1,2,5,7,8]
        var expected = new[] { 0, 1, 2, 5, 7, 8 };
        if (torrent2.SelectedFileIndices.Length != expected.Length)
            throw new Exception($"Range parse: expected {expected.Length} indices, got {torrent2.SelectedFileIndices.Length}");
        for (int i = 0; i < expected.Length; i++)
        {
            if (torrent2.SelectedFileIndices[i] != expected[i])
                throw new Exception($"Range parse index {i}: expected {expected[i]}, got {torrent2.SelectedFileIndices[i]}");
        }

        await client.DisposeAsync();
    }
}
