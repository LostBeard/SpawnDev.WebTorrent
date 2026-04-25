using SpawnDev.UnitTesting;
using SpawnDev.WebTorrent;

namespace SpawnDev.WebTorrent.Demo.Shared;

/// <summary>
/// Live-swarm integration: connect to the public Sintel WebTorrent swarm and verify
/// that pieces transfer over WebRTC against a real JS WebTorrent peer. Requires internet
/// connectivity + at least one reachable peer; will surface a helpful error via the
/// unit-test runner if the swarm is unavailable. Migrated from NUnit InteropTests.cs.
/// </summary>
public abstract partial class WebTorrentTestBase
{
    [TestMethod(Timeout = 180000, RetryCount = 2)]
    public async Task Interop_LiveSwarm_Sintel_DownloadsPieces()
    {
        // Public Sintel swarm over openwebtorrent. RetryCount=2 absorbs normal
        // public-tracker flake (openwebtorrent occasionally drops a handshake or
        // has a slow-propagation moment). Real failure = kernel/wire bug.
        const string sintelMagnet = "magnet:?xt=urn:btih:08ada5a7a6183aae1e09d831df6748d566095a10&dn=Sintel" +
            "&tr=wss%3A%2F%2Ftracker.openwebtorrent.com" +
            "&ws=https%3A%2F%2Fwebtorrent.io%2Ftorrents%2F";

        var client = new WebTorrentClient();
        try
        {
            var torrent = client.Add(sintelMagnet, new AddTorrentOptions { DisableWebSeeds = true });

            var metadataDeadline = DateTime.UtcNow.AddSeconds(60);
            while (!torrent.HasMetadata && DateTime.UtcNow < metadataDeadline)
                await Task.Delay(1000);

            if (!torrent.HasMetadata)
                throw new Exception(
                    $"no metadata after 60s (peers={torrent.NumPeers}). " +
                    "Tracker connection or WebRTC signaling failed — real network required.");

            var downloadDeadline = DateTime.UtcNow.AddSeconds(60);
            while (torrent.Downloaded == 0 && DateTime.UtcNow < downloadDeadline)
                await Task.Delay(1000);

            if (torrent.Downloaded == 0)
                throw new Exception(
                    $"downloaded 0 bytes from live Sintel swarm in 60s (peers={torrent.NumPeers}). " +
                    "Either the swarm was unreachable or piece transfer over WebRTC is broken.");
        }
        finally
        {
            await client.DisposeAsync();
        }
    }
}
