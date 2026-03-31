using SpawnDev.UnitTesting;
using SpawnDev.WebTorrent.Discovery;
using SpawnDev.WebTorrent.ModelDelivery;
using SpawnDev.WebTorrent.Storage;
using SpawnDev.WebTorrent.Torrent;

namespace SpawnDev.WebTorrent.Demo.Shared.UnitTests;

/// <summary>
/// Server integration tests. These test the full client-server flow:
/// tracker announcements, web seed downloads, torrent creation.
///
/// Note: Tests that require a running ServerApp are marked with
/// longer timeouts and will skip gracefully if the server is unavailable.
/// </summary>
public abstract partial class WebTorrentTestBase
{
    // ═══════════════════════════════════════════════════════════
    //  Server-Side Tests (run without network)
    // ═══════════════════════════════════════════════════════════

    // Server-specific tests (TorrentTracker, WebSeedServer) require the
    // SpawnDev.WebTorrent.Server reference which isn't available in browser.
    // Those tests run in the ServerApp console project, not in Blazor WASM.

    [TestMethod]
    public async Task Server_TorrentCreator_EndToEnd()
    {
        // Create data, create torrent, parse it, verify integrity
        var data = new byte[65536]; // 64KB
        for (int i = 0; i < data.Length; i++) data[i] = (byte)((i * 13 + 7) % 256);

        var (torrentBytes, created) = TorrentCreator.CreateFromBytes("server-test.bin", data,
            new TorrentCreatorOptions
            {
                PieceLength = 16384,
                Trackers = new[] { "wss://tracker.test.com" },
                WebSeeds = new[] { "https://cdn.test.com/server-test.bin" },
                Comment = "Server integration test",
            });

        // Parse the .torrent
        var parsed = TorrentParser.Parse(torrentBytes);

        // Verify info hashes match
        if (!parsed.InfoHash.SequenceEqual(created.InfoHash))
            throw new Exception("InfoHash mismatch");

        // Verify all piece hashes match
        if (parsed.PieceHashes.Length != created.PieceHashes.Length)
            throw new Exception($"Piece count mismatch: {parsed.PieceHashes.Length} vs {created.PieceHashes.Length}");

        for (int i = 0; i < parsed.PieceHashes.Length; i++)
            if (!parsed.PieceHashes[i].SequenceEqual(created.PieceHashes[i]))
                throw new Exception($"Piece hash mismatch at {i}");

        // Verify each piece against the data
        await using var store = new MemoryChunkStore(16384);
        var pm = new PieceManager(parsed, store);
        int pieces = (data.Length + 16383) / 16384;
        for (int i = 0; i < pieces; i++)
        {
            int pieceLen = Math.Min(16384, data.Length - i * 16384);
            var piece = new byte[pieceLen];
            Array.Copy(data, i * 16384, piece, 0, pieceLen);
            pm.GetNextBlock(i);
            bool ok = await pm.ReceiveBlockAsync(i, 0, piece);
            if (!ok) throw new Exception($"Piece {i} verification failed");
        }

        if (!pm.IsComplete)
            throw new Exception("All pieces received but not marked complete");

        // Verify web seeds are in the parsed metadata
        if (parsed.UrlList.Length == 0)
            throw new Exception("Web seeds not in parsed .torrent");
    }

    // ═══════════════════════════════════════════════════════════
    //  WebSocket Tracker Client Tests (unit-level)
    // ═══════════════════════════════════════════════════════════

    [TestMethod]
    public async Task TrackerClient_CreateWithPeerId()
    {
        var peerId = new byte[20];
        "-SD0210-"u8.CopyTo(peerId);
        Random.Shared.NextBytes(peerId.AsSpan(8));

        var client = new WebSocketTrackerClient("wss://tracker.example.com/announce", peerId);

        if (client.Type != "ws-tracker")
            throw new Exception($"Expected type 'ws-tracker', got '{client.Type}'");
        if (client.IsConnected)
            throw new Exception("Should not be connected before StartAsync");

        await client.DisposeAsync();
    }

    // ═══════════════════════════════════════════════════════════
    //  Bencode Round-Trip Stress Tests
    // ═══════════════════════════════════════════════════════════

    [TestMethod]
    public async Task Bencode_LargeTorrent_RoundTrip()
    {
        // Create a larger torrent (256KB, 16 pieces) and verify full round-trip
        var data = new byte[262144];
        Random.Shared.NextBytes(data);

        var (torrentBytes, original) = TorrentCreator.CreateFromBytes("large.bin", data,
            new TorrentCreatorOptions
            {
                PieceLength = 16384,
                Trackers = new[]
                {
                    "wss://tracker1.example.com",
                    "wss://tracker2.example.com",
                },
                WebSeeds = new[]
                {
                    "https://seed1.example.com/large.bin",
                    "https://seed2.example.com/large.bin",
                },
                Comment = "Large torrent stress test",
            });

        var parsed = TorrentParser.Parse(torrentBytes);

        if (!parsed.InfoHash.SequenceEqual(original.InfoHash))
            throw new Exception("Large torrent InfoHash mismatch");
        if (parsed.PieceHashes.Length != 16)
            throw new Exception($"Expected 16 pieces, got {parsed.PieceHashes.Length}");
        if (parsed.Name != "large.bin")
            throw new Exception($"Name mismatch: '{parsed.Name}'");
        if (parsed.TotalLength != 262144)
            throw new Exception($"TotalLength mismatch: {parsed.TotalLength}");
    }

    // ═══════════════════════════════════════════════════════════
    //  Magnet URI Tests
    // ═══════════════════════════════════════════════════════════

    [TestMethod]
    public async Task Magnet_Base32InfoHash()
    {
        // Base32-encoded info hash (used by some older magnet links)
        // "JBSWY3DPEHPK3PXP" is Base32 for "Hello!" (6 bytes) — not a real 20-byte hash,
        // but tests the Base32 decoder
        var magnet = "magnet:?xt=urn:btih:JBSWY3DPEHPK3PXPJBSWY3DPEHPK3PXP&dn=test";
        var meta = TorrentParser.ParseMagnet(magnet);

        if (meta.InfoHash.Length != 20)
            throw new Exception($"Base32 info hash should decode to 20 bytes, got {meta.InfoHash.Length}");
        if (meta.Name != "test")
            throw new Exception($"Name should be 'test', got '{meta.Name}'");
    }

    [TestMethod]
    public async Task Magnet_MultipleTrackers()
    {
        var magnet = "magnet:?xt=urn:btih:d2474e86c95b19b8bcfdb92bc12c9d44667ce52e"
            + "&tr=wss://tracker1.example.com"
            + "&tr=wss://tracker2.example.com"
            + "&tr=wss://tracker3.example.com";

        var meta = TorrentParser.ParseMagnet(magnet);

        if (meta.AnnounceList.Length != 3)
            throw new Exception($"Expected 3 trackers, got {meta.AnnounceList.Length}");
    }

    // ═══════════════════════════════════════════════════════════
    //  FileChunkStore Tests (desktop only)
    // ═══════════════════════════════════════════════════════════

    [TestMethod]
    public async Task FileChunkStore_PutGetRoundTrip()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"wt_fcs_{Guid.NewGuid():N}");
        try
        {
            await using var store = new Storage.FileChunkStore(tempDir, 16384);

            var data = new byte[16384];
            for (int i = 0; i < data.Length; i++) data[i] = (byte)(i % 256);

            await store.PutAsync(0, data);
            var result = await store.GetAsync(0);

            if (result == null) throw new Exception("GetAsync returned null");
            if (!result.SequenceEqual(data))
                throw new Exception("FileChunkStore round-trip data mismatch");

            // Partial read
            var partial = await store.GetAsync(0, 100, 50);
            if (partial == null || partial.Length != 50)
                throw new Exception("Partial read failed");
            for (int i = 0; i < 50; i++)
                if (partial[i] != (byte)((100 + i) % 256))
                    throw new Exception($"Partial read mismatch at {i}");

            // Clear
            await store.ClearAsync();
            var cleared = await store.GetAsync(0);
            if (cleared != null)
                throw new Exception("Data should be cleared");
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }
}
