using SpawnDev.UnitTesting;
using SpawnDev.WebTorrent;

namespace SpawnDev.WebTorrent.Demo.Shared;

public abstract partial class WebTorrentTestBase
{
    // tracker.webtorrent.dev is fickle and blocks some origins (confirmed by TJ 2026-04-16).
    // openwebtorrent.com is the most reliable public WSS tracker observed.
    // hub.spawndev.com is NOT included: these tests verify public Sintel swarm
    // connectivity, not our own tracker. Using only openwebtorrent narrows the
    // live-infra dependency to one well-known tracker rather than fanning out to
    // a personal-infra node that wouldn't host Sintel peers anyway.
    private const string SintelMagnet = "magnet:?xt=urn:btih:08ada5a7a6183aae1e09d831df6748d566095a10&dn=Sintel" +
        "&tr=wss%3A%2F%2Ftracker.openwebtorrent.com" +
        "&ws=https%3A%2F%2Fwebtorrent.io%2Ftorrents%2F";

    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(500);
        }
    }

    [TestMethod(Timeout = 90000, RetryCount = 2)]
    public async Task Network_TrackerConnect_Announces()
    {
        var client = CreateIsolatedClient();
        var torrent = client.Add(SintelMagnet);

        await WaitUntilAsync(() => torrent.NumPeers > 0 || torrent.HasMetadata, 60000);

        if (torrent.NumPeers == 0 && !torrent.HasMetadata)
            throw new Exception("No tracker response within 60s — no peers or metadata");
        await client.DisposeAsync();
    }

    [TestMethod(Timeout = 150000, RetryCount = 2)]
    public async Task Network_MagnetAdd_PeersFound()
    {
        var client = CreateIsolatedClient();
        var torrent = client.Add(SintelMagnet);

        await WaitUntilAsync(() => torrent.NumPeers > 0 || torrent.HasMetadata, 120000);

        if (torrent.NumPeers == 0 && !torrent.HasMetadata)
            throw new Exception("No peers found within 120s");
        await client.DisposeAsync();
    }

    [TestMethod(Timeout = 120000, RetryCount = 2)]
    public async Task Network_MagnetAdd_MetadataReceived()
    {
        var client = CreateIsolatedClient();
        var torrent = client.Add(SintelMagnet);

        await WaitUntilAsync(() => torrent.HasMetadata, 90000);

        if (!torrent.HasMetadata)
            throw new Exception($"Metadata not received within 90s (peers={torrent.NumPeers})");
        if (torrent.Name != "Sintel")
            throw new Exception($"Expected name 'Sintel', got '{torrent.Name}'");
        await client.DisposeAsync();
    }

    [TestMethod(Timeout = 240000, RetryCount = 2)]
    public async Task Network_LiveSwarm_DownloadsPieces()
    {
        var client = CreateIsolatedClient();
        var torrent = client.Add(SintelMagnet);

        await WaitUntilAsync(() => torrent.HasMetadata, 90000);

        if (!torrent.HasMetadata)
            throw new Exception($"Metadata not received within 90s (peers={torrent.NumPeers})");

        await WaitUntilAsync(() => torrent.Downloaded > 0, 120000);

        if (torrent.Downloaded == 0)
            throw new Exception($"No data downloaded from live swarm within 120s (peers={torrent.NumPeers}, hasMeta={torrent.HasMetadata})");

        await client.DisposeAsync();
    }
}
