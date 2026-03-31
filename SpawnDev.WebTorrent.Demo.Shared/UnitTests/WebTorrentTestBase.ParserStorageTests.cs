using SpawnDev.BlazorJS;
using SpawnDev.BlazorJS.JSObjects;
using SpawnDev.UnitTesting;
using SpawnDev.WebTorrent.Bencode;
using SpawnDev.WebTorrent.Storage;
using SpawnDev.WebTorrent.Torrent;
using System.Security.Cryptography;
using System.Text;

namespace SpawnDev.WebTorrent.Demo.Shared.UnitTests;

/// <summary>
/// Tests for TorrentParser, TorrentCreator, Bencode, FileChunkStore, and MemoryChunkStore.
/// Pure logic — no network, no browser APIs.
/// </summary>
public abstract partial class WebTorrentTestBase
{
    // ═══════════════════════════════════════════════════════════
    //  TorrentParser — Magnet URI Parsing
    // ═══════════════════════════════════════════════════════════

    [TestMethod]
    public async Task Parser_Magnet_HexHash()
    {
        var magnet = "magnet:?xt=urn:btih:dd8255ecdc7ca55fb0bbf81323d87062db1f6d1c&dn=Big+Buck+Bunny";
        var meta = TorrentParser.ParseMagnet(magnet);
        if (meta.InfoHash == null || meta.InfoHash.Length != 20)
            throw new Exception("InfoHash should be 20 bytes");
        if (meta.Name != "Big Buck Bunny")
            throw new Exception($"Name should be 'Big Buck Bunny', got '{meta.Name}'");
    }

    [TestMethod]
    public async Task Parser_Magnet_Base32Hash()
    {
        // Base32 encode a known 20-byte hash
        var hash = new byte[20];
        for (int i = 0; i < 20; i++) hash[i] = (byte)(i + 1);
        var base32 = Base32Encode(hash);
        var magnet = $"magnet:?xt=urn:btih:{base32}&dn=TestTorrent";
        var meta = TorrentParser.ParseMagnet(magnet);
        if (meta.InfoHash == null || !meta.InfoHash.SequenceEqual(hash))
            throw new Exception("Base32 hash should decode correctly");
    }

    [TestMethod]
    public async Task Parser_Magnet_MultipleTrackers()
    {
        var magnet = "magnet:?xt=urn:btih:dd8255ecdc7ca55fb0bbf81323d87062db1f6d1c" +
                     "&tr=wss%3A%2F%2Ftracker1.example.com" +
                     "&tr=wss%3A%2F%2Ftracker2.example.com" +
                     "&tr=udp%3A%2F%2Ftracker3.example.com%3A6969";
        var meta = TorrentParser.ParseMagnet(magnet);
        if (meta.AnnounceList == null || meta.AnnounceList.Length != 3)
            throw new Exception($"Should have 3 tracker tiers, got {meta.AnnounceList?.Length}");
        if (!meta.AnnounceList[0][0].Contains("tracker1"))
            throw new Exception("First tracker mismatch");
    }

    [TestMethod]
    public async Task Parser_Magnet_WebSeeds()
    {
        var magnet = "magnet:?xt=urn:btih:dd8255ecdc7ca55fb0bbf81323d87062db1f6d1c" +
                     "&ws=https%3A%2F%2Fseed1.example.com%2F" +
                     "&ws=https%3A%2F%2Fseed2.example.com%2F";
        var meta = TorrentParser.ParseMagnet(magnet);
        if (meta.UrlList == null || meta.UrlList.Length != 2)
            throw new Exception($"Should have 2 web seeds, got {meta.UrlList?.Length}");
    }

    [TestMethod]
    public async Task Parser_Magnet_Bep53_FileSelection()
    {
        var magnet = "magnet:?xt=urn:btih:dd8255ecdc7ca55fb0bbf81323d87062db1f6d1c&so=0,2,4";
        var meta = TorrentParser.ParseMagnet(magnet);
        if (meta.SelectedFileIndices == null || meta.SelectedFileIndices.Length != 3)
            throw new Exception($"Should have 3 selected indices, got {meta.SelectedFileIndices?.Length}");
        if (meta.SelectedFileIndices[1] != 2)
            throw new Exception($"Second index should be 2, got {meta.SelectedFileIndices[1]}");
    }

    [TestMethod]
    public async Task Parser_Magnet_ExactSource()
    {
        var magnet = "magnet:?xt=urn:btih:dd8255ecdc7ca55fb0bbf81323d87062db1f6d1c" +
                     "&xs=https%3A%2F%2Fexample.com%2Ftest.torrent";
        var meta = TorrentParser.ParseMagnet(magnet);
        if (meta.ExactSource != "https://example.com/test.torrent")
            throw new Exception($"ExactSource mismatch: {meta.ExactSource}");
    }

    [TestMethod]
    public async Task Parser_Magnet_PlusAsSpace()
    {
        var magnet = "magnet:?xt=urn:btih:dd8255ecdc7ca55fb0bbf81323d87062db1f6d1c&dn=Hello+World+Test";
        var meta = TorrentParser.ParseMagnet(magnet);
        if (meta.Name != "Hello World Test")
            throw new Exception($"'+' should decode as space: got '{meta.Name}'");
    }

    [TestMethod]
    public async Task Parser_Magnet_InvalidUri_Throws()
    {
        bool threw = false;
        try { TorrentParser.ParseMagnet("http://not-a-magnet"); }
        catch (ArgumentException) { threw = true; }
        if (!threw) throw new Exception("Should throw for non-magnet URI");
    }

    // ═══════════════════════════════════════════════════════════
    //  TorrentParser — .torrent File Parsing (round-trip)
    // ═══════════════════════════════════════════════════════════

    [TestMethod]
    public async Task Parser_RoundTrip_SingleFile()
    {
        // Create a torrent, serialize it, then parse it back
        var data = new byte[65536];
        Random.Shared.NextBytes(data);
        var (torrentBytes, created) = TorrentCreator.CreateFromBytes("test-roundtrip.bin", data, new TorrentCreatorOptions { PieceLength = 16384 });

        var parsed = TorrentParser.Parse(torrentBytes);
        if (parsed.Name != "test-roundtrip.bin")
            throw new Exception($"Name mismatch: {parsed.Name}");
        if (parsed.TotalLength != 65536)
            throw new Exception($"TotalLength mismatch: {parsed.TotalLength}");
        if (parsed.PieceLength != 16384)
            throw new Exception($"PieceLength mismatch: {parsed.PieceLength}");
        if (parsed.PieceCount != 4)
            throw new Exception($"PieceCount mismatch: {parsed.PieceCount}");
        if (parsed.Files == null || parsed.Files.Length != 1)
            throw new Exception($"Should have 1 file");
        if (parsed.InfoHash == null || parsed.InfoHash.Length != 20)
            throw new Exception("InfoHash should be 20 bytes");
    }

    [TestMethod]
    public async Task Parser_RoundTrip_InfoHashConsistent()
    {
        var data = new byte[32768];
        Random.Shared.NextBytes(data);
        var (torrentBytes, _) = TorrentCreator.CreateFromBytes("hash-test.bin", data);

        var parsed1 = TorrentParser.Parse(torrentBytes);
        var parsed2 = TorrentParser.Parse(torrentBytes);
        if (!parsed1.InfoHash!.SequenceEqual(parsed2.InfoHash!))
            throw new Exception("Same .torrent should produce same info hash");
    }

    [TestMethod]
    public async Task Parser_RoundTrip_PieceHashes()
    {
        var data = new byte[65536];
        Random.Shared.NextBytes(data);
        var (torrentBytes, _) = TorrentCreator.CreateFromBytes("pieces-test.bin", data, new TorrentCreatorOptions { PieceLength = 16384 });

        var parsed = TorrentParser.Parse(torrentBytes);
        if (parsed.PieceHashes == null) throw new Exception("PieceHashes null");
        if (parsed.PieceHashes.Length != 4)
            throw new Exception($"Expected 4 piece hashes, got {parsed.PieceHashes.Length}");

        // Verify first piece hash matches the algorithm used (SHA-256 default, SHA-1 legacy)
        var expectedHash = parsed.PieceHashAlgorithm == "SHA-256"
            ? SHA256.HashData(data.AsSpan(0, 16384))
            : SHA1.HashData(data.AsSpan(0, 16384));
        if (!parsed.PieceHashes[0].SequenceEqual(expectedHash))
            throw new Exception($"First piece hash mismatch (algorithm={parsed.PieceHashAlgorithm})");
    }

    [TestMethod]
    public async Task Parser_RoundTrip_PrivateTorrent()
    {
        var data = new byte[16384];
        Random.Shared.NextBytes(data);
        var (torrentBytes, _) = TorrentCreator.CreateFromBytes("private.bin", data, new TorrentCreatorOptions { IsPrivate = true });
        var parsed = TorrentParser.Parse(torrentBytes);
        if (!parsed.IsPrivate) throw new Exception("Private flag should survive round-trip");
    }

    [TestMethod]
    public async Task Parser_RoundTrip_Trackers()
    {
        var data = new byte[16384];
        Random.Shared.NextBytes(data);
        var trackers = new[] { "wss://tracker1.example.com", "udp://tracker2.example.com:6969" };
        var (torrentBytes, _) = TorrentCreator.CreateFromBytes("trackers.bin", data, new TorrentCreatorOptions { Trackers = trackers });
        var parsed = TorrentParser.Parse(torrentBytes);
        if (parsed.AnnounceList == null)
            throw new Exception("AnnounceList should not be null");
        var flatTrackers = parsed.AnnounceList.SelectMany(t => t).ToArray();
        if (!flatTrackers.Contains("wss://tracker1.example.com"))
            throw new Exception("Tracker 1 missing from round-trip");
    }

    [TestMethod]
    public async Task Parser_RoundTrip_WebSeeds()
    {
        var data = new byte[16384];
        Random.Shared.NextBytes(data);
        var webSeeds = new[] { "https://seed.example.com/files/" };
        var (torrentBytes, _) = TorrentCreator.CreateFromBytes("webseed.bin", data, new TorrentCreatorOptions { WebSeeds = webSeeds });
        var parsed = TorrentParser.Parse(torrentBytes);
        if (parsed.UrlList == null || !parsed.UrlList.Contains("https://seed.example.com/files/"))
            throw new Exception("Web seed should survive round-trip");
    }

    // ═══════════════════════════════════════════════════════════
    //  Bencode — Encoding/Decoding
    // ═══════════════════════════════════════════════════════════

    [TestMethod]
    public async Task Bencode_RoundTrip_Dict()
    {
        var dict = new Dictionary<string, object>
        {
            ["name"] = Encoding.UTF8.GetBytes("test"),
            ["count"] = 42L,
        };
        var encoded = BencodeEncoder.Encode(dict);
        var (decoded, _) = BencodeDecoder.DecodeDictionary(encoded, 0);
        if (decoded["count"] is not long val || val != 42)
            throw new Exception("Dictionary round-trip failed for integer");
    }

    [TestMethod]
    public async Task Bencode_RoundTrip_List()
    {
        var list = new List<object> { 1L, 2L, 3L, Encoding.UTF8.GetBytes("four") };
        var encoded = BencodeEncoder.Encode(list);
        var (decoded, _) = BencodeDecoder.DecodeList(encoded, 0);
        if (decoded.Count != 4)
            throw new Exception($"List count: expected 4, got {decoded.Count}");
        if (decoded[2] is not long v || v != 3)
            throw new Exception("List round-trip failed for integer");
    }

    [TestMethod]
    public async Task Bencode_NegativeInt()
    {
        var encoded = BencodeEncoder.EncodeInt(-99);
        if (encoded != "i-99e")
            throw new Exception($"Expected 'i-99e', got '{encoded}'");
    }

    [TestMethod]
    public async Task Bencode_ZeroInt()
    {
        var encoded = BencodeEncoder.EncodeInt(0);
        if (encoded != "i0e")
            throw new Exception($"Expected 'i0e', got '{encoded}'");
    }

    [TestMethod]
    public async Task Bencode_EmptyString()
    {
        var encoded = BencodeEncoder.EncodeString("");
        if (encoded != "0:")
            throw new Exception($"Expected '0:', got '{encoded}'");
    }

    [TestMethod]
    public async Task Bencode_BinaryData()
    {
        var data = new byte[] { 0x00, 0xFF, 0x80, 0x01 };
        var encoded = BencodeEncoder.Encode(data);
        var (decoded, _) = BencodeDecoder.DecodeRawString(encoded, 0);
        if (!decoded.SequenceEqual(data))
            throw new Exception("Binary data round-trip failed");
    }

    // ═══════════════════════════════════════════════════════════
    //  FileChunkStore — Desktop Persistent Storage
    // ═══════════════════════════════════════════════════════════

    [TestMethod]
    public async Task FileStore_PutGetClear()
    {
        if (OperatingSystem.IsBrowser())
            throw new UnsupportedTestException("FileChunkStore requires filesystem");

        var dir = Path.Combine(Path.GetTempPath(), $"wt-test-{Guid.NewGuid():N}");
        try
        {
            await using var store = new FileChunkStore(dir, 16384);

            // Put
            var data = new byte[16384];
            for (int i = 0; i < data.Length; i++) data[i] = (byte)(i % 256);
            await store.PutAsync(0, data);

            // Get
            var result = await store.GetAsync(0);
            if (result == null || !result.SequenceEqual(data))
                throw new Exception("Get data mismatch");

            // Partial get
            var partial = await store.GetAsync(0, 100, 50);
            if (partial == null || partial.Length != 50)
                throw new Exception("Partial read failed");
            if (partial[0] != data[100])
                throw new Exception("Partial data mismatch");

            // Get non-existent
            var missing = await store.GetAsync(99);
            if (missing != null)
                throw new Exception("Missing chunk should return null");

            // Remove
            await store.RemoveAsync(0);
            var removed = await store.GetAsync(0);
            if (removed != null)
                throw new Exception("Removed chunk should return null");

            // Clear
            await store.PutAsync(1, data);
            await store.PutAsync(2, data);
            await store.ClearAsync();
            if (await store.GetAsync(1) != null || await store.GetAsync(2) != null)
                throw new Exception("Clear should remove all chunks");

            Console.WriteLine("[FileStore] Put/Get/Remove/Clear verified");
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [TestMethod]
    public async Task FileStore_ChunkLength()
    {
        if (OperatingSystem.IsBrowser())
            throw new UnsupportedTestException("FileChunkStore requires filesystem");

        var dir = Path.Combine(Path.GetTempPath(), $"wt-test-{Guid.NewGuid():N}");
        try
        {
            await using var store = new FileChunkStore(dir, 32768);
            if (store.ChunkLength != 32768)
                throw new Exception($"ChunkLength: {store.ChunkLength}");
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [TestMethod]
    public async Task FileStore_MultipleChunks()
    {
        if (OperatingSystem.IsBrowser())
            throw new UnsupportedTestException("FileChunkStore requires filesystem");

        var dir = Path.Combine(Path.GetTempPath(), $"wt-test-{Guid.NewGuid():N}");
        try
        {
            await using var store = new FileChunkStore(dir, 1024);
            for (int i = 0; i < 10; i++)
            {
                var data = new byte[1024];
                System.Array.Fill(data, (byte)i);
                await store.PutAsync(i, data);
            }
            for (int i = 0; i < 10; i++)
            {
                var result = await store.GetAsync(i);
                if (result == null || result[0] != (byte)i)
                    throw new Exception($"Chunk {i} data mismatch");
            }
            Console.WriteLine("[FileStore] 10 chunks written and verified");
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    // ═══════════════════════════════════════════════════════════
    //  TorrentCreator — Torrent File Creation
    // ═══════════════════════════════════════════════════════════

    [TestMethod]
    public async Task Creator_CustomPieceLength()
    {
        var data = new byte[131072]; // 128 KB
        Random.Shared.NextBytes(data);
        var (_, meta) = TorrentCreator.CreateFromBytes("custom-piece.bin", data, new TorrentCreatorOptions { PieceLength = 32768 });
        if (meta.PieceLength != 32768)
            throw new Exception($"PieceLength: {meta.PieceLength}");
        if (meta.PieceCount != 4)
            throw new Exception($"PieceCount: {meta.PieceCount}");
    }

    [TestMethod]
    public async Task Creator_NonAlignedData()
    {
        // 50000 bytes with 16384 piece length = 4 pieces (last one partial)
        var data = new byte[50000];
        Random.Shared.NextBytes(data);
        var (_, meta) = TorrentCreator.CreateFromBytes("non-aligned.bin", data, new TorrentCreatorOptions { PieceLength = 16384 });
        if (meta.TotalLength != 50000)
            throw new Exception($"TotalLength: {meta.TotalLength}");
        // ceil(50000 / 16384) = 4 pieces
        if (meta.PieceCount != 4)
            throw new Exception($"PieceCount should be 4, got {meta.PieceCount}");
    }

    [TestMethod]
    public async Task Creator_TinyData()
    {
        var data = new byte[] { 1, 2, 3 };
        var (_, meta) = TorrentCreator.CreateFromBytes("tiny.bin", data);
        if (meta.TotalLength != 3)
            throw new Exception($"TotalLength: {meta.TotalLength}");
        if (meta.PieceCount != 1)
            throw new Exception($"PieceCount: {meta.PieceCount}");
    }

    [TestMethod]
    public async Task Creator_InfoHash_Valid()
    {
        var data = new byte[16384];
        Random.Shared.NextBytes(data);
        var (_, meta) = TorrentCreator.CreateFromBytes("hash-valid.bin", data);
        if (meta.InfoHash == null || meta.InfoHash.Length != 20)
            throw new Exception("InfoHash should be 20 bytes");
        var hexHash = Convert.ToHexString(meta.InfoHash).ToLowerInvariant();
        if (hexHash.Length != 40)
            throw new Exception($"Hex hash should be 40 chars, got {hexHash.Length}");
        Console.WriteLine($"[Creator] InfoHash: {hexHash}");
    }

    // ═══════════════════════════════════════════════════════════
    //  MemoryChunkStore — Additional Coverage
    // ═══════════════════════════════════════════════════════════

    [TestMethod]
    public async Task MemoryStore_PartialGet()
    {
        await using var store = new MemoryChunkStore(1024);
        var data = new byte[1024];
        for (int i = 0; i < data.Length; i++) data[i] = (byte)(i % 256);
        await store.PutAsync(0, data);

        var partial = await store.GetAsync(0, 512, 256);
        if (partial == null || partial.Length != 256)
            throw new Exception("Partial read failed");
        if (partial[0] != data[512])
            throw new Exception("Partial data offset mismatch");
    }

    [TestMethod]
    public async Task MemoryStore_RemoveChunk()
    {
        await using var store = new MemoryChunkStore(1024);
        await store.PutAsync(0, new byte[1024]);
        await store.RemoveAsync(0);
        var result = await store.GetAsync(0);
        if (result != null)
            throw new Exception("Removed chunk should return null");
    }

    [TestMethod]
    public async Task MemoryStore_GetNonExistent()
    {
        await using var store = new MemoryChunkStore(1024);
        var result = await store.GetAsync(42);
        if (result != null)
            throw new Exception("Non-existent chunk should return null");
    }

    // ═══════════════════════════════════════════════════════════
    //  Helpers
    // ═══════════════════════════════════════════════════════════

    private static string Base32Encode(byte[] data)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var sb = new StringBuilder();
        int buffer = 0, bitsLeft = 0;
        foreach (byte b in data)
        {
            buffer = (buffer << 8) | b;
            bitsLeft += 8;
            while (bitsLeft >= 5)
            {
                bitsLeft -= 5;
                sb.Append(alphabet[(buffer >> bitsLeft) & 0x1F]);
            }
        }
        if (bitsLeft > 0)
            sb.Append(alphabet[(buffer << (5 - bitsLeft)) & 0x1F]);
        return sb.ToString();
    }

    // ═══════════════════════════════════════════════════════════
    //  FileChunkStore — Desktop Storage
    // ═══════════════════════════════════════════════════════════

    [TestMethod]
    public async Task FileChunkStore_WriteReadDelete()
    {
        if (OperatingSystem.IsBrowser())
            throw new UnsupportedTestException("FileChunkStore requires desktop filesystem");

        var tmpDir = Path.Combine(Path.GetTempPath(), $"wt_test_{Guid.NewGuid():N}");
        try
        {
            await using var store = new FileChunkStore(tmpDir, 16384);

            // Put a chunk
            var data = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 };
            await store.PutAsync(0, data);

            // Read it back
            var result = await store.GetAsync(0);
            if (result == null) throw new Exception("Should read back stored chunk");
            if (!result.SequenceEqual(data)) throw new Exception("Data mismatch after read");

            // Partial read
            var partial = await store.GetAsync(0, 2, 3);
            if (partial == null) throw new Exception("Partial read should work");
            if (partial.Length != 3) throw new Exception($"Partial length: {partial.Length}");
            if (partial[0] != 3 || partial[1] != 4 || partial[2] != 5)
                throw new Exception("Partial data mismatch");

            // Read non-existent
            var missing = await store.GetAsync(99);
            if (missing != null) throw new Exception("Non-existent chunk should return null");

            // Remove
            await store.RemoveAsync(0);
            var deleted = await store.GetAsync(0);
            if (deleted != null) throw new Exception("Deleted chunk should return null");

            Console.WriteLine("[FileChunkStore] Write/Read/Delete: OK");
        }
        finally
        {
            if (Directory.Exists(tmpDir)) Directory.Delete(tmpDir, true);
        }
    }

    [TestMethod]
    public async Task FileChunkStore_ClearAll()
    {
        if (OperatingSystem.IsBrowser())
            throw new UnsupportedTestException("FileChunkStore requires desktop filesystem");

        var tmpDir = Path.Combine(Path.GetTempPath(), $"wt_test_{Guid.NewGuid():N}");
        try
        {
            await using var store = new FileChunkStore(tmpDir, 16384);

            await store.PutAsync(0, new byte[] { 1, 2, 3 });
            await store.PutAsync(1, new byte[] { 4, 5, 6 });
            await store.PutAsync(2, new byte[] { 7, 8, 9 });

            await store.ClearAsync();

            var c0 = await store.GetAsync(0);
            var c1 = await store.GetAsync(1);
            var c2 = await store.GetAsync(2);
            if (c0 != null || c1 != null || c2 != null)
                throw new Exception("All chunks should be cleared");

            Console.WriteLine("[FileChunkStore] ClearAll: OK");
        }
        finally
        {
            if (Directory.Exists(tmpDir)) Directory.Delete(tmpDir, true);
        }
    }

    [TestMethod]
    public async Task FileChunkStore_Overwrite()
    {
        if (OperatingSystem.IsBrowser())
            throw new UnsupportedTestException("FileChunkStore requires desktop filesystem");

        var tmpDir = Path.Combine(Path.GetTempPath(), $"wt_test_{Guid.NewGuid():N}");
        try
        {
            await using var store = new FileChunkStore(tmpDir, 16384);

            await store.PutAsync(0, new byte[] { 1, 2, 3 });
            await store.PutAsync(0, new byte[] { 10, 20, 30, 40 }); // overwrite

            var result = await store.GetAsync(0);
            if (result == null) throw new Exception("Should read overwritten chunk");
            if (result.Length != 4) throw new Exception($"Overwritten length: {result.Length}");
            if (result[0] != 10 || result[3] != 40)
                throw new Exception("Overwritten data mismatch");

            Console.WriteLine("[FileChunkStore] Overwrite: OK");
        }
        finally
        {
            if (Directory.Exists(tmpDir)) Directory.Delete(tmpDir, true);
        }
    }

    // ═══════════════════════════════════════════════════════════
    //  MemoryChunkStore — Dedicated Tests
    // ═══════════════════════════════════════════════════════════

    [TestMethod]
    public async Task MemoryChunkStore_WriteReadDelete()
    {
        await using var store = new MemoryChunkStore(16384);

        var data = new byte[] { 10, 20, 30, 40, 50 };
        await store.PutAsync(0, data);

        var result = await store.GetAsync(0);
        if (result == null) throw new Exception("Should read back stored chunk");
        if (!result.SequenceEqual(data)) throw new Exception("Data mismatch");

        // Partial read
        var partial = await store.GetAsync(0, 1, 3);
        if (partial == null) throw new Exception("Partial read should work");
        if (partial.Length != 3) throw new Exception($"Partial length: {partial.Length}");
        if (partial[0] != 20 || partial[1] != 30 || partial[2] != 40)
            throw new Exception("Partial data mismatch");

        // Remove
        await store.RemoveAsync(0);
        var deleted = await store.GetAsync(0);
        if (deleted != null) throw new Exception("Deleted chunk should return null");

        Console.WriteLine("[MemoryChunkStore] Write/Read/Delete: OK");
    }

    // ═══════════════════════════════════════════════════════════
    //  AsyncFSChunkStore — OPFS / Native FS Storage
    // ═══════════════════════════════════════════════════════════

    [TestMethod]
    public async Task AsyncFSChunkStore_WriteReadDelete()
    {
        SpawnDev.AsyncFileSystem.IAsyncFS? fs = null;
        if (OperatingSystem.IsBrowser())
        {
            if (JS == null) throw new UnsupportedTestException("Requires BlazorJSRuntime");
            var opfs = new SpawnDev.AsyncFileSystem.BrowserWASM.AsyncFSFileSystemDirectoryHandle(JS);
            await opfs.Ready;
            fs = opfs;
        }
        else
        {
            throw new UnsupportedTestException("AsyncFSChunkStore OPFS test requires browser");
        }

        var testPath = $"test_chunks_{Guid.NewGuid():N}";
        await using var store = new AsyncFSChunkStore(fs, testPath, 16384);

        // Put a chunk
        var data = new byte[] { 10, 20, 30, 40, 50 };
        await store.PutAsync(0, data);

        // Read it back
        var result = await store.GetAsync(0);
        if (result == null) throw new Exception("Should read back stored chunk");
        if (!result.SequenceEqual(data)) throw new Exception("Data mismatch after read");

        // Partial read
        var partial = await store.GetAsync(0, 1, 3);
        if (partial == null) throw new Exception("Partial read should work");
        if (partial.Length != 3) throw new Exception($"Partial length: {partial.Length}");
        if (partial[0] != 20 || partial[1] != 30 || partial[2] != 40)
            throw new Exception("Partial data mismatch");

        // Read non-existent
        var missing = await store.GetAsync(99);
        if (missing != null) throw new Exception("Non-existent chunk should return null");

        // Remove
        await store.RemoveAsync(0);
        var deleted = await store.GetAsync(0);
        if (deleted != null) throw new Exception("Deleted chunk should return null");

        // Clean up
        await store.ClearAsync();

        Console.WriteLine("[AsyncFSChunkStore] OPFS Write/Read/Delete: OK");
    }

    [TestMethod]
    public async Task AsyncFSChunkStore_ClearAll()
    {
        SpawnDev.AsyncFileSystem.IAsyncFS? fs = null;
        if (OperatingSystem.IsBrowser())
        {
            if (JS == null) throw new UnsupportedTestException("Requires BlazorJSRuntime");
            var opfs = new SpawnDev.AsyncFileSystem.BrowserWASM.AsyncFSFileSystemDirectoryHandle(JS);
            await opfs.Ready;
            fs = opfs;
        }
        else
        {
            throw new UnsupportedTestException("AsyncFSChunkStore OPFS test requires browser");
        }

        var testPath = $"test_chunks_{Guid.NewGuid():N}";
        await using var store = new AsyncFSChunkStore(fs, testPath, 16384);

        await store.PutAsync(0, new byte[] { 1, 2, 3 });
        await store.PutAsync(1, new byte[] { 4, 5, 6 });
        await store.PutAsync(2, new byte[] { 7, 8, 9 });

        await store.ClearAsync();

        var c0 = await store.GetAsync(0);
        var c1 = await store.GetAsync(1);
        var c2 = await store.GetAsync(2);
        if (c0 != null || c1 != null || c2 != null)
            throw new Exception("All chunks should be cleared");

        Console.WriteLine("[AsyncFSChunkStore] OPFS ClearAll: OK");
    }

    // ═══════════════════════════════════════════════════════════
    //  MemoryChunkStore — Dedicated Tests
    // ═══════════════════════════════════════════════════════════

    [TestMethod]
    public async Task MemoryChunkStore_MultipleChunks()
    {
        await using var store = new MemoryChunkStore(16384);

        for (int i = 0; i < 10; i++)
            await store.PutAsync(i, new byte[] { (byte)i, (byte)(i * 2) });

        for (int i = 0; i < 10; i++)
        {
            var result = await store.GetAsync(i);
            if (result == null) throw new Exception($"Chunk {i} missing");
            if (result[0] != (byte)i || result[1] != (byte)(i * 2))
                throw new Exception($"Chunk {i} data mismatch");
        }

        await store.ClearAsync();

        for (int i = 0; i < 10; i++)
        {
            var result = await store.GetAsync(i);
            if (result != null) throw new Exception($"Chunk {i} should be cleared");
        }

        Console.WriteLine("[MemoryChunkStore] Multiple chunks + clear: OK");
    }

    // ═══════════════════════════════════════════════════════════
    //  Random Access Streaming — TorrentFileStream.ReadAsync
    // ═══════════════════════════════════════════════════════════

    [TestMethod]
    public async Task RandomAccess_ReadEntireFile()
    {
        // Create a 64KB file split into 4 x 16KB pieces
        var data = new byte[65536];
        for (int i = 0; i < data.Length; i++) data[i] = (byte)(i % 251); // prime pattern

        var (torrentBytes, metadata) = TorrentCreator.CreateFromBytes("random.bin", data,
            new TorrentCreatorOptions { PieceLength = 16384 });

        await using var store = new MemoryChunkStore(16384);
        var pm = new PieceManager(metadata, store);

        // Store all 4 pieces
        for (int i = 0; i < 4; i++)
        {
            var pieceData = data[(i * 16384)..((i + 1) * 16384)];
            await pm.ReceiveCompletePieceAsync(i, pieceData);
        }

        // Create a TorrentSwarm-like file stream setup
        var file = metadata.Files[0];

        // Read entire file
        var result = new byte[data.Length];
        int offset = 0;
        while (offset < data.Length)
        {
            int pieceIdx = offset / metadata.PieceLength;
            int pieceOffset = offset % metadata.PieceLength;
            int toRead = Math.Min(metadata.PieceLength - pieceOffset, data.Length - offset);
            var piece = await store.GetAsync(pieceIdx, pieceOffset, toRead);
            if (piece == null) throw new Exception($"Piece {pieceIdx} missing");
            System.Array.Copy(piece, 0, result, offset, piece.Length);
            offset += piece.Length;
        }

        if (!result.SequenceEqual(data))
            throw new Exception("Full read data mismatch");

        Console.WriteLine("[RandomAccess] Read entire file: OK");
    }

    [TestMethod]
    public async Task RandomAccess_ReadMiddleRange()
    {
        var data = new byte[65536];
        for (int i = 0; i < data.Length; i++) data[i] = (byte)(i % 251);

        var (_, metadata) = TorrentCreator.CreateFromBytes("random.bin", data,
            new TorrentCreatorOptions { PieceLength = 16384 });

        await using var store = new MemoryChunkStore(16384);
        var pm = new PieceManager(metadata, store);
        for (int i = 0; i < 4; i++)
            await pm.ReceiveCompletePieceAsync(i, data[(i * 16384)..((i + 1) * 16384)]);

        // Read from middle of piece 1 into piece 2 (cross-piece boundary)
        int rangeStart = 20000; // in piece 1 (offset 3616 into piece 1)
        int rangeLen = 10000;   // crosses into piece 2
        var expected = data[rangeStart..(rangeStart + rangeLen)];

        var result = new byte[rangeLen];
        int remaining = rangeLen;
        int resultOffset = 0;
        long readPos = rangeStart;
        while (remaining > 0)
        {
            int pieceIdx = (int)(readPos / metadata.PieceLength);
            int pieceOffset = (int)(readPos % metadata.PieceLength);
            int toRead = Math.Min(metadata.PieceLength - pieceOffset, remaining);
            var chunk = await store.GetAsync(pieceIdx, pieceOffset, toRead);
            if (chunk == null) throw new Exception($"Piece {pieceIdx} missing for range read");
            System.Array.Copy(chunk, 0, result, resultOffset, chunk.Length);
            resultOffset += chunk.Length;
            readPos += chunk.Length;
            remaining -= chunk.Length;
        }

        if (!result.SequenceEqual(expected))
            throw new Exception("Cross-piece range read data mismatch");

        Console.WriteLine("[RandomAccess] Read middle range (cross-piece): OK");
    }

    [TestMethod]
    public async Task RandomAccess_ReadLastByte()
    {
        var data = new byte[65536];
        for (int i = 0; i < data.Length; i++) data[i] = (byte)(i % 251);

        var (_, metadata) = TorrentCreator.CreateFromBytes("random.bin", data,
            new TorrentCreatorOptions { PieceLength = 16384 });

        await using var store = new MemoryChunkStore(16384);
        var pm = new PieceManager(metadata, store);
        for (int i = 0; i < 4; i++)
            await pm.ReceiveCompletePieceAsync(i, data[(i * 16384)..((i + 1) * 16384)]);

        // Read just the last byte
        int lastOffset = data.Length - 1;
        int pieceIdx = lastOffset / metadata.PieceLength;
        int pieceOffset = lastOffset % metadata.PieceLength;
        var chunk = await store.GetAsync(pieceIdx, pieceOffset, 1);
        if (chunk == null || chunk.Length != 1) throw new Exception("Last byte read failed");
        if (chunk[0] != data[lastOffset]) throw new Exception($"Last byte: expected {data[lastOffset]}, got {chunk[0]}");

        Console.WriteLine("[RandomAccess] Read last byte: OK");
    }

    [TestMethod]
    public async Task RandomAccess_ReadFirstByte()
    {
        var data = new byte[65536];
        for (int i = 0; i < data.Length; i++) data[i] = (byte)(i % 251);

        var (_, metadata) = TorrentCreator.CreateFromBytes("random.bin", data,
            new TorrentCreatorOptions { PieceLength = 16384 });

        await using var store = new MemoryChunkStore(16384);
        var pm = new PieceManager(metadata, store);
        for (int i = 0; i < 4; i++)
            await pm.ReceiveCompletePieceAsync(i, data[(i * 16384)..((i + 1) * 16384)]);

        // Read just the first byte
        var chunk = await store.GetAsync(0, 0, 1);
        if (chunk == null || chunk.Length != 1) throw new Exception("First byte read failed");
        if (chunk[0] != data[0]) throw new Exception($"First byte: expected {data[0]}, got {chunk[0]}");

        Console.WriteLine("[RandomAccess] Read first byte: OK");
    }

    [TestMethod]
    public async Task RandomAccess_ReadExactPieceBoundary()
    {
        var data = new byte[65536];
        for (int i = 0; i < data.Length; i++) data[i] = (byte)(i % 251);

        var (_, metadata) = TorrentCreator.CreateFromBytes("random.bin", data,
            new TorrentCreatorOptions { PieceLength = 16384 });

        await using var store = new MemoryChunkStore(16384);
        var pm = new PieceManager(metadata, store);
        for (int i = 0; i < 4; i++)
            await pm.ReceiveCompletePieceAsync(i, data[(i * 16384)..((i + 1) * 16384)]);

        // Read exactly one piece starting at piece boundary
        var chunk = await store.GetAsync(2); // piece 2 = bytes 32768..49151
        if (chunk == null) throw new Exception("Piece 2 read failed");
        var expected = data[32768..49152];
        if (!chunk.SequenceEqual(expected))
            throw new Exception("Exact piece boundary read mismatch");

        Console.WriteLine("[RandomAccess] Read exact piece boundary: OK");
    }

    [TestMethod]
    public async Task RandomAccess_MultipleSmallReads()
    {
        var data = new byte[65536];
        for (int i = 0; i < data.Length; i++) data[i] = (byte)(i % 251);

        var (_, metadata) = TorrentCreator.CreateFromBytes("random.bin", data,
            new TorrentCreatorOptions { PieceLength = 16384 });

        await using var store = new MemoryChunkStore(16384);
        var pm = new PieceManager(metadata, store);
        for (int i = 0; i < 4; i++)
            await pm.ReceiveCompletePieceAsync(i, data[(i * 16384)..((i + 1) * 16384)]);

        // Read 100 bytes at 100 random offsets and verify each
        var rng = new Random(42); // deterministic seed
        for (int trial = 0; trial < 100; trial++)
        {
            int offset = rng.Next(0, data.Length - 100);
            int length = rng.Next(1, 101);
            if (offset + length > data.Length) length = data.Length - offset;

            var expected = data[offset..(offset + length)];

            // Read crossing piece boundaries
            var result = new byte[length];
            int rem = length;
            int rOff = 0;
            long rPos = offset;
            while (rem > 0)
            {
                int pIdx = (int)(rPos / metadata.PieceLength);
                int pOff = (int)(rPos % metadata.PieceLength);
                int toRead = Math.Min(metadata.PieceLength - pOff, rem);
                var c = await store.GetAsync(pIdx, pOff, toRead);
                if (c == null) throw new Exception($"Trial {trial}: piece {pIdx} missing");
                System.Array.Copy(c, 0, result, rOff, c.Length);
                rOff += c.Length;
                rPos += c.Length;
                rem -= c.Length;
            }

            if (!result.SequenceEqual(expected))
                throw new Exception($"Trial {trial}: mismatch at offset {offset} length {length}");
        }

        Console.WriteLine("[RandomAccess] 100 random reads verified: OK");
    }

    [TestMethod]
    public async Task RandomAccess_PartialDownload_ReadDownloadedPiece()
    {
        // 1MB file, 16KB pieces = 64 pieces. Only download pieces 0, 10, 63.
        var data = new byte[1048576];
        for (int i = 0; i < data.Length; i++) data[i] = (byte)((i * 7 + 13) % 251);

        var (_, metadata) = TorrentCreator.CreateFromBytes("large.bin", data,
            new TorrentCreatorOptions { PieceLength = 16384 });

        await using var store = new MemoryChunkStore(16384);
        var pm = new PieceManager(metadata, store);

        // Only store 3 out of 64 pieces
        int[] downloadedPieces = { 0, 10, 63 };
        foreach (var pi in downloadedPieces)
        {
            int start = pi * 16384;
            int end = Math.Min(start + 16384, data.Length);
            await pm.ReceiveCompletePieceAsync(pi, data[start..end]);
        }

        // Read from a downloaded piece (piece 10) — should succeed
        int p10Start = 10 * 16384;
        var chunk = await store.GetAsync(10, 0, 100);
        if (chunk == null) throw new Exception("Downloaded piece 10 should be readable");
        var expected = data[p10Start..(p10Start + 100)];
        if (!chunk.SequenceEqual(expected))
            throw new Exception("Piece 10 data mismatch");

        // Read from an undownloaded piece (piece 5) — should return null
        var missing = await store.GetAsync(5, 0, 100);
        if (missing != null) throw new Exception("Undownloaded piece 5 should return null");

        // Read last piece (piece 63, may be shorter than 16384)
        var lastPiece = await store.GetAsync(63);
        if (lastPiece == null) throw new Exception("Downloaded piece 63 should be readable");
        int lastPieceStart = 63 * 16384;
        int lastPieceLen = data.Length - lastPieceStart;
        var expectedLast = data[lastPieceStart..(lastPieceStart + lastPieceLen)];
        if (!lastPiece.SequenceEqual(expectedLast))
            throw new Exception("Last piece data mismatch");

        // Verify bitfield reflects partial download
        if (!pm.Bitfield[0] || !pm.Bitfield[10] || !pm.Bitfield[63])
            throw new Exception("Downloaded pieces should be in bitfield");
        if (pm.Bitfield[5] || pm.Bitfield[30])
            throw new Exception("Undownloaded pieces should not be in bitfield");
        if (pm.CompletedCount != 3)
            throw new Exception($"Should have 3 completed, got {pm.CompletedCount}");

        Console.WriteLine($"[RandomAccess] Partial download (3/{metadata.PieceCount} pieces): OK");
    }

    [TestMethod]
    public async Task RandomAccess_LargeFile_CrossPieceBoundary()
    {
        // 256KB file, 16KB pieces = 16 pieces. Download all.
        var data = new byte[262144];
        for (int i = 0; i < data.Length; i++) data[i] = (byte)((i * 3 + 7) % 253);

        var (_, metadata) = TorrentCreator.CreateFromBytes("large2.bin", data,
            new TorrentCreatorOptions { PieceLength = 16384 });

        await using var store = new MemoryChunkStore(16384);
        var pm = new PieceManager(metadata, store);
        for (int i = 0; i < metadata.PieceCount; i++)
        {
            int start = i * 16384;
            int end = Math.Min(start + 16384, data.Length);
            await pm.ReceiveCompletePieceAsync(i, data[start..end]);
        }

        // Read 50KB starting at offset 100000 — crosses pieces 6, 7, 8
        int rangeStart = 100000;
        int rangeLen = 51200;
        var expected = data[rangeStart..(rangeStart + rangeLen)];

        var result = new byte[rangeLen];
        int rem = rangeLen;
        int rOff = 0;
        long rPos = rangeStart;
        while (rem > 0)
        {
            int pIdx = (int)(rPos / metadata.PieceLength);
            int pOff = (int)(rPos % metadata.PieceLength);
            int toRead = Math.Min(metadata.PieceLength - pOff, rem);
            var c = await store.GetAsync(pIdx, pOff, toRead);
            if (c == null) throw new Exception($"Piece {pIdx} missing for cross-piece read");
            System.Array.Copy(c, 0, result, rOff, c.Length);
            rOff += c.Length;
            rPos += c.Length;
            rem -= c.Length;
        }

        if (!result.SequenceEqual(expected))
            throw new Exception("Large file cross-piece read mismatch");

        Console.WriteLine($"[RandomAccess] Large file cross-piece (50KB across 3 pieces): OK");
    }

    // ═══════════════════════════════════════════════════════════
    //  Service Worker Streaming — End-to-End
    // ═══════════════════════════════════════════════════════════

    [TestMethod]
    public async Task Persistence_SaveAndRestore()
    {
        if (!OperatingSystem.IsBrowser())
            throw new UnsupportedTestException("Requires browser");
        if (Client == null || AsyncFs == null)
            throw new UnsupportedTestException("Requires DI WebTorrentClient + IAsyncFS");

        // Seed a torrent via the DI singleton
        var data = new byte[16384];
        for (int i = 0; i < data.Length; i++) data[i] = (byte)(i % 199);
        var swarm = await Client.SeedAsync(data, "persist-test.bin",
            new TorrentCreatorOptions { PieceLength = 16384 });
        var hash = swarm.InfoHashHex;

        // Verify state file was saved to OPFS
        var statePath = $"webtorrent/_state/{hash}.torrent";
        var exists = await AsyncFs.FileExists(statePath);
        if (!exists)
            throw new Exception($"State file not saved: {statePath}");

        var stateBytes = await AsyncFs.ReadBytes(statePath);
        if (stateBytes == null || stateBytes.Length == 0)
            throw new Exception("State file is empty");

        // Verify we can parse it back
        var restored = Torrent.TorrentParser.Parse(stateBytes);
        if (restored.InfoHashHex != hash)
            throw new Exception($"Restored hash mismatch: {restored.InfoHashHex} != {hash}");

        // Clean up
        await Client.RemoveAsync(swarm);

        // Verify state file was removed
        var existsAfter = await AsyncFs.FileExists(statePath);
        if (existsAfter)
            throw new Exception("State file should be removed after RemoveAsync");

        // Now save a NEW torrent and verify it would be found by RestoreTorrentsAsync
        var data2 = new byte[16384];
        for (int i = 0; i < data2.Length; i++) data2[i] = (byte)(i % 173);
        var swarm2 = await Client.SeedAsync(data2, "persist-test2.bin",
            new TorrentCreatorOptions { PieceLength = 16384 });
        var hash2 = swarm2.InfoHashHex;

        // Verify state directory has the file
        var files = await AsyncFs.GetFiles("webtorrent/_state");
        var hasFile = files.Any(f => f.Contains(hash2));
        if (!hasFile)
            throw new Exception($"State file for {hash2[..8]} not found in _state directory. Files: {string.Join(", ", files)}");

        await Client.RemoveAsync(swarm2);

        Console.WriteLine($"[Persistence] Save/restore cycle verified: {hash[..8]}...");
    }

    [TestMethod]
    public async Task Persistence_AsyncFsInjected()
    {
        if (!OperatingSystem.IsBrowser())
            throw new UnsupportedTestException("Requires browser");
        if (AsyncFs == null)
            throw new Exception("IAsyncFS not available via DI");

        Console.WriteLine($"[Persistence] IAsyncFS injected: {AsyncFs.GetType().Name}");
    }

    [TestMethod]
    public async Task ServiceWorker_HealthCheck()
    {
        if (!OperatingSystem.IsBrowser())
            throw new UnsupportedTestException("Service worker requires browser");
        if (JS == null) throw new UnsupportedTestException("Requires BlazorJSRuntime");

        using var response = await JS.Fetch("/webtorrent-sw-check");
        if (response.Status != 200)
            throw new Exception($"SW health check failed: status={response.Status}");

        var json = await response.Text();
        if (!json.Contains("SpawnDev.WebTorrent"))
            throw new Exception($"Wrong SW responding: {json}");

        Console.WriteLine($"[SW] Health check: {json}");
    }

    [TestMethod]
    public async Task ServiceWorker_IsRegistered()
    {
        if (!OperatingSystem.IsBrowser())
            throw new UnsupportedTestException("Service worker requires browser");
        if (JS == null) throw new UnsupportedTestException("Requires BlazorJSRuntime");

        using var swContainer = JS.Get<ServiceWorkerContainer>("navigator.serviceWorker");
        if (swContainer == null)
            throw new Exception("navigator.serviceWorker not available");

        using var controller = swContainer.Controller;
        if (controller == null)
            throw new Exception("No service worker controller — SW not active");

        // Check what script URL the controller is using
        var scriptUrl = controller.ScriptURL;
        Console.WriteLine($"[SW] Controller active: state={controller.State}, script={scriptUrl}");

        if (!scriptUrl.Contains("webtorrent-sw.js"))
            throw new Exception($"Wrong service worker controlling the page: {scriptUrl}. Expected webtorrent-sw.js");
    }

    [TestMethod]
    public async Task ServiceWorker_ClientHasStreamHandler()
    {
        if (!OperatingSystem.IsBrowser())
            throw new UnsupportedTestException("Requires browser");
        if (Client == null) throw new UnsupportedTestException("Requires DI WebTorrentClient");

        if (Client.StreamHandler == null)
            throw new Exception("WebTorrentClient.StreamHandler is null — DI not wired");

        Console.WriteLine($"[SW] Client has stream handler: OK");
    }

    [TestMethod]
    public async Task ServiceWorker_InterceptsWebtorrentUrl()
    {
        if (!OperatingSystem.IsBrowser())
            throw new UnsupportedTestException("Service worker requires browser");
        if (JS == null) throw new UnsupportedTestException("Requires BlazorJSRuntime");

        // Fetch a /webtorrent/ URL with a fake hash
        // If SW intercepts: we get our 404 with body "No handler for this request"
        // If SW does NOT intercept: we get a static server 404 with HTML body
        using var response = await JS.Fetch("/webtorrent/0000000000000000000000000000000000000000/0");
        var body = await response.Text();

        if (body.Contains("<!DOCTYPE") || body.Contains("<html"))
            throw new Exception("Got HTML 404 from static server — SW is NOT intercepting");

        // Our handler returns plain text responses
        Console.WriteLine($"[SW] /webtorrent/ intercept: status={response.Status}, body={body}");
    }

    [TestMethod]
    public async Task ServiceWorker_StreamsRealTorrentData()
    {
        if (!OperatingSystem.IsBrowser())
            throw new UnsupportedTestException("Service worker requires browser");
        if (JS == null || Client == null) throw new UnsupportedTestException("Requires BlazorJSRuntime + DI WebTorrentClient");

        // Seed via the DI singleton client so the SW handler can find it
        var data = new byte[65536];
        for (int i = 0; i < data.Length; i++) data[i] = (byte)(i % 251);

        var swarm = await Client.SeedAsync(data, "sw-test.bin",
            new TorrentCreatorOptions { PieceLength = 16384 });

        var hash = Convert.ToHexString(swarm.InfoHash).ToLowerInvariant();
        Console.WriteLine($"[SW Test] Seeded: hash={hash}, hasMetadata={swarm.HasMetadata}, files={swarm.Files?.Length}, store={swarm.Store?.GetType().Name}");
        Console.WriteLine($"[SW Test] Client torrents: {Client.Torrents.Count}, streamHandler={Client.StreamHandler != null}");

        // Fetch the full file via the service worker URL
        Console.WriteLine($"[SW Test] Fetching /webtorrent/{hash}/0 ...");
        using var response = await JS.Fetch($"/webtorrent/{hash}/0");
        var status = response.Status;
        if (status != 200 && status != 206)
            throw new Exception($"Expected 200 or 206, got {status}");

        using var arrayBuffer = await response.ArrayBuffer();
        using var uint8 = new Uint8Array(arrayBuffer);
        var receivedData = uint8.ReadBytes();

        if (receivedData.Length != data.Length)
            throw new Exception($"Expected {data.Length} bytes, got {receivedData.Length}");
        if (!receivedData.SequenceEqual(data))
            throw new Exception("Data mismatch — SW served wrong data");

        // Clean up
        await Client.RemoveAsync(swarm);

        Console.WriteLine($"[SW] Streams real torrent data: {receivedData.Length} bytes, verified");
    }

    [TestMethod]
    public async Task ServiceWorker_RangeRequest()
    {
        if (!OperatingSystem.IsBrowser())
            throw new UnsupportedTestException("Service worker requires browser");
        if (JS == null || Client == null) throw new UnsupportedTestException("Requires BlazorJSRuntime + DI WebTorrentClient");

        // Seed via the DI singleton client
        var data = new byte[65536];
        for (int i = 0; i < data.Length; i++) data[i] = (byte)(i % 251);

        var swarm = await Client.SeedAsync(data, "sw-range-test.bin",
            new TorrentCreatorOptions { PieceLength = 16384 });

        var hash = Convert.ToHexString(swarm.InfoHash).ToLowerInvariant();

        // Fetch with a Range header — should get 206 Partial Content
        using var response = await JS.Fetch($"/webtorrent/{hash}/0", new FetchOptions
        {
            Headers = new Dictionary<string, string> { ["Range"] = "bytes=10000-19999" }
        });

        var status = response.Status;
        if (status != 206)
            throw new Exception($"Expected 206 Partial Content, got {status}");

        using var arrayBuffer = await response.ArrayBuffer();
        using var uint8 = new Uint8Array(arrayBuffer);
        var receivedData = uint8.ReadBytes();

        if (receivedData.Length != 10000)
            throw new Exception($"Expected 10000 bytes, got {receivedData.Length}");

        var expected = data[10000..20000];
        if (!receivedData.SequenceEqual(expected))
            throw new Exception("Range data mismatch");

        // Clean up
        await Client.RemoveAsync(swarm);

        Console.WriteLine($"[SW] Range request: bytes=10000-19999, got {receivedData.Length} bytes, verified");
    }

    // ═══════════════════════════════════════════════════════════
    //  SHA-256 Piece Hashing Tests
    // ═══════════════════════════════════════════════════════════

    [TestMethod]
    public async Task SHA256_CreateTorrent_HasCorrectHashSize()
    {
        var data = new byte[32768];
        for (int i = 0; i < data.Length; i++) data[i] = (byte)(i % 251);

        var (torrentBytes, metadata) = TorrentCreator.CreateFromBytes("sha256-test.bin", data,
            new TorrentCreatorOptions { PieceLength = 16384, HashAlgorithm = "SHA-256" });

        if (metadata.PieceHashes.Length == 0)
            throw new Exception("No piece hashes");
        if (metadata.PieceHashes[0].Length != 32)
            throw new Exception($"Expected 32-byte SHA-256 hash, got {metadata.PieceHashes[0].Length}");
        if (metadata.PieceHashAlgorithm != "SHA-256")
            throw new Exception($"Expected SHA-256, got {metadata.PieceHashAlgorithm}");

        Console.WriteLine($"[SHA-256] Created torrent: {metadata.PieceHashes.Length} pieces, {metadata.PieceHashes[0].Length}-byte hashes, algorithm={metadata.PieceHashAlgorithm}");
    }

    [TestMethod]
    public async Task SHA256_ParseRoundTrip_PreservesHashes()
    {
        var data = new byte[65536];
        for (int i = 0; i < data.Length; i++) data[i] = (byte)(i % 199);

        var (torrentBytes, original) = TorrentCreator.CreateFromBytes("sha256-roundtrip.bin", data,
            new TorrentCreatorOptions { PieceLength = 16384, HashAlgorithm = "SHA-256" });

        // Parse the torrent bytes back
        var parsed = TorrentParser.Parse(torrentBytes);

        if (parsed.PieceHashAlgorithm != "SHA-256")
            throw new Exception($"Parsed algorithm: {parsed.PieceHashAlgorithm}, expected SHA-256");
        if (parsed.PieceHashes.Length != original.PieceHashes.Length)
            throw new Exception($"Piece count mismatch: {parsed.PieceHashes.Length} vs {original.PieceHashes.Length}");

        for (int i = 0; i < parsed.PieceHashes.Length; i++)
        {
            if (!parsed.PieceHashes[i].SequenceEqual(original.PieceHashes[i]))
                throw new Exception($"Hash mismatch at piece {i}");
        }

        Console.WriteLine($"[SHA-256] Round-trip: {parsed.PieceHashes.Length} pieces, all hashes match");
    }

    [TestMethod]
    public async Task SHA256_VerifyPiece_SyncAndAsync()
    {
        var data = new byte[16384];
        for (int i = 0; i < data.Length; i++) data[i] = (byte)(i % 173);

        var (_, metadata) = TorrentCreator.CreateFromBytes("sha256-verify.bin", data,
            new TorrentCreatorOptions { PieceLength = 16384, HashAlgorithm = "SHA-256" });

        // Sync verify
        if (!metadata.VerifyPiece(0, data))
            throw new Exception("Sync SHA-256 verify failed for correct data");

        // Verify with wrong data
        var badData = new byte[16384];
        if (metadata.VerifyPiece(0, badData))
            throw new Exception("Sync SHA-256 verify passed for wrong data");

        Console.WriteLine("[SHA-256] Sync verify: correct=PASS, wrong=REJECT");

        // Async verify (uses IPortableCrypto if available)
        var crypto = Client!.Crypto;
        if (!await metadata.VerifyPieceAsync(0, data, crypto))
            throw new Exception("Async SHA-256 verify failed for correct data");
        if (await metadata.VerifyPieceAsync(0, badData, crypto))
            throw new Exception("Async SHA-256 verify passed for wrong data");

        Console.WriteLine("[SHA-256] Async verify: correct=PASS, wrong=REJECT");
    }

    [TestMethod]
    public async Task SHA256_SeedAndDownload_FullPipeline()
    {
        var data = new byte[65536];
        for (int i = 0; i < data.Length; i++) data[i] = (byte)(i % 211);

        // Create SHA-256 torrent
        var (torrentBytes, metadata) = TorrentCreator.CreateFromBytes("sha256-pipeline.bin", data,
            new TorrentCreatorOptions { PieceLength = 16384, HashAlgorithm = "SHA-256" });

        if (metadata.PieceHashAlgorithm != "SHA-256")
            throw new Exception($"Wrong algorithm: {metadata.PieceHashAlgorithm}");

        // Simulate piece storage and verification
        var store = new MemoryChunkStore(16384);
        var pm = new PieceManager(metadata, store);

        for (int i = 0; i < metadata.PieceCount; i++)
        {
            int offset = i * metadata.PieceLength;
            int len = Math.Min(metadata.PieceLength, data.Length - offset);
            var pieceData = new byte[len];
            System.Array.Copy(data, offset, pieceData, 0, len);

            var ok = await pm.ReceiveCompletePieceAsync(i, pieceData);
            if (!ok)
                throw new Exception($"Piece {i} failed SHA-256 verification");
        }

        if (!pm.IsComplete)
            throw new Exception($"Not complete: {pm.CompletedCount}/{pm.PieceCount}");

        Console.WriteLine($"[SHA-256] Full pipeline: {pm.CompletedCount}/{pm.PieceCount} pieces verified with SHA-256");
    }

    [TestMethod]
    public async Task SHA1_BackwardsCompatible_StillWorks()
    {
        var data = new byte[32768];
        for (int i = 0; i < data.Length; i++) data[i] = (byte)(i % 157);

        // Explicitly use SHA-1 (backwards compat)
        var (torrentBytes, metadata) = TorrentCreator.CreateFromBytes("sha1-compat.bin", data,
            new TorrentCreatorOptions { PieceLength = 16384, HashAlgorithm = "SHA-1" });

        if (metadata.PieceHashes[0].Length != 20)
            throw new Exception($"Expected 20-byte SHA-1 hash, got {metadata.PieceHashes[0].Length}");
        if (metadata.PieceHashAlgorithm != "SHA-1")
            throw new Exception($"Expected SHA-1, got {metadata.PieceHashAlgorithm}");

        // Verify
        if (!metadata.VerifyPiece(0, data.AsSpan(0, 16384).ToArray()))
            throw new Exception("SHA-1 verify failed");

        // Parse round-trip
        var parsed = TorrentParser.Parse(torrentBytes);
        if (parsed.PieceHashAlgorithm != "SHA-1")
            throw new Exception($"Parsed as {parsed.PieceHashAlgorithm}, expected SHA-1");

        Console.WriteLine("[SHA-1] Backwards compatible: create, verify, parse all work");
    }
}
