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

            // 🔴 TELL "THE SWARM WAS UNREACHABLE" APART FROM "OUR CODE IS BROKEN".
            //
            // This test used to throw one message for both, and it said so itself: "Either the swarm was
            // unreachable or piece transfer over WebRTC is broken." A test that cannot distinguish an
            // ENVIRONMENT failure from a PRODUCT failure indicts our code every time a public tracker has a
            // bad minute - and it is the only reason this suite goes red on an otherwise green sweep
            // (MEASURED 2026-09-08: three full sweeps, 2 failures / 0 / this one).
            //
            // NumPeers is the evidence, and the test already has it: with ZERO peers we never got the chance
            // to be wrong, so there is nothing to assert and this is a SKIP. With a peer connected and still
            // no data, that IS ours, and it fails loudly.
            if (!torrent.HasMetadata)
            {
                // 🔴 ZERO PEERS IS *OUR* FAILURE, NOT THE SWARM'S. I briefly classified this as a skip; the
                // Captain disproved it on the spot by opening the SpawnDev.BlazorJS.WebTorrent demo on GitHub
                // Pages and connecting to at least SEVEN WebRTC peers on this exact swarm, minutes after our
                // DESKTOP lane reported none. The swarm is healthy - observed, not assumed - so finding no
                // peers is a defect in our desktop tracker/WebRTC path and must fail loudly.
                //
                // ⚠️ Every live-swarm failure in this suite is on the DESKTOP lane. The browser lane talks to
                // the same swarm over the same trackers.
                if (torrent.NumPeers == 0)
                    throw new Exception(
                        "no peer connected within 60s (peers=0), yet this swarm demonstrably has WebRTC peers "
                        + "(verified from the browser demo). Our desktop tracker/WebRTC signaling is not "
                        + "reaching them.");
                throw new Exception(
                    $"no metadata after 60s despite {torrent.NumPeers} connected peer(s). Peers connected and "
                    + "still sent us no metadata - that is a BEP 9 / wire defect on our side.");
            }

            var downloadDeadline = DateTime.UtcNow.AddSeconds(60);
            while (torrent.Downloaded == 0 && DateTime.UtcNow < downloadDeadline)
                await Task.Delay(1000);

            if (torrent.Downloaded == 0)
            {
                if (torrent.NumPeers == 0)
                    throw new Exception(
                        "every peer dropped before any piece transferred (peers=0 after metadata). The swarm "
                        + "has peers, so losing all of them mid-transfer is ours to explain.");
                throw new Exception(
                    $"downloaded 0 bytes in 60s from {torrent.NumPeers} connected peer(s) that already gave us "
                    + "metadata. They are there and talking, so piece transfer over WebRTC is broken on our side.");
            }
        }
        finally
        {
            await client.DisposeAsync();
        }
    }
}
