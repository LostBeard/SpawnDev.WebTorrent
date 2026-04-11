using SpawnDev.UnitTesting;
using SpawnDev.WebTorrent;

namespace SpawnDev.WebTorrent.Demo.Shared;

/// <summary>
/// Tests for JS WebTorrent API parity features — every new property, method,
/// option, and event added for parity.
/// </summary>
public abstract partial class WebTorrentTestBase
{
    // ── Client Aggregate Stats ──

    [TestMethod]
    public async Task Client_DownloadSpeed_Aggregate()
    {
        var client = CreateIsolatedClient();
        await client.SeedAsync("agg1.bin", MakeDeterministicData(16384, seed: 950));
        await client.SeedAsync("agg2.bin", MakeDeterministicData(16384, seed: 951));

        // Aggregate speed should be sum of torrent speeds (both 0 since no peers)
        if (double.IsNaN(client.DownloadSpeed)) throw new Exception("DownloadSpeed is NaN");
        if (client.Torrents.Count != 2) throw new Exception($"Expected 2 torrents, got {client.Torrents.Count}");
        await client.DisposeAsync();
    }

    [TestMethod]
    public async Task Client_UploadSpeed_Aggregate()
    {
        var client = CreateIsolatedClient();
        await client.SeedAsync("upagg.bin", MakeDeterministicData(16384, seed: 952));
        if (double.IsNaN(client.UploadSpeed)) throw new Exception("UploadSpeed is NaN");
        await client.DisposeAsync();
    }

    [TestMethod]
    public async Task Client_Progress_Aggregate()
    {
        var client = CreateIsolatedClient();
        await client.SeedAsync("prog.bin", MakeDeterministicData(16384, seed: 953));
        // Seeded torrent = 100% progress
        if (client.Progress < 0.99) throw new Exception($"Progress should be ~1.0, got {client.Progress}");
        await client.DisposeAsync();
    }

    [TestMethod]
    public async Task Client_Ratio_Aggregate()
    {
        var client = CreateIsolatedClient();
        await client.SeedAsync("ratio.bin", MakeDeterministicData(16384, seed: 954));
        // No uploads yet, ratio should be 0
        if (double.IsNaN(client.Ratio)) throw new Exception("Ratio is NaN");
        await client.DisposeAsync();
    }

    // ── Client Throttle ──

    [TestMethod]
    public async Task Client_ThrottleDownload()
    {
        var client = CreateIsolatedClient();
        client.ThrottleDownload(50000);
        if (client.DownloadRateLimiter.Rate != 50000)
            throw new Exception($"Expected 50000, got {client.DownloadRateLimiter.Rate}");
        client.ThrottleDownload(-1); // unlimited
        if (client.DownloadRateLimiter.Rate != -1)
            throw new Exception("Should be unlimited (-1)");
        await client.DisposeAsync();
    }

    [TestMethod]
    public async Task Client_ThrottleUpload()
    {
        var client = CreateIsolatedClient();
        client.ThrottleUpload(25000);
        if (client.UploadRateLimiter.Rate != 25000)
            throw new Exception($"Expected 25000, got {client.UploadRateLimiter.Rate}");
        await client.DisposeAsync();
    }

    // ── Client Options ──

    [TestMethod]
    public async Task Client_Options_Toggles()
    {
        var client = new WebTorrentClient(new WebTorrentClientOptions
        {
            EnableTrackers = false,
            EnableDht = false,
            EnableLsd = false,
            EnableUtPex = false,
        });
        if (client.EnableTrackers) throw new Exception("Trackers should be disabled");
        if (client.EnableDht) throw new Exception("DHT should be disabled");
        if (client.EnableLsd) throw new Exception("LSD should be disabled");
        if (client.EnableUtPex) throw new Exception("ut_pex should be disabled");
        await client.DisposeAsync();
    }

    [TestMethod]
    public async Task Client_Options_RateLimits()
    {
        var client = new WebTorrentClient(new WebTorrentClientOptions
        {
            DownloadLimit = 100000,
            UploadLimit = 50000,
        });
        if (client.DownloadRateLimiter.Rate != 100000)
            throw new Exception($"DL rate: expected 100000, got {client.DownloadRateLimiter.Rate}");
        if (client.UploadRateLimiter.Rate != 50000)
            throw new Exception($"UL rate: expected 50000, got {client.UploadRateLimiter.Rate}");
        await client.DisposeAsync();
    }

    [TestMethod]
    public async Task Client_Blocklist()
    {
        var client = new WebTorrentClient(new WebTorrentClientOptions
        {
            Blocklist = new HashSet<string> { "10.0.0.1", "192.168.1.100" },
        });
        if (!client.Blocklist.Contains("10.0.0.1")) throw new Exception("Should contain blocked IP");
        if (!client.Blocklist.Contains("192.168.1.100")) throw new Exception("Should contain blocked IP");
        if (client.Blocklist.Contains("8.8.8.8")) throw new Exception("Should not contain unblocked IP");
        await client.DisposeAsync();
    }

    // ── Torrent TimeRemaining ──

    [TestMethod]
    public async Task Torrent_TimeRemaining_DoneIsZero()
    {
        var client = CreateIsolatedClient();
        var torrent = await client.SeedAsync("tr.bin", MakeDeterministicData(16384, seed: 955));
        if (torrent.TimeRemaining != 0)
            throw new Exception($"Done torrent TimeRemaining should be 0, got {torrent.TimeRemaining}");
        await client.DisposeAsync();
    }

    [TestMethod]
    public async Task Torrent_TimeRemaining_NoSpeedIsNegative()
    {
        var client = CreateIsolatedClient();
        var (torrentBytes, _) = TorrentCreator.CreateFromBytes("tr-slow.bin", MakeDeterministicData(32768, seed: 956));
        var torrent = client.Add(torrentBytes, new AddTorrentOptions { Paused = true });
        // Not downloading, no speed => TimeRemaining should be -1
        if (torrent.TimeRemaining != -1)
            throw new Exception($"No-speed TimeRemaining should be -1, got {torrent.TimeRemaining}");
        await client.DisposeAsync();
    }

    // ── Torrent MaxWebConns Configurable ──

    [TestMethod]
    public async Task Torrent_MaxWebConns_Configurable()
    {
        var client = CreateIsolatedClient();
        var (torrentBytes, _) = TorrentCreator.CreateFromBytes("mwc.bin", MakeDeterministicData(16384, seed: 957));
        var torrent = client.Add(torrentBytes, new AddTorrentOptions { MaxWebConns = 8 });
        if (torrent.MaxWebConns != 8)
            throw new Exception($"Expected MaxWebConns=8, got {torrent.MaxWebConns}");
        await client.DisposeAsync();
    }

    // ── Torrent RemovePeer ──

    [TestMethod]
    public async Task Torrent_RemovePeer_ByPeerId()
    {
        var client = CreateIsolatedClient();
        var torrent = await client.SeedAsync("rmpeer.bin", MakeDeterministicData(16384, seed: 958));

        // Verify initial state: no wires/peers
        if (torrent.Wires.Count != 0)
            throw new Exception($"Expected 0 wires, got {torrent.Wires.Count}");
        if (torrent.NumPeers != 0)
            throw new Exception($"Expected 0 peers, got {torrent.NumPeers}");

        // RemovePeer on non-existent peer should not throw
        torrent.RemovePeer("nonexistent-peer-id");
        if (torrent.NumPeers != 0)
            throw new Exception("NumPeers should still be 0 after removing non-existent peer");

        await client.DisposeAsync();
    }

    // ── Torrent RescanFiles ──

    [TestMethod]
    public async Task Torrent_RescanFiles_VerifiesPieces()
    {
        var client = CreateIsolatedClient();
        var data = MakeDeterministicData(32768, seed: 959);
        var torrent = await client.SeedAsync("rescan.bin", data);

        // All pieces should be verified
        if (!torrent.Done) throw new Exception("Should be done before rescan");

        await torrent.RescanFilesAsync();

        // After rescan, should still be done (pieces are valid)
        if (!torrent.Done) throw new Exception("Should still be done after rescan");
        if (torrent.Bitfield.Any(b => !b)) throw new Exception("All pieces should still be verified");

        await client.DisposeAsync();
    }

    // ── Torrent Events Exist ──

    [TestMethod]
    public async Task Torrent_RescanFiles_PreservesState()
    {
        // Verify RescanFilesAsync re-verifies all piece hashes and preserves Done state
        var client = CreateIsolatedClient();
        var data = MakeDeterministicData(32768, seed: 960); // 2 pieces

        var torrent = await client.SeedAsync("rescan.bin", data);

        if (!torrent.Done) throw new Exception("Torrent should be Done after seeding");
        if (torrent.CompletedPieces != torrent.PieceCount)
            throw new Exception($"CompletedPieces ({torrent.CompletedPieces}) != PieceCount ({torrent.PieceCount})");

        // Rescan verifies every piece hash against the store
        await torrent.RescanFilesAsync();

        if (!torrent.Done) throw new Exception("Torrent should still be Done after rescan");
        if (torrent.Bitfield.Any(b => !b)) throw new Exception("All pieces should still be verified after rescan");

        // Verify data is still readable after rescan
        var readBack = await torrent.ReadFileAsync(0);
        if (!readBack.SequenceEqual(data)) throw new Exception("Data should still be readable after rescan");

        await client.DisposeAsync();
    }

    // ── Deselect Option ──

    [TestMethod]
    public async Task Torrent_Deselect_NoPiecesSelected()
    {
        var client = CreateIsolatedClient();
        var (torrentBytes, _) = TorrentCreator.CreateFromBytes("desel.bin", MakeDeterministicData(32768, seed: 961));
        var torrent = client.Add(torrentBytes, new AddTorrentOptions { Deselect = true });

        // Should have metadata but no pieces selected (not downloading)
        if (!torrent.HasMetadata) throw new Exception("Should have metadata");
        if (!torrent.Paused && torrent.Bitfield.Any(b => b))
            throw new Exception("No pieces should be downloaded in deselect mode");

        await client.DisposeAsync();
    }

    // ── File.Size Alias ──

    [TestMethod]
    public async Task File_Size_MatchesLength()
    {
        var client = CreateIsolatedClient();
        var data = MakeDeterministicData(24576, seed: 962);
        var torrent = await client.SeedAsync("size.bin", data);
        var file = torrent.Files![0];

        if (file.Size != file.Length) throw new Exception($"Size ({file.Size}) should equal Length ({file.Length})");
        if (file.Size != 24576) throw new Exception($"Size should be 24576, got {file.Size}");

        await client.DisposeAsync();
    }

    // ── File.CreateReadStream(start, end) Range ──

    [TestMethod]
    public async Task File_CreateReadStream_Range()
    {
        var client = CreateIsolatedClient();
        var data = MakeDeterministicData(32768, seed: 963);
        var torrent = await client.SeedAsync("range-stream.bin", data);

        using var stream = torrent.Files![0].CreateReadStream(100, 599);
        var buffer = new byte[500];
        var read = await stream.ReadAsync(buffer);

        if (read != 500) throw new Exception($"Expected 500, got {read}");
        if (!buffer.SequenceEqual(data[100..600])) throw new Exception("Range stream data mismatch");

        await client.DisposeAsync();
    }

    // ── File.ArrayBufferAsync(start, end) ──

    [TestMethod]
    public async Task File_ArrayBufferAsync_Range()
    {
        var client = CreateIsolatedClient();
        var data = MakeDeterministicData(16384, seed: 964);
        var torrent = await client.SeedAsync("arrbuf.bin", data);

        var slice = await torrent.Files![0].ArrayBufferAsync(200, 499);
        if (slice.Length != 300) throw new Exception($"Expected 300, got {slice.Length}");
        if (!slice.SequenceEqual(data[200..500])) throw new Exception("ArrayBuffer range mismatch");

        await client.DisposeAsync();
    }

    // ── Blob API ──

    [TestMethod]
    public async Task Torrent_TorrentFileBlob_ExistsInBrowser()
    {
        if (!OperatingSystem.IsBrowser())
            throw new UnsupportedTestException("Blob requires browser JS runtime");
        var torrent = await Client.SeedAsync("blob.bin", MakeDeterministicData(16384, seed: 970));
        using var blob = torrent.TorrentFileBlob;
        if (blob == null) throw new Exception("TorrentFileBlob should not be null in browser");
        await Client.RemoveAsync(torrent);
    }

    [TestMethod]
    public async Task Torrent_TorrentFileBlob_NullOnDesktop()
    {
        if (OperatingSystem.IsBrowser())
            throw new UnsupportedTestException("Testing desktop behavior");
        var client = CreateIsolatedClient();
        var torrent = await client.SeedAsync("blob-dt.bin", MakeDeterministicData(16384, seed: 971));
        if (torrent.TorrentFileBlob != null)
            throw new Exception("TorrentFileBlob should be null on desktop (no JS runtime)");
        await client.DisposeAsync();
    }

    [TestMethod]
    public async Task File_BlobAsync_InBrowser()
    {
        if (!OperatingSystem.IsBrowser())
            throw new UnsupportedTestException("Blob requires browser JS runtime");
        var torrent = await Client.SeedAsync("fblob.bin", MakeDeterministicData(16384, seed: 972));
        using var blob = await torrent.Files![0].BlobAsync();
        if (blob == null) throw new Exception("File BlobAsync should not be null");
        if (blob.Size != 16384) throw new Exception($"Blob size wrong: {blob.Size}");
        await Client.RemoveAsync(torrent);
    }

    [TestMethod]
    public async Task File_BlobAsync_NullWhenNotDone()
    {
        var client = CreateIsolatedClient();
        var (torrentBytes, _) = TorrentCreator.CreateFromBytes("blobnd.bin", MakeDeterministicData(16384, seed: 973));
        var torrent = client.Add(torrentBytes, new AddTorrentOptions { Paused = true });
        // File not done — BlobAsync should return null
        var blob = await torrent.Files![0].BlobAsync();
        if (blob != null) throw new Exception("BlobAsync should return null when file not done");
        await client.DisposeAsync();
    }

    // ── File.StreamAsync() IAsyncEnumerable ──

    [TestMethod]
    public async Task File_StreamAsync_FullFile()
    {
        var client = CreateIsolatedClient();
        var data = MakeDeterministicData(16384, seed: 965);
        var torrent = await client.SeedAsync("asyncenum.bin", data);

        var allBytes = new List<byte>();
        await foreach (var chunk in torrent.Files![0].StreamAsync())
        {
            allBytes.AddRange(chunk);
        }

        if (allBytes.Count != data.Length) throw new Exception($"Expected {data.Length}, got {allBytes.Count}");
        if (!allBytes.ToArray().SequenceEqual(data)) throw new Exception("StreamAsync data mismatch");

        await client.DisposeAsync();
    }

    [TestMethod]
    public async Task File_StreamAsync_Range()
    {
        var client = CreateIsolatedClient();
        var data = MakeDeterministicData(32768, seed: 966);
        var torrent = await client.SeedAsync("asyncrange.bin", data);

        var allBytes = new List<byte>();
        await foreach (var chunk in torrent.Files![0].StreamAsync(start: 1000, end: 2999, chunkSize: 512))
        {
            allBytes.AddRange(chunk);
        }

        if (allBytes.Count != 2000) throw new Exception($"Expected 2000, got {allBytes.Count}");
        if (!allBytes.ToArray().SequenceEqual(data[1000..3000])) throw new Exception("StreamAsync range mismatch");

        await client.DisposeAsync();
    }
}
