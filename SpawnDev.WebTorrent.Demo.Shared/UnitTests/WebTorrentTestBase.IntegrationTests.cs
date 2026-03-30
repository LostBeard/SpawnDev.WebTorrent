using SpawnDev.UnitTesting;
using SpawnDev.WebTorrent.ModelDelivery;
using SpawnDev.WebTorrent.Torrent;

namespace SpawnDev.WebTorrent.Demo.Shared.UnitTests;

public abstract partial class WebTorrentTestBase
{
    /// <summary>
    /// Detect server URL: use localhost when running locally (PlaywrightMultiTest),
    /// use hub.spawndev.com when running on GitHub Pages or other remote hosts.
    /// </summary>
    private static string GetTestServerUrl()
    {
        // Try localhost first (PlaywrightMultiTest starts ServerApp locally)
        // If not available, use the live production server
        return _useProductionServer ? ProductionServerUrl : LocalServerUrl;
    }

    private const string LocalServerUrl = "https://localhost:5560";
    private const string LocalServerProbeUrl = "http://localhost:5561"; // HTTP probe (cert-free)
    private const string ProductionServerUrl = "https://hub.spawndev.com:44365";
    private static bool _useProductionServer = false;

    private static HttpClient CreateTestHttpClient(int timeoutSeconds = 10)
    {
        var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, _, _, _) => true
        };
        return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(timeoutSeconds) };
    }

    private static async Task<bool> IsServerAvailableAsync()
    {
        // Try localhost first — probe HTTP port (no cert issues), use HTTPS for actual requests
        try
        {
            using var http = CreateTestHttpClient(2);
            var response = await http.GetAsync(LocalServerProbeUrl);
            if (response.IsSuccessStatusCode)
            {
                _useProductionServer = false;
                return true;
            }
        }
        catch { }

        // Fall back to production server
        try
        {
            using var http = CreateTestHttpClient(5);
            var response = await http.GetAsync(ProductionServerUrl);
            if (response.IsSuccessStatusCode)
            {
                _useProductionServer = true;
                return true;
            }
        }
        catch { }

        return false;
    }

    [TestMethod]
    public async Task Integration_ServerInfo_ReturnsJson()
    {
        if (!await IsServerAvailableAsync())
            throw new UnsupportedTestException("Server not running at " + GetTestServerUrl());

        using var http = new HttpClient();
        var response = await http.GetStringAsync(GetTestServerUrl());

        if (!response.Contains("SpawnDev.WebTorrent.Server"))
            throw new Exception($"Unexpected response: {response[..Math.Min(200, response.Length)]}");
    }

    [TestMethod]
    public async Task Integration_Stats_ReturnsSwarmInfo()
    {
        if (!await IsServerAvailableAsync())
            throw new UnsupportedTestException("Server not running at " + GetTestServerUrl());

        using var http = new HttpClient();
        var response = await http.GetStringAsync($"{GetTestServerUrl()}/stats");

        if (!response.Contains("swarms") || !response.Contains("totalPeers"))
            throw new Exception($"Missing fields: {response[..Math.Min(200, response.Length)]}");
    }

    [TestMethod(Timeout = 60000)]
    public async Task Integration_HuggingFace_FetchAndCacheTorrent()
    {
        if (!await IsServerAvailableAsync())
            throw new UnsupportedTestException("Server not running at " + GetTestServerUrl());

        using var http = new HttpClient();
        http.Timeout = TimeSpan.FromSeconds(30);

        var response = await http.GetAsync($"{GetTestServerUrl()}/torrent/Xenova/clip-vit-base-patch32/onnx/text_model.onnx");
        if (!response.IsSuccessStatusCode)
            throw new Exception($"Torrent endpoint returned {response.StatusCode}");

        var torrentBytes = await response.Content.ReadAsByteArrayAsync();
        if (torrentBytes.Length < 100)
            throw new Exception($"Torrent too small: {torrentBytes.Length} bytes");

        var metadata = TorrentParser.Parse(torrentBytes);
        if (metadata.InfoHash.Length != 20) throw new Exception("Bad InfoHash");
        if (metadata.PieceHashes.Length == 0) throw new Exception("No pieces");

        Console.WriteLine($"[Integration] .torrent OK: {metadata.Name}, {metadata.TotalLength:N0} bytes, {metadata.PieceHashes.Length} pieces");
    }

    [TestMethod(Timeout = 60000)]
    public async Task Integration_MagnetUri_ReturnsValid()
    {
        if (!await IsServerAvailableAsync())
            throw new UnsupportedTestException("Server not running at " + GetTestServerUrl());

        using var http = new HttpClient();
        http.Timeout = TimeSpan.FromSeconds(30);

        var response = await http.GetStringAsync($"{GetTestServerUrl()}/magnet/Xenova/clip-vit-base-patch32/onnx/text_model.onnx");
        if (!response.Contains("magnetUri") || !response.Contains("urn:btih:"))
            throw new Exception($"Invalid magnet: {response[..Math.Min(200, response.Length)]}");

        Console.WriteLine($"[Integration] Magnet OK");
    }

    [TestMethod(Timeout = 120000)]
    public async Task Integration_ModelTorrentClient_DownloadSmallFile()
    {
        if (!await IsServerAvailableAsync())
            throw new UnsupportedTestException("Server not running at " + GetTestServerUrl());

        // Download a small config file (< 1KB) to test the full pipeline
        // without triggering range request issues on larger model files
        await using var client = new ModelTorrentClient(new ModelTorrentOptions
        {
            ServerBaseUrl = GetTestServerUrl(),
        });

        var data = await client.DownloadModelAsync(
            "Xenova/clip-vit-base-patch32", "config.json",
            progress: new Progress<double>(p => Console.WriteLine($"[Integration] Download: {p:P0}")));

        if (data.Length == 0) throw new Exception("Downloaded 0 bytes");
        Console.WriteLine($"[Integration] Downloaded {data.Length:N0} bytes via ModelTorrentClient");
    }

    [TestMethod(Timeout = 120000)]
    public async Task Integration_ModelTorrentClient_DownloadLargerFile()
    {
        if (!await IsServerAvailableAsync())
            throw new UnsupportedTestException("Server not running at " + GetTestServerUrl());

        // Download a larger ONNX model file to test multi-piece range requests
        await using var client = new ModelTorrentClient(new ModelTorrentOptions
        {
            ServerBaseUrl = GetTestServerUrl(),
        });

        var data = await client.DownloadModelAsync(
            "Xenova/clip-vit-base-patch32", "onnx/text_model.onnx",
            progress: new Progress<double>(p => Console.WriteLine($"[Integration] Large download: {p:P0}")));

        if (data.Length == 0) throw new Exception("Downloaded 0 bytes");
        if (data.Length < 1000) throw new Exception($"File too small: {data.Length} bytes — expected a model file");
        Console.WriteLine($"[Integration] Downloaded {data.Length:N0} bytes (multi-piece) via ModelTorrentClient");
    }
}
