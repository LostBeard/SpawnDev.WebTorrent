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

        // Verify first piece hash matches SHA1 of first 16384 bytes
        var expectedHash = SHA1.HashData(data.AsSpan(0, 16384));
        if (!parsed.PieceHashes[0].SequenceEqual(expectedHash))
            throw new Exception("First piece hash mismatch");
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
                Array.Fill(data, (byte)i);
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
}
