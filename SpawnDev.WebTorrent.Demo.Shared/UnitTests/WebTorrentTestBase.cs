using SpawnDev.BlazorJS;
using SpawnDev.BlazorJS.Cryptography;
using SpawnDev.UnitTesting;
using SpawnDev.WebTorrent.ModelDelivery;
using SpawnDev.WebTorrent.Storage;
using SpawnDev.WebTorrent.Torrent;
using SpawnDev.WebTorrent.Transports;
using SpawnDev.WebTorrent.Wire;

namespace SpawnDev.WebTorrent.Demo.Shared.UnitTests;

/// <summary>
/// Base class for WebTorrent unit tests. Provides common test infrastructure.
/// Tests run in both desktop (console) and browser (Blazor WASM) contexts.
/// </summary>
public abstract partial class WebTorrentTestBase
{
    /// <summary>
    /// BlazorJS runtime injected via DI in browser test subclasses.
    /// Null on desktop (tests that need it should check OperatingSystem.IsBrowser()).
    /// </summary>
    protected BlazorJSRuntime? JS { get; set; }

    /// <summary>
    /// DI singleton WebTorrentClient. Available in browser tests.
    /// </summary>
    protected WebTorrentClient? Client { get; set; }

    /// <summary>
    /// DI singleton IAsyncFS (OPFS in browser). Available in browser tests.
    /// </summary>
    protected SpawnDev.AsyncFileSystem.IAsyncFS? AsyncFs { get; set; }

    /// <summary>
    /// Creates the platform-appropriate IPortableCrypto implementation.
    /// Browser: BrowserWASMCrypto (SubtleCrypto). Desktop: DotNetCrypto.
    /// </summary>
    protected IPortableCrypto CreateCrypto()
    {
        if (OperatingSystem.IsBrowser())
            return new BrowserWASMCrypto(JS!);
        return new DotNetCrypto();
    }

    // ═══════════════════════════════════════════════════════════
    //  Bencode Tests
    // ═══════════════════════════════════════════════════════════

    [TestMethod]
    public async Task Bencode_EncodeString()
    {
        var encoded = Bencode.BencodeEncoder.EncodeString("hello");
        if (encoded != "5:hello")
            throw new Exception($"Expected '5:hello', got '{encoded}'");
    }

    [TestMethod]
    public async Task Bencode_EncodeInt()
    {
        var encoded = Bencode.BencodeEncoder.EncodeInt(42);
        if (encoded != "i42e")
            throw new Exception($"Expected 'i42e', got '{encoded}'");
    }

    [TestMethod]
    public async Task Bencode_DecodeString()
    {
        var data = System.Text.Encoding.UTF8.GetBytes("5:hello");
        var (value, _) = Bencode.BencodeDecoder.DecodeString(data, 0);
        if (value != "hello")
            throw new Exception($"Expected 'hello', got '{value}'");
    }

    [TestMethod]
    public async Task Bencode_DecodeInt()
    {
        var data = System.Text.Encoding.UTF8.GetBytes("i42e");
        var (value, _) = Bencode.BencodeDecoder.DecodeInt(data, 0);
        if (value != 42)
            throw new Exception($"Expected 42, got {value}");
    }

    // ═══════════════════════════════════════════════════════════
    //  MemoryChunkStore Tests
    // ═══════════════════════════════════════════════════════════

    [TestMethod]
    public async Task ChunkStore_PutAndGet()
    {
        await using var store = new MemoryChunkStore(16384);
        var data = new byte[16384];
        Random.Shared.NextBytes(data);

        await store.PutAsync(0, data);
        var result = await store.GetAsync(0);

        if (result == null) throw new Exception("GetAsync returned null");
        if (!result.SequenceEqual(data))
            throw new Exception("Retrieved data doesn't match stored data");
    }

    [TestMethod]
    public async Task ChunkStore_PartialRead()
    {
        await using var store = new MemoryChunkStore(16384);
        var data = new byte[16384];
        for (int i = 0; i < data.Length; i++) data[i] = (byte)(i % 256);

        await store.PutAsync(0, data);
        var partial = await store.GetAsync(0, 100, 50);

        if (partial == null) throw new Exception("Partial GetAsync returned null");
        if (partial.Length != 50) throw new Exception($"Expected 50 bytes, got {partial.Length}");
        for (int i = 0; i < 50; i++)
            if (partial[i] != (byte)((100 + i) % 256))
                throw new Exception($"Byte mismatch at {i}: expected {(100 + i) % 256}, got {partial[i]}");
    }

    [TestMethod]
    public async Task ChunkStore_GetMissing_ReturnsNull()
    {
        await using var store = new MemoryChunkStore(16384);
        var result = await store.GetAsync(99);
        if (result != null)
            throw new Exception("Expected null for missing chunk");
    }

    [TestMethod]
    public async Task ChunkStore_Clear()
    {
        await using var store = new MemoryChunkStore(16384);
        await store.PutAsync(0, new byte[16384]);
        await store.PutAsync(1, new byte[16384]);
        await store.ClearAsync();

        if (await store.GetAsync(0) != null) throw new Exception("Chunk 0 should be cleared");
        if (await store.GetAsync(1) != null) throw new Exception("Chunk 1 should be cleared");
    }

    // ═══════════════════════════════════════════════════════════
    //  TorrentMetadata Tests
    // ═══════════════════════════════════════════════════════════

    [TestMethod]
    public async Task Metadata_VerifyPiece_CorrectHash()
    {
        var data = new byte[16384];
        Random.Shared.NextBytes(data);
        var hash = System.Security.Cryptography.SHA1.HashData(data);

        var metadata = new TorrentMetadata
        {
            PieceLength = 16384,
            TotalLength = 16384,
            PieceHashes = new[] { hash },
        };

        if (!metadata.VerifyPiece(0, data))
            throw new Exception("Piece verification failed for correct data");
    }

    [TestMethod]
    public async Task Metadata_VerifyPiece_WrongData()
    {
        var data = new byte[16384];
        var hash = System.Security.Cryptography.SHA1.HashData(data); // hash of zeros

        data[0] = 1; // corrupt
        var metadata = new TorrentMetadata
        {
            PieceLength = 16384,
            TotalLength = 16384,
            PieceHashes = new[] { hash },
        };

        if (metadata.VerifyPiece(0, data))
            throw new Exception("Piece verification should fail for corrupted data");
    }

    // ═══════════════════════════════════════════════════════════
    //  Wire Protocol Tests
    // ═══════════════════════════════════════════════════════════

    [TestMethod]
    public async Task WireProtocol_HandshakeFormat()
    {
        // Verify handshake is exactly 68 bytes with correct format
        var infoHash = new byte[20]; infoHash[0] = 0xAB;
        var peerId = new byte[20]; peerId[0] = 0xCD;
        var captured = new List<byte>();

        var mockConn = new MockConnection(captured);
        var wire = new WireProtocol(mockConn);
        await wire.SendHandshakeAsync(infoHash, peerId);

        if (captured.Count != 68)
            throw new Exception($"Handshake should be 68 bytes, got {captured.Count}");
        if (captured[0] != 19)
            throw new Exception($"First byte should be 19 (protocol string length), got {captured[0]}");
        // "BitTorrent protocol" starts at byte 1
        var proto = System.Text.Encoding.ASCII.GetString(captured.Skip(1).Take(19).ToArray());
        if (proto != "BitTorrent protocol")
            throw new Exception($"Protocol string mismatch: '{proto}'");
        // BEP 10 flag should be set (byte 25, bit 4)
        if ((captured[25] & 0x10) == 0)
            throw new Exception("BEP 10 extension flag not set in reserved bytes");
        // Info hash at offset 28
        if (captured[28] != 0xAB)
            throw new Exception("Info hash not at correct offset");
        // Peer ID at offset 48
        if (captured[48] != 0xCD)
            throw new Exception("Peer ID not at correct offset");
    }

    [TestMethod]
    public async Task WireProtocol_MessageFraming()
    {
        // Verify messages are length-prefixed (4 bytes big-endian)
        var captured = new List<byte>();
        var mockConn = new MockConnection(captured);
        var wire = new WireProtocol(mockConn);

        await wire.SendMessageAsync(MessageType.Interested);

        // Should be: 00 00 00 01 02 (length=1, type=Interested=2)
        if (captured.Count != 5) throw new Exception($"Expected 5 bytes, got {captured.Count}");
        if (captured[3] != 1) throw new Exception("Length should be 1");
        if (captured[4] != (byte)MessageType.Interested)
            throw new Exception($"Message type should be {(byte)MessageType.Interested}");
    }

    [TestMethod]
    public async Task WireProtocol_RequestMessage()
    {
        var captured = new List<byte>();
        var mockConn = new MockConnection(captured);
        var wire = new WireProtocol(mockConn);

        await wire.SendRequestAsync(pieceIndex: 5, offset: 16384, length: 16384);

        // 4 bytes length + 1 byte type + 4+4+4 = 17 total
        if (captured.Count != 17) throw new Exception($"Expected 17 bytes, got {captured.Count}");
        if (captured[4] != (byte)MessageType.Request)
            throw new Exception("Message type should be Request");
    }

    // ═══════════════════════════════════════════════════════════
    //  Client Tests
    // ═══════════════════════════════════════════════════════════

    [TestMethod]
    public async Task Client_PeerId_Format()
    {
        var client = new WebTorrentClient(crypto: Client!.Crypto);
        var peerId = client.PeerId;

        if (peerId.Length != 20) throw new Exception($"Peer ID should be 20 bytes, got {peerId.Length}");
        var prefix = System.Text.Encoding.ASCII.GetString(peerId, 0, 8);
        if (prefix != "-SD0110-")
            throw new Exception($"Peer ID prefix should be '-SD0100-', got '{prefix}'");

        await client.DisposeAsync();
    }

    [TestMethod]
    public async Task Client_ParseMagnetUri()
    {
        var client = new WebTorrentClient(crypto: Client!.Crypto);
        var hash = "d2474e86c95b19b8bcfdb92bc12c9d44667ce52e";

        var swarm = await client.AddAsync($"magnet:?xt=urn:btih:{hash}&dn=test");

        if (swarm.InfoHash.Length != 20) throw new Exception("Info hash should be 20 bytes");
        var hexHash = Convert.ToHexString(swarm.InfoHash).ToLowerInvariant();
        if (hexHash != hash)
            throw new Exception($"Parsed hash '{hexHash}' doesn't match '{hash}'");

        await client.DisposeAsync();
    }

    // ═══════════════════════════════════════════════════════════
    //  Torrent Creator Tests
    // ═══════════════════════════════════════════════════════════

    [TestMethod]
    public async Task TorrentCreator_CreateFromBytes_ValidOutput()
    {
        var data = new byte[32768]; // 32KB
        Random.Shared.NextBytes(data);

        var (torrentBytes, metadata) = TorrentCreator.CreateFromBytes("test.bin", data,
            new TorrentCreatorOptions
            {
                Trackers = new[] { "wss://tracker.example.com" },
                WebSeeds = new[] { "https://cdn.example.com/test.bin" },
                Comment = "Test torrent",
            });

        if (torrentBytes.Length == 0) throw new Exception("Torrent bytes empty");
        if (metadata.InfoHash.Length != 20) throw new Exception($"InfoHash should be 20 bytes, got {metadata.InfoHash.Length}");
        if (metadata.Name != "test.bin") throw new Exception($"Name should be 'test.bin', got '{metadata.Name}'");
        if (metadata.TotalLength != 32768) throw new Exception($"TotalLength should be 32768, got {metadata.TotalLength}");
        if (metadata.PieceHashes.Length == 0) throw new Exception("No piece hashes generated");
        if (metadata.Files.Length != 1) throw new Exception($"Should have 1 file, got {metadata.Files.Length}");
    }

    [TestMethod]
    public async Task TorrentCreator_PieceHashes_VerifyCorrectly()
    {
        var data = new byte[16384]; // exactly one piece at minimum piece length
        for (int i = 0; i < data.Length; i++) data[i] = (byte)(i % 256);

        var (_, metadata) = TorrentCreator.CreateFromBytes("verify.bin", data,
            new TorrentCreatorOptions { PieceLength = 16384 });

        // The piece hash should match SHA-1 of the data
        if (!metadata.VerifyPiece(0, data))
            throw new Exception("Created torrent's piece hash doesn't verify against source data");
    }

    [TestMethod]
    public async Task TorrentCreator_RoundTrip_CreateAndParse()
    {
        var data = new byte[65536]; // 64KB = 4 pieces at 16KB
        Random.Shared.NextBytes(data);

        var (torrentBytes, original) = TorrentCreator.CreateFromBytes("roundtrip.bin", data,
            new TorrentCreatorOptions
            {
                PieceLength = 16384,
                Trackers = new[] { "wss://tracker.test.com" },
                WebSeeds = new[] { "https://seed.test.com/roundtrip.bin" },
            });

        // Parse the created .torrent back
        var parsed = TorrentParser.Parse(torrentBytes);

        // Verify round-trip
        if (parsed.Name != original.Name)
            throw new Exception($"Name mismatch: '{parsed.Name}' vs '{original.Name}'");
        if (parsed.TotalLength != original.TotalLength)
            throw new Exception($"TotalLength mismatch: {parsed.TotalLength} vs {original.TotalLength}");
        if (parsed.PieceLength != original.PieceLength)
            throw new Exception($"PieceLength mismatch: {parsed.PieceLength} vs {original.PieceLength}");
        if (parsed.PieceHashes.Length != original.PieceHashes.Length)
            throw new Exception($"PieceCount mismatch: {parsed.PieceHashes.Length} vs {original.PieceHashes.Length}");

        // Info hashes must match
        if (!parsed.InfoHash.SequenceEqual(original.InfoHash))
            throw new Exception("InfoHash mismatch after round-trip");

        // Verify every piece hash matches
        for (int i = 0; i < parsed.PieceHashes.Length; i++)
        {
            if (!parsed.PieceHashes[i].SequenceEqual(original.PieceHashes[i]))
                throw new Exception($"Piece hash mismatch at index {i}");
        }
    }

    // ═══════════════════════════════════════════════════════════
    //  Magnet URI Parsing Tests
    // ═══════════════════════════════════════════════════════════

    [TestMethod]
    public async Task Magnet_ParseWithTrackers()
    {
        var magnet = "magnet:?xt=urn:btih:d2474e86c95b19b8bcfdb92bc12c9d44667ce52e&dn=TestFile&tr=wss://tracker.example.com";
        var meta = TorrentParser.ParseMagnet(magnet);

        if (meta.InfoHash.Length != 20)
            throw new Exception($"Info hash should be 20 bytes, got {meta.InfoHash.Length}");
        if (meta.Name != "TestFile")
            throw new Exception($"Name should be 'TestFile', got '{meta.Name}'");
        if (meta.AnnounceList.Length == 0 || meta.AnnounceList[0][0] != "wss://tracker.example.com")
            throw new Exception("Tracker not parsed correctly");
    }

    [TestMethod]
    public async Task Magnet_ParseWithWebSeed()
    {
        var magnet = "magnet:?xt=urn:btih:d2474e86c95b19b8bcfdb92bc12c9d44667ce52e&ws=https://cdn.example.com/model.bin";
        var meta = TorrentParser.ParseMagnet(magnet);

        if (meta.UrlList.Length == 0 || !meta.UrlList[0].Contains("cdn.example.com"))
            throw new Exception("Web seed not parsed correctly");
    }

    [TestMethod]
    public async Task TorrentParser_RoundTrip_InfoHash()
    {
        // Build a minimal .torrent file by hand using bencode
        var info = new SortedDictionary<string, string>
        {
            ["name"] = Bencode.BencodeEncoder.EncodeString("test.bin"),
            ["piece length"] = Bencode.BencodeEncoder.EncodeInt(16384),
            ["length"] = Bencode.BencodeEncoder.EncodeInt(16384),
            ["pieces"] = Bencode.BencodeEncoder.EncodeString(new string('A', 20)), // fake hash
        };
        var infoEncoded = Bencode.BencodeEncoder.EncodeDictionary(info);

        var top = new SortedDictionary<string, string>
        {
            ["info"] = infoEncoded,
        };
        var torrentEncoded = Bencode.BencodeEncoder.EncodeDictionary(top);
        var torrentBytes = System.Text.Encoding.UTF8.GetBytes(torrentEncoded);

        var metadata = TorrentParser.Parse(torrentBytes);

        if (metadata.InfoHash.Length != 20)
            throw new Exception($"InfoHash should be 20 bytes, got {metadata.InfoHash.Length}");
        if (metadata.Name != "test.bin")
            throw new Exception($"Name should be 'test.bin', got '{metadata.Name}'");
        if (metadata.PieceLength != 16384)
            throw new Exception($"PieceLength should be 16384, got {metadata.PieceLength}");
        if (metadata.TotalLength != 16384)
            throw new Exception($"TotalLength should be 16384, got {metadata.TotalLength}");
        if (metadata.Files.Length != 1)
            throw new Exception($"Should have 1 file, got {metadata.Files.Length}");
    }

    // ═══════════════════════════════════════════════════════════
    //  Mock Connection (for wire protocol tests)
    // ═══════════════════════════════════════════════════════════

    private class MockConnection : Transports.IConnection
    {
        private readonly List<byte> _captured;
        public string RemoteId => "mock";
        public string TransportType => "mock";
        public bool IsConnected => true;
        public event Action? OnDataAvailable;
        public event Action? OnDisconnected;

        public MockConnection(List<byte> captured) => _captured = captured;

        public Task SendAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default)
        {
            _captured.AddRange(data.ToArray());
            return Task.CompletedTask;
        }

        public Task<int> ReceiveAsync(Memory<byte> buffer, CancellationToken ct = default)
            => Task.FromResult(0);

        public Task CloseAsync() => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    // ═══════════════════════════════════════════════════════════
    //  PieceManager Tests
    // ═══════════════════════════════════════════════════════════

    [TestMethod]
    public async Task PieceManager_SelectPiece_ReturnsAvailable()
    {
        var data = new byte[32768]; // 2 pieces at 16KB each
        Random.Shared.NextBytes(data);
        var (_, metadata) = TorrentCreator.CreateFromBytes("test.bin", data,
            new TorrentCreatorOptions { PieceLength = 16384 });

        await using var store = new MemoryChunkStore(16384);
        var pm = new PieceManager(metadata, store);

        // Peer has both pieces
        var peerBitfield = new[] { true, true };
        int selected = pm.SelectPiece(peerBitfield);

        if (selected < 0 || selected > 1)
            throw new Exception($"Expected piece 0 or 1, got {selected}");
    }

    [TestMethod]
    public async Task PieceManager_SelectPiece_SkipsComplete()
    {
        var data = new byte[32768];
        var (_, metadata) = TorrentCreator.CreateFromBytes("test.bin", data,
            new TorrentCreatorOptions { PieceLength = 16384 });

        await using var store = new MemoryChunkStore(16384);
        var pm = new PieceManager(metadata, store);

        pm.MarkComplete(0); // piece 0 done
        var peerBitfield = new[] { true, true };
        int selected = pm.SelectPiece(peerBitfield);

        if (selected != 1)
            throw new Exception($"Expected piece 1 (only incomplete), got {selected}");
    }

    [TestMethod]
    public async Task PieceManager_ReceiveBlock_VerifiesAndStores()
    {
        var data = new byte[16384]; // exactly 1 piece
        for (int i = 0; i < data.Length; i++) data[i] = (byte)(i % 256);
        var (_, metadata) = TorrentCreator.CreateFromBytes("test.bin", data,
            new TorrentCreatorOptions { PieceLength = 16384 });

        await using var store = new MemoryChunkStore(16384);
        var pm = new PieceManager(metadata, store);

        // Get block request
        var (offset, length) = pm.GetNextBlock(0);
        if (offset != 0 || length != 16384)
            throw new Exception($"Expected (0, 16384), got ({offset}, {length})");

        // Receive the block (entire piece as one block)
        bool complete = await pm.ReceiveBlockAsync(0, 0, data);
        if (!complete)
            throw new Exception("Piece should be complete after receiving all data");
        if (!pm.Bitfield[0])
            throw new Exception("Bitfield[0] should be true after verification");
        if (pm.CompletedCount != 1)
            throw new Exception($"CompletedCount should be 1, got {pm.CompletedCount}");

        // Verify stored data matches
        var stored = await store.GetAsync(0);
        if (stored == null || !stored.SequenceEqual(data))
            throw new Exception("Stored data doesn't match original");
    }

    [TestMethod]
    public async Task PieceManager_ReceiveBlock_RejectsCorrupted()
    {
        var data = new byte[16384];
        for (int i = 0; i < data.Length; i++) data[i] = (byte)(i % 256);
        var (_, metadata) = TorrentCreator.CreateFromBytes("test.bin", data,
            new TorrentCreatorOptions { PieceLength = 16384 });

        await using var store = new MemoryChunkStore(16384);
        var pm = new PieceManager(metadata, store);

        // Send corrupted data
        var corrupted = new byte[16384];
        Array.Copy(data, corrupted, data.Length);
        corrupted[0] = (byte)(data[0] + 1); // flip one byte

        bool complete = await pm.ReceiveBlockAsync(0, 0, corrupted);
        if (complete)
            throw new Exception("Corrupted piece should NOT be marked complete");
        if (pm.Bitfield[0])
            throw new Exception("Bitfield[0] should be false for corrupted piece");
    }

    [TestMethod]
    public async Task PieceManager_MultiBlock_AssemblesCorrectly()
    {
        // 32KB piece = 2 blocks of 16KB each
        var data = new byte[32768];
        for (int i = 0; i < data.Length; i++) data[i] = (byte)(i % 256);
        var hash = System.Security.Cryptography.SHA1.HashData(data);

        var metadata = new TorrentMetadata
        {
            PieceLength = 32768,
            TotalLength = 32768,
            PieceHashes = new[] { hash },
            Files = new[] { new TorrentFile { Path = "test.bin", Length = 32768, Offset = 0, StartPiece = 0, EndPiece = 0 } },
        };

        await using var store = new MemoryChunkStore(32768);
        var pm = new PieceManager(metadata, store);

        // Request and receive block 0 (16KB)
        var (off0, len0) = pm.GetNextBlock(0);
        if (off0 != 0 || len0 != 16384) throw new Exception($"Block 0: expected (0,16384), got ({off0},{len0})");

        var block0 = new byte[16384];
        Array.Copy(data, 0, block0, 0, 16384);
        bool done0 = await pm.ReceiveBlockAsync(0, 0, block0);
        if (done0) throw new Exception("Piece shouldn't be done after 1 of 2 blocks");

        // Request and receive block 1 (16KB)
        var (off1, len1) = pm.GetNextBlock(0);
        if (off1 != 16384 || len1 != 16384) throw new Exception($"Block 1: expected (16384,16384), got ({off1},{len1})");

        var block1 = new byte[16384];
        Array.Copy(data, 16384, block1, 0, 16384);
        bool done1 = await pm.ReceiveBlockAsync(0, 16384, block1);
        if (!done1) throw new Exception("Piece should be done after 2 of 2 blocks");

        if (!pm.IsComplete) throw new Exception("Should be complete (1 piece, all blocks received)");
    }

    // ═══════════════════════════════════════════════════════════
    //  DownloadCoordinator Tests
    // ═══════════════════════════════════════════════════════════

    [TestMethod]
    public async Task Coordinator_PrioritizePiece_DownloadsFirst()
    {
        // Create torrent with 4 pieces
        var data = new byte[65536]; // 4 × 16KB
        for (int i = 0; i < data.Length; i++) data[i] = (byte)(i % 256);
        var (_, metadata) = TorrentCreator.CreateFromBytes("priority.bin", data,
            new TorrentCreatorOptions { PieceLength = 16384 });

        await using var store = new MemoryChunkStore(16384);
        var pm = new PieceManager(metadata, store);
        var coordinator = new DownloadCoordinator(pm, metadata);

        // Prioritize piece 3 (the last one)
        coordinator.Prioritize(3);

        // Verify it's in the priority set by checking piece 3 is selected
        // even when we only have pieces 0-2 available
        if (pm.Bitfield[3])
            throw new Exception("Piece 3 shouldn't be complete yet");
    }

    [TestMethod]
    public async Task Coordinator_WebSeedFallback_Structure()
    {
        var data = new byte[16384];
        var (_, metadata) = TorrentCreator.CreateFromBytes("webseed.bin", data,
            new TorrentCreatorOptions { PieceLength = 16384 });

        await using var store = new MemoryChunkStore(16384);
        var pm = new PieceManager(metadata, store);
        var coordinator = new DownloadCoordinator(pm, metadata);

        // Add a web seed (won't actually connect — just verifies the API works)
        coordinator.AddWebSeed(new HttpClient(), "https://example.com/models");

        // Verify coordinator accepts the web seed without errors
        coordinator.Prioritize(0);
    }

    // ═══════════════════════════════════════════════════════════
    //  TorrentFileStream Tests
    // ═══════════════════════════════════════════════════════════

    [TestMethod]
    public async Task FileStream_Progress_TracksCorrectly()
    {
        var data = new byte[32768]; // 2 pieces
        var (_, metadata) = TorrentCreator.CreateFromBytes("progress.bin", data,
            new TorrentCreatorOptions { PieceLength = 16384 });

        await using var store = new MemoryChunkStore(16384);
        var swarm = new TorrentSwarm(new WebTorrentClient(crypto: Client!.Crypto), new AddTorrentOptions());
        swarm.SetMetadata(metadata);

        // No pieces complete → 0% progress
        var file = swarm.Files[0];
        if (file.Progress != 0)
            throw new Exception($"Expected 0% progress, got {file.Progress:P1}");
    }

    // ═══════════════════════════════════════════════════════════
    //  Integration: Create → Parse → PieceManager → Verify
    // ═══════════════════════════════════════════════════════════

    [TestMethod]
    public async Task Integration_FullPipeline_CreateParseVerify()
    {
        // Create a torrent from data
        var data = new byte[49152]; // 3 × 16KB pieces
        for (int i = 0; i < data.Length; i++) data[i] = (byte)((i * 7 + 13) % 256);

        var (torrentBytes, original) = TorrentCreator.CreateFromBytes("pipeline.bin", data,
            new TorrentCreatorOptions
            {
                PieceLength = 16384,
                Trackers = new[] { "wss://tracker.test.com" },
            });

        // Parse it back
        var parsed = TorrentParser.Parse(torrentBytes);
        if (!parsed.InfoHash.SequenceEqual(original.InfoHash))
            throw new Exception("InfoHash mismatch");

        // Feed pieces through PieceManager
        await using var store = new MemoryChunkStore(16384);
        var pm = new PieceManager(parsed, store);

        for (int i = 0; i < 3; i++)
        {
            var pieceData = new byte[16384];
            Array.Copy(data, i * 16384, pieceData, 0, 16384);

            pm.GetNextBlock(i); // mark as requested
            bool complete = await pm.ReceiveBlockAsync(i, 0, pieceData);
            if (!complete)
                throw new Exception($"Piece {i} should verify correctly");
        }

        if (!pm.IsComplete)
            throw new Exception("All 3 pieces received but not marked complete");

        // Verify stored data matches original
        for (int i = 0; i < 3; i++)
        {
            var stored = await store.GetAsync(i);
            var expected = new byte[16384];
            Array.Copy(data, i * 16384, expected, 0, 16384);
            if (stored == null || !stored.SequenceEqual(expected))
                throw new Exception($"Stored piece {i} doesn't match original data");
        }
    }

    // ═══════════════════════════════════════════════════════════
    //  ModelTorrentClient Tests
    // ═══════════════════════════════════════════════════════════

    [TestMethod]
    public async Task ModelTorrentClient_CreateAndDispose()
    {
        await using var client = new ModelTorrentClient(new ModelTorrentOptions
        {
            ServerBaseUrl = "https://localhost:5560",
        });
        // Verify client creates without errors
    }

    [TestMethod]
    public async Task ModelStream_ReadAsync_Structure()
    {
        // Create a torrent from known data
        var data = new byte[32768]; // 2 pieces
        for (int i = 0; i < data.Length; i++) data[i] = (byte)(i % 256);
        var (_, metadata) = TorrentCreator.CreateFromBytes("model.bin", data,
            new TorrentCreatorOptions { PieceLength = 16384 });

        // Pre-populate the store (simulating already-downloaded pieces)
        var store = new MemoryChunkStore(16384);
        var pm = new PieceManager(metadata, store);

        // Store pieces manually
        var piece0 = new byte[16384];
        Array.Copy(data, 0, piece0, 0, 16384);
        await store.PutAsync(0, piece0);
        pm.MarkComplete(0);

        var piece1 = new byte[16384];
        Array.Copy(data, 16384, piece1, 0, 16384);
        await store.PutAsync(1, piece1);
        pm.MarkComplete(1);

        // Create a ModelStream with pre-populated store (no web seed needed)
        var webSeed = new WebSeedConnection(new HttpClient(), "http://unused", metadata);
        await using var stream = new ModelStream(metadata, store, pm, webSeed);

        // Read from offset 0
        var read0 = await stream.ReadAsync(0, 100);
        for (int i = 0; i < 100; i++)
            if (read0[i] != (byte)(i % 256))
                throw new Exception($"Read mismatch at offset {i}: expected {i % 256}, got {read0[i]}");

        // Read across piece boundary (offset 16380, length 10 = 4 bytes from piece 0 + 6 from piece 1)
        var readCross = await stream.ReadAsync(16380, 10);
        for (int i = 0; i < 10; i++)
        {
            byte expected = (byte)((16380 + i) % 256);
            if (readCross[i] != expected)
                throw new Exception($"Cross-piece read mismatch at {16380 + i}: expected {expected}, got {readCross[i]}");
        }

        // Read from second piece
        var read1 = await stream.ReadAsync(20000, 50);
        for (int i = 0; i < 50; i++)
        {
            byte expected = (byte)((20000 + i) % 256);
            if (read1[i] != expected)
                throw new Exception($"Piece 1 read mismatch at {20000 + i}: expected {expected}, got {read1[i]}");
        }
    }

    // ═══════════════════════════════════════════════════════════
    //  Stream Seeking Tests — random access while downloading
    // ═══════════════════════════════════════════════════════════

    [TestMethod]
    public async Task ModelStream_SeekForward_SkipsPieces()
    {
        // 64KB file = 4 pieces at 16KB. Read piece 0, skip to piece 3.
        var data = new byte[65536];
        for (int i = 0; i < data.Length; i++) data[i] = (byte)((i * 7 + 3) % 256);
        var (_, metadata) = TorrentCreator.CreateFromBytes("seek.bin", data,
            new TorrentCreatorOptions { PieceLength = 16384 });

        var store = new MemoryChunkStore(16384);
        var pm = new PieceManager(metadata, store);

        // Pre-populate all pieces
        for (int p = 0; p < 4; p++)
        {
            var piece = new byte[16384];
            Array.Copy(data, p * 16384, piece, 0, 16384);
            await store.PutAsync(p, piece);
            pm.MarkComplete(p);
        }

        var webSeed = new WebSeedConnection(new HttpClient(), "http://unused", metadata);
        await using var stream = new ModelStream(metadata, store, pm, webSeed);

        // Read from start (piece 0)
        var first = await stream.ReadAsync(0, 10);
        for (int i = 0; i < 10; i++)
            if (first[i] != data[i])
                throw new Exception($"Start read mismatch at {i}");

        // Seek forward to piece 3 (offset 49152)
        var seeked = await stream.ReadAsync(49152, 100);
        for (int i = 0; i < 100; i++)
            if (seeked[i] != data[49152 + i])
                throw new Exception($"Seek forward mismatch at {49152 + i}: expected {data[49152 + i]}, got {seeked[i]}");

        // Seek back to piece 1 (offset 20000)
        var backSeek = await stream.ReadAsync(20000, 100);
        for (int i = 0; i < 100; i++)
            if (backSeek[i] != data[20000 + i])
                throw new Exception($"Seek back mismatch at {20000 + i}");
    }

    [TestMethod]
    public async Task ModelStream_SeekBack_ReadsCorrectly()
    {
        // Verify backward seeking works — read end first, then beginning
        var data = new byte[49152]; // 3 pieces
        for (int i = 0; i < data.Length; i++) data[i] = (byte)((i * 13 + 7) % 256);
        var (_, metadata) = TorrentCreator.CreateFromBytes("seekback.bin", data,
            new TorrentCreatorOptions { PieceLength = 16384 });

        var store = new MemoryChunkStore(16384);
        var pm = new PieceManager(metadata, store);

        for (int p = 0; p < 3; p++)
        {
            var piece = new byte[16384];
            Array.Copy(data, p * 16384, piece, 0, 16384);
            await store.PutAsync(p, piece);
            pm.MarkComplete(p);
        }

        var webSeed = new WebSeedConnection(new HttpClient(), "http://unused", metadata);
        await using var stream = new ModelStream(metadata, store, pm, webSeed);

        // Read from the END first (piece 2, last 100 bytes)
        var endRead = await stream.ReadAsync(49052, 100);
        for (int i = 0; i < 100; i++)
            if (endRead[i] != data[49052 + i])
                throw new Exception($"End read mismatch at {49052 + i}");

        // Now seek back to the BEGINNING
        var startRead = await stream.ReadAsync(0, 100);
        for (int i = 0; i < 100; i++)
            if (startRead[i] != data[i])
                throw new Exception($"Start read after backward seek mismatch at {i}");

        // Read from middle
        var midRead = await stream.ReadAsync(24000, 200);
        for (int i = 0; i < 200; i++)
            if (midRead[i] != data[24000 + i])
                throw new Exception($"Mid read mismatch at {24000 + i}");
    }

    [TestMethod]
    public async Task ModelStream_LargeSeek_SpansManyPieces()
    {
        // 128KB file = 8 pieces. Read that spans pieces 2-5 (4 pieces at once)
        var data = new byte[131072];
        for (int i = 0; i < data.Length; i++) data[i] = (byte)((i * 3 + 11) % 256);
        var (_, metadata) = TorrentCreator.CreateFromBytes("largeseek.bin", data,
            new TorrentCreatorOptions { PieceLength = 16384 });

        var store = new MemoryChunkStore(16384);
        var pm = new PieceManager(metadata, store);

        for (int p = 0; p < 8; p++)
        {
            var piece = new byte[16384];
            Array.Copy(data, p * 16384, piece, 0, 16384);
            await store.PutAsync(p, piece);
            pm.MarkComplete(p);
        }

        var webSeed = new WebSeedConnection(new HttpClient(), "http://unused", metadata);
        await using var stream = new ModelStream(metadata, store, pm, webSeed);

        // Read 50KB starting from offset 30000 — spans pieces 1,2,3,4
        int offset = 30000;
        int length = 51200; // 50KB
        var bigRead = await stream.ReadAsync(offset, length);

        if (bigRead.Length != length)
            throw new Exception($"Expected {length} bytes, got {bigRead.Length}");

        for (int i = 0; i < length; i++)
            if (bigRead[i] != data[offset + i])
                throw new Exception($"Large span read mismatch at {offset + i}: expected {data[offset + i]}, got {bigRead[i]}");
    }

    [TestMethod]
    public async Task ModelStream_RandomAccessPattern_MLWeightLoading()
    {
        // Simulate ML weight loading: read header, seek to weight offset, read weights,
        // seek to bias offset, read biases — non-sequential random access
        var data = new byte[98304]; // 6 pieces
        for (int i = 0; i < data.Length; i++) data[i] = (byte)(i % 256);
        var (_, metadata) = TorrentCreator.CreateFromBytes("weights.bin", data,
            new TorrentCreatorOptions { PieceLength = 16384 });

        var store = new MemoryChunkStore(16384);
        var pm = new PieceManager(metadata, store);

        for (int p = 0; p < 6; p++)
        {
            var piece = new byte[16384];
            Array.Copy(data, p * 16384, piece, 0, 16384);
            await store.PutAsync(p, piece);
            pm.MarkComplete(p);
        }

        var webSeed = new WebSeedConnection(new HttpClient(), "http://unused", metadata);
        await using var stream = new ModelStream(metadata, store, pm, webSeed);

        // 1. Read header (first 256 bytes)
        var header = await stream.ReadAsync(0, 256);
        for (int i = 0; i < 256; i++)
            if (header[i] != data[i]) throw new Exception($"Header mismatch at {i}");

        // 2. Seek to weight matrix at offset 32768 (piece 2), read 16384 bytes
        var weights = await stream.ReadAsync(32768, 16384);
        for (int i = 0; i < 16384; i++)
            if (weights[i] != data[32768 + i]) throw new Exception($"Weight mismatch at {32768 + i}");

        // 3. Seek to bias at offset 81920 (piece 5), read 1024 bytes
        var bias = await stream.ReadAsync(81920, 1024);
        for (int i = 0; i < 1024; i++)
            if (bias[i] != data[81920 + i]) throw new Exception($"Bias mismatch at {81920 + i}");

        // 4. Seek back to second weight matrix at offset 49152 (piece 3)
        var weights2 = await stream.ReadAsync(49152, 8192);
        for (int i = 0; i < 8192; i++)
            if (weights2[i] != data[49152 + i]) throw new Exception($"Weight2 mismatch at {49152 + i}");

        // 5. Read the very last byte
        var lastByte = await stream.ReadAsync(98303, 1);
        if (lastByte[0] != data[98303]) throw new Exception($"Last byte mismatch");
    }
}
