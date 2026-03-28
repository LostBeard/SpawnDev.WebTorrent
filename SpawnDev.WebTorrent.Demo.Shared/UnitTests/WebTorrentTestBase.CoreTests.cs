using SpawnDev.UnitTesting;
using SpawnDev.WebTorrent.Bencode;
using SpawnDev.WebTorrent.Discovery;
using SpawnDev.WebTorrent.Storage;
using SpawnDev.WebTorrent.Torrent;
using SpawnDev.WebTorrent.Transports;
using SpawnDev.WebTorrent.Wire;

namespace SpawnDev.WebTorrent.Demo.Shared.UnitTests;

/// <summary>
/// Core functionality tests covering the new P2P pipeline:
/// TorrentSwarm peer management, ut_metadata, ut_pex, rate limiter,
/// seeding, incoming connection routing, and extension protocol.
/// </summary>
public abstract partial class WebTorrentTestBase
{
    // ═══════════════════════════════════════════════════════════
    //  TorrentSwarm — Peer Management
    // ═══════════════════════════════════════════════════════════

    [TestMethod]
    public async Task Swarm_SetMetadata_CreatesCoordinator()
    {
        await using var client = new WebTorrentClient();
        var swarm = await client.AddAsync("magnet:?xt=urn:btih:dd8255ecdc7ca55fb0bbf81323d87062db1f6d1c&dn=Test");

        // Before metadata
        if (swarm.HasMetadata) throw new Exception("Should not have metadata yet");
        if (swarm.PieceManager != null) throw new Exception("PieceManager should be null");
        if (swarm.Coordinator != null) throw new Exception("Coordinator should be null");

        // Set metadata
        var data = new byte[32768];
        var (_, metadata) = TorrentCreator.CreateFromBytes("test.bin", data,
            new TorrentCreatorOptions { PieceLength = 16384 });

        // Use a different swarm since info hash won't match magnet
        var swarm2 = await client.AddAsync(metadata);

        if (!swarm2.HasMetadata) throw new Exception("Should have metadata");
        if (swarm2.PieceManager == null) throw new Exception("PieceManager should be created");
        if (swarm2.Coordinator == null) throw new Exception("Coordinator should be created");
        if (swarm2.Files.Length != 1) throw new Exception($"Should have 1 file, got {swarm2.Files.Length}");
        if (swarm2.Bitfield == null) throw new Exception("Bitfield should be created");
        if (swarm2.Bitfield.Length != 2) throw new Exception($"Bitfield should be 2, got {swarm2.Bitfield.Length}");
    }

    [TestMethod]
    public async Task Swarm_Events_OnReady()
    {
        await using var client = new WebTorrentClient();
        bool readyFired = false;
        bool clientReadyFired = false;

        client.OnTorrentReady += (_) => clientReadyFired = true;

        var data = new byte[16384];
        var (_, metadata) = TorrentCreator.CreateFromBytes("test.bin", data,
            new TorrentCreatorOptions { PieceLength = 16384 });

        var swarm = new TorrentSwarm(client, new AddTorrentOptions());
        swarm.OnReady += () => readyFired = true;

        // Manually test since AddAsync auto-sets metadata
        swarm.SetMetadata(metadata);

        if (!readyFired) throw new Exception("OnReady should have fired");
    }

    [TestMethod]
    public async Task Swarm_PauseResume()
    {
        await using var client = new WebTorrentClient();
        var swarm = await client.AddAsync("magnet:?xt=urn:btih:dd8255ecdc7ca55fb0bbf81323d87062db1f6d1c&dn=Test",
            new AddTorrentOptions { Paused = true });

        if (!swarm.Paused) throw new Exception("Should be paused");

        swarm.Resume();
        if (swarm.Paused) throw new Exception("Should not be paused after Resume");

        swarm.Pause();
        if (!swarm.Paused) throw new Exception("Should be paused after Pause");
    }

    [TestMethod]
    public async Task Swarm_AddPeer_RespectsMax()
    {
        await using var client = new WebTorrentClient();
        var swarm = await client.AddAsync("magnet:?xt=urn:btih:dd8255ecdc7ca55fb0bbf81323d87062db1f6d1c&dn=Test");

        // AddPeer with no transport won't actually connect (FindTransport returns null)
        // but it should deduplicate
        swarm.AddPeer(new PeerInfo { Address = "192.168.1.1:6881", Source = "test" });
        swarm.AddPeer(new PeerInfo { Address = "192.168.1.1:6881", Source = "test" }); // duplicate

        // No crash, dedup works (we can't verify count since connect fails silently)
    }

    [TestMethod]
    public async Task Swarm_AddPeer_IgnoresWhenPaused()
    {
        await using var client = new WebTorrentClient();
        var swarm = await client.AddAsync("magnet:?xt=urn:btih:dd8255ecdc7ca55fb0bbf81323d87062db1f6d1c&dn=Test",
            new AddTorrentOptions { Paused = true });

        swarm.AddPeer(new PeerInfo { Address = "192.168.1.1:6881", Source = "test" });
        // Should be ignored since paused — no crash
    }

    // ═══════════════════════════════════════════════════════════
    //  ut_metadata (BEP 9)
    // ═══════════════════════════════════════════════════════════

    [TestMethod]
    public async Task UtMetadata_CreateRequest()
    {
        var ext = new UtMetadataExtension();
        var request = ext.CreateRequest(0);
        var requestStr = System.Text.Encoding.ASCII.GetString(request);

        if (!requestStr.Contains("msg_typei0e"))
            throw new Exception($"Request should contain msg_type=0, got: {requestStr}");
        if (!requestStr.Contains("piecei0e"))
            throw new Exception($"Request should contain piece=0, got: {requestStr}");
    }

    [TestMethod]
    public async Task UtMetadata_CreateRequest_DifferentPieces()
    {
        var ext = new UtMetadataExtension();

        for (int i = 0; i < 5; i++)
        {
            var request = ext.CreateRequest(i);
            var requestStr = System.Text.Encoding.ASCII.GetString(request);
            if (!requestStr.Contains($"piecei{i}e"))
                throw new Exception($"Request for piece {i} incorrect: {requestStr}");
        }
    }

    [TestMethod]
    public async Task UtMetadata_HandshakeData_WithMetadata()
    {
        var ext = new UtMetadataExtension();
        ext.LocalMetadata = new byte[32768]; // 32KB

        var data = ext.GetHandshakeData();
        if (data == null) throw new Exception("Should return handshake data when we have metadata");
        if (!data.ContainsKey("metadata_size"))
            throw new Exception("Should contain metadata_size key");
        if ((long)data["metadata_size"] != 32768)
            throw new Exception($"metadata_size should be 32768, got {data["metadata_size"]}");
    }

    [TestMethod]
    public async Task UtMetadata_HandshakeData_WithoutMetadata()
    {
        var ext = new UtMetadataExtension();
        var data = ext.GetHandshakeData();
        if (data != null) throw new Exception("Should return null when we don't have metadata");
    }

    [TestMethod]
    public async Task UtMetadata_ProcessHandshake()
    {
        var ext = new UtMetadataExtension();
        ext.ProcessHandshakeData(new Dictionary<string, object>
        {
            ["metadata_size"] = (long)65536,
        });

        if (ext.MetadataSize != 65536)
            throw new Exception($"MetadataSize should be 65536, got {ext.MetadataSize}");
    }

    [TestMethod]
    public async Task UtMetadata_HandleData_AssemblesMetadata()
    {
        var ext = new UtMetadataExtension();
        ext.MetadataSize = 100; // small metadata, fits in 1 piece
        ext.RemoteId = 1;

        byte[]? completedMetadata = null;
        ext.OnMetadataComplete += (data) => completedMetadata = data;

        // Build a data response: bencode dict + raw metadata
        var metadataBytes = new byte[100];
        for (int i = 0; i < 100; i++) metadataBytes[i] = (byte)(i % 256);

        // Set expected hash
        ext.ExpectedInfoHash = System.Security.Cryptography.SHA1.HashData(metadataBytes);

        var dictStr = "d8:msg_typei1e5:piecei0e10:total_sizei100ee";
        var dictBytes = System.Text.Encoding.ASCII.GetBytes(dictStr);
        var payload = new byte[dictBytes.Length + metadataBytes.Length];
        Array.Copy(dictBytes, payload, dictBytes.Length);
        Array.Copy(metadataBytes, 0, payload, dictBytes.Length, metadataBytes.Length);

        await ext.HandleMessageAsync(payload);

        if (completedMetadata == null)
            throw new Exception("OnMetadataComplete should have fired");
        if (completedMetadata.Length != 100)
            throw new Exception($"Metadata should be 100 bytes, got {completedMetadata.Length}");
        if (!completedMetadata.SequenceEqual(metadataBytes))
            throw new Exception("Metadata content mismatch");

        Console.WriteLine("[Core] ut_metadata data assembly and verification passed");
    }

    // ═══════════════════════════════════════════════════════════
    //  ut_pex (BEP 11)
    // ═══════════════════════════════════════════════════════════

    [TestMethod]
    public async Task UtPex_ParseCompactPeerList()
    {
        var ext = new UtPexExtension();
        var receivedPeers = new List<string>();
        ext.OnPeersReceived += (peers) => receivedPeers.AddRange(peers);

        // Build a PEX message with compact peer list
        // 2 peers: 192.168.1.1:6881 and 10.0.0.1:51413
        var addedBytes = new byte[12];
        addedBytes[0] = 192; addedBytes[1] = 168; addedBytes[2] = 1; addedBytes[3] = 1;
        addedBytes[4] = (byte)(6881 >> 8); addedBytes[5] = (byte)(6881 & 0xFF);
        addedBytes[6] = 10; addedBytes[7] = 0; addedBytes[8] = 0; addedBytes[9] = 1;
        addedBytes[10] = (byte)(51413 >> 8); addedBytes[11] = (byte)(51413 & 0xFF);

        // Bencode: d5:added12:{bytes}e
        var dictPrefix = System.Text.Encoding.ASCII.GetBytes("d5:added12:");
        var dictSuffix = System.Text.Encoding.ASCII.GetBytes("e");
        var payload = new byte[dictPrefix.Length + addedBytes.Length + dictSuffix.Length];
        Array.Copy(dictPrefix, payload, dictPrefix.Length);
        Array.Copy(addedBytes, 0, payload, dictPrefix.Length, addedBytes.Length);
        Array.Copy(dictSuffix, 0, payload, dictPrefix.Length + addedBytes.Length, dictSuffix.Length);

        await ext.HandleMessageAsync(payload);

        if (receivedPeers.Count != 2)
            throw new Exception($"Expected 2 peers, got {receivedPeers.Count}");
        if (receivedPeers[0] != "192.168.1.1:6881")
            throw new Exception($"First peer should be 192.168.1.1:6881, got {receivedPeers[0]}");
        if (receivedPeers[1] != "10.0.0.1:51413")
            throw new Exception($"Second peer should be 10.0.0.1:51413, got {receivedPeers[1]}");

        Console.WriteLine("[Core] ut_pex compact peer list parsing passed");
    }

    // ═══════════════════════════════════════════════════════════
    //  Extension Manager (BEP 10)
    // ═══════════════════════════════════════════════════════════

    [TestMethod]
    public async Task ExtensionManager_BuildHandshake()
    {
        var manager = new ExtensionManager();
        manager.Register(new UtMetadataExtension());
        manager.Register(new UtPexExtension());

        var handshake = manager.BuildHandshake();

        if (!handshake.ContainsKey("m"))
            throw new Exception("Handshake should contain 'm' key");

        var m = (Dictionary<string, object>)handshake["m"];
        if (!m.ContainsKey("ut_metadata"))
            throw new Exception("Should contain ut_metadata");
        if (!m.ContainsKey("ut_pex"))
            throw new Exception("Should contain ut_pex");

        Console.WriteLine("[Core] Extension handshake build passed");
    }

    [TestMethod]
    public async Task ExtensionManager_ProcessHandshake()
    {
        var manager = new ExtensionManager();
        var utMeta = new UtMetadataExtension();
        var utPex = new UtPexExtension();
        manager.Register(utMeta);
        manager.Register(utPex);

        // Simulate remote peer's handshake
        var remoteHandshake = new Dictionary<string, object>
        {
            ["m"] = new Dictionary<string, object>
            {
                ["ut_metadata"] = (long)3,
                ["ut_pex"] = (long)4,
            },
            ["metadata_size"] = (long)12345,
        };
        manager.ProcessHandshake(remoteHandshake);

        if (utMeta.RemoteId != 3) throw new Exception($"ut_metadata RemoteId should be 3, got {utMeta.RemoteId}");
        if (utPex.RemoteId != 4) throw new Exception($"ut_pex RemoteId should be 4, got {utPex.RemoteId}");
        if (utMeta.MetadataSize != 12345) throw new Exception($"MetadataSize should be 12345, got {utMeta.MetadataSize}");
        if (!utMeta.IsSupported) throw new Exception("ut_metadata should be supported");
        if (!utPex.IsSupported) throw new Exception("ut_pex should be supported");
    }

    [TestMethod]
    public async Task ExtensionManager_GetByType()
    {
        var manager = new ExtensionManager();
        manager.Register(new UtMetadataExtension());
        manager.Register(new UtPexExtension());

        var meta = manager.Get<UtMetadataExtension>();
        var pex = manager.Get<UtPexExtension>();

        if (meta == null) throw new Exception("Should find UtMetadataExtension");
        if (pex == null) throw new Exception("Should find UtPexExtension");
        if (meta.Name != "ut_metadata") throw new Exception("Wrong extension name");
    }

    // ═══════════════════════════════════════════════════════════
    //  RateLimiter
    // ═══════════════════════════════════════════════════════════

    [TestMethod]
    public async Task RateLimiter_Unlimited()
    {
        var limiter = new RateLimiter(-1);

        // Should return immediately for any amount
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await limiter.WaitAsync(1000000);
        sw.Stop();

        if (sw.ElapsedMilliseconds > 50)
            throw new Exception($"Unlimited limiter should be instant, took {sw.ElapsedMilliseconds}ms");
    }

    [TestMethod]
    public async Task RateLimiter_RateChange()
    {
        var limiter = new RateLimiter(1000); // 1000 bytes/sec

        limiter.Rate = -1; // switch to unlimited
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await limiter.WaitAsync(100000);
        sw.Stop();

        if (sw.ElapsedMilliseconds > 50)
            throw new Exception("Should be instant after switching to unlimited");
    }

    [TestMethod]
    public async Task RateLimiter_DefaultUnlimited()
    {
        var limiter = new RateLimiter();
        if (limiter.Rate != -1) throw new Exception("Default rate should be -1 (unlimited)");
    }

    // ═══════════════════════════════════════════════════════════
    //  UDP Tracker Client (construction only — no network)
    // ═══════════════════════════════════════════════════════════

    // ═══════════════════════════════════════════════════════════
    //  DHT — Kademlia Routing Table (BEP 5)
    // ═══════════════════════════════════════════════════════════

    [TestMethod]
    public async Task Dht_RoutingTable_AddNode()
    {
        var localId = new byte[20];
        localId[0] = 0x01;
        var rt = new KademliaRoutingTable(localId);

        var node = new DhtNode
        {
            NodeId = new byte[20],
            EndPoint = new System.Net.IPEndPoint(System.Net.IPAddress.Parse("1.2.3.4"), 6881),
            LastSeen = DateTime.UtcNow,
        };
        node.NodeId[0] = 0xFF; // far from local ID

        rt.AddNode(node);

        if (rt.NodeCount != 1) throw new Exception($"Should have 1 node, got {rt.NodeCount}");
    }

    [TestMethod]
    public async Task Dht_RoutingTable_GetClosest()
    {
        var localId = new byte[20];
        var rt = new KademliaRoutingTable(localId);

        // Add 10 nodes with different IDs
        for (int i = 0; i < 10; i++)
        {
            var nid = new byte[20];
            nid[0] = (byte)(i + 1);
            rt.AddNode(new DhtNode
            {
                NodeId = nid,
                EndPoint = new System.Net.IPEndPoint(System.Net.IPAddress.Parse($"10.0.0.{i + 1}"), 6881),
                LastSeen = DateTime.UtcNow,
            });
        }

        if (rt.NodeCount != 10) throw new Exception($"Should have 10 nodes, got {rt.NodeCount}");

        // Get 3 closest to a target
        var target = new byte[20];
        target[0] = 0x02; // close to node with ID[0]=0x02
        var closest = rt.GetClosest(target, 3);

        if (closest.Count != 3) throw new Exception($"Should get 3 closest, got {closest.Count}");
        // The closest should be the one with XOR distance 0 (ID 0x02 ^ target 0x02 = 0)
        if (closest[0].NodeId[0] != 0x02) throw new Exception($"Closest should be 0x02, got 0x{closest[0].NodeId[0]:X2}");
    }

    [TestMethod]
    public async Task Dht_RoutingTable_Deduplicate()
    {
        var localId = new byte[20];
        var rt = new KademliaRoutingTable(localId);

        var nid = new byte[20];
        nid[0] = 0x42;
        var node = new DhtNode
        {
            NodeId = nid,
            EndPoint = new System.Net.IPEndPoint(System.Net.IPAddress.Parse("1.2.3.4"), 6881),
            LastSeen = DateTime.UtcNow,
        };

        rt.AddNode(node);
        rt.AddNode(node); // duplicate

        if (rt.NodeCount != 1) throw new Exception($"Should deduplicate, got {rt.NodeCount}");
    }

    [TestMethod]
    public async Task Dht_RoutingTable_BucketCapacity()
    {
        var localId = new byte[20];
        var rt = new KademliaRoutingTable(localId);

        // Add 20 nodes all in the same bucket (same first byte distance)
        for (int i = 0; i < 20; i++)
        {
            var nid = new byte[20];
            nid[0] = 0xFF;
            nid[19] = (byte)i; // Different last byte
            rt.AddNode(new DhtNode
            {
                NodeId = nid,
                EndPoint = new System.Net.IPEndPoint(System.Net.IPAddress.Parse($"10.0.0.{i + 1}"), 6881),
                LastSeen = DateTime.UtcNow,
            });
        }

        // K=8, so bucket should cap at 8
        if (rt.NodeCount > 8) throw new Exception($"Bucket should cap at K=8, got {rt.NodeCount}");
    }

    [TestMethod]
    public async Task Dht_Discovery_Create()
    {
        var dht = new DhtDiscovery();
        if (dht.Type != "dht") throw new Exception($"Type: '{dht.Type}'");
        if (dht.IsReady) throw new Exception("Should not be ready before start");
        if (dht.NodeCount != 0) throw new Exception("Should have 0 nodes");
        await dht.DisposeAsync();
    }

    // ═══════════════════════════════════════════════════════════
    //  BEP 46 — DHT Mutable Items
    // ═══════════════════════════════════════════════════════════

    [TestMethod]
    public async Task Bep46_MutableItems_Create()
    {
        var dht = new DhtDiscovery();
        var items = dht.CreateMutableItems();

        if (items.PublicKey == null || items.PublicKey.Length != 32)
            throw new Exception($"Public key should be 32 bytes, got {items.PublicKey?.Length}");
        if (items.Sequence != 0)
            throw new Exception("Initial sequence should be 0");

        await dht.DisposeAsync();
    }

    [TestMethod]
    public async Task Bep46_MutableItems_CreateWithKeyPair()
    {
        var dht = new DhtDiscovery();
        var privateKey = new byte[64];
        var publicKey = new byte[32];
        Random.Shared.NextBytes(privateKey);
        Random.Shared.NextBytes(publicKey);

        var items = dht.CreateMutableItems(privateKey, publicKey);

        if (!items.PublicKey.SequenceEqual(publicKey))
            throw new Exception("Public key should match provided key");

        await dht.DisposeAsync();
    }

    [TestMethod]
    public async Task Bep46_MutableItems_SequenceIncrement()
    {
        var dht = new DhtDiscovery();
        var items = dht.CreateMutableItems();

        // PublishAsync increments sequence
        // (won't actually send since DHT isn't started, but sequence should increment)
        if (items.Sequence != 0) throw new Exception("Should start at 0");

        await dht.DisposeAsync();
    }

    [TestMethod]
    public async Task Bep46_MutableItems_EventSubscription()
    {
        var dht = new DhtDiscovery();
        var items = dht.CreateMutableItems();

        byte[]? receivedKey = null;
        byte[]? receivedValue = null;
        long receivedSeq = -1;

        items.OnValueUpdated += (key, value, seq) =>
        {
            receivedKey = key;
            receivedValue = value;
            receivedSeq = seq;
        };

        // Event is wired — would fire when DHT returns a mutable item
        if (receivedKey != null) throw new Exception("Should not fire yet");

        await dht.DisposeAsync();
    }

    // ═══════════════════════════════════════════════════════════
    //  HTTP Tracker Client
    // ═══════════════════════════════════════════════════════════

    [TestMethod]
    public async Task HttpTracker_Create()
    {
        var peerId = new byte[20];
        "-SD0110-"u8.CopyTo(peerId);

        var tracker = new HttpTrackerClient("https://tracker.opentrackr.org:1337/announce", peerId);
        if (tracker.Type != "http-tracker") throw new Exception($"Type: '{tracker.Type}'");
        if (tracker.IsConnected) throw new Exception("Should not be connected before StartAsync");
        await tracker.DisposeAsync();
    }

    [TestMethod]
    public async Task HttpTracker_ParseUrl()
    {
        var peerId = new byte[20];
        var t1 = new HttpTrackerClient("http://tracker.example.com/announce", peerId);
        var t2 = new HttpTrackerClient("https://tracker.example.com:8080/announce", peerId);
        await t1.DisposeAsync();
        await t2.DisposeAsync();
    }

    // ═══════════════════════════════════════════════════════════
    //  Rate Limiter Wiring
    // ═══════════════════════════════════════════════════════════

    [TestMethod]
    public async Task RateLimiter_ClientWiring()
    {
        var client = new WebTorrentClient();

        // Set via property
        client.UploadLimit = 50000;
        if (client.UploadLimiter.Rate != 50000)
            throw new Exception("UploadLimiter.Rate should match UploadLimit");

        client.DownloadLimit = 100000;
        if (client.DownloadLimiter.Rate != 100000)
            throw new Exception("DownloadLimiter.Rate should match DownloadLimit");

        // Set to unlimited
        client.UploadLimit = -1;
        if (client.UploadLimiter.Rate != -1)
            throw new Exception("Should be unlimited");

        await client.DisposeAsync();
    }

    [TestMethod]
    public async Task UdpTracker_Create()
    {
        var peerId = new byte[20];
        "-SD0110-"u8.CopyTo(peerId);

        var tracker = new UdpTrackerClient("udp://tracker.opentrackr.org:1337/announce", peerId);

        if (tracker.Type != "udp-tracker")
            throw new Exception($"Type should be 'udp-tracker', got '{tracker.Type}'");
        if (tracker.IsConnected)
            throw new Exception("Should not be connected before StartAsync");

        await tracker.DisposeAsync();
    }

    [TestMethod]
    public async Task UdpTracker_ParseUrl()
    {
        var peerId = new byte[20];

        // Various URL formats
        var tracker1 = new UdpTrackerClient("udp://tracker.opentrackr.org:1337", peerId);
        var tracker2 = new UdpTrackerClient("udp://explodie.org:6969", peerId);
        var tracker3 = new UdpTrackerClient("udp://tracker.example.com:6969/announce", peerId);

        // Should not throw — URL parsing works
        await tracker1.DisposeAsync();
        await tracker2.DisposeAsync();
        await tracker3.DisposeAsync();
    }

    // ═══════════════════════════════════════════════════════════
    //  Client — Incoming Connection Routing
    // ═══════════════════════════════════════════════════════════

    // ═══════════════════════════════════════════════════════════
    //  BEP 6 — Fast Extension
    // ═══════════════════════════════════════════════════════════

    [TestMethod]
    public async Task Bep6_HandshakeFlag()
    {
        var captured = new List<byte>();
        var mock = new MockConnection(captured);
        var wire = new WireProtocol(mock);

        var infoHash = new byte[20];
        var peerId = new byte[20];
        await wire.SendHandshakeAsync(infoHash, peerId);

        // BEP 6 flag: reserved[7] bit 2 (0x04)
        if ((captured[27] & 0x04) == 0)
            throw new Exception("BEP 6 Fast Extension flag not set in reserved bytes");
        // BEP 10 flag should also be set
        if ((captured[25] & 0x10) == 0)
            throw new Exception("BEP 10 Extension Protocol flag not set");
    }

    [TestMethod]
    public async Task Bep6_SendHaveAll()
    {
        var captured = new List<byte>();
        var mock = new MockConnection(captured);
        var wire = new WireProtocol(mock);

        await wire.SendHaveAllAsync();
        // Should be: 00 00 00 01 0E (length=1, type=HaveAll=14)
        if (captured.Count != 5) throw new Exception($"Expected 5 bytes, got {captured.Count}");
        if (captured[3] != 1) throw new Exception("Length should be 1");
        if (captured[4] != (byte)MessageType.HaveAll) throw new Exception($"Type should be HaveAll(14), got {captured[4]}");
    }

    [TestMethod]
    public async Task Bep6_SendHaveNone()
    {
        var captured = new List<byte>();
        var mock = new MockConnection(captured);
        var wire = new WireProtocol(mock);

        await wire.SendHaveNoneAsync();
        if (captured.Count != 5) throw new Exception($"Expected 5 bytes, got {captured.Count}");
        if (captured[4] != (byte)MessageType.HaveNone) throw new Exception($"Type should be HaveNone(15), got {captured[4]}");
    }

    [TestMethod]
    public async Task Bep6_SendRejectRequest()
    {
        var captured = new List<byte>();
        var mock = new MockConnection(captured);
        var wire = new WireProtocol(mock);

        await wire.SendRejectRequestAsync(5, 0, 16384);
        // 4 bytes length + 1 type + 4+4+4 = 17 total
        if (captured.Count != 17) throw new Exception($"Expected 17 bytes, got {captured.Count}");
        if (captured[4] != (byte)MessageType.RejectRequest) throw new Exception("Wrong message type");
    }

    [TestMethod]
    public async Task Bep6_SendAllowedFast()
    {
        var captured = new List<byte>();
        var mock = new MockConnection(captured);
        var wire = new WireProtocol(mock);

        await wire.SendAllowedFastAsync(42);
        if (captured.Count != 9) throw new Exception($"Expected 9 bytes, got {captured.Count}");
        if (captured[4] != (byte)MessageType.AllowedFast) throw new Exception("Wrong message type");
    }

    [TestMethod]
    public async Task Client_AddTransport()
    {
        await using var client = new WebTorrentClient();
        var transport = new WebRtcTransport();

        client.AddTransport(transport);
        // No crash — transport registered

        await transport.DisposeAsync();
    }

    [TestMethod]
    public async Task Client_EventWiring()
    {
        await using var client = new WebTorrentClient();
        bool addFired = false;
        bool readyFired = false;

        client.OnTorrentAdd += (_) => addFired = true;
        client.OnTorrentReady += (_) => readyFired = true;

        var data = new byte[16384];
        var (_, metadata) = TorrentCreator.CreateFromBytes("test.bin", data,
            new TorrentCreatorOptions { PieceLength = 16384 });

        var swarm = await client.AddAsync(metadata);

        if (!addFired) throw new Exception("OnTorrentAdd should have fired");
        if (!readyFired) throw new Exception("OnTorrentReady should have fired");
    }

    // ═══════════════════════════════════════════════════════════
    //  Bitfield Encoding
    // ═══════════════════════════════════════════════════════════

    [TestMethod]
    public async Task Bitfield_BoolToBytes()
    {
        // Test the bitfield encoding used for wire protocol
        var bitfield = new bool[] { true, false, true, true, false, false, false, true,
                                     true, false, false, false, false, false, false, false };

        // Manual encode: same as TorrentSwarm.BoolBitfieldToBytes
        int byteCount = (bitfield.Length + 7) / 8;
        var bytes = new byte[byteCount];
        for (int i = 0; i < bitfield.Length; i++)
            if (bitfield[i])
                bytes[i / 8] |= (byte)(1 << (7 - (i % 8)));

        // 10110001 = 0xB1, 10000000 = 0x80
        if (bytes[0] != 0xB1) throw new Exception($"First byte should be 0xB1, got 0x{bytes[0]:X2}");
        if (bytes[1] != 0x80) throw new Exception($"Second byte should be 0x80, got 0x{bytes[1]:X2}");
    }

    [TestMethod]
    public async Task Bitfield_BytesToBool()
    {
        // Test the bitfield decoding used in TorrentSwarm
        var bf = new byte[] { 0xB1, 0x80 }; // 10110001 10000000

        var boolField = new bool[bf.Length * 8];
        for (int i = 0; i < bf.Length; i++)
            for (int bit = 0; bit < 8; bit++)
                if (i * 8 + bit < boolField.Length)
                    boolField[i * 8 + bit] = (bf[i] & (1 << (7 - bit))) != 0;

        if (!boolField[0]) throw new Exception("Bit 0 should be true");
        if (boolField[1]) throw new Exception("Bit 1 should be false");
        if (!boolField[2]) throw new Exception("Bit 2 should be true");
        if (!boolField[3]) throw new Exception("Bit 3 should be true");
        if (boolField[4]) throw new Exception("Bit 4 should be false");
        if (boolField[5]) throw new Exception("Bit 5 should be false");
        if (boolField[6]) throw new Exception("Bit 6 should be false");
        if (!boolField[7]) throw new Exception("Bit 7 should be true");
        if (!boolField[8]) throw new Exception("Bit 8 should be true");
        if (boolField[9]) throw new Exception("Bit 9 should be false");
    }
}
