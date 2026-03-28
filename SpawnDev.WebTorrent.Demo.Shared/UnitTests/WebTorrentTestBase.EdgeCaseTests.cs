using SpawnDev.UnitTesting;
using SpawnDev.WebTorrent.Storage;
using SpawnDev.WebTorrent.Torrent;

namespace SpawnDev.WebTorrent.Demo.Shared.UnitTests;

/// <summary>
/// Edge case and error handling tests.
/// These cover boundary conditions, invalid inputs, and error recovery.
/// </summary>
public abstract partial class WebTorrentTestBase
{
    // ═══════════════════════════════════════════════════════════
    //  Invalid Input Handling
    // ═══════════════════════════════════════════════════════════

    [TestMethod]
    public async Task Edge_InvalidMagnet_Throws()
    {
        await using var client = new WebTorrentClient();
        bool threw = false;
        try { await client.AddAsync("not-a-magnet"); }
        catch (ArgumentException) { threw = true; }
        if (!threw) throw new Exception("Should throw ArgumentException for invalid input");
    }

    [TestMethod]
    public async Task Edge_InvalidInfoHash_Throws()
    {
        await using var client = new WebTorrentClient();
        bool threw = false;
        try { await client.AddAsync("ZZZZZZ"); }
        catch (ArgumentException) { threw = true; }
        if (!threw) throw new Exception("Should throw for invalid hash");
    }

    [TestMethod]
    public async Task Edge_EmptyData_Seed()
    {
        await using var client = new WebTorrentClient();
        var data = new byte[0];
        bool threw = false;
        try { await client.SeedAsync(data, "empty.bin"); }
        catch { threw = true; }
        // Empty data should either work (0 pieces) or throw cleanly
        Console.WriteLine($"[Edge] Empty seed: threw={threw}");
    }

    [TestMethod]
    public async Task Edge_VerySmallData_Seed()
    {
        await using var client = new WebTorrentClient();
        var data = new byte[1]; // 1 byte
        data[0] = 0x42;

        var swarm = await client.SeedAsync(data, "tiny.bin",
            new TorrentCreatorOptions { PieceLength = 16384 });

        if (swarm.Metadata!.TotalLength != 1) throw new Exception($"Size: {swarm.Metadata.TotalLength}");
        if (swarm.PieceManager!.PieceCount != 1) throw new Exception($"Pieces: {swarm.PieceManager.PieceCount}");
        if (!swarm.Done) throw new Exception("Should be done");

        var result = await swarm.Files[0].ReadAsync(0, 1);
        if (result[0] != 0x42) throw new Exception($"Data mismatch: 0x{result[0]:X2}");
    }

    [TestMethod]
    public async Task Edge_LargeData_Seed()
    {
        await using var client = new WebTorrentClient();
        var data = new byte[524288]; // 512KB = 32 pieces at 16KB
        Random.Shared.NextBytes(data);

        var swarm = await client.SeedAsync(data, "large.bin",
            new TorrentCreatorOptions { PieceLength = 16384 });

        if (swarm.PieceManager!.PieceCount != 32) throw new Exception($"Pieces: {swarm.PieceManager.PieceCount}");
        if (!swarm.Done) throw new Exception("Should be done");

        // Random access read near the end
        var chunk = await swarm.Files[0].ReadAsync(500000, 24288);
        for (int i = 0; i < 24288; i++)
            if (chunk[i] != data[500000 + i])
                throw new Exception($"Mismatch at {500000 + i}");
    }

    // ═══════════════════════════════════════════════════════════
    //  PieceManager Edge Cases
    // ═══════════════════════════════════════════════════════════

    [TestMethod]
    public async Task Edge_PieceManager_InvalidIndex()
    {
        var data = new byte[16384];
        var (_, metadata) = TorrentCreator.CreateFromBytes("edge.bin", data,
            new TorrentCreatorOptions { PieceLength = 16384 });

        await using var store = new MemoryChunkStore(16384);
        var pm = new PieceManager(metadata, store);

        // Out of range indices should not crash
        var ok = await pm.ReceiveCompletePieceAsync(-1, new byte[16384]);
        if (ok) throw new Exception("Should return false for -1");

        ok = await pm.ReceiveCompletePieceAsync(999, new byte[16384]);
        if (ok) throw new Exception("Should return false for out-of-range");
    }

    [TestMethod]
    public async Task Edge_PieceManager_WrongHash()
    {
        var data = new byte[16384];
        for (int i = 0; i < data.Length; i++) data[i] = (byte)i;

        var (_, metadata) = TorrentCreator.CreateFromBytes("hash-test.bin", data,
            new TorrentCreatorOptions { PieceLength = 16384 });

        await using var store = new MemoryChunkStore(16384);
        var pm = new PieceManager(metadata, store);

        // Send wrong data (all zeros instead of the pattern)
        var wrongData = new byte[16384];
        var ok = await pm.ReceiveCompletePieceAsync(0, wrongData);
        if (ok) throw new Exception("Should reject piece with wrong hash");
        if (pm.CompletedCount != 0) throw new Exception("Should have 0 completed");
    }

    [TestMethod]
    public async Task Edge_PieceManager_DuplicatePiece()
    {
        var data = new byte[16384];
        Random.Shared.NextBytes(data);

        var (_, metadata) = TorrentCreator.CreateFromBytes("dup.bin", data,
            new TorrentCreatorOptions { PieceLength = 16384 });

        await using var store = new MemoryChunkStore(16384);
        var pm = new PieceManager(metadata, store);

        // First receive should succeed
        var ok = await pm.ReceiveCompletePieceAsync(0, data);
        if (!ok) throw new Exception("First receive should succeed");

        // Second receive of same piece should return true (already complete)
        ok = await pm.ReceiveCompletePieceAsync(0, data);
        if (!ok) throw new Exception("Duplicate should return true (already complete)");
        if (pm.CompletedCount != 1) throw new Exception("Should still be 1 completed");
    }

    // ═══════════════════════════════════════════════════════════
    //  ChunkStore Edge Cases
    // ═══════════════════════════════════════════════════════════

    [TestMethod]
    public async Task Edge_MemoryStore_GetNonexistent()
    {
        await using var store = new MemoryChunkStore(16384);
        var result = await store.GetAsync(999);
        if (result != null) throw new Exception("Should return null for nonexistent chunk");
    }

    [TestMethod]
    public async Task Edge_MemoryStore_RemoveAsync()
    {
        await using var store = new MemoryChunkStore(16384);
        var data = new byte[16384];
        await store.PutAsync(0, data);

        var result = await store.GetAsync(0);
        if (result == null) throw new Exception("Should exist after put");

        await store.RemoveAsync(0);
        result = await store.GetAsync(0);
        if (result != null) throw new Exception("Should be null after remove");
    }

    [TestMethod]
    public async Task Edge_MemoryStore_PartialRead_OutOfBounds()
    {
        await using var store = new MemoryChunkStore(16384);
        var data = new byte[16384];
        await store.PutAsync(0, data);

        // Request beyond chunk bounds
        var result = await store.GetAsync(0, 16000, 1000);
        if (result != null) throw new Exception("Should return null for out-of-bounds partial read");
    }

    // ═══════════════════════════════════════════════════════════
    //  TorrentParser Edge Cases
    // ═══════════════════════════════════════════════════════════

    [TestMethod]
    public async Task Edge_Parser_MagnetWithNoName()
    {
        var magnet = "magnet:?xt=urn:btih:dd8255ecdc7ca55fb0bbf81323d87062db1f6d1c";
        var meta = TorrentParser.ParseMagnet(magnet);

        if (meta.InfoHash.Length != 20) throw new Exception("Should parse hash");
        // Name should be empty or default
        Console.WriteLine($"[Edge] No-name magnet: name='{meta.Name}'");
    }

    [TestMethod]
    public async Task Edge_Parser_MagnetWithMultipleWebSeeds()
    {
        var magnet = "magnet:?xt=urn:btih:dd8255ecdc7ca55fb0bbf81323d87062db1f6d1c"
            + "&ws=https://seed1.example.com/file"
            + "&ws=https://seed2.example.com/file"
            + "&ws=https://seed3.example.com/file";

        var meta = TorrentParser.ParseMagnet(magnet);
        if (meta.UrlList.Length != 3)
            throw new Exception($"Should have 3 web seeds, got {meta.UrlList.Length}");
    }

    [TestMethod]
    public async Task Edge_Parser_MagnetWithFileSelection()
    {
        var magnet = "magnet:?xt=urn:btih:dd8255ecdc7ca55fb0bbf81323d87062db1f6d1c&so=0,1,2,10,20";
        var meta = TorrentParser.ParseMagnet(magnet);

        if (meta.SelectedFileIndices == null || meta.SelectedFileIndices.Length != 5)
            throw new Exception($"Should have 5 selected indices");
        if (meta.SelectedFileIndices[4] != 20)
            throw new Exception($"Last index should be 20, got {meta.SelectedFileIndices[4]}");
    }

    // ═══════════════════════════════════════════════════════════
    //  Bencode Edge Cases
    // ═══════════════════════════════════════════════════════════

    [TestMethod]
    public async Task Edge_Bencode_NegativeInt()
    {
        var encoded = Bencode.BencodeEncoder.EncodeInt(-42);
        if (encoded != "i-42e") throw new Exception($"Expected 'i-42e', got '{encoded}'");

        var data = System.Text.Encoding.UTF8.GetBytes("i-42e");
        var (value, _) = Bencode.BencodeDecoder.DecodeInt(data, 0);
        if (value != -42) throw new Exception($"Expected -42, got {value}");
    }

    [TestMethod]
    public async Task Edge_Bencode_ZeroInt()
    {
        var encoded = Bencode.BencodeEncoder.EncodeInt(0);
        if (encoded != "i0e") throw new Exception($"Expected 'i0e', got '{encoded}'");
    }

    [TestMethod]
    public async Task Edge_Bencode_EmptyString()
    {
        var encoded = Bencode.BencodeEncoder.EncodeString("");
        if (encoded != "0:") throw new Exception($"Expected '0:', got '{encoded}'");

        var data = System.Text.Encoding.UTF8.GetBytes("0:");
        var (value, _) = Bencode.BencodeDecoder.DecodeString(data, 0);
        if (value != "") throw new Exception($"Expected empty string, got '{value}'");
    }

    [TestMethod]
    public async Task Edge_Bencode_LargeInt()
    {
        var big = 9999999999L;
        var encoded = Bencode.BencodeEncoder.EncodeInt(big);
        var data = System.Text.Encoding.UTF8.GetBytes(encoded);
        var (value, _) = Bencode.BencodeDecoder.DecodeInt(data, 0);
        if (value != big) throw new Exception($"Expected {big}, got {value}");
    }

    // ═══════════════════════════════════════════════════════════
    //  Multi-File Torrent
    // ═══════════════════════════════════════════════════════════

    [TestMethod(Timeout = 30000)]
    public async Task Edge_MultiFile_TorrentParsing()
    {
        // Big Buck Bunny is a multi-file torrent
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        byte[] torrentBytes;
        try { torrentBytes = await http.GetByteArrayAsync("https://webtorrent.io/torrents/big-buck-bunny.torrent"); }
        catch (Exception ex) { throw new UnsupportedTestException($"Fetch failed: {ex.Message}"); }

        var metadata = TorrentParser.Parse(torrentBytes);

        if (metadata.Files.Length < 2)
            throw new Exception($"BBB should be multi-file, got {metadata.Files.Length} files");

        // Files should have correct offsets (non-overlapping, sequential)
        long expectedOffset = 0;
        foreach (var file in metadata.Files)
        {
            if (file.Offset != expectedOffset)
                throw new Exception($"File '{file.Path}' offset {file.Offset} != expected {expectedOffset}");
            expectedOffset += file.Length;
        }

        if (expectedOffset != metadata.TotalLength)
            throw new Exception($"Total file lengths {expectedOffset} != TotalLength {metadata.TotalLength}");

        // Each file should have valid piece range
        foreach (var file in metadata.Files)
        {
            if (file.StartPiece < 0 || file.EndPiece >= metadata.PieceCount)
                throw new Exception($"File '{file.Path}' has invalid piece range: {file.StartPiece}-{file.EndPiece}");
            if (file.StartPiece > file.EndPiece)
                throw new Exception($"File '{file.Path}' StartPiece > EndPiece");
        }

        Console.WriteLine($"[Edge] Multi-file: {metadata.Files.Length} files, offsets validated");
    }

    // ═══════════════════════════════════════════════════════════
    //  Client Lifecycle
    // ═══════════════════════════════════════════════════════════

    [TestMethod]
    public async Task Edge_Client_DisposeWhileActive()
    {
        var client = new WebTorrentClient();
        var data = new byte[32768];
        Random.Shared.NextBytes(data);

        await client.SeedAsync(data, "dispose-test.bin",
            new TorrentCreatorOptions { PieceLength = 16384 });

        // Dispose while torrent is active
        await client.DisposeAsync();

        // Should not crash — all resources cleaned up
        Console.WriteLine("[Edge] Dispose while active: no crash");
    }

    [TestMethod]
    public async Task Edge_Client_AddRemoveRapidly()
    {
        await using var client = new WebTorrentClient();

        // Rapidly add and remove torrents
        for (int i = 0; i < 10; i++)
        {
            var data = new byte[16384];
            data[0] = (byte)i;
            var swarm = await client.SeedAsync(data, $"rapid-{i}.bin",
                new TorrentCreatorOptions { PieceLength = 16384 });
            await client.RemoveAsync(swarm);
        }

        if (client.Torrents.Count != 0)
            throw new Exception($"Should have 0 after rapid add/remove, got {client.Torrents.Count}");
    }

    // ═══════════════════════════════════════════════════════════
    //  File Type Detection
    // ═══════════════════════════════════════════════════════════

    [TestMethod]
    public async Task Edge_File_MimeType_AllTypes()
    {
        await using var client = new WebTorrentClient();

        var types = new Dictionary<string, string>
        {
            ["movie.mp4"] = "video/mp4", ["movie.webm"] = "video/webm", ["movie.mkv"] = "video/x-matroska",
            ["movie.avi"] = "video/x-msvideo", ["movie.mov"] = "video/quicktime", ["movie.ogv"] = "video/ogg",
            ["song.mp3"] = "audio/mpeg", ["song.ogg"] = "audio/ogg", ["song.flac"] = "audio/flac",
            ["song.wav"] = "audio/wav", ["song.aac"] = "audio/aac",
            ["pic.jpg"] = "image/jpeg", ["pic.jpeg"] = "image/jpeg", ["pic.png"] = "image/png",
            ["pic.gif"] = "image/gif", ["pic.webp"] = "image/webp", ["pic.svg"] = "image/svg+xml",
            ["doc.pdf"] = "application/pdf", ["doc.txt"] = "text/plain", ["doc.json"] = "application/json",
            ["doc.xml"] = "application/xml", ["doc.html"] = "text/html", ["file.zip"] = "application/zip",
            ["sub.srt"] = "text/plain", ["unknown.xyz"] = "application/octet-stream",
        };

        foreach (var (name, expected) in types)
        {
            var data = new byte[16384];
            var (_, metadata) = TorrentCreator.CreateFromBytes(name, data,
                new TorrentCreatorOptions { PieceLength = 16384 });
            var swarm = await client.AddAsync(metadata);
            if (swarm.Files[0].Type != expected)
                throw new Exception($"{name}: expected '{expected}', got '{swarm.Files[0].Type}'");
        }

        Console.WriteLine($"[Edge] All {types.Count} MIME types correct");
    }
}
