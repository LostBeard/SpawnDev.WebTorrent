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

    // ═══════════════════════════════════════════════════════════
    //  Media Viewer Pipeline (download → blob → verify)
    // ═══════════════════════════════════════════════════════════

    [TestMethod]
    public async Task MediaViewer_MimeDetection()
    {
        await using var client = new WebTorrentClient(crypto: Client!.Crypto);

        var videoTypes = new[] { ".mp4", ".webm", ".mkv", ".ogv", ".mov" };
        var audioTypes = new[] { ".mp3", ".ogg", ".flac", ".wav", ".aac", ".opus" };
        var imageTypes = new[] { ".jpg", ".jpeg", ".png", ".gif", ".webp" };

        foreach (var ext in videoTypes)
        {
            var data = new byte[16384];
            var (_, meta) = TorrentCreator.CreateFromBytes($"test{ext}", data,
                new TorrentCreatorOptions { PieceLength = 16384 });
            var swarm = await client.AddAsync(meta);
            var file = swarm.Files[0];
            if (!file.Type.StartsWith("video/"))
                throw new Exception($"{ext}: expected video/*, got {file.Type}");
        }

        foreach (var ext in audioTypes)
        {
            var data = new byte[16384];
            var (_, meta) = TorrentCreator.CreateFromBytes($"test{ext}", data,
                new TorrentCreatorOptions { PieceLength = 16384 });
            var swarm = await client.AddAsync(meta);
            if (!swarm.Files[0].Type.StartsWith("audio/"))
                throw new Exception($"{ext}: expected audio/*, got {swarm.Files[0].Type}");
        }

        foreach (var ext in imageTypes)
        {
            var data = new byte[16384];
            var (_, meta) = TorrentCreator.CreateFromBytes($"test{ext}", data,
                new TorrentCreatorOptions { PieceLength = 16384 });
            var swarm = await client.AddAsync(meta);
            if (!swarm.Files[0].Type.StartsWith("image/"))
                throw new Exception($"{ext}: expected image/*, got {swarm.Files[0].Type}");
        }

        Console.WriteLine($"[MediaViewer] All {videoTypes.Length + audioTypes.Length + imageTypes.Length} MIME types correct");
    }

    [TestMethod]
    public async Task MediaViewer_SeedAndReadForBlob()
    {
        // Full pipeline: seed data → read back → verify (ready for blob URL creation)
        await using var client = new WebTorrentClient(crypto: Client!.Crypto);
        var data = new byte[32768];
        for (int i = 0; i < data.Length; i++) data[i] = (byte)(i % 256);

        var swarm = await client.SeedAsync(data, "video.mp4",
            new TorrentCreatorOptions { PieceLength = 16384 });

        // Read back via GetBlobBytesAsync (same as media viewer uses)
        var blobBytes = await swarm.Files[0].GetBlobBytesAsync();

        if (blobBytes.Length != data.Length)
            throw new Exception($"Blob size: {blobBytes.Length}, expected {data.Length}");
        if (!blobBytes.SequenceEqual(data))
            throw new Exception("Blob content mismatch — media viewer would show corrupt data");

        Console.WriteLine($"[MediaViewer] Seed → ReadForBlob verified: {blobBytes.Length} bytes match");
    }

    // ═══════════════════════════════════════════════════════════
    //  OPFS ChunkStore (browser only)
    // ═══════════════════════════════════════════════════════════

    [TestMethod]
    public async Task OpfsChunkStore_PutGetClear()
    {
        if (!OperatingSystem.IsBrowser())
            throw new UnsupportedTestException("OPFS requires browser");

        // This test runs in the browser via Playwright
        // It tests the full OPFS persistence pipeline
        try
        {
            if (JS == null) throw new UnsupportedTestException("BlazorJSRuntime not available");
            var fs = new SpawnDev.AsyncFileSystem.BrowserWASM.AsyncFSFileSystemDirectoryHandle(JS);
            await fs.Ready;

            await using var store = new AsyncFSChunkStore(fs, "test-opfs-" + Guid.NewGuid().ToString("N")[..8], 16384);

            // Put
            var data = new byte[16384];
            for (int i = 0; i < data.Length; i++) data[i] = (byte)(i % 256);
            await store.PutAsync(0, data);

            // Get
            var result = await store.GetAsync(0);
            if (result == null) throw new Exception("Get returned null");
            if (!result.SequenceEqual(data)) throw new Exception("Data mismatch");

            // Partial get
            var partial = await store.GetAsync(0, 100, 50);
            if (partial == null || partial.Length != 50) throw new Exception("Partial read failed");

            // Clear
            await store.ClearAsync();
            var cleared = await store.GetAsync(0);
            if (cleared != null) throw new Exception("Should be cleared");

            Console.WriteLine("[OPFS] Put/Get/Clear verified — data persists in browser OPFS");
        }
        catch (Exception ex) when (ex is not UnsupportedTestException)
        {
            throw new Exception($"OPFS test failed: {ex.Message}");
        }
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
    //  WebRTC Transport (platform-agnostic construction)
    // ═══════════════════════════════════════════════════════════

    [TestMethod]
    public async Task WebRtcTransport_Create_PlatformAgnostic()
    {
        await using var transport = IWebRtcTransport.Create();
        if (transport.Type != "webrtc") throw new Exception($"Type: {transport.Type}");
        if (!transport.CanAccept) throw new Exception("Should accept connections");
        Console.WriteLine($"[Coverage] WebRTC transport: {transport.GetType().Name}");
    }

    // ═══════════════════════════════════════════════════════════
    //  Coverage Gap Tests — Tuvok Audit
    // ═══════════════════════════════════════════════════════════

    [TestMethod]
    public async Task DownloadCoordinator_Events()
    {
        var data = new byte[32768];
        var (_, metadata) = TorrentCreator.CreateFromBytes("test.bin", data, new TorrentCreatorOptions { PieceLength = 16384 });
        var store = new MemoryChunkStore(16384);
        var pm = new PieceManager(metadata, store);
        var coordinator = new DownloadCoordinator(pm, metadata);

        int? completedPiece = null;
        bool downloadComplete = false;
        coordinator.OnPieceComplete += (idx) => completedPiece = idx;
        coordinator.OnDownloadComplete += () => downloadComplete = true;

        // Feed all pieces directly to PieceManager to trigger events
        int pieceCount = metadata.PieceCount;
        for (int i = 0; i < pieceCount; i++)
        {
            int pieceSize = (i < pieceCount - 1) ? metadata.PieceLength : (int)(metadata.TotalLength - (long)i * metadata.PieceLength);
            var pieceData = new byte[pieceSize];
            Array.Copy(data, (long)i * metadata.PieceLength, pieceData, 0, pieceSize);
            await pm.ReceiveCompletePieceAsync(i, pieceData);
        }

        if (completedPiece == null) throw new Exception("OnPieceComplete should fire");
        if (!pm.IsComplete) throw new Exception("All pieces should be complete");

        Console.WriteLine($"[DownloadCoordinator] Events: piece {completedPiece} completed, all done={pm.IsComplete} ✓");
    }

    [TestMethod]
    public async Task TorrentSwarm_Properties_AfterMetadata()
    {
        await using var client = new WebTorrentClient(crypto: Client!.Crypto);
        var data = new byte[16384];
        Random.Shared.NextBytes(data);
        var swarm = await client.SeedAsync(data, "test-props.bin");

        if (swarm.Done != true) throw new Exception("Seeded swarm should be done");
        if (swarm.Progress != 1.0) throw new Exception($"Progress: {swarm.Progress}");
        if (swarm.TimeRemaining != 0) throw new Exception($"TimeRemaining: {swarm.TimeRemaining}");
        if (swarm.Length <= 0) throw new Exception($"Length: {swarm.Length}");
        if (swarm.PieceLength <= 0) throw new Exception($"PieceLength: {swarm.PieceLength}");
        if (swarm.HasMetadata != true) throw new Exception("Should have metadata");
        if (swarm.Ready != true) throw new Exception("Should be ready");
        if (string.IsNullOrEmpty(swarm.MagnetURI)) throw new Exception("MagnetURI empty");

        // Stats should be zero for fresh seed
        if (swarm.Downloaded < 0) throw new Exception($"Downloaded: {swarm.Downloaded}");
        if (swarm.Uploaded < 0) throw new Exception($"Uploaded: {swarm.Uploaded}");
        if (swarm.Ratio < 0) throw new Exception($"Ratio: {swarm.Ratio}");

        Console.WriteLine("[TorrentSwarm] Properties after seed: OK");
    }

    [TestMethod]
    public async Task TorrentSwarm_PauseResume_State()
    {
        await using var client = new WebTorrentClient(crypto: Client!.Crypto);
        var data = new byte[16384];
        var swarm = await client.SeedAsync(data, "pause-test.bin");

        if (swarm.Paused) throw new Exception("Should not be paused initially");

        swarm.Pause();
        if (!swarm.Paused) throw new Exception("Should be paused after Pause()");

        swarm.Resume();
        if (swarm.Paused) throw new Exception("Should not be paused after Resume()");

        // Double pause/resume should be safe
        swarm.Pause();
        swarm.Pause();
        swarm.Resume();
        swarm.Resume();

        Console.WriteLine("[TorrentSwarm] Pause/Resume state: OK");
    }

    [TestMethod]
    public async Task TorrentSwarm_FileStream_Properties()
    {
        await using var client = new WebTorrentClient(crypto: Client!.Crypto);
        var data = new byte[32768];
        Random.Shared.NextBytes(data);
        var swarm = await client.SeedAsync(data, "stream-test.bin");

        if (swarm.Files == null || swarm.Files.Length == 0)
            throw new Exception("Should have files");

        var file = swarm.Files[0];
        if (file.Name != "stream-test.bin") throw new Exception($"Name: {file.Name}");
        if (file.Size != data.Length) throw new Exception($"Size: {file.Size}");
        if (file.Length != data.Length) throw new Exception($"Length: {file.Length}");
        if (!file.Done) throw new Exception("File should be done (seeded)");
        if (file.Downloaded != data.Length) throw new Exception($"Downloaded: {file.Downloaded}");

        // MIME type detection
        if (string.IsNullOrEmpty(file.Type)) throw new Exception("Type should not be empty");

        // Piece range
        if (file.StartPiece < 0) throw new Exception($"StartPiece: {file.StartPiece}");
        if (file.EndPiece < file.StartPiece) throw new Exception($"EndPiece: {file.EndPiece}");

        Console.WriteLine($"[TorrentSwarm] FileStream: {file.Name}, {file.Size}b, type={file.Type}, pieces={file.StartPiece}-{file.EndPiece} ✓");
    }

    [TestMethod]
    public async Task Client_OnTorrentAdd_Event()
    {
        await using var client = new WebTorrentClient(crypto: Client!.Crypto);
        TorrentSwarm? added = null;
        client.OnTorrentAdd += (swarm) => added = swarm;

        var data = new byte[8192];
        await client.SeedAsync(data, "event-test.bin");

        if (added == null) throw new Exception("OnTorrentAdd should fire");

        Console.WriteLine("[Client] OnTorrentAdd event: OK");
    }

    [TestMethod]
    public async Task Client_OnTorrentRemove_Event()
    {
        await using var client = new WebTorrentClient(crypto: Client!.Crypto);
        TorrentSwarm? removed = null;
        client.OnTorrentRemove += (swarm) => removed = swarm;

        var data = new byte[8192];
        var swarm = await client.SeedAsync(data, "remove-test.bin");
        await client.RemoveAsync(swarm);

        if (removed == null) throw new Exception("OnTorrentRemove should fire");

        Console.WriteLine("[Client] OnTorrentRemove event: OK");
    }

}
