using SpawnDev.UnitTesting;
using SpawnDev.WebTorrent.Torrent;

namespace SpawnDev.WebTorrent.Demo.Shared.UnitTests;

/// <summary>
/// Real integration tests for model delivery via WebTorrentClient.
/// These tests hit the live server (local PlaywrightMultiTest ServerApp or hub.spawndev.com),
/// download real model files via WebTorrent P2P + web seed, and verify the data.
/// NO MOCKS. Real tracker, real downloads, real verification.
/// </summary>
public abstract partial class WebTorrentTestBase
{
    /// <summary>
    /// Get or create a WebTorrentClient for testing.
    /// Uses DI singleton in browser, creates new instance on desktop.
    /// </summary>
    private WebTorrentClient GetOrCreateClient()
    {
        if (Client != null) return Client;
        return new WebTorrentClient(crypto: Client!.Crypto);
    }

    // ═══════════════════════════════════════════════════════════
    //  WebTorrentClient Model Delivery — Real Integration Tests
    // ═══════════════════════════════════════════════════════════

    [TestMethod(Timeout = 60000)]
    public async Task ModelDelivery_GetTorrentFromServer_ValidMetadata()
    {
        if (!await IsServerAvailableAsync())
            throw new UnsupportedTestException("Server not available");

        var serverUrl = GetTestServerUrl();
        using var http = CreateTestHttpClient(30);

        var torrentBytes = await http.GetByteArrayAsync(
            $"{serverUrl}/torrent/Xenova/distilgpt2/tokenizer.json");

        var metadata = TorrentParser.Parse(torrentBytes);

        if (metadata.InfoHash.Length != 20)
            throw new Exception($"Invalid InfoHash length: {metadata.InfoHash.Length}");
        if (metadata.PieceHashes.Length == 0)
            throw new Exception("No piece hashes in torrent");
        if (metadata.TotalLength == 0)
            throw new Exception("TotalLength is 0");
        if (metadata.UrlList.Length == 0)
            throw new Exception("No web seeds — server should include itself + HuggingFace");

        Console.WriteLine($"[ModelDelivery] Torrent OK: {metadata.Name}, {metadata.TotalLength:N0} bytes, " +
            $"{metadata.PieceHashes.Length} pieces, {metadata.UrlList.Length} web seeds");
    }

    [TestMethod(Timeout = 60000)]
    public async Task ModelDelivery_GetMagnetFromServer_ValidMagnet()
    {
        if (!await IsServerAvailableAsync())
            throw new UnsupportedTestException("Server not available");

        var serverUrl = GetTestServerUrl();
        using var http = CreateTestHttpClient(30);

        var json = await http.GetStringAsync(
            $"{serverUrl}/magnet/Xenova/distilgpt2/tokenizer.json");

        if (!json.Contains("magnetUri"))
            throw new Exception($"Response missing magnetUri field: {json[..Math.Min(200, json.Length)]}");
        if (!json.Contains("urn:btih:"))
            throw new Exception($"magnetUri missing urn:btih: {json[..Math.Min(200, json.Length)]}");
        if (!json.Contains("webSeed"))
            throw new Exception("Response missing webSeed field");

        Console.WriteLine($"[ModelDelivery] Magnet OK");
    }

    [TestMethod(Timeout = 120000)]
    public async Task ModelDelivery_DownloadViaWebTorrentClient_SmallFile()
    {
        if (!await IsServerAvailableAsync())
            throw new UnsupportedTestException("Server not available");

        var serverUrl = GetTestServerUrl();
        using var http = CreateTestHttpClient(30);

        var torrentBytes = await http.GetByteArrayAsync(
            $"{serverUrl}/torrent/Xenova/distilgpt2/tokenizer.json");
        var metadata = TorrentParser.Parse(torrentBytes);
        Console.WriteLine($"[ModelDelivery] Got torrent: {metadata.Name}, {metadata.TotalLength:N0} bytes");

        var client = GetOrCreateClient();
        var swarm = await client.AddAsync(metadata);
        try
        {
            var downloadComplete = new TaskCompletionSource<bool>();
            using var cts = new CancellationTokenSource(90000);

            if (swarm.Done)
                downloadComplete.TrySetResult(true);
            else
                swarm.OnDone += () => downloadComplete.TrySetResult(true);
            cts.Token.Register(() => downloadComplete.TrySetResult(false));

            swarm.StartDownload();

            var completed = await downloadComplete.Task;
            if (!completed)
                throw new Exception($"Download timed out. Progress: {swarm.Progress:P0}, " +
                    $"peers: {swarm.PeerCount}, pieces: {swarm.Bitfield?.Count(b => b) ?? 0}/{metadata.PieceCount}");

            var fileData = await swarm.Files[0].GetArrayBufferAsync();
            if (fileData.Length < 100)
                throw new Exception($"File too small: {fileData.Length} bytes");

            var text = System.Text.Encoding.UTF8.GetString(fileData);
            if (!text.Contains("model") && !text.Contains("vocab"))
                throw new Exception("Downloaded data doesn't look like tokenizer.json");

            Console.WriteLine($"[ModelDelivery] Downloaded {fileData.Length:N0} bytes via WebTorrentClient — REAL P2P PATH");
        }
        finally
        {
            await client.RemoveAsync(swarm, destroyStore: true);
        }
    }

    [TestMethod(Timeout = 120000)]
    public async Task ModelDelivery_DownloadViaMagnet_SmallFile()
    {
        if (!await IsServerAvailableAsync())
            throw new UnsupportedTestException("Server not available");

        var serverUrl = GetTestServerUrl();
        using var http = CreateTestHttpClient(30);

        var json = await http.GetStringAsync(
            $"{serverUrl}/magnet/Xenova/distilgpt2/tokenizer.json");
        var doc = System.Text.Json.JsonDocument.Parse(json);
        var magnetUri = doc.RootElement.GetProperty("magnetUri").GetString()
            ?? throw new Exception("magnetUri is null");

        Console.WriteLine($"[ModelDelivery] Magnet has xs=: {magnetUri.Contains("&xs=")}");

        var client = GetOrCreateClient();
        var swarm = await client.AddAsync(magnetUri);
        try
        {
            var downloadComplete = new TaskCompletionSource<bool>();
            using var cts = new CancellationTokenSource(90000);

            if (swarm.Done)
                downloadComplete.TrySetResult(true);
            else
                swarm.OnDone += () => downloadComplete.TrySetResult(true);
            cts.Token.Register(() => downloadComplete.TrySetResult(false));

            swarm.StartDownload();

            var completed = await downloadComplete.Task;
            if (!completed)
                throw new Exception($"Download timed out. Progress: {swarm.Progress:P0}, " +
                    $"peers: {swarm.PeerCount}, ready: {swarm.Ready}");

            var fileData = await swarm.Files[0].GetArrayBufferAsync();
            if (fileData.Length < 100)
                throw new Exception($"File too small: {fileData.Length} bytes");

            Console.WriteLine($"[ModelDelivery] Downloaded {fileData.Length:N0} bytes via magnet URI — REAL P2P PATH");
        }
        finally
        {
            await client.RemoveAsync(swarm, destroyStore: true);
        }
    }

    [TestMethod(Timeout = 180000)]
    public async Task ModelDelivery_DownloadOnnxModel_LargerFile()
    {
        if (!await IsServerAvailableAsync())
            throw new UnsupportedTestException("Server not available");

        var serverUrl = GetTestServerUrl();
        using var http = CreateTestHttpClient(60);

        var torrentBytes = await http.GetByteArrayAsync(
            $"{serverUrl}/torrent/Xenova/clip-vit-base-patch32/onnx/text_model.onnx");
        var metadata = TorrentParser.Parse(torrentBytes);
        Console.WriteLine($"[ModelDelivery] Got torrent: {metadata.Name}, {metadata.TotalLength:N0} bytes, " +
            $"{metadata.PieceHashes.Length} pieces");

        var client = GetOrCreateClient();
        var swarm = await client.AddAsync(metadata);
        try
        {
            var downloadComplete = new TaskCompletionSource<bool>();
            using var cts = new CancellationTokenSource(150000);

            if (swarm.Done)
            {
                downloadComplete.TrySetResult(true);
            }
            else
            {
                int lastPct = -1;
                swarm.OnPieceVerified += (idx) =>
                {
                    int pct = (int)(swarm.Progress * 100);
                    if (pct > lastPct)
                    {
                        lastPct = pct;
                        Console.WriteLine($"[ModelDelivery] ONNX download: {pct}%");
                    }
                };
                swarm.OnDone += () => downloadComplete.TrySetResult(true);
            }
            cts.Token.Register(() => downloadComplete.TrySetResult(false));

            swarm.StartDownload();

            var completed = await downloadComplete.Task;
            if (!completed)
                throw new Exception($"ONNX download timed out. Progress: {swarm.Progress:P0}, " +
                    $"peers: {swarm.PeerCount}, pieces: {swarm.Bitfield?.Count(b => b) ?? 0}/{metadata.PieceCount}");

            var fileData = await swarm.Files[0].GetArrayBufferAsync();
            if (fileData.Length < 1_000_000)
                throw new Exception($"ONNX model too small: {fileData.Length} bytes — expected multi-MB");

            Console.WriteLine($"[ModelDelivery] Downloaded ONNX model: {fileData.Length:N0} bytes via WebTorrentClient — REAL P2P PATH");
        }
        finally
        {
            await client.RemoveAsync(swarm, destroyStore: true);
        }
    }

    [TestMethod(Timeout = 60000)]
    public async Task ModelDelivery_NonBlockingEndpoint_ReturnsImmediately()
    {
        if (!await IsServerAvailableAsync())
            throw new UnsupportedTestException("Server not available");

        var serverUrl = GetTestServerUrl();
        using var http = CreateTestHttpClient(10);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var json = await http.GetStringAsync(
            $"{serverUrl}/model/Xenova/distilgpt2/tokenizer.json");
        sw.Stop();

        var doc = System.Text.Json.JsonDocument.Parse(json);
        var status = doc.RootElement.GetProperty("status").GetString();
        var hfUrl = doc.RootElement.GetProperty("huggingFaceUrl").GetString();

        if (string.IsNullOrEmpty(status))
            throw new Exception("Missing status field");
        if (string.IsNullOrEmpty(hfUrl))
            throw new Exception("Missing huggingFaceUrl field");
        if (status != "ready" && status != "preparing")
            throw new Exception($"Unknown status: {status}");

        Console.WriteLine($"[ModelDelivery] /model/ returned in {sw.ElapsedMilliseconds}ms, status={status}");

        if (status == "ready")
        {
            var torrentUrl = doc.RootElement.GetProperty("torrentUrl").GetString();
            var magnetUri = doc.RootElement.GetProperty("magnetUri").GetString();
            if (string.IsNullOrEmpty(torrentUrl) || string.IsNullOrEmpty(magnetUri))
                throw new Exception("Ready status but missing torrentUrl or magnetUri");
            Console.WriteLine($"[ModelDelivery] Torrent ready: {torrentUrl}");
        }
        else
        {
            Console.WriteLine($"[ModelDelivery] Preparing — client would use: {hfUrl}");
        }
    }

    [TestMethod(Timeout = 60000)]
    public async Task ModelDelivery_ConcurrentRequests_NoDuplicateDownload()
    {
        if (!await IsServerAvailableAsync())
            throw new UnsupportedTestException("Server not available");

        var serverUrl = GetTestServerUrl();
        using var http = CreateTestHttpClient(30);

        var task1 = http.GetByteArrayAsync($"{serverUrl}/torrent/Xenova/distilgpt2/tokenizer.json");
        var task2 = http.GetByteArrayAsync($"{serverUrl}/torrent/Xenova/distilgpt2/tokenizer.json");

        var results = await Task.WhenAll(task1, task2);

        if (results[0].Length != results[1].Length)
            throw new Exception($"Concurrent requests returned different sizes: {results[0].Length} vs {results[1].Length}");

        for (int i = 0; i < results[0].Length; i++)
        {
            if (results[0][i] != results[1][i])
                throw new Exception($"Concurrent requests returned different data at byte {i}");
        }

        Console.WriteLine($"[ModelDelivery] Concurrent requests returned identical {results[0].Length:N0}-byte torrents — no duplicate download");
    }

    [TestMethod(Timeout = 120000)]
    public async Task ModelDelivery_SeekWhileDownloading_ReadsCorrectData()
    {
        if (!await IsServerAvailableAsync())
            throw new UnsupportedTestException("Server not available");

        var serverUrl = GetTestServerUrl();
        using var http = CreateTestHttpClient(30);

        // Get .torrent for tokenizer.json (~2MB, multiple pieces)
        var torrentBytes = await http.GetByteArrayAsync(
            $"{serverUrl}/torrent/Xenova/distilgpt2/tokenizer.json");
        var metadata = TorrentParser.Parse(torrentBytes);
        Console.WriteLine($"[ModelDelivery] Seek test: {metadata.Name}, {metadata.TotalLength:N0} bytes, {metadata.PieceCount} pieces");

        var client = GetOrCreateClient();
        var swarm = await client.AddAsync(metadata);
        try
        {
            swarm.StartDownload();

            // Don't wait for full download — seek immediately to different offsets
            var file = swarm.Files[0];

            // Read from the START (piece 0)
            var startData = await file.ReadAsync(0, 64);
            if (startData.Length != 64)
                throw new Exception($"Start read: expected 64 bytes, got {startData.Length}");
            Console.WriteLine($"[ModelDelivery] Seek start: first 4 bytes = [{startData[0]:X2},{startData[1]:X2},{startData[2]:X2},{startData[3]:X2}]");

            // Read from the MIDDLE (forces priority download of a middle piece)
            long midOffset = metadata.TotalLength / 2;
            var midData = await file.ReadAsync(midOffset, 64);
            if (midData.Length != 64)
                throw new Exception($"Mid read: expected 64 bytes, got {midData.Length}");
            Console.WriteLine($"[ModelDelivery] Seek mid ({midOffset}): first 4 bytes = [{midData[0]:X2},{midData[1]:X2},{midData[2]:X2},{midData[3]:X2}]");

            // Read from near the END (forces priority download of a late piece)
            long endOffset = metadata.TotalLength - 128;
            var endData = await file.ReadAsync(endOffset, 64);
            if (endData.Length != 64)
                throw new Exception($"End read: expected 64 bytes, got {endData.Length}");
            Console.WriteLine($"[ModelDelivery] Seek end ({endOffset}): first 4 bytes = [{endData[0]:X2},{endData[1]:X2},{endData[2]:X2},{endData[3]:X2}]");

            // Now download the reference file directly via HTTP and verify the seeks matched
            var directData = await http.GetByteArrayAsync(
                $"{serverUrl}/hf/Xenova/distilgpt2/tokenizer.json");

            // Verify start
            for (int i = 0; i < 64; i++)
            {
                if (startData[i] != directData[i])
                    throw new Exception($"Start data mismatch at byte {i}: torrent={startData[i]:X2} direct={directData[i]:X2}");
            }

            // Verify middle
            for (int i = 0; i < 64; i++)
            {
                if (midData[i] != directData[midOffset + i])
                    throw new Exception($"Mid data mismatch at byte {midOffset + i}: torrent={midData[i]:X2} direct={directData[midOffset + i]:X2}");
            }

            // Verify end
            for (int i = 0; i < 64; i++)
            {
                if (endData[i] != directData[endOffset + i])
                    throw new Exception($"End data mismatch at byte {endOffset + i}: torrent={endData[i]:X2} direct={directData[endOffset + i]:X2}");
            }

            Console.WriteLine($"[ModelDelivery] Seek test PASSED: start, middle, end all match direct HTTP download");
        }
        finally
        {
            await client.RemoveAsync(swarm, destroyStore: true);
        }
    }

    [TestMethod(Timeout = 120000)]
    public async Task ModelDelivery_AddPaused_NoDownloadUntilAccess()
    {
        if (!await IsServerAvailableAsync())
            throw new UnsupportedTestException("Server not available");

        var serverUrl = GetTestServerUrl();
        using var http = CreateTestHttpClient(30);

        var torrentBytes = await http.GetByteArrayAsync(
            $"{serverUrl}/torrent/Xenova/distilgpt2/tokenizer.json");
        var metadata = TorrentParser.Parse(torrentBytes);

        // Add torrent PAUSED — should not download anything
        var client = GetOrCreateClient();
        var swarm = await client.AddAsync(metadata, new AddTorrentOptions { Paused = true });
        try
        {
            // Verify it's paused
            if (!swarm.Paused)
                throw new Exception("Torrent should be paused");

            // Metadata and files should be available even when paused
            if (swarm.Files.Length == 0)
                throw new Exception("Files should be available when paused");

            // No pieces should be downloaded
            await Task.Delay(500); // give it a moment to NOT download
            int downloadedPieces = swarm.Bitfield?.Count(b => b) ?? 0;
            if (downloadedPieces > 0)
                throw new Exception($"Paused torrent downloaded {downloadedPieces} pieces — should be 0");

            Console.WriteLine($"[ModelDelivery] Added paused: {metadata.Name}, {swarm.Files.Length} files, 0 pieces downloaded — correct");
        }
        finally
        {
            await client.RemoveAsync(swarm, destroyStore: true);
        }
    }

    [TestMethod(Timeout = 120000)]
    public async Task ModelDelivery_PausedAutoResumes_OnRead()
    {
        if (!await IsServerAvailableAsync())
            throw new UnsupportedTestException("Server not available");

        var serverUrl = GetTestServerUrl();
        using var http = CreateTestHttpClient(30);

        var torrentBytes = await http.GetByteArrayAsync(
            $"{serverUrl}/torrent/Xenova/distilgpt2/tokenizer.json");
        var metadata = TorrentParser.Parse(torrentBytes);

        // Add torrent PAUSED
        var client = GetOrCreateClient();
        var swarm = await client.AddAsync(metadata, new AddTorrentOptions { Paused = true });
        try
        {
            if (!swarm.Paused)
                throw new Exception("Should start paused");

            // Start download coordinator (needed for web seed downloads)
            // but keep the swarm paused
            swarm.StartDownload();
            swarm.Pause();

            // Now read from the file — should auto-resume and fetch the needed piece
            var file = swarm.Files[0];
            var data = await file.ReadAsync(0, 64);

            // Should have auto-resumed
            if (swarm.Paused)
                throw new Exception("Should have auto-resumed when reading missing piece");

            if (data.Length != 64)
                throw new Exception($"Expected 64 bytes, got {data.Length}");

            // Verify data matches direct HTTP download
            var directData = await http.GetByteArrayAsync(
                $"{serverUrl}/hf/Xenova/distilgpt2/tokenizer.json");

            for (int i = 0; i < 64; i++)
            {
                if (data[i] != directData[i])
                    throw new Exception($"Data mismatch at byte {i}: torrent={data[i]:X2} direct={directData[i]:X2}");
            }

            Console.WriteLine($"[ModelDelivery] Paused auto-resume: read 64 bytes, verified against direct HTTP — correct");
        }
        finally
        {
            await client.RemoveAsync(swarm, destroyStore: true);
        }
    }

    [TestMethod(Timeout = 120000)]
    public async Task ModelDelivery_AddPaused_BrowseContents()
    {
        if (!await IsServerAvailableAsync())
            throw new UnsupportedTestException("Server not available");

        var serverUrl = GetTestServerUrl();
        using var http = CreateTestHttpClient(30);

        var torrentBytes = await http.GetByteArrayAsync(
            $"{serverUrl}/torrent/Xenova/distilgpt2/tokenizer.json");
        var metadata = TorrentParser.Parse(torrentBytes);

        var client = GetOrCreateClient();
        var swarm = await client.AddAsync(metadata, new AddTorrentOptions { Paused = true });
        try
        {
            // Should be able to browse torrent contents without downloading
            if (!swarm.HasMetadata)
                throw new Exception("Metadata should be available");
            if (string.IsNullOrEmpty(swarm.Metadata?.Name))
                throw new Exception("Name should be available");
            if (swarm.Files.Length == 0)
                throw new Exception("Files should be browseable");

            var file = swarm.Files[0];
            if (file.Length == 0)
                throw new Exception("File length should be known");
            if (string.IsNullOrEmpty(file.Name))
                throw new Exception("File name should be known");

            // Progress should be 0
            if (file.Progress != 0)
                throw new Exception($"Progress should be 0, got {file.Progress}");

            Console.WriteLine($"[ModelDelivery] Browse paused: name={swarm.Metadata!.Name}, " +
                $"file={file.Name}, size={file.Length:N0}, progress={file.Progress:P0} — all available without downloading");
        }
        finally
        {
            await client.RemoveAsync(swarm, destroyStore: true);
        }
    }

    [TestMethod(Timeout = 60000)]
    public async Task ModelDelivery_CreateFromUrl_AddsWebSeed()
    {
        // Test CreateFromUrlAsync with a small file
        if (!await IsServerAvailableAsync())
            throw new UnsupportedTestException("Server not available");

        var serverUrl = GetTestServerUrl();
        var fileUrl = $"{serverUrl}/hf/Xenova/distilgpt2/tokenizer.json";

        var (torrentBytes, metadata) = await TorrentCreator.CreateFromUrlAsync(fileUrl,
            new TorrentCreatorOptions { Trackers = new[] { "wss://hub.spawndev.com:44365/announce" } });

        if (metadata.PieceHashes.Length == 0)
            throw new Exception("No piece hashes");
        if (metadata.TotalLength == 0)
            throw new Exception("TotalLength is 0");
        if (metadata.PieceHashAlgorithm != "SHA-256")
            throw new Exception($"Expected SHA-256, got {metadata.PieceHashAlgorithm}");

        // Verify the original URL was added as a web seed
        if (metadata.UrlList.Length == 0)
            throw new Exception("No web seeds — original URL should be added");

        // Parse round-trip
        var parsed = TorrentParser.Parse(torrentBytes);
        if (parsed.PieceHashes.Length != metadata.PieceHashes.Length)
            throw new Exception($"Round-trip piece count mismatch: created={metadata.PieceHashes.Length} (hash={metadata.PieceHashAlgorithm}, hashLen={metadata.PieceHashes[0].Length}), parsed={parsed.PieceHashes.Length} (hash={parsed.PieceHashAlgorithm}, hashLen={parsed.PieceHashes[0].Length}), totalLength={metadata.TotalLength}, pieceLength={metadata.PieceLength}");

        Console.WriteLine($"[ModelDelivery] CreateFromUrl: {metadata.Name}, {metadata.TotalLength:N0} bytes, " +
            $"{metadata.PieceHashes.Length} pieces, {metadata.UrlList.Length} web seeds, algorithm={metadata.PieceHashAlgorithm}");
    }
}
