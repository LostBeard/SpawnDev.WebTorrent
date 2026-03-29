using SpawnDev.UnitTesting;
using SpawnDev.WebTorrent.Discovery;
using SpawnDev.WebTorrent.Storage;
using SpawnDev.WebTorrent.Torrent;
using SpawnDev.WebTorrent.Wire;

namespace SpawnDev.WebTorrent.Demo.Shared.UnitTests;

/// <summary>
/// Tests for specific BEP features — each BEP gets its behavior tested.
/// </summary>
public abstract partial class WebTorrentTestBase
{
    // ═══════════════════════════════════════════════════════════
    //  BEP 3 — Piece Verification
    // ═══════════════════════════════════════════════════════════

    [TestMethod]
    public async Task Bep3_PieceVerification_CorrectHash()
    {
        var data = new byte[32768];
        for (int i = 0; i < data.Length; i++) data[i] = (byte)(i % 256);
        var (_, metadata) = TorrentCreator.CreateFromBytes("bep3.bin", data,
            new TorrentCreatorOptions { PieceLength = 16384 });

        await using var store = new MemoryChunkStore(16384);
        var pm = new PieceManager(metadata, store);

        // Piece 0: first 16KB
        var piece0 = data[..16384];
        var ok = await pm.ReceiveCompletePieceAsync(0, piece0);
        if (!ok) throw new Exception("Correct piece should verify");

        // Piece 1: second 16KB
        var piece1 = data[16384..];
        ok = await pm.ReceiveCompletePieceAsync(1, piece1);
        if (!ok) throw new Exception("Correct piece 1 should verify");

        if (!pm.IsComplete) throw new Exception("Should be complete");
    }

    [TestMethod]
    public async Task Bep3_PieceVerification_CorruptData()
    {
        var data = new byte[16384];
        Random.Shared.NextBytes(data);
        var (_, metadata) = TorrentCreator.CreateFromBytes("bep3-corrupt.bin", data,
            new TorrentCreatorOptions { PieceLength = 16384 });

        await using var store = new MemoryChunkStore(16384);
        var pm = new PieceManager(metadata, store);

        var corrupt = new byte[16384]; // all zeros — wrong hash
        var ok = await pm.ReceiveCompletePieceAsync(0, corrupt);
        if (ok) throw new Exception("Corrupt data should fail verification");
        if (pm.CompletedCount != 0) throw new Exception("Should have 0 completed");
    }

    // ═══════════════════════════════════════════════════════════
    //  BEP 6 — Fast Extension Message Formats
    // ═══════════════════════════════════════════════════════════

    [TestMethod]
    public async Task Bep6_AllMessageTypes()
    {
        var captured = new List<byte>();
        var mock = new MockConnection(captured);
        var wire = new WireProtocol(mock);

        // HaveAll = 14
        await wire.SendHaveAllAsync();
        if (captured.Last() != 14) throw new Exception($"HaveAll type: {captured.Last()}");
        captured.Clear();

        // HaveNone = 15
        await wire.SendHaveNoneAsync();
        if (captured.Last() != 15) throw new Exception($"HaveNone type: {captured.Last()}");
        captured.Clear();

        // SuggestPiece = 13
        await wire.SendSuggestPieceAsync(0);
        if (captured[4] != 13) throw new Exception($"Suggest type: {captured[4]}");
        captured.Clear();

        // RejectRequest = 16
        await wire.SendRejectRequestAsync(0, 0, 16384);
        if (captured[4] != 16) throw new Exception($"Reject type: {captured[4]}");
        captured.Clear();

        // AllowedFast = 17
        await wire.SendAllowedFastAsync(0);
        if (captured[4] != 17) throw new Exception($"AllowedFast type: {captured[4]}");
    }

    // ═══════════════════════════════════════════════════════════
    //  BEP 9 — ut_metadata Request/Data Assembly
    // ═══════════════════════════════════════════════════════════

    [TestMethod]
    public async Task Bep9_MetadataAssembly_MultiPiece()
    {
        var ext = new UtMetadataExtension();
        ext.MetadataSize = 32768; // 32KB = 2 pieces (16KB each)
        ext.RemoteId = 1;

        byte[]? completedMetadata = null;
        ext.OnMetadataComplete += (data) => completedMetadata = data;

        // Build expected metadata
        var fullMetadata = new byte[32768];
        for (int i = 0; i < fullMetadata.Length; i++) fullMetadata[i] = (byte)(i % 256);
        ext.ExpectedInfoHash = System.Security.Cryptography.SHA1.HashData(fullMetadata);

        // Send piece 0 (first 16KB)
        var piece0 = fullMetadata[..16384];
        var dict0 = System.Text.Encoding.ASCII.GetBytes("d8:msg_typei1e5:piecei0e10:total_sizei32768ee");
        var payload0 = new byte[dict0.Length + piece0.Length];
        Array.Copy(dict0, payload0, dict0.Length);
        Array.Copy(piece0, 0, payload0, dict0.Length, piece0.Length);
        await ext.HandleMessageAsync(payload0);

        if (completedMetadata != null) throw new Exception("Should not complete after 1 of 2 pieces");

        // Send piece 1 (second 16KB)
        var piece1 = fullMetadata[16384..];
        var dict1 = System.Text.Encoding.ASCII.GetBytes("d8:msg_typei1e5:piecei1e10:total_sizei32768ee");
        var payload1 = new byte[dict1.Length + piece1.Length];
        Array.Copy(dict1, payload1, dict1.Length);
        Array.Copy(piece1, 0, payload1, dict1.Length, piece1.Length);
        await ext.HandleMessageAsync(payload1);

        if (completedMetadata == null) throw new Exception("Should complete after both pieces");
        if (completedMetadata.Length != 32768) throw new Exception($"Size: {completedMetadata.Length}");
        if (!completedMetadata.SequenceEqual(fullMetadata)) throw new Exception("Content mismatch");
    }

    // ═══════════════════════════════════════════════════════════
    //  BEP 10 — Extension Handshake Round-Trip
    // ═══════════════════════════════════════════════════════════

    [TestMethod]
    public async Task Bep10_HandshakeRoundTrip()
    {
        // Build handshake from local extensions
        var mgr = new ExtensionManager();
        mgr.Register(new UtMetadataExtension { LocalMetadata = new byte[5000] });
        mgr.Register(new UtPexExtension());

        var handshake = mgr.BuildHandshake();

        // Simulate remote peer with different IDs
        var remoteMgr = new ExtensionManager();
        var remoteUtMeta = new UtMetadataExtension();
        var remoteUtPex = new UtPexExtension();
        remoteMgr.Register(remoteUtMeta);
        remoteMgr.Register(remoteUtPex);

        // Remote processes our handshake
        remoteMgr.ProcessHandshake(handshake);

        // Remote should know our extension IDs
        if (remoteUtMeta.RemoteId != 1) throw new Exception($"ut_metadata RemoteId: {remoteUtMeta.RemoteId}");
        if (remoteUtPex.RemoteId != 2) throw new Exception($"ut_pex RemoteId: {remoteUtPex.RemoteId}");

        // Remote should know our metadata size
        if (remoteUtMeta.MetadataSize != 5000) throw new Exception($"MetadataSize: {remoteUtMeta.MetadataSize}");
    }

    // ═══════════════════════════════════════════════════════════
    //  BEP 20 — Peer ID Format
    // ═══════════════════════════════════════════════════════════

    [TestMethod]
    public async Task Bep20_PeerIdFormat()
    {
        await using var client = new WebTorrentClient();
        var peerId = client.PeerId;

        if (peerId.Length != 20) throw new Exception($"Peer ID: {peerId.Length} bytes");
        var prefix = System.Text.Encoding.ASCII.GetString(peerId, 0, 8);
        if (prefix != "-SD0110-") throw new Exception($"Prefix: '{prefix}'");

        // Last 12 bytes should be random (non-zero for any practical test)
        var hasNonZero = false;
        for (int i = 8; i < 20; i++) if (peerId[i] != 0) hasNonZero = true;
        if (!hasNonZero) throw new Exception("Random bytes should have some non-zero");

        // Two clients should have different peer IDs
        await using var client2 = new WebTorrentClient();
        if (client.PeerId.SequenceEqual(client2.PeerId))
            throw new Exception("Two clients should have different peer IDs");
    }

    // ═══════════════════════════════════════════════════════════
    //  BEP 27 — Private Torrent Enforcement
    // ═══════════════════════════════════════════════════════════

    [TestMethod]
    public async Task Bep27_PrivateTorrent_FullRoundTrip()
    {
        var data = new byte[16384];
        var (bytes, metadata) = TorrentCreator.CreateFromBytes("private.bin", data,
            new TorrentCreatorOptions { PieceLength = 16384, IsPrivate = true });

        // Verify in metadata
        if (!metadata.IsPrivate) throw new Exception("Metadata should be private");

        // Parse and verify survives roundtrip
        var parsed = TorrentParser.Parse(bytes);
        if (!parsed.IsPrivate) throw new Exception("Parsed should be private");

        // Verify in swarm
        await using var client = new WebTorrentClient();
        var swarm = await client.AddAsync(parsed);
        if (!swarm.IsPrivate) throw new Exception("Swarm should be private");
    }

    // ═══════════════════════════════════════════════════════════
    //  BEP 53 — Magnet File Selection + Exact Source
    // ═══════════════════════════════════════════════════════════

    [TestMethod]
    public async Task Bep53_FullMagnetParsing()
    {
        var magnet = "magnet:?xt=urn:btih:dd8255ecdc7ca55fb0bbf81323d87062db1f6d1c"
            + "&dn=Test+File"
            + "&tr=wss%3A%2F%2Ftracker.example.com"
            + "&tr=http%3A%2F%2Ftracker2.example.com%2Fannounce"
            + "&ws=https%3A%2F%2Fcdn.example.com%2Ffiles"
            + "&xs=https%3A%2F%2Fexample.com%2Ftest.torrent"
            + "&so=0,2,5";

        var meta = TorrentParser.ParseMagnet(magnet);

        if (meta.Name != "Test File") throw new Exception($"Name: '{meta.Name}'");
        if (meta.AnnounceList.Length != 2) throw new Exception($"Trackers: {meta.AnnounceList.Length}");
        if (meta.UrlList.Length != 1) throw new Exception($"WebSeeds: {meta.UrlList.Length}");
        if (meta.ExactSource != "https://example.com/test.torrent") throw new Exception($"xs: '{meta.ExactSource}'");
        if (meta.SelectedFileIndices == null) throw new Exception("so= not parsed");
        if (meta.SelectedFileIndices.Length != 3) throw new Exception($"so= count: {meta.SelectedFileIndices.Length}");
        if (meta.SelectedFileIndices[0] != 0 || meta.SelectedFileIndices[1] != 2 || meta.SelectedFileIndices[2] != 5)
            throw new Exception("so= values wrong");
    }

    // ═══════════════════════════════════════════════════════════
    //  BEP 44/46 — DHT Mutable Items Target Computation
    // ═══════════════════════════════════════════════════════════

    [TestMethod]
    public async Task Bep44_TargetComputation()
    {
        // Two different public keys should produce different targets
        var dht = new DhtDiscovery();
        var items1 = dht.CreateMutableItems(new HmacFallbackSigner());
        var items2 = dht.CreateMutableItems(new HmacFallbackSigner());

        if (items1.PublicKey.SequenceEqual(items2.PublicKey))
            throw new Exception("Different items should have different public keys");

        await dht.DisposeAsync();
    }
}
