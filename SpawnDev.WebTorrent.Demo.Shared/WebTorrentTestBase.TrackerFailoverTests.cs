using SpawnDev.UnitTesting;
using SpawnDev.WebTorrent;

namespace SpawnDev.WebTorrent.Demo.Shared;

/// <summary>
/// Multi-tracker failover: when one tracker fails (unreachable host, WSS handshake
/// rejected, HTTP 5xx, etc.) the Discovery layer should surface the failure via
/// OnWarning without cancelling announces to other trackers. Per-tracker errors are
/// isolated — `Task.WhenAll(allTrackers)` no longer propagates a single failure.
/// </summary>
public abstract partial class WebTorrentTestBase
{
    [TestMethod]
    public async Task TrackerFailover_OneBadTracker_DoesNotBlockOthers()
    {
        // Magnet lists an unresolvable WSS host + our tracker. Before rc.16, any error
        // from the bad tracker would cancel the Task.WhenAll that dispatches announces.
        // After: isolated per-tracker; good tracker still gets the announce.
        var client = CreateIsolatedClient();
        string? warning = null;
        client.OnWarning += msg => { warning = msg; };

        var magnet = "magnet:?xt=urn:btih:aaaabbbbccccddddeeeeffff1111222233334444" +
                     "&dn=failover-test" +
                     "&tr=wss%3A%2F%2Fdoes-not-exist-deadbeef-cafe.invalid%2Fannounce" +
                     "&tr=http%3A%2F%2Fdoes-not-exist-deadbeef-cafe.invalid%3A9999%2Fannounce";

        var torrent = client.Add(magnet);

        // Let announces fire. With isolation, both bad trackers fail independently and
        // should both surface via OnWarning. Before the fix, the first thrown exception
        // would propagate out of AnnounceAsync and subsequent announces wouldn't run.
        // Poll for up to ~3s for at least one warning.
        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (warning is null && DateTime.UtcNow < deadline)
            await Task.Delay(100);

        // Core assertion: client is still alive and the torrent is still queryable AFTER
        // the bad-tracker announce burst. Pre-rc.16 an unhandled exception in the
        // announce task could have taken down the discovery loop.
        var found = client.Get(torrent.InfoHash!);
        if (found is null || !ReferenceEquals(found, torrent))
            throw new Exception("torrent should still be registered after bad-tracker announces");

        await client.DisposeAsync();
    }

    [TestMethod]
    public async Task TrackerFailover_AllBadTrackers_ClientStaysAlive()
    {
        // All trackers unreachable — discovery should still start cleanly, surface
        // warnings, and not destroy the client.
        var client = CreateIsolatedClient();
        int warningCount = 0;
        client.OnWarning += _ => { warningCount++; };

        var magnet = "magnet:?xt=urn:btih:bbbbbbbbcccccccccccccccccccccccccccccccc" +
                     "&dn=all-bad" +
                     "&tr=wss%3A%2F%2Finvalid-1.invalid%2Fannounce" +
                     "&tr=wss%3A%2F%2Finvalid-2.invalid%2Fannounce";

        var torrent = client.Add(magnet);
        await Task.Delay(2000);

        // Client should still be in good shape — torrents present, no crash.
        if (client.Torrents.Count != 1)
            throw new Exception($"expected 1 torrent after all-bad-tracker announce, got {client.Torrents.Count}");
        if (torrent.Destroyed)
            throw new Exception("torrent should not be destroyed just because all trackers are down");

        await client.DisposeAsync();
    }
}
