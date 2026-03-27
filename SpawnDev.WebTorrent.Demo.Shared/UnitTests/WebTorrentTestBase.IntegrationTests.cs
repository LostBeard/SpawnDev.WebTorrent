using SpawnDev.UnitTesting;
using SpawnDev.WebTorrent.ModelDelivery;
using SpawnDev.WebTorrent.Torrent;

namespace SpawnDev.WebTorrent.Demo.Shared.UnitTests;

public abstract partial class WebTorrentTestBase
{
    private const string TestServerUrl = "http://localhost:5561";

    private static async Task<bool> IsServerAvailableAsync()
    {
        try
        {
            using var http = new HttpClient();
            http.Timeout = TimeSpan.FromSeconds(3);
            var response = await http.GetAsync(TestServerUrl);
            return response.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    [TestMethod]
    public async Task Integration_ServerInfo_ReturnsJson()
    {
        if (!await IsServerAvailableAsync())
            throw new UnsupportedTestException("Server not running at " + TestServerUrl);

        using var http = new HttpClient();
        var response = await http.GetStringAsync(TestServerUrl);

        if (!response.Contains("SpawnDev.WebTorrent.Server"))
            throw new Exception($"Unexpected response: {response[..Math.Min(200, response.Length)]}");
    }

    [TestMethod]
    public async Task Integration_Stats_ReturnsSwarmInfo()
    {
        if (!await IsServerAvailableAsync())
            throw new UnsupportedTestException("Server not running at " + TestServerUrl);

        using var http = new HttpClient();
        var response = await http.GetStringAsync($"{TestServerUrl}/stats");

        if (!response.Contains("swarms") || !response.Contains("totalPeers"))
            throw new Exception($"Missing fields: {response[..Math.Min(200, response.Length)]}");
    }

    [TestMethod(Timeout = 60000)]
    public async Task Integration_HuggingFace_FetchAndCacheTorrent()
    {
        if (!await IsServerAvailableAsync())
            throw new UnsupportedTestException("Server not running at " + TestServerUrl);

        using var http = new HttpClient();
        http.Timeout = TimeSpan.FromSeconds(30);

        var response = await http.GetAsync($"{TestServerUrl}/torrent/Xenova/clip-vit-base-patch32/onnx/text_model.onnx");
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
            throw new UnsupportedTestException("Server not running at " + TestServerUrl);

        using var http = new HttpClient();
        http.Timeout = TimeSpan.FromSeconds(30);

        var response = await http.GetStringAsync($"{TestServerUrl}/magnet/Xenova/clip-vit-base-patch32/onnx/text_model.onnx");
        if (!response.Contains("magnetUri") || !response.Contains("urn:btih:"))
            throw new Exception($"Invalid magnet: {response[..Math.Min(200, response.Length)]}");

        Console.WriteLine($"[Integration] Magnet OK");
    }

    [TestMethod(Timeout = 120000)]
    public async Task Integration_ModelTorrentClient_DownloadSmallFile()
    {
        if (!await IsServerAvailableAsync())
            throw new UnsupportedTestException("Server not running at " + TestServerUrl);

        await using var client = new ModelTorrentClient(new ModelTorrentOptions
        {
            ServerBaseUrl = TestServerUrl,
        });

        var data = await client.DownloadModelAsync(
            "Xenova/clip-vit-base-patch32", "onnx/text_model.onnx",
            progress: new Progress<double>(p => Console.WriteLine($"[Integration] Download: {p:P0}")));

        if (data.Length == 0) throw new Exception("Downloaded 0 bytes");
        Console.WriteLine($"[Integration] Downloaded {data.Length:N0} bytes via ModelTorrentClient");
    }
}
