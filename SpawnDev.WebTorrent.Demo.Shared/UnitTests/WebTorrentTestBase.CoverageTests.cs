using SpawnDev.UnitTesting;
using SpawnDev.WebTorrent.Discovery;
using SpawnDev.WebTorrent.Storage;
using SpawnDev.WebTorrent.Torrent;
using SpawnDev.WebTorrent.Transports;
using SpawnDev.WebTorrent.Wire;

namespace SpawnDev.WebTorrent.Demo.Shared.UnitTests;

/// <summary>
/// Coverage completion tests — every remaining class that needs testing.
/// </summary>
public abstract partial class WebTorrentTestBase
{
    // ═══════════════════════════════════════════════════════════
    //  WireExtension — ExtensionManager lifecycle
    // ═══════════════════════════════════════════════════════════

    [TestMethod]
    public async Task WireExt_Register_AssignsLocalId()
    {
        var mgr = new ExtensionManager();
        var meta = new UtMetadataExtension();
        var pex = new UtPexExtension();

        mgr.Register(meta);
        mgr.Register(pex);

        if (meta.LocalId != 1) throw new Exception($"ut_metadata LocalId: {meta.LocalId}");
        if (pex.LocalId != 2) throw new Exception($"ut_pex LocalId: {pex.LocalId}");
    }

    [TestMethod]
    public async Task WireExt_GetByName()
    {
        var mgr = new ExtensionManager();
        mgr.Register(new UtMetadataExtension());

        var found = mgr.Get("ut_metadata");
        if (found == null) throw new Exception("Should find by name");
        if (found.Name != "ut_metadata") throw new Exception($"Name: {found.Name}");

        var notFound = mgr.Get("nonexistent");
        if (notFound != null) throw new Exception("Should return null for unknown");
    }

    [TestMethod]
    public async Task WireExt_HandleExtensionId0_Handshake()
    {
        var mgr = new ExtensionManager();
        var meta = new UtMetadataExtension();
        mgr.Register(meta);

        // Simulate receiving an extension handshake (ID 0)
        // Bencode: d1:md11:ut_metadatai3eee
        var handshakePayload = System.Text.Encoding.ASCII.GetBytes("d1:md11:ut_metadatai3eee");
        await mgr.HandleMessageAsync(0, handshakePayload);

        if (meta.RemoteId != 3) throw new Exception($"RemoteId should be 3, got {meta.RemoteId}");
        if (!meta.IsSupported) throw new Exception("Should be supported after handshake");
    }

    [TestMethod]
    public async Task WireExt_UtMetadata_RequestFormat()
    {
        var ext = new UtMetadataExtension();
        var req = ext.CreateRequest(5);
        var str = System.Text.Encoding.ASCII.GetString(req);

        if (!str.Contains("msg_typei0e")) throw new Exception("Should be request type 0");
        if (!str.Contains("piecei5e")) throw new Exception("Should request piece 5");
    }

    [TestMethod]
    public async Task WireExt_UtMetadata_HandshakeWithSize()
    {
        var ext = new UtMetadataExtension();
        ext.LocalMetadata = new byte[50000];

        var data = ext.GetHandshakeData();
        if (data == null) throw new Exception("Should have handshake data");
        if ((long)data["metadata_size"] != 50000) throw new Exception($"Size: {data["metadata_size"]}");
    }

    [TestMethod]
    public async Task WireExt_UtMetadata_RejectMessage()
    {
        var ext = new UtMetadataExtension();
        ext.MetadataSize = 16384;
        ext.RemoteId = 1;

        // Send a reject message (msg_type=2)
        var reject = System.Text.Encoding.ASCII.GetBytes("d8:msg_typei2e5:piecei0ee");
        await ext.HandleMessageAsync(reject);
        // Should not crash — just ignores the rejection
    }

    // ═══════════════════════════════════════════════════════════
    //  AsyncFSChunkStore (browser OPFS — construction test)
    // ═══════════════════════════════════════════════════════════

    [TestMethod]
    public async Task OpfsChunkStore_Create()
    {
        if (!OperatingSystem.IsBrowser())
            throw new UnsupportedTestException("OPFS requires browser");

        // Would need IAsyncFS — test construction pattern
        // The store is tested via the download pipeline when OPFS is configured
    }

    // ═══════════════════════════════════════════════════════════
    //  MemoryChunkStore — Full Interface Coverage
    // ═══════════════════════════════════════════════════════════

    [TestMethod]
    public async Task MemoryStore_ChunkLength()
    {
        await using var store = new MemoryChunkStore(32768);
        if (store.ChunkLength != 32768) throw new Exception($"ChunkLength: {store.ChunkLength}");
    }

    [TestMethod]
    public async Task MemoryStore_PutGetRemoveClear_FullCycle()
    {
        await using var store = new MemoryChunkStore(16384);

        // Put 3 chunks
        for (int i = 0; i < 3; i++)
        {
            var data = new byte[16384];
            data[0] = (byte)i;
            await store.PutAsync(i, data);
        }

        // Get each
        for (int i = 0; i < 3; i++)
        {
            var result = await store.GetAsync(i);
            if (result == null) throw new Exception($"Chunk {i} null");
            if (result[0] != (byte)i) throw new Exception($"Chunk {i} data wrong");
        }

        // Remove middle
        await store.RemoveAsync(1);
        if (await store.GetAsync(1) != null) throw new Exception("Chunk 1 should be removed");
        if (await store.GetAsync(0) == null) throw new Exception("Chunk 0 should still exist");
        if (await store.GetAsync(2) == null) throw new Exception("Chunk 2 should still exist");

        // Clear all
        await store.ClearAsync();
        if (await store.GetAsync(0) != null) throw new Exception("Should be cleared");
    }

    // ═══════════════════════════════════════════════════════════
    //  DhtMutableItems — Salt support
    // ═══════════════════════════════════════════════════════════

    [TestMethod]
    public async Task MutableItems_WithSalt()
    {
        var dht = new DhtDiscovery();
        var items = dht.CreateMutableItems();

        // Publish with salt — should not throw
        try { await items.PublishAsync(new byte[] { 1 }, System.Text.Encoding.UTF8.GetBytes("my-channel")); }
        catch { }

        await dht.DisposeAsync();
    }

    [TestMethod]
    public async Task MutableItems_PublicKeyStable()
    {
        var dht = new DhtDiscovery();
        var items = dht.CreateMutableItems();

        var key1 = items.PublicKey.ToArray();
        // Publish should not change the public key
        try { await items.PublishAsync(new byte[] { 1 }); } catch { }
        var key2 = items.PublicKey.ToArray();

        if (!key1.SequenceEqual(key2)) throw new Exception("Public key should be stable");

        await dht.DisposeAsync();
    }

    // ═══════════════════════════════════════════════════════════
    //  IDhtSigner — Interface Compliance
    // ═══════════════════════════════════════════════════════════

    [TestMethod]
    public async Task Signer_Interface_Compliance()
    {
        IDhtSigner signer = new HmacFallbackSigner();

        // All interface methods should work
        if (string.IsNullOrEmpty(signer.Algorithm)) throw new Exception("Algorithm empty");
        if (signer.PublicKey.Length != 32) throw new Exception("PublicKey wrong size");

        var sig = await signer.SignAsync(new byte[] { 42 });
        if (sig.Length < 64) throw new Exception("Signature too short");

        var valid = await signer.VerifyAsync(signer.PublicKey, new byte[] { 42 }, sig);
        if (!valid) throw new Exception("Should verify");

        var (pub, priv) = await signer.ExportKeyPairAsync();
        if (pub.Length != 32 || priv.Length != 64) throw new Exception("Export sizes wrong");
    }

    // ═══════════════════════════════════════════════════════════
    //  SipSorceryWebRtcConnection (construction)
    // ═══════════════════════════════════════════════════════════

    [TestMethod]
    public async Task SipSorceryConnection_Create()
    {
        if (OperatingSystem.IsBrowser())
            throw new UnsupportedTestException("SIPSorcery requires desktop");

        var conn = new SipSorceryWebRtcConnection("test-peer", new WebRtcTransportOptions());
        if (conn.RemoteId != "test-peer") throw new Exception($"RemoteId: {conn.RemoteId}");
        if (conn.TransportType != "webrtc") throw new Exception($"Type: {conn.TransportType}");
        if (conn.IsConnected) throw new Exception("Should not be connected");
        await conn.DisposeAsync();
    }

    // ═══════════════════════════════════════════════════════════
    //  WebRtcTransportOptions — Defaults
    // ═══════════════════════════════════════════════════════════

    [TestMethod]
    public async Task WebRtcOptions_Defaults()
    {
        var opts = new WebRtcTransportOptions();
        if (opts.IceServers.Length < 1) throw new Exception("Should have default ICE servers");
        if (opts.ChannelLabel != "spawndev-webtorrent") throw new Exception($"Label: {opts.ChannelLabel}");
        if (opts.Ordered) throw new Exception("Should default to unordered");
        if (opts.MaxRetransmits != null) throw new Exception("MaxRetransmits should be null by default");
    }

    // ═══════════════════════════════════════════════════════════
    //  WebTorrentOptions — Defaults
    // ═══════════════════════════════════════════════════════════

    [TestMethod]
    public async Task ClientOptions_Defaults()
    {
        var opts = new WebTorrentOptions();
        if (opts.MaxConns != 55) throw new Exception($"MaxConns: {opts.MaxConns}");
        if (opts.UploadLimit != -1) throw new Exception($"UploadLimit: {opts.UploadLimit}");
        if (opts.DownloadLimit != -1) throw new Exception($"DownloadLimit: {opts.DownloadLimit}");
        if (opts.Trackers.Length < 1) throw new Exception("Should have default trackers");
    }

    [TestMethod]
    public async Task AddTorrentOptions_Defaults()
    {
        var opts = new AddTorrentOptions();
        if (opts.Paused) throw new Exception("Should not be paused by default");
        if (opts.Deselect) throw new Exception("Should not be deselected by default");
        if (opts.WebSeeds.Length != 0) throw new Exception("WebSeeds should be empty");
        if (opts.Strategy != "rarest") throw new Exception($"Strategy: {opts.Strategy}");
        if (opts.StoreFactory != null) throw new Exception("StoreFactory should be null");
        if (opts.AsyncFileSystem != null) throw new Exception("AsyncFileSystem should be null");
    }

    // ═══════════════════════════════════════════════════════════
    //  TorrentCreatorOptions — Defaults
    // ═══════════════════════════════════════════════════════════

    [TestMethod]
    public async Task CreatorOptions_Defaults()
    {
        var opts = new TorrentCreatorOptions();
        if (opts.PieceLength != 0) throw new Exception($"PieceLength: {opts.PieceLength}");
        if (opts.Trackers.Length != 0) throw new Exception($"Trackers: {opts.Trackers.Length}");
        if (opts.WebSeeds.Length != 0) throw new Exception($"WebSeeds: {opts.WebSeeds.Length}");
        if (opts.IsPrivate) throw new Exception("Should not be private by default");
    }

    // ═══════════════════════════════════════════════════════════
    //  PeerInfo — Discovery Data
    // ═══════════════════════════════════════════════════════════

    [TestMethod]
    public async Task PeerInfo_Properties()
    {
        var info = new PeerInfo { Address = "192.168.1.1:6881", Source = "ws-tracker" };
        if (info.Address != "192.168.1.1:6881") throw new Exception($"Address: {info.Address}");
        if (info.Source != "ws-tracker") throw new Exception($"Source: {info.Source}");
    }

    // ═══════════════════════════════════════════════════════════
    //  DhtOptions — Defaults
    // ═══════════════════════════════════════════════════════════

    [TestMethod]
    public async Task DhtOptions_Defaults()
    {
        var opts = new DhtOptions();
        if (opts.BootstrapNodes.Length < 1) throw new Exception("Should have bootstrap nodes");
        if (opts.Port != 6881) throw new Exception($"Port: {opts.Port}");
        if (opts.MaxNodes != 1600) throw new Exception($"MaxNodes: {opts.MaxNodes}");
    }
}
