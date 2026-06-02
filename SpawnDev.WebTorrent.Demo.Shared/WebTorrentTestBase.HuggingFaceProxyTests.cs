using System.Net.Http.Json;
using SpawnDev.UnitTesting;
using SpawnDev.WebTorrent;

namespace SpawnDev.WebTorrent.Demo.Shared;

/// <summary>
/// Live integration against the SpawnDev HuggingFace proxy hub (hub.spawndev.com). Proves the
/// production model-delivery path: a browser asks the proxy for a magnet, adds it, and resolves
/// metadata WITHOUT any peer in the swarm — because the magnet carries an HTTP exact-source
/// (<c>xs=</c>) URL to the full .torrent. Before the <c>xs=</c> fetch existed, the first client
/// (zero peers, web-seed-only hub) had no ut_metadata source and stalled forever. These tests
/// guard that bootstrap. Requires internet; the hub fetches the model from HuggingFace on the
/// first request, so the magnet GET can be slow on a cold cache (generous timeout).
/// </summary>
public abstract partial class WebTorrentTestBase
{
    // Public hub running SpawnDev.WebTorrent.Server.HuggingFace.
    const string HubBaseUrl = "https://hub.spawndev.com:44365";

    // A small, real ONNX model file. The proxy resolves it to
    // https://huggingface.co/{repo}/resolve/main/{path} upstream, caches + torrents it.
    const string HfRepoId = "onnx-community/mobilenetv3_small_100.lamb_in1k";
    const string HfFilePath = "onnx/model.onnx";

    private sealed record MagnetResult(string MagnetUri, string RepoId, string FilePath, string WebSeed);

    /// <summary>Ask the proxy for a magnet. Blocks server-side until the file is fetched + torrented.</summary>
    private static async Task<string> GetHubMagnetAsync(HttpClient http)
    {
        // 120s: a cold-cache first request makes the hub download + SHA-256 hash the whole file.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        var url = $"{HubBaseUrl}/magnet/{HfRepoId}/{HfFilePath}";
        var result = await http.GetFromJsonAsync<MagnetResult>(url, cts.Token);
        if (result == null || string.IsNullOrEmpty(result.MagnetUri))
            throw new Exception($"Hub returned no magnet for {HfRepoId}/{HfFilePath} ({url})");
        return result.MagnetUri;
    }

    /// <summary>
    /// Core guard: a hub magnet must resolve metadata peer-free via its HTTP exact-source.
    /// This is the test that FAILS without the xs= bootstrap (no peers + web-seed-only hub).
    /// </summary>
    [TestMethod(Timeout = 240000, RetryCount = 2)]
    public async Task HuggingFaceProxy_Magnet_ResolvesMetadataPeerFree()
    {
        using var http = new HttpClient();
        var magnet = await GetHubMagnetAsync(http);

        var client = new WebTorrentClient();
        try
        {
            var torrent = client.Add(magnet);

            // The magnet must carry an HTTP exact-source — that is what enables peer-free metadata.
            if (string.IsNullOrEmpty(torrent.ExactSourceUrl))
                throw new Exception(
                    "hub magnet has no HTTP xs= exact-source; cannot bootstrap metadata peer-free. " +
                    $"magnet={magnet}");

            // No peers should be needed — metadata comes from the xs= .torrent over HTTP.
            var deadline = DateTime.UtcNow.AddSeconds(45);
            while (!torrent.HasMetadata && DateTime.UtcNow < deadline)
                await Task.Delay(500);

            if (!torrent.HasMetadata)
                throw new Exception(
                    $"no metadata after 45s with zero peers (peers={torrent.NumPeers}). " +
                    "The HTTP xs= exact-source .torrent fetch did not resolve metadata.");

            if (torrent.Files == null || torrent.Files.Length < 1)
                throw new Exception("metadata resolved but Files is empty");

            if (torrent.Files[0].Length <= 0)
                throw new Exception($"file length is {torrent.Files[0].Length}; expected > 0");
        }
        finally
        {
            await client.DisposeAsync();
        }
    }

    /// <summary>
    /// AddAsync (documented in README + Docs/huggingface.md) must return a ready torrent once
    /// metadata resolves, then a range read must pull real bytes from the HTTP web seed.
    /// </summary>
    [TestMethod(Timeout = 240000, RetryCount = 2)]
    public async Task HuggingFaceProxy_AddAsync_ReadsBytesFromWebSeed()
    {
        using var http = new HttpClient();
        var magnet = await GetHubMagnetAsync(http);

        var client = new WebTorrentClient();
        try
        {
            using var addCts = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            var torrent = await client.AddAsync(magnet, ct: addCts.Token);

            if (!torrent.HasMetadata)
                throw new Exception("AddAsync returned but torrent has no metadata");
            if (torrent.Files == null || torrent.Files.Length < 1)
                throw new Exception("AddAsync returned but Files is empty");

            // Read the first 4 KiB — served by the hub web seed (zero peers in the swarm).
            var head = await torrent.Files[0].ReadAsync(0, 4096, addCts.Token);
            if (head == null || head.Length == 0)
                throw new Exception("read 0 bytes from web seed");

            // A real ONNX file is non-zero protobuf at offset 0 — guard against a zero-fill read.
            bool anyNonZero = false;
            foreach (var b in head) { if (b != 0) { anyNonZero = true; break; } }
            if (!anyNonZero)
                throw new Exception($"first {head.Length} bytes were all zero — web-seed read returned a hole");
        }
        finally
        {
            await client.DisposeAsync();
        }
    }
}
