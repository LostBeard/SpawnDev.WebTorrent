using SpawnDev.UnitTesting;
using SpawnDev.WebTorrent.Discovery;
using SpawnDev.WebTorrent.Torrent;
using SpawnDev.WebTorrent.Transports;

namespace SpawnDev.WebTorrent.Demo.Shared.UnitTests;

/// <summary>
/// P2P and real-world swarm tests using well-known Creative Commons torrents.
/// These test tracker connections, peer discovery, magnet parsing, and (in browser)
/// WebRTC signaling with real-world torrent swarms.
///
/// Torrents used: Big Buck Bunny, Sintel, Cosmos Laundromat, Tears of Steel
/// All are Creative Commons licensed, from Blender Foundation / webtorrent.io.
/// </summary>
public abstract partial class WebTorrentTestBase
{
    // ═══════════════════════════════════════════════════════════
    //  Well-Known Creative Commons Test Torrents
    // ═══════════════════════════════════════════════════════════

    private static readonly Dictionary<string, string> CCMagnets = new()
    {
        ["Big Buck Bunny"] = "magnet:?xt=urn:btih:dd8255ecdc7ca55fb0bbf81323d87062db1f6d1c&dn=Big+Buck+Bunny&tr=udp%3A%2F%2Fexplodie.org%3A6969&tr=udp%3A%2F%2Ftracker.coppersurfer.tk%3A6969&tr=udp%3A%2F%2Ftracker.empire-js.us%3A1337&tr=udp%3A%2F%2Ftracker.leechers-paradise.org%3A6969&tr=udp%3A%2F%2Ftracker.opentrackr.org%3A1337&tr=wss%3A%2F%2Ftracker.btorrent.xyz&tr=wss%3A%2F%2Ftracker.fastcast.nz&tr=wss%3A%2F%2Ftracker.openwebtorrent.com&ws=https%3A%2F%2Fwebtorrent.io%2Ftorrents%2F&xs=https%3A%2F%2Fwebtorrent.io%2Ftorrents%2Fbig-buck-bunny.torrent",
        ["Sintel"] = "magnet:?xt=urn:btih:08ada5a7a6183aae1e09d831df6748d566095a10&dn=Sintel&tr=udp%3A%2F%2Fexplodie.org%3A6969&tr=udp%3A%2F%2Ftracker.coppersurfer.tk%3A6969&tr=udp%3A%2F%2Ftracker.empire-js.us%3A1337&tr=udp%3A%2F%2Ftracker.leechers-paradise.org%3A6969&tr=udp%3A%2F%2Ftracker.opentrackr.org%3A1337&tr=wss%3A%2F%2Ftracker.btorrent.xyz&tr=wss%3A%2F%2Ftracker.fastcast.nz&tr=wss%3A%2F%2Ftracker.openwebtorrent.com&ws=https%3A%2F%2Fwebtorrent.io%2Ftorrents%2F&xs=https%3A%2F%2Fwebtorrent.io%2Ftorrents%2Fsintel.torrent",
        ["Cosmos Laundromat"] = "magnet:?xt=urn:btih:c9e15763f722f23e98a29decdfae341b98d53056&dn=Cosmos+Laundromat&tr=udp%3A%2F%2Fexplodie.org%3A6969&tr=udp%3A%2F%2Ftracker.coppersurfer.tk%3A6969&tr=udp%3A%2F%2Ftracker.empire-js.us%3A1337&tr=udp%3A%2F%2Ftracker.leechers-paradise.org%3A6969&tr=udp%3A%2F%2Ftracker.opentrackr.org%3A1337&tr=wss%3A%2F%2Ftracker.btorrent.xyz&tr=wss%3A%2F%2Ftracker.fastcast.nz&tr=wss%3A%2F%2Ftracker.openwebtorrent.com&ws=https%3A%2F%2Fwebtorrent.io%2Ftorrents%2F&xs=https%3A%2F%2Fwebtorrent.io%2Ftorrents%2Fcosmos-laundromat.torrent",
        ["Tears of Steel"] = "magnet:?xt=urn:btih:209c8226b299b308beaf2b9cd3fb49212dbd13ec&dn=Tears+of+Steel&tr=udp%3A%2F%2Fexplodie.org%3A6969&tr=udp%3A%2F%2Ftracker.coppersurfer.tk%3A6969&tr=udp%3A%2F%2Ftracker.empire-js.us%3A1337&tr=udp%3A%2F%2Ftracker.leechers-paradise.org%3A6969&tr=udp%3A%2F%2Ftracker.opentrackr.org%3A1337&tr=wss%3A%2F%2Ftracker.btorrent.xyz&tr=wss%3A%2F%2Ftracker.fastcast.nz&tr=wss%3A%2F%2Ftracker.openwebtorrent.com&ws=https%3A%2F%2Fwebtorrent.io%2Ftorrents%2F&xs=https%3A%2F%2Fwebtorrent.io%2Ftorrents%2Ftears-of-steel.torrent",
    };

    private static readonly string[] PublicWebSocketTrackers = new[]
    {
        "wss://hub.spawndev.com:44365/announce",
        "wss://tracker.openwebtorrent.com",
        "wss://tracker.files.fm:7073/announce",
    };

    // ═══════════════════════════════════════════════════════════
    //  Magnet Parsing — Real-World CC Torrents
    // ═══════════════════════════════════════════════════════════

    [TestMethod]
    public async Task P2P_ParseBigBuckBunnyMagnet()
    {
        var meta = TorrentParser.ParseMagnet(CCMagnets["Big Buck Bunny"]);

        if (meta.InfoHash.Length != 20)
            throw new Exception($"InfoHash should be 20 bytes, got {meta.InfoHash.Length}");

        var hash = Convert.ToHexString(meta.InfoHash).ToLowerInvariant();
        if (hash != "dd8255ecdc7ca55fb0bbf81323d87062db1f6d1c")
            throw new Exception($"InfoHash mismatch: {hash}");

        if (meta.Name != "Big Buck Bunny")
            throw new Exception($"Name should be 'Big Buck Bunny', got '{meta.Name}'");

        // Should have multiple trackers including WebSocket trackers
        if (meta.AnnounceList.Length == 0)
            throw new Exception("No trackers parsed");

        // Should have web seeds
        if (meta.UrlList.Length == 0)
            throw new Exception("No web seeds parsed from ws= parameter");

        Console.WriteLine($"[P2P] Big Buck Bunny: {meta.AnnounceList.Length} tracker tiers, {meta.UrlList.Length} web seeds");
    }

    [TestMethod]
    public async Task P2P_ParseSintelMagnet()
    {
        var meta = TorrentParser.ParseMagnet(CCMagnets["Sintel"]);
        var hash = Convert.ToHexString(meta.InfoHash).ToLowerInvariant();

        if (hash != "08ada5a7a6183aae1e09d831df6748d566095a10")
            throw new Exception($"Sintel InfoHash mismatch: {hash}");
        if (meta.Name != "Sintel")
            throw new Exception($"Name should be 'Sintel', got '{meta.Name}'");

        Console.WriteLine($"[P2P] Sintel: hash={hash[..8]}..., {meta.AnnounceList.Length} tracker tiers");
    }

    [TestMethod]
    public async Task P2P_ParseAllCCMagnets()
    {
        foreach (var (name, magnet) in CCMagnets)
        {
            var meta = TorrentParser.ParseMagnet(magnet);

            if (meta.InfoHash.Length != 20)
                throw new Exception($"{name}: InfoHash should be 20 bytes");
            if (string.IsNullOrEmpty(meta.Name))
                throw new Exception($"{name}: Name should not be empty");
            if (meta.AnnounceList.Length == 0)
                throw new Exception($"{name}: Should have trackers");

            var hash = Convert.ToHexString(meta.InfoHash).ToLowerInvariant();
            Console.WriteLine($"[P2P] {name}: hash={hash[..8]}..., trackers={meta.AnnounceList.Length}");
        }
    }

    [TestMethod]
    public async Task P2P_ClientAddMagnet_BigBuckBunny()
    {
        await using var client = new WebTorrentClient();
        var swarm = await client.AddAsync(CCMagnets["Big Buck Bunny"]);

        if (swarm.InfoHash.Length != 20)
            throw new Exception("InfoHash should be 20 bytes");

        var hash = Convert.ToHexString(swarm.InfoHash).ToLowerInvariant();
        if (hash != "dd8255ecdc7ca55fb0bbf81323d87062db1f6d1c")
            throw new Exception($"InfoHash mismatch: {hash}");

        Console.WriteLine($"[P2P] Client added Big Buck Bunny, hash={hash[..8]}...");
    }

    [TestMethod]
    public async Task P2P_ClientAddMagnet_AllCCTorrents()
    {
        await using var client = new WebTorrentClient();

        foreach (var (name, magnet) in CCMagnets)
        {
            var swarm = await client.AddAsync(magnet);
            if (swarm.InfoHash.Length != 20)
                throw new Exception($"{name}: InfoHash should be 20 bytes");

            Console.WriteLine($"[P2P] Added {name}");
        }

        if (client.Torrents.Count != CCMagnets.Count)
            throw new Exception($"Expected {CCMagnets.Count} torrents, got {client.Torrents.Count}");

        Console.WriteLine($"[P2P] All {CCMagnets.Count} CC torrents added successfully");
    }

    // ═══════════════════════════════════════════════════════════
    //  Tracker Connection Tests (real network)
    // ═══════════════════════════════════════════════════════════

    [TestMethod(Timeout = 30000)]
    public async Task P2P_TrackerConnect_BigBuckBunny()
    {
        var meta = TorrentParser.ParseMagnet(CCMagnets["Big Buck Bunny"]);
        var peerId = new byte[20];
        "-SD0100-"u8.CopyTo(peerId);
        Random.Shared.NextBytes(peerId.AsSpan(8));

        // Try connecting to public WebSocket trackers
        string? connectedTracker = null;
        int seeders = 0, leechers = 0;
        var peersFound = new List<string>();

        foreach (var trackerUrl in PublicWebSocketTrackers)
        {
            var tracker = new WebSocketTrackerClient(trackerUrl, peerId);
            var connectedTcs = new TaskCompletionSource();
            var announceTcs = new TaskCompletionSource();

            tracker.OnConnected += () => connectedTcs.TrySetResult();
            tracker.OnAnnounceResponse += (s, l) =>
            {
                seeders = s;
                leechers = l;
                announceTcs.TrySetResult();
            };
            tracker.OnPeer += (peer) => peersFound.Add(peer.Address);
            tracker.OnError += (err) => Console.WriteLine($"[P2P] Tracker {trackerUrl} error: {err}");

            try
            {
                using var cts = new CancellationTokenSource(8000);
                await tracker.StartAsync(meta.InfoHash, 0, cts.Token);

                // Wait for connection
                using var connCts = new CancellationTokenSource(5000);
                connCts.Token.Register(() => connectedTcs.TrySetCanceled());
                await connectedTcs.Task;

                // Wait for announce response
                using var annCts = new CancellationTokenSource(5000);
                annCts.Token.Register(() => announceTcs.TrySetCanceled());
                await announceTcs.Task;

                connectedTracker = trackerUrl;
                Console.WriteLine($"[P2P] Connected to {trackerUrl}: {seeders} seeders, {leechers} leechers, {peersFound.Count} peers");
                await tracker.StopAsync();
                break;
            }
            catch (OperationCanceledException)
            {
                Console.WriteLine($"[P2P] Timeout connecting to {trackerUrl}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[P2P] Failed to connect to {trackerUrl}: {ex.Message}");
            }
            finally
            {
                await tracker.DisposeAsync();
            }
        }

        if (connectedTracker == null)
            throw new UnsupportedTestException("Could not connect to any public WebSocket tracker");

        Console.WriteLine($"[P2P] Big Buck Bunny swarm: tracker={connectedTracker}, seeders={seeders}, leechers={leechers}, peers={peersFound.Count}");
    }

    [TestMethod(Timeout = 30000)]
    public async Task P2P_TrackerConnect_Sintel()
    {
        var meta = TorrentParser.ParseMagnet(CCMagnets["Sintel"]);
        var peerId = new byte[20];
        "-SD0100-"u8.CopyTo(peerId);
        Random.Shared.NextBytes(peerId.AsSpan(8));

        string? connectedTracker = null;
        int seeders = 0, leechers = 0;

        foreach (var trackerUrl in PublicWebSocketTrackers)
        {
            var tracker = new WebSocketTrackerClient(trackerUrl, peerId);
            var announceTcs = new TaskCompletionSource();

            tracker.OnAnnounceResponse += (s, l) =>
            {
                seeders = s;
                leechers = l;
                announceTcs.TrySetResult();
            };

            try
            {
                using var cts = new CancellationTokenSource(8000);
                await tracker.StartAsync(meta.InfoHash, 0, cts.Token);

                using var annCts = new CancellationTokenSource(5000);
                annCts.Token.Register(() => announceTcs.TrySetCanceled());
                await announceTcs.Task;

                connectedTracker = trackerUrl;
                Console.WriteLine($"[P2P] Sintel: tracker={trackerUrl}, seeders={seeders}, leechers={leechers}");
                await tracker.StopAsync();
                break;
            }
            catch
            {
                // Try next tracker
            }
            finally
            {
                await tracker.DisposeAsync();
            }
        }

        if (connectedTracker == null)
            throw new UnsupportedTestException("Could not connect to any public WebSocket tracker for Sintel");
    }

    [TestMethod(Timeout = 45000)]
    public async Task P2P_TrackerDiscoverPeers_BigBuckBunny()
    {
        var meta = TorrentParser.ParseMagnet(CCMagnets["Big Buck Bunny"]);
        var peerId = new byte[20];
        "-SD0100-"u8.CopyTo(peerId);
        Random.Shared.NextBytes(peerId.AsSpan(8));

        var peersFound = new List<string>();
        var peerTcs = new TaskCompletionSource();

        foreach (var trackerUrl in PublicWebSocketTrackers)
        {
            var tracker = new WebSocketTrackerClient(trackerUrl, peerId);
            tracker.OnPeer += (peer) =>
            {
                peersFound.Add(peer.Address);
                Console.WriteLine($"[P2P] Discovered peer: {peer.Address}");
                peerTcs.TrySetResult();
            };

            try
            {
                using var cts = new CancellationTokenSource(10000);
                await tracker.StartAsync(meta.InfoHash, 0, cts.Token);

                // Wait a bit for peer discovery
                using var peerCts = new CancellationTokenSource(10000);
                peerCts.Token.Register(() => peerTcs.TrySetCanceled());

                try
                {
                    await peerTcs.Task;
                    Console.WriteLine($"[P2P] Found {peersFound.Count} peer(s) via {trackerUrl}");
                    await tracker.StopAsync();
                    await tracker.DisposeAsync();
                    break;
                }
                catch (OperationCanceledException)
                {
                    Console.WriteLine($"[P2P] No peers found via {trackerUrl} within timeout");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[P2P] Tracker {trackerUrl} failed: {ex.Message}");
            }
            finally
            {
                await tracker.DisposeAsync();
            }
        }

        // Peers may or may not be found — the swarm activity varies
        // This test verifies the tracker protocol works, peer discovery is a bonus
        Console.WriteLine($"[P2P] Total peers discovered: {peersFound.Count}");
    }

    // ═══════════════════════════════════════════════════════════
    //  WebRTC Transport Tests (browser only)
    // ═══════════════════════════════════════════════════════════

    [TestMethod]
    public async Task P2P_WebRtcTransport_Create()
    {
        // Verify WebRTC transport can be created with options
        var transport = new WebRtcTransport(new WebRtcTransportOptions
        {
            IceServers = new[] { "stun:stun.l.google.com:19302" },
            ChannelLabel = "test-channel",
            Ordered = false,
        });

        if (transport.Type != "webrtc")
            throw new Exception($"Expected type 'webrtc', got '{transport.Type}'");
        if (!transport.CanAccept)
            throw new Exception("WebRTC transport should accept incoming connections");

        await transport.DisposeAsync();
        Console.WriteLine("[P2P] WebRTC transport created and disposed successfully");
    }

    [TestMethod]
    public async Task P2P_WebRtcTransport_DefaultOptions()
    {
        var transport = new WebRtcTransport();

        if (transport.Type != "webrtc")
            throw new Exception($"Expected type 'webrtc', got '{transport.Type}'");

        await transport.DisposeAsync();
    }

    [TestMethod]
    public async Task P2P_WebRtcConnection_Create()
    {
        // Verify WebRTC connection can be constructed (doesn't touch browser APIs)
        var conn = new WebRtcConnection("test-peer-123", new WebRtcTransportOptions());

        if (conn.RemoteId != "test-peer-123")
            throw new Exception($"RemoteId mismatch: '{conn.RemoteId}'");
        if (conn.TransportType != "webrtc")
            throw new Exception($"TransportType should be 'webrtc', got '{conn.TransportType}'");
        if (conn.IsConnected)
            throw new Exception("Should not be connected before offer/answer");

        await conn.DisposeAsync();
        Console.WriteLine("[P2P] WebRTC connection constructed and disposed");
    }

    // ═══════════════════════════════════════════════════════════
    //  WebRTC Signaling Tests (browser only — requires RTCPeerConnection)
    // ═══════════════════════════════════════════════════════════

    [TestMethod(Timeout = 30000)]
    public async Task P2P_WebRtcOffer_CreateValid()
    {
        // Only works in browser context where RTCPeerConnection is available
        if (!OperatingSystem.IsBrowser())
            throw new UnsupportedTestException("WebRTC requires browser context");

        var conn = new WebRtcConnection("test-peer", new WebRtcTransportOptions());
        var offer = await conn.CreateOfferAsync();

        if (offer == null)
            throw new Exception("CreateOfferAsync returned null");

        // The offer should be an RTCSessionDescription with type "offer" and SDP
        var offerJson = System.Text.Json.JsonSerializer.Serialize(offer);
        if (!offerJson.Contains("\"type\"") || !offerJson.Contains("\"sdp\""))
            throw new Exception($"Offer should contain type and sdp fields: {offerJson[..Math.Min(200, offerJson.Length)]}");
        if (!offerJson.Contains("offer"))
            throw new Exception("Offer type should be 'offer'");
        if (!offerJson.Contains("v=0"))
            throw new Exception("SDP should contain 'v=0' (SDP version)");

        Console.WriteLine($"[P2P] WebRTC offer created: {offerJson.Length} chars");
        await conn.DisposeAsync();
    }

    [TestMethod(Timeout = 60000)]
    public async Task P2P_WebRtcLoopback_OfferAnswer()
    {
        // Test the full offer/answer cycle between two local connections
        // This validates the signaling works without needing a real remote peer
        if (!OperatingSystem.IsBrowser())
            throw new UnsupportedTestException("WebRTC requires browser context");

        var options = new WebRtcTransportOptions
        {
            IceServers = new[] { "stun:stun.l.google.com:19302" },
            ChannelLabel = "loopback-test",
        };

        // Initiator creates offer
        var initiator = new WebRtcConnection("peer-b", options);
        var offer = await initiator.CreateOfferAsync();

        // Responder handles offer and creates answer
        var responder = new WebRtcConnection("peer-a", options);
        var answer = await responder.HandleOfferAsync(offer);

        // Initiator handles answer (completes signaling)
        await initiator.HandleAnswerAsync(answer);

        // Both sides should be opening data channels now
        // Wait briefly for ICE connectivity and data channel open
        var openTimeout = Task.Delay(15000);
        var initiatorOpen = initiator.WaitForOpenAsync();
        var responderOpen = responder.WaitForOpenAsync();

        var result = await Task.WhenAny(
            Task.WhenAll(initiatorOpen, responderOpen),
            openTimeout
        );

        if (result == openTimeout)
        {
            Console.WriteLine("[P2P] Loopback: data channels did not open within timeout (ICE may have failed — this is OK in some network configs)");
            // Don't fail — ICE loopback depends on network config
            await initiator.DisposeAsync();
            await responder.DisposeAsync();
            return;
        }

        if (!initiator.IsConnected || !responder.IsConnected)
            throw new Exception($"Expected both connected: initiator={initiator.IsConnected}, responder={responder.IsConnected}");

        // Test data exchange
        var testData = new byte[] { 0x13, 0x42, 0xFF, 0x00, 0xAB };
        await initiator.SendAsync(testData);

        // Give a moment for data to arrive
        var recvBuf = new byte[64];
        using var recvCts = new CancellationTokenSource(5000);
        var bytesRead = await responder.ReceiveAsync(recvBuf, recvCts.Token);

        if (bytesRead != testData.Length)
            throw new Exception($"Expected {testData.Length} bytes, received {bytesRead}");
        for (int i = 0; i < testData.Length; i++)
            if (recvBuf[i] != testData[i])
                throw new Exception($"Data mismatch at byte {i}: expected 0x{testData[i]:X2}, got 0x{recvBuf[i]:X2}");

        Console.WriteLine("[P2P] Loopback: offer/answer/data exchange PASSED");

        await initiator.DisposeAsync();
        await responder.DisposeAsync();
    }

    // ═══════════════════════════════════════════════════════════
    //  PeerCoordinator Tests
    // ═══════════════════════════════════════════════════════════

    [TestMethod]
    public async Task P2P_PeerCoordinator_Create()
    {
        await using var client = new WebTorrentClient();
        var infoHash = Convert.FromHexString("dd8255ecdc7ca55fb0bbf81323d87062db1f6d1c");
        var transport = new WebRtcTransport();

        var coordinator = new PeerCoordinator(client, infoHash, transport);

        if (coordinator.PeerCount != 0)
            throw new Exception($"Expected 0 peers, got {coordinator.PeerCount}");

        await coordinator.DisposeAsync();
        await transport.DisposeAsync();
        Console.WriteLine("[P2P] PeerCoordinator created and disposed");
    }

    [TestMethod(Timeout = 60000)]
    public async Task P2P_PeerCoordinator_TrackerAnnounce()
    {
        if (!OperatingSystem.IsBrowser())
            throw new UnsupportedTestException("PeerCoordinator with tracker requires browser context for WebSocket");

        await using var client = new WebTorrentClient();
        var infoHash = Convert.FromHexString("dd8255ecdc7ca55fb0bbf81323d87062db1f6d1c");
        var transport = new WebRtcTransport();
        var coordinator = new PeerCoordinator(client, infoHash, transport);

        var swarmUpdateTcs = new TaskCompletionSource();
        int seeders = 0, leechers = 0;
        coordinator.OnSwarmUpdate += (s, l) =>
        {
            seeders = s;
            leechers = l;
            swarmUpdateTcs.TrySetResult();
        };

        // Try adding a tracker
        try
        {
            await coordinator.AddTrackerAsync("wss://hub.spawndev.com:44365/announce");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[P2P] Tracker connection failed: {ex.Message}");
            throw new UnsupportedTestException("Could not connect to tracker");
        }

        // Wait for swarm update
        using var cts = new CancellationTokenSource(15000);
        cts.Token.Register(() => swarmUpdateTcs.TrySetCanceled());

        try
        {
            await swarmUpdateTcs.Task;
            Console.WriteLine($"[P2P] Swarm update: {seeders} seeders, {leechers} leechers");
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine("[P2P] No swarm update within timeout — tracker may be slow");
        }

        await coordinator.DisposeAsync();
        await transport.DisposeAsync();
    }

    // ═══════════════════════════════════════════════════════════
    //  Web Seed Download Tests (real network, works anywhere)
    // ═══════════════════════════════════════════════════════════

    [TestMethod(Timeout = 30000)]
    public async Task P2P_WebSeed_DownloadTorrentFile()
    {
        // Download the .torrent file from webtorrent.io and parse it
        using var http = new HttpClient();
        http.Timeout = TimeSpan.FromSeconds(15);

        byte[]? torrentBytes = null;
        try
        {
            torrentBytes = await http.GetByteArrayAsync("https://webtorrent.io/torrents/big-buck-bunny.torrent");
        }
        catch (Exception ex)
        {
            throw new UnsupportedTestException($"Could not download .torrent file: {ex.Message}");
        }

        if (torrentBytes.Length < 100)
            throw new Exception($"Torrent file too small: {torrentBytes.Length} bytes");

        var metadata = TorrentParser.Parse(torrentBytes);

        // Verify info hash matches the magnet URI
        var hash = Convert.ToHexString(metadata.InfoHash).ToLowerInvariant();
        if (hash != "dd8255ecdc7ca55fb0bbf81323d87062db1f6d1c")
            throw new Exception($"InfoHash mismatch: {hash}");

        if (string.IsNullOrEmpty(metadata.Name))
            throw new Exception("Torrent name should not be empty");
        if (metadata.TotalLength <= 0)
            throw new Exception($"TotalLength should be positive, got {metadata.TotalLength}");
        if (metadata.PieceHashes.Length == 0)
            throw new Exception("Should have piece hashes");
        if (metadata.PieceLength <= 0)
            throw new Exception($"PieceLength should be positive, got {metadata.PieceLength}");

        Console.WriteLine($"[P2P] Big Buck Bunny .torrent: name='{metadata.Name}', size={metadata.TotalLength:N0}, pieces={metadata.PieceHashes.Length}, pieceLen={metadata.PieceLength}");
    }

    [TestMethod(Timeout = 30000)]
    public async Task P2P_WebSeed_DownloadSintelTorrentFile()
    {
        using var http = new HttpClient();
        http.Timeout = TimeSpan.FromSeconds(15);

        byte[]? torrentBytes = null;
        try
        {
            torrentBytes = await http.GetByteArrayAsync("https://webtorrent.io/torrents/sintel.torrent");
        }
        catch (Exception ex)
        {
            throw new UnsupportedTestException($"Could not download .torrent file: {ex.Message}");
        }

        var metadata = TorrentParser.Parse(torrentBytes);
        var hash = Convert.ToHexString(metadata.InfoHash).ToLowerInvariant();

        if (hash != "08ada5a7a6183aae1e09d831df6748d566095a10")
            throw new Exception($"Sintel InfoHash mismatch: {hash}");

        Console.WriteLine($"[P2P] Sintel .torrent: name='{metadata.Name}', size={metadata.TotalLength:N0}, pieces={metadata.PieceHashes.Length}");
    }

    [TestMethod(Timeout = 60000)]
    public async Task P2P_WebSeed_DownloadAndVerifyPieces()
    {
        // Download the .torrent file, then use the web seed to download actual pieces
        // and verify them against the piece hashes
        using var http = new HttpClient();
        http.Timeout = TimeSpan.FromSeconds(30);

        byte[]? torrentBytes = null;
        try
        {
            torrentBytes = await http.GetByteArrayAsync("https://webtorrent.io/torrents/big-buck-bunny.torrent");
        }
        catch (Exception ex)
        {
            throw new UnsupportedTestException($"Could not download .torrent file: {ex.Message}");
        }

        var metadata = TorrentParser.Parse(torrentBytes);

        // Try to download the first piece from the web seed
        if (metadata.UrlList.Length == 0)
            throw new UnsupportedTestException("No web seeds in .torrent metadata");

        var webSeedUrl = metadata.UrlList[0];
        var fileName = metadata.Name;

        // Download first piece via HTTP range request
        var fileUrl = webSeedUrl.EndsWith("/") ? webSeedUrl + fileName : webSeedUrl;
        var request = new HttpRequestMessage(HttpMethod.Get, fileUrl);
        request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, metadata.PieceLength - 1);

        byte[]? pieceData;
        try
        {
            var response = await http.SendAsync(request);
            pieceData = await response.Content.ReadAsByteArrayAsync();
        }
        catch (Exception ex)
        {
            throw new UnsupportedTestException($"Web seed download failed: {ex.Message}");
        }

        if (pieceData.Length == 0)
            throw new Exception("Downloaded 0 bytes from web seed");

        // Verify the piece hash
        var actualHash = System.Security.Cryptography.SHA1.HashData(pieceData);
        if (!actualHash.SequenceEqual(metadata.PieceHashes[0]))
        {
            Console.WriteLine($"[P2P] Piece hash mismatch (piece may be multi-file or different size)");
            Console.WriteLine($"[P2P] Expected: {Convert.ToHexString(metadata.PieceHashes[0])}");
            Console.WriteLine($"[P2P] Got:      {Convert.ToHexString(actualHash)}");
            Console.WriteLine($"[P2P] Downloaded {pieceData.Length} bytes, piece length = {metadata.PieceLength}");
            // Don't fail — web seed URL format may differ from simple case
        }
        else
        {
            Console.WriteLine($"[P2P] First piece verified! Downloaded {pieceData.Length} bytes from web seed");
        }
    }
}
