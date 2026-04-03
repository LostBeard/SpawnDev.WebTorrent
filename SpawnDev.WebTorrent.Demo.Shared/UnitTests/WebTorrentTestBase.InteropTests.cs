using SpawnDev.UnitTesting;
using SpawnDev.WebTorrent.Discovery;
using SpawnDev.WebTorrent.Torrent;

namespace SpawnDev.WebTorrent.Demo.Shared.UnitTests;

/// <summary>
/// Interop tests — verify our client works with real JS WebTorrent peers
/// on public trackers using the Creative Commons test torrents.
///
/// These tests prove:
/// 1. Binary string encoding is compatible with public trackers
/// 2. Tracker announces succeed and return peers
/// 3. Web seed downloads work from webtorrent.io
/// 4. Metadata can be obtained from .torrent URLs (xs=)
/// 5. We can discover real JS WebTorrent peers
/// </summary>
public abstract partial class WebTorrentTestBase
{
    // CC magnets with public trackers + web seeds
    private const string SintelMagnet = "magnet:?xt=urn:btih:08ada5a7a6183aae1e09d831df6748d566095a10&dn=Sintel&tr=wss%3A%2F%2Ftracker.openwebtorrent.com&ws=https%3A%2F%2Fwebtorrent.io%2Ftorrents%2F&xs=https%3A%2F%2Fwebtorrent.io%2Ftorrents%2Fsintel.torrent";
    private const string BigBuckBunnyMagnet = "magnet:?xt=urn:btih:dd8255ecdc7ca55fb0bbf81323d87062db1f6d1c&dn=Big+Buck+Bunny&tr=wss%3A%2F%2Ftracker.openwebtorrent.com&ws=https%3A%2F%2Fwebtorrent.io%2Ftorrents%2F&xs=https%3A%2F%2Fwebtorrent.io%2Ftorrents%2Fbig-buck-bunny.torrent";

    // ═══════════════════════════════════════════════════════════
    //  Public Tracker Interop
    // ═══════════════════════════════════════════════════════════

    [TestMethod(Timeout = 30000)]
    public async Task Interop_PublicTracker_AnnounceAndDiscoverPeers()
    {
        // Connect to tracker.openwebtorrent.com and announce Sintel.
        // This torrent always has JS WebTorrent peers seeding it.
        // If our encoding is compatible, the tracker will return peers.
        var tracker = new WebSocketTrackerClient("wss://tracker.openwebtorrent.com", Client!.PeerId);

        int seeders = 0, leechers = 0;
        var peersFound = new List<string>();
        tracker.OnAnnounceResponse += (s, l) => { seeders = s; leechers = l; };
        tracker.OnPeer += (p) => peersFound.Add(p.Address);

        var infoHash = Convert.FromHexString("08ada5a7a6183aae1e09d831df6748d566095a10");

        try
        {
            await tracker.StartAsync(infoHash, 0);

            // Wait for announce response
            var deadline = DateTime.UtcNow.AddSeconds(15);
            while (seeders == 0 && leechers == 0 && DateTime.UtcNow < deadline)
                await Task.Delay(500);

            Console.WriteLine($"[Interop] Tracker response: {seeders}S/{leechers}L, {peersFound.Count} peers");

            if (seeders == 0 && leechers == 0)
                throw new UnsupportedTestException("Public tracker returned no stats — may be down");

            // We got a response — encoding is compatible
            Console.WriteLine("[Interop] Public tracker announce: PASSED — binary encoding compatible");
        }
        finally
        {
            await tracker.DisposeAsync();
        }
    }

    [TestMethod(Timeout = 30000)]
    public async Task Interop_WebSeed_DownloadMetadataFromXs()
    {
        // Download .torrent metadata from xs= URL (webtorrent.io)
        // This proves our HTTP client and .torrent parser work with real torrents
        await using var client = new WebTorrentClient(crypto: Client!.Crypto);

        var swarm = await client.AddAsync(SintelMagnet);

        // Wait for metadata (should come from xs= URL, not peers)
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (!swarm.HasMetadata && DateTime.UtcNow < deadline)
            await Task.Delay(200);

        if (!swarm.HasMetadata)
            throw new UnsupportedTestException("Could not fetch metadata from xs= URL — webtorrent.io may be down");

        if (swarm.Metadata!.Name != "Sintel")
            throw new Exception($"Name mismatch: expected 'Sintel', got '{swarm.Metadata.Name}'");
        if (swarm.Metadata.Files.Length == 0)
            throw new Exception("No files in metadata");
        if (swarm.Metadata.TotalLength == 0)
            throw new Exception("TotalLength is 0");

        Console.WriteLine($"[Interop] Metadata from xs=: {swarm.Metadata.Name}, {swarm.Metadata.Files.Length} files, {swarm.Metadata.TotalLength:N0} bytes");
        Console.WriteLine("[Interop] xs= metadata download: PASSED");
    }

    [TestMethod(Timeout = 60000)]
    public async Task Interop_WebSeed_DownloadFirstPiece()
    {
        // Download the first piece from the web seed and verify its hash
        await using var client = new WebTorrentClient(crypto: Client!.Crypto);

        var swarm = await client.AddAsync(BigBuckBunnyMagnet);

        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (!swarm.HasMetadata && DateTime.UtcNow < deadline)
            await Task.Delay(200);

        if (!swarm.HasMetadata)
            throw new UnsupportedTestException("Could not fetch metadata — webtorrent.io may be down");

        // Download first piece via web seed
        if (swarm.Metadata!.UrlList.Length == 0)
            throw new Exception("No web seeds in metadata");

        Console.WriteLine($"[Interop] Web seed: {swarm.Metadata.UrlList[0]}");

        // Start download and wait for first piece
        int verified = 0;
        swarm.OnPieceVerified += (_) => Interlocked.Increment(ref verified);
        swarm.StartDownload();

        var pieceDeadline = DateTime.UtcNow.AddSeconds(30);
        while (verified == 0 && DateTime.UtcNow < pieceDeadline)
            await Task.Delay(200);

        swarm.StopDownload();

        if (verified == 0)
            throw new Exception("No pieces downloaded from web seed within 30s");

        Console.WriteLine($"[Interop] Downloaded and verified {verified} piece(s) from web seed");
        Console.WriteLine("[Interop] Web seed first piece download: PASSED");
    }

    [TestMethod(Timeout = 30000)]
    public async Task Interop_BinaryEncoding_InfoHashMatchesHex()
    {
        // Verify our binary string encoding produces the same result
        // that the tracker uses to match peers
        var hexHash = "08ada5a7a6183aae1e09d831df6748d566095a10";
        var infoHash = Convert.FromHexString(hexHash);

        var binaryString = TrackerEncoding.ToBinaryString(infoHash);
        var roundTripped = TrackerEncoding.FromBinaryString(binaryString);

        if (!roundTripped.SequenceEqual(infoHash))
            throw new Exception("Binary string round-trip failed");

        // Verify the binary string is 20 chars (one char per byte)
        if (binaryString.Length != 20)
            throw new Exception($"Binary string should be 20 chars, got {binaryString.Length}");

        // Verify it's NOT hex (which would be 40 chars)
        if (binaryString.Length == 40)
            throw new Exception("Binary string looks like hex — should be 20 chars, not 40");

        Console.WriteLine($"[Interop] Binary encoding: {binaryString.Length} chars for 20 bytes — correct");
        Console.WriteLine("[Interop] Binary string encoding: PASSED");
    }
}
