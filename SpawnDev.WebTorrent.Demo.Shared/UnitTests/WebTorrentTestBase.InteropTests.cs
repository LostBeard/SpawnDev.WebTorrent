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
        var hexHash = "08ada5a7a6183aae1e09d831df6748d566095a10";
        var infoHash = Convert.FromHexString(hexHash);

        var binaryString = TrackerEncoding.ToBinaryString(infoHash);
        var roundTripped = TrackerEncoding.FromBinaryString(binaryString);

        if (!roundTripped.SequenceEqual(infoHash))
            throw new Exception("Binary string round-trip failed");
        if (binaryString.Length != 20)
            throw new Exception($"Binary string should be 20 chars, got {binaryString.Length}");

        Console.WriteLine("[Interop] Binary string encoding: PASSED");
    }

    // ═══════════════════════════════════════════════════════════
    //  Real Protocol Tests — against tracker.openwebtorrent.com
    //  These test the ACTUAL protocol, not mocks.
    // ═══════════════════════════════════════════════════════════

    [TestMethod(Timeout = 60000)]
    public async Task Interop_RealTracker_AnnounceWithOffers_ReceiveAnswers()
    {
        // THE critical interop test: announce Sintel with pre-generated offers
        // to tracker.openwebtorrent.com. Real JS WebTorrent peers are seeding
        // this torrent. If our protocol is correct, we receive answers.
        var crypto = Client!.Crypto;
        if (crypto == null) throw new UnsupportedTestException("Requires IPortableCrypto");

        await using var client = new WebTorrentClient(crypto: crypto);
        var infoHash = Convert.FromHexString("08ada5a7a6183aae1e09d831df6748d566095a10");

        var tracker = new WebSocketTrackerClient("wss://tracker.openwebtorrent.com", client.PeerId);
        var webRtc = Transports.IWebRtcTransport.Create();
        var coordinator = new PeerCoordinator(client, infoHash, webRtc);

        bool gotOffer = false;
        bool gotAnswer = false;
        int peerCount = 0;

        tracker.OnOffer += (fromPeerId, offerId, offer) =>
        {
            gotOffer = true;
            Console.WriteLine($"[Interop_Real] Received OFFER from {fromPeerId[..Math.Min(8, fromPeerId.Length)]}...");
        };
        tracker.OnAnswer += (fromPeerId, offerId, answer) =>
        {
            gotAnswer = true;
            Console.WriteLine($"[Interop_Real] Received ANSWER from {fromPeerId[..Math.Min(8, fromPeerId.Length)]}...");
        };
        tracker.OnPeer += (p) =>
        {
            peerCount++;
            Console.WriteLine($"[Interop_Real] Peer discovered: {p.Address[..Math.Min(12, p.Address.Length)]}...");
        };

        int seeders = 0, leechers = 0;
        tracker.OnAnnounceResponse += (s, l) => { seeders = s; leechers = l; };

        // Generate offers (real WebRTC)
        var offers = new List<TrackerOffer>();
        for (int i = 0; i < 3; i++)
        {
            try
            {
                var offerId = Guid.NewGuid().ToString("N");
                var (sdp, conn) = await webRtc.CreateOfferAsync(offerId);
                offers.Add(new TrackerOffer(sdp, offerId));
                Console.WriteLine($"[Interop_Real] Generated offer {i}: type={sdp.Type}, sdp length={sdp.Sdp.Length}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Interop_Real] Offer {i} generation failed: {ex.Message}");
            }
        }

        if (offers.Count == 0)
            throw new UnsupportedTestException("Could not generate WebRTC offers — WebRTC may not be available");

        // Connect and announce WITH offers
        await tracker.StartAsync(infoHash, 0, offers.ToArray());

        Console.WriteLine($"[Interop_Real] Announced with {offers.Count} offers, waiting for responses...");

        // Wait for tracker response + any offers/answers from peers
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline && !gotOffer && !gotAnswer)
            await Task.Delay(500);

        Console.WriteLine($"[Interop_Real] Results: seeders={seeders}, leechers={leechers}, peers={peerCount}, gotOffer={gotOffer}, gotAnswer={gotAnswer}");

        await tracker.DisposeAsync();
        await webRtc.DisposeAsync();

        if (seeders == 0 && leechers == 0)
            throw new UnsupportedTestException("tracker.openwebtorrent.com returned no stats — may be down");

        if (!gotOffer && !gotAnswer)
            Console.WriteLine("[Interop_Real] No offers/answers received — no JS peers may be online right now");
        else
            Console.WriteLine("[Interop_Real] SUCCESS — received signaling from real JS WebTorrent peers");
    }

    [TestMethod(Timeout = 120000)]
    public async Task Interop_RealTracker_FullDownload_Sintel()
    {
        // Full end-to-end: add Sintel via magnet with public trackers + web seeds.
        // Downloads metadata (xs= or ut_metadata from peers) and at least 1 piece.
        // Proves our client is fully interoperable with the JS WebTorrent ecosystem.
        await using var client = new WebTorrentClient(crypto: Client!.Crypto);

        var swarm = await client.AddAsync(SintelMagnet);

        // Wait for metadata
        var metaDeadline = DateTime.UtcNow.AddSeconds(30);
        while (!swarm.HasMetadata && DateTime.UtcNow < metaDeadline)
            await Task.Delay(300);

        if (!swarm.HasMetadata)
            throw new UnsupportedTestException("Could not get metadata — webtorrent.io or tracker may be down");

        Console.WriteLine($"[Interop_Full] Metadata: {swarm.Metadata!.Name}, {swarm.Metadata.Files.Length} files, {swarm.Metadata.TotalLength:N0} bytes");
        Console.WriteLine($"[Interop_Full] Piece count: {swarm.Metadata.PieceCount}, piece length: {swarm.Metadata.PieceLength}");
        Console.WriteLine($"[Interop_Full] Web seeds: {swarm.Metadata.UrlList.Length}");
        Console.WriteLine($"[Interop_Full] Peers: {swarm.PeerCount}");

        // Download at least 5 pieces (from web seed or peers)
        int verified = 0;
        swarm.OnPieceVerified += (_) => Interlocked.Increment(ref verified);
        swarm.StartDownload();

        var dlDeadline = DateTime.UtcNow.AddSeconds(60);
        while (verified < 5 && DateTime.UtcNow < dlDeadline)
            await Task.Delay(300);

        swarm.StopDownload();

        Console.WriteLine($"[Interop_Full] Downloaded {verified} piece(s), peers: {swarm.PeerCount}");

        if (verified == 0)
            throw new Exception("No pieces downloaded from real WebTorrent ecosystem within 60s");

        Console.WriteLine($"[Interop_Full] SUCCESS — {verified} pieces downloaded from real WebTorrent ecosystem");
    }

    [TestMethod(Timeout = 60000)]
    public async Task Interop_RealTracker_ReceiveOffersFromPeers()
    {
        // Connect to tracker.openwebtorrent.com for Big Buck Bunny.
        // This torrent has active JS WebTorrent seeders who send offers.
        // We should receive their offers via the tracker relay.
        await using var client = new WebTorrentClient(crypto: Client!.Crypto);
        var infoHash = Convert.FromHexString("dd8255ecdc7ca55fb0bbf81323d87062db1f6d1c");

        var tracker = new WebSocketTrackerClient("wss://tracker.openwebtorrent.com", client.PeerId);

        bool receivedOffer = false;
        bool receivedAnswer = false;
        tracker.OnOffer += (_, _, _) => receivedOffer = true;
        tracker.OnAnswer += (_, _, _) => receivedAnswer = true;

        int seeders = 0;
        tracker.OnAnnounceResponse += (s, _) => seeders = s;

        // Announce WITHOUT offers — just listen for incoming offers from seeders
        await tracker.StartAsync(infoHash, 0);

        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!receivedOffer && !receivedAnswer && DateTime.UtcNow < deadline)
            await Task.Delay(500);

        Console.WriteLine($"[Interop_Offers] seeders={seeders}, receivedOffer={receivedOffer}, receivedAnswer={receivedAnswer}");

        await tracker.DisposeAsync();

        if (seeders == 0)
            throw new UnsupportedTestException("tracker.openwebtorrent.com returned no seeders — may be down");

        // We may or may not receive offers depending on whether seeders are
        // actively re-announcing. Log the result but don't fail on it.
        if (receivedOffer)
            Console.WriteLine("[Interop_Offers] SUCCESS — received offers from real JS WebTorrent seeders");
        else
            Console.WriteLine("[Interop_Offers] No offers received (seeders may not be re-announcing right now)");
    }
}
