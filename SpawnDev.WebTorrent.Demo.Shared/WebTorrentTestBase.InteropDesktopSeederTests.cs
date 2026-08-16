using SpawnDev.SpawnJS;
using SpawnDev.UnitTesting;

namespace SpawnDev.WebTorrent.Demo.Shared;

/// <summary>
/// Cross-platform P2P download proof: a REAL browser (SpawnJS RTCPeerConnection) leeches a full file
/// from a REAL desktop (SipSorcery) seeder over WebRTC, then byte-verifies it. The PMT GlobalSetup runs
/// a desktop <c>WebTorrentClient</c> seeding deterministic 32 KiB data via the hub.spawndev.com tracker
/// and writes the magnet to <c>wwwroot/_test-desktop-seeder.json</c>; this test consumes it. Exercises
/// the SAME shared code the desktop two-client test proves — ut_metadata (BEP 9) fetch, the WebRTC wire,
/// piece store, RarityMap — but across the desktop↔browser boundary, which is the product's whole point.
/// Browser-only (it reads the served wwwroot config and needs a browser RTCPeerConnection).
/// </summary>
public abstract partial class WebTorrentTestBase
{
    [TestMethod(Timeout = 150000, RetryCount = 2)]
    public async Task Interop_DesktopSeeder_BrowserDownloadsAndVerifies()
    {
        if (!OperatingSystem.IsBrowser())
            throw new UnsupportedTestException("Browser↔desktop-seeder P2P download is browser-only (reads the served wwwroot config + needs a browser RTCPeerConnection)");

        // The GlobalSetup desktop seeder writes its magnet here. Deterministic 32 KiB payload.
        var origin = SpawnJSRuntime.Instance.Get<string>("location.origin")
                     ?? throw new Exception("could not read location.origin");
        using var http = new HttpClient { BaseAddress = new Uri(origin) };

        string configJson;
        try { configJson = await http.GetStringAsync("_test-desktop-seeder.json"); }
        catch (Exception ex) { throw new Exception($"desktop seeder config not served (GlobalSetup seeder not running?): {ex.Message}"); }

        var config = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, System.Text.Json.JsonElement>>(configJson);
        if (config == null || !config.TryGetValue("magnetUri", out var magnetEl) || magnetEl.GetString() is not string magnet || string.IsNullOrEmpty(magnet))
            throw new Exception($"desktop seeder config missing magnetUri: {configJson}");
        int dataLength = config.TryGetValue("dataLength", out var dlEl) ? dlEl.GetInt32() : 32768;
        var infoHash = config.TryGetValue("infoHash", out var ihEl) ? ihEl.GetString() : null;

        // Force a REAL fresh P2P transfer. The PMT Chromium profile is persistent, so a prior run's
        // OPFS copy of this (deterministic, fixed-infohash) torrent would restore as Done — the test
        // would "pass" without downloading a single byte from the seeder. Clear any persisted copy.
        if (!string.IsNullOrEmpty(infoHash))
        {
            var stale = Client.Get(infoHash);
            if (stale != null) await Client.RemoveWithDataAsync(stale);
        }

        // Browser downloader uses the shared DI client (OPFS-backed in the browser).
        using var addCts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var torrent = await Client.AddAsync(magnet, ct: addCts.Token);
        try
        {
            if (!torrent.HasMetadata)
                throw new Exception($"browser never received metadata from the desktop seeder (ut_metadata over WebRTC failed): peers={torrent.PeerCount}");

            // Wait for the FULL download over cross-platform WebRTC.
            var deadline = DateTime.UtcNow.AddSeconds(75);
            while (!torrent.Done && DateTime.UtcNow < deadline)
                await Task.Delay(250);

            if (!torrent.Done)
                throw new Exception(
                    $"browser did not complete the WebRTC download from the desktop seeder: progress={torrent.Progress:P1}, " +
                    $"downloaded={torrent.Downloaded}/{dataLength}, peers={torrent.PeerCount}, hasMeta={torrent.HasMetadata}");

            // Byte-verify against the seeder's deterministic data: TestData[i] = (i*7+13) % 256.
            if (torrent.Files == null || torrent.Files.Length == 0)
                throw new Exception("completed torrent exposed no files");
            var actual = await torrent.Files[0].ReadAsync(0, dataLength);
            if (actual.Length != dataLength)
                throw new Exception($"read {actual.Length} bytes, expected {dataLength}");
            var expected = new byte[dataLength];
            for (int i = 0; i < dataLength; i++) expected[i] = (byte)((i * 7 + 13) % 256);
            if (!actual.SequenceEqual(expected))
                throw new Exception("downloaded bytes did not match the desktop seeder's deterministic payload (corrupt/incomplete cross-platform transfer)");
        }
        finally
        {
            // RemoveWithData: don't leave OPFS residue that would let the NEXT run restore-as-Done.
            await Client.RemoveWithDataAsync(torrent);
        }
    }
}
