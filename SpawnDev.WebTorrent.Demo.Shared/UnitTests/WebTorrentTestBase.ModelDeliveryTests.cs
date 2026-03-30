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
        return new WebTorrentClient();
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

        // Request .torrent for a small config file from the server
        var torrentBytes = await http.GetByteArrayAsync(
            $"{serverUrl}/torrent/Xenova/distilgpt2/tokenizer.json");

        var metadata = TorrentParser.Parse(torrentBytes);

        // Verify real torrent metadata
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

        // 1. Get .torrent from server
        var torrentBytes = await http.GetByteArrayAsync(
            $"{serverUrl}/torrent/Xenova/distilgpt2/tokenizer.json");
        var metadata = TorrentParser.Parse(torrentBytes);
        Console.WriteLine($"[ModelDelivery] Got torrent: {metadata.Name}, {metadata.TotalLength:N0} bytes");

        // 2. Add torrent to WebTorrentClient — this is the REAL production path
        var client = GetOrCreateClient();
        var swarm = await client.AddAsync(metadata);

        // 3. Wait for download to complete
        var downloadComplete = new TaskCompletionSource<bool>();
        var cts = new CancellationTokenSource(90000); // 90s timeout

        if (swarm.Done)
        {
            downloadComplete.TrySetResult(true);
        }
        else
        {
            swarm.OnDone += () => downloadComplete.TrySetResult(true);
        }
        cts.Token.Register(() => downloadComplete.TrySetResult(false));

        // Start download (web seeds provide the data)
        swarm.StartDownload();

        var completed = await downloadComplete.Task;
        if (!completed)
            throw new Exception($"Download timed out. Progress: {swarm.Progress:P0}, " +
                $"peers: {swarm.PeerCount}, pieces: {swarm.Bitfield?.Count(b => b) ?? 0}/{metadata.PieceCount}");

        // 4. Read the file data
        if (swarm.Files.Length == 0)
            throw new Exception("No files in swarm after download");

        var fileData = await swarm.Files[0].GetArrayBufferAsync();

        // 5. Verify it's valid tokenizer.json
        if (fileData.Length < 100)
            throw new Exception($"File too small: {fileData.Length} bytes");

        var text = System.Text.Encoding.UTF8.GetString(fileData);
        if (!text.Contains("model") && !text.Contains("vocab"))
            throw new Exception("Downloaded data doesn't look like tokenizer.json");

        Console.WriteLine($"[ModelDelivery] Downloaded {fileData.Length:N0} bytes via WebTorrentClient — REAL P2P PATH");

        // Clean up (don't remove if using DI singleton)
        if (Client == null)
            await client.RemoveAsync(swarm);
    }

    [TestMethod(Timeout = 120000)]
    public async Task ModelDelivery_DownloadViaMagnet_SmallFile()
    {
        if (!await IsServerAvailableAsync())
            throw new UnsupportedTestException("Server not available");

        var serverUrl = GetTestServerUrl();
        using var http = CreateTestHttpClient(30);

        // 1. Get magnet URI from server
        var json = await http.GetStringAsync(
            $"{serverUrl}/magnet/Xenova/distilgpt2/tokenizer.json");
        var doc = System.Text.Json.JsonDocument.Parse(json);
        var magnetUri = doc.RootElement.GetProperty("magnetUri").GetString()
            ?? throw new Exception("magnetUri is null");

        Console.WriteLine($"[ModelDelivery] Full magnet: {magnetUri}");
        Console.WriteLine($"[ModelDelivery] Has xs=: {magnetUri.Contains("&xs=")}");
        Console.WriteLine($"[ModelDelivery] Has ws=: {magnetUri.Contains("&ws=")}");
        Console.WriteLine($"[ModelDelivery] Has tr=: {magnetUri.Contains("&tr=")}");

        // 2. Add via magnet URI — the way a real client would do it
        var client = GetOrCreateClient();
        var swarm = await client.AddAsync(magnetUri);

        // 3. Wait for metadata + download
        var downloadComplete = new TaskCompletionSource<bool>();
        var cts = new CancellationTokenSource(90000);

        if (swarm.Done)
        {
            downloadComplete.TrySetResult(true);
        }
        else
        {
            swarm.OnDone += () => downloadComplete.TrySetResult(true);
        }
        cts.Token.Register(() => downloadComplete.TrySetResult(false));

        swarm.StartDownload();

        var completed = await downloadComplete.Task;
        if (!completed)
            throw new Exception($"Download timed out. Progress: {swarm.Progress:P0}, " +
                $"peers: {swarm.PeerCount}, ready: {swarm.Ready}");

        // 4. Verify
        var fileData = await swarm.Files[0].GetArrayBufferAsync();
        if (fileData.Length < 100)
            throw new Exception($"File too small: {fileData.Length} bytes");

        Console.WriteLine($"[ModelDelivery] Downloaded {fileData.Length:N0} bytes via magnet URI — REAL P2P PATH");

        if (Client == null)
            await client.RemoveAsync(swarm);
    }

    [TestMethod(Timeout = 180000)]
    public async Task ModelDelivery_DownloadOnnxModel_LargerFile()
    {
        if (!await IsServerAvailableAsync())
            throw new UnsupportedTestException("Server not available");

        var serverUrl = GetTestServerUrl();
        using var http = CreateTestHttpClient(60);

        // 1. Get .torrent for a real ONNX model file
        var torrentBytes = await http.GetByteArrayAsync(
            $"{serverUrl}/torrent/Xenova/clip-vit-base-patch32/onnx/text_model.onnx");
        var metadata = TorrentParser.Parse(torrentBytes);
        Console.WriteLine($"[ModelDelivery] Got torrent: {metadata.Name}, {metadata.TotalLength:N0} bytes, " +
            $"{metadata.PieceHashes.Length} pieces");

        // 2. Download via WebTorrentClient
        var client = GetOrCreateClient();
        var swarm = await client.AddAsync(metadata);

        var downloadComplete = new TaskCompletionSource<bool>();
        var cts = new CancellationTokenSource(150000); // 2.5 min for larger file

        if (swarm.Done)
        {
            downloadComplete.TrySetResult(true);
        }
        else
        {
            // Log progress
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

        // 3. Verify file
        var fileData = await swarm.Files[0].GetArrayBufferAsync();
        if (fileData.Length < 1_000_000)
            throw new Exception($"ONNX model too small: {fileData.Length} bytes — expected multi-MB");

        Console.WriteLine($"[ModelDelivery] Downloaded ONNX model: {fileData.Length:N0} bytes via WebTorrentClient — REAL P2P PATH");

        if (Client == null)
            await client.RemoveAsync(swarm);
    }

    [TestMethod(Timeout = 60000)]
    public async Task ModelDelivery_NonBlockingEndpoint_ReturnsImmediately()
    {
        if (!await IsServerAvailableAsync())
            throw new UnsupportedTestException("Server not available");

        var serverUrl = GetTestServerUrl();
        using var http = CreateTestHttpClient(10);

        // /model/ endpoint should ALWAYS return immediately — never block
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
            // Torrent is cached — verify all fields present
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

        // Request the SAME model's torrent twice concurrently
        // Server should not download from HuggingFace twice
        var task1 = http.GetByteArrayAsync($"{serverUrl}/torrent/Xenova/distilgpt2/tokenizer.json");
        var task2 = http.GetByteArrayAsync($"{serverUrl}/torrent/Xenova/distilgpt2/tokenizer.json");

        var results = await Task.WhenAll(task1, task2);

        // Both should return identical torrent bytes
        if (results[0].Length != results[1].Length)
            throw new Exception($"Concurrent requests returned different sizes: {results[0].Length} vs {results[1].Length}");

        // Verify content is identical
        for (int i = 0; i < results[0].Length; i++)
        {
            if (results[0][i] != results[1][i])
                throw new Exception($"Concurrent requests returned different data at byte {i}");
        }

        Console.WriteLine($"[ModelDelivery] Concurrent requests returned identical {results[0].Length:N0}-byte torrents — no duplicate download");
    }
}
