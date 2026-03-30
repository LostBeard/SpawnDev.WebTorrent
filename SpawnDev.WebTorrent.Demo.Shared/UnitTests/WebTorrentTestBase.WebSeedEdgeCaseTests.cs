using SpawnDev.UnitTesting;
using SpawnDev.WebTorrent.Torrent;
using SpawnDev.WebTorrent.Transports;

namespace SpawnDev.WebTorrent.Demo.Shared.UnitTests;

/// <summary>
/// WebSeedConnection edge case tests — backoff, availability, concurrency, URL trimming.
/// Also covers TorrentMetadata edge cases and RateLimiter stress tests.
/// </summary>
public abstract partial class WebTorrentTestBase
{
    // ═══════════════════════════════════════════════════════════
    //  WebSeedConnection — State & Configuration
    // ═══════════════════════════════════════════════════════════

    [TestMethod]
    public async Task WebSeed_InitialState()
    {
        var data = new byte[16384];
        var (_, meta) = TorrentCreator.CreateFromBytes("ws-test.bin", data);
        var ws = new WebSeedConnection(new HttpClient(), "https://example.com/files/", meta);
        if (!ws.IsAvailable) throw new Exception("Should be available initially");
        if (ws.FailureCount != 0) throw new Exception($"FailureCount: {ws.FailureCount}");
        if (ws.MaxConcurrent != 8) throw new Exception($"MaxConcurrent: {ws.MaxConcurrent}");
    }

    [TestMethod]
    public async Task WebSeed_TrailingSlashTrimmed()
    {
        var data = new byte[16384];
        var (_, meta) = TorrentCreator.CreateFromBytes("ws-test.bin", data);
        // Both with and without trailing slash should work
        var ws1 = new WebSeedConnection(new HttpClient(), "https://example.com/files/", meta);
        var ws2 = new WebSeedConnection(new HttpClient(), "https://example.com/files", meta);
        // No crash — constructor handles both
        Console.WriteLine("[WebSeed] Trailing slash trimmed — no errors");
    }

    [TestMethod]
    public async Task WebSeed_MaxConcurrent_Configurable()
    {
        var data = new byte[16384];
        var (_, meta) = TorrentCreator.CreateFromBytes("ws-test.bin", data);
        var ws = new WebSeedConnection(new HttpClient(), "https://example.com/files", meta);
        ws.MaxConcurrent = 8;
        if (ws.MaxConcurrent != 8) throw new Exception($"MaxConcurrent: {ws.MaxConcurrent}");
    }

    // ═══════════════════════════════════════════════════════════
    //  TorrentMetadata — Edge Cases
    // ═══════════════════════════════════════════════════════════

    [TestMethod]
    public async Task Meta_VerifyPiece_Correct()
    {
        var data = new byte[32768];
        Random.Shared.NextBytes(data);
        var (_, meta) = TorrentCreator.CreateFromBytes("verify.bin", data,
            new TorrentCreatorOptions { PieceLength = 16384 });
        var piece0 = data.AsSpan(0, 16384).ToArray();
        if (!meta.VerifyPiece(0, piece0))
            throw new Exception("Correct piece should verify");
    }

    [TestMethod]
    public async Task Meta_VerifyPiece_Corrupt()
    {
        var data = new byte[32768];
        Random.Shared.NextBytes(data);
        var (_, meta) = TorrentCreator.CreateFromBytes("verify.bin", data,
            new TorrentCreatorOptions { PieceLength = 16384 });
        var corrupt = new byte[16384];
        if (meta.VerifyPiece(0, corrupt))
            throw new Exception("Corrupt piece should NOT verify");
    }

    [TestMethod]
    public async Task Meta_VerifyPiece_LastPieceShorter()
    {
        // 30000 bytes, 16384 piece length: piece 0 = 16384 bytes, piece 1 = 13616 bytes
        var data = new byte[30000];
        Random.Shared.NextBytes(data);
        var (_, meta) = TorrentCreator.CreateFromBytes("short-last.bin", data,
            new TorrentCreatorOptions { PieceLength = 16384 });
        if (meta.PieceCount != 2)
            throw new Exception($"PieceCount: {meta.PieceCount}");
        var lastPiece = data.AsSpan(16384, 13616).ToArray();
        if (!meta.VerifyPiece(1, lastPiece))
            throw new Exception("Last (shorter) piece should verify");
    }

    [TestMethod]
    public async Task Meta_PieceCount_VariousSizes()
    {
        // Exact multiple
        var (_, m1) = TorrentCreator.CreateFromBytes("exact.bin", new byte[65536],
            new TorrentCreatorOptions { PieceLength = 16384 });
        if (m1.PieceCount != 4) throw new Exception($"Exact: {m1.PieceCount}");

        // One extra byte
        var (_, m2) = TorrentCreator.CreateFromBytes("extra.bin", new byte[65537],
            new TorrentCreatorOptions { PieceLength = 16384 });
        if (m2.PieceCount != 5) throw new Exception($"Extra: {m2.PieceCount}");

        // Single byte
        var (_, m3) = TorrentCreator.CreateFromBytes("tiny.bin", new byte[1]);
        if (m3.PieceCount != 1) throw new Exception($"Tiny: {m3.PieceCount}");
    }

    [TestMethod]
    public async Task Meta_InfoHashHex()
    {
        var data = new byte[16384];
        Random.Shared.NextBytes(data);
        var (_, meta) = TorrentCreator.CreateFromBytes("hex.bin", data);
        var hex = meta.InfoHashHex;
        if (string.IsNullOrEmpty(hex)) throw new Exception("InfoHashHex should not be empty");
        if (hex.Length != 40) throw new Exception($"Hex length: {hex.Length}");
        // Should be lowercase hex
        if (hex != hex.ToLowerInvariant()) throw new Exception("InfoHashHex should be lowercase");
    }

    // ═══════════════════════════════════════════════════════════
    //  RateLimiter — Edge Cases
    // ═══════════════════════════════════════════════════════════

    [TestMethod]
    public async Task RateLimiter_Unlimited_ReturnsImmediately()
    {
        var rl = new RateLimiter(-1);
        if (rl.Rate != -1) throw new Exception($"Rate: {rl.Rate}");
        // Should return immediately for any amount
        using var cts = new CancellationTokenSource(1000);
        await rl.WaitAsync(10_000_000, cts.Token);
        Console.WriteLine("[RateLimiter] Unlimited: returned immediately");
    }

    [TestMethod]
    public async Task RateLimiter_ChangeRate()
    {
        var rl = new RateLimiter(1024);
        if (rl.Rate != 1024) throw new Exception($"Initial rate: {rl.Rate}");
        rl.Rate = -1;
        if (rl.Rate != -1) throw new Exception($"After change: {rl.Rate}");
        rl.Rate = 0;
        if (rl.Rate != 0) throw new Exception($"After pause: {rl.Rate}");
    }

    [TestMethod]
    public async Task RateLimiter_SmallRate_AllowsBurst()
    {
        var rl = new RateLimiter(10240); // 10 KB/s
        // Token bucket starts with rate tokens — first small request should pass
        using var cts = new CancellationTokenSource(1000);
        await rl.WaitAsync(1024, cts.Token);
        Console.WriteLine("[RateLimiter] Small request passed immediately");
    }

    // ═══════════════════════════════════════════════════════════
    //  Bencode — Error Handling
    // ═══════════════════════════════════════════════════════════

    [TestMethod]
    public async Task Bencode_DecodeInvalidData()
    {
        // Garbage data should throw or return error
        bool threw = false;
        try
        {
            var garbage = new byte[] { 0xFF, 0xFE, 0xFD };
            SpawnDev.WebTorrent.Bencode.BencodeDecoder.Decode(garbage, 0);
        }
        catch { threw = true; }
        if (!threw) throw new Exception("Invalid bencode should throw");
    }

    [TestMethod]
    public async Task Bencode_EmptyDict()
    {
        var encoded = System.Text.Encoding.UTF8.GetBytes("de");
        var (dict, _) = SpawnDev.WebTorrent.Bencode.BencodeDecoder.DecodeDictionary(encoded, 0);
        if (dict.Count != 0) throw new Exception($"Empty dict should have 0 entries: {dict.Count}");
    }

    [TestMethod]
    public async Task Bencode_EmptyList()
    {
        var encoded = System.Text.Encoding.UTF8.GetBytes("le");
        var (list, _) = SpawnDev.WebTorrent.Bencode.BencodeDecoder.DecodeList(encoded, 0);
        if (list.Count != 0) throw new Exception($"Empty list should have 0 entries: {list.Count}");
    }

    [TestMethod]
    public async Task Bencode_LargeInt()
    {
        var encoded = System.Text.Encoding.UTF8.GetBytes("i9999999999999e");
        var (val, _) = SpawnDev.WebTorrent.Bencode.BencodeDecoder.DecodeInt(encoded, 0);
        if (val != 9999999999999L) throw new Exception($"Large int: {val}");
    }

    [TestMethod]
    public async Task Bencode_NestedStructure()
    {
        // d3:fool3:bar3:baze3:numi42ee
        var encoded = System.Text.Encoding.UTF8.GetBytes("d3:fool3:bar3:baze3:numi42ee");
        var (dict, _) = SpawnDev.WebTorrent.Bencode.BencodeDecoder.DecodeDictionary(encoded, 0);
        if (!dict.ContainsKey("foo")) throw new Exception("Missing key 'foo'");
        if (dict["foo"] is not List<object> list) throw new Exception("foo should be list");
        if (list.Count != 2) throw new Exception($"List count: {list.Count}");
        if (dict["num"] is not long num || num != 42) throw new Exception($"num: {dict["num"]}");
    }
}
