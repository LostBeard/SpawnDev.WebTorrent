using SpawnDev.UnitTesting;
using SpawnDev.WebTorrent;

namespace SpawnDev.WebTorrent.Demo.Shared;

public abstract partial class WebTorrentTestBase
{
    private const string SintelMagnet = "magnet:?xt=urn:btih:08ada5a7a6183aae1e09d831df6748d566095a10&dn=Sintel" +
        "&tr=wss%3A%2F%2Ftracker.openwebtorrent.com" +
        "&tr=wss%3A%2F%2Ftracker.webtorrent.dev" +
        "&tr=wss%3A%2F%2Fhub.spawndev.com%3A44365%2Fannounce" +
        "&ws=https%3A%2F%2Fwebtorrent.io%2Ftorrents%2F";

    [TestMethod]
    public async Task Network_TrackerConnect_Announces()
    {
        // Test that a WSS tracker connection results in peer discovery
        if (OperatingSystem.IsBrowser())
            throw new UnsupportedTestException("Uses SipSorceryPeer — desktop only");

        var client = CreateIsolatedClient();
        client.PeerFactory = (initiator) => new SipSorceryPeer(initiator, trickle: false);
        var torrent = client.Add(SintelMagnet);

        // Wait for any peer to appear (proves tracker announced successfully)
        using var cts = new CancellationTokenSource(60000);
        while (!cts.IsCancellationRequested)
        {
            if (torrent.NumPeers > 0 || torrent.HasMetadata) break;
            await Task.Delay(500, cts.Token);
        }

        if (torrent.NumPeers == 0 && !torrent.HasMetadata)
            throw new Exception("No tracker response within 60s — no peers or metadata");
        await client.DisposeAsync();
    }

    [TestMethod]
    public async Task Network_MagnetAdd_PeersFound()
    {
        if (OperatingSystem.IsBrowser())
            throw new UnsupportedTestException("Uses SipSorceryPeer — desktop only");
        var client = CreateIsolatedClient();
        client.PeerFactory = (initiator) => new SipSorceryPeer(initiator, trickle: false);
        var torrent = client.Add(SintelMagnet);

        // Wait for any wire connection (NumPeers = Wires.Count) OR metadata (proves peer connected)
        using var cts = new CancellationTokenSource(120000);
        while (!cts.IsCancellationRequested)
        {
            if (torrent.NumPeers > 0 || torrent.HasMetadata) break;
            await Task.Delay(500, cts.Token);
        }

        if (torrent.NumPeers == 0 && !torrent.HasMetadata)
            throw new Exception("No peers found within 120s");
        await client.DisposeAsync();
    }

    [TestMethod]
    public async Task Network_MagnetAdd_MetadataReceived()
    {
        if (OperatingSystem.IsBrowser())
            throw new UnsupportedTestException("Uses SipSorceryPeer — desktop only");
        var client = CreateIsolatedClient();
        client.PeerFactory = (initiator) => new SipSorceryPeer(initiator, trickle: false);
        var torrent = client.Add(SintelMagnet);

        using var cts = new CancellationTokenSource(60000);
        while (!cts.IsCancellationRequested)
        {
            if (torrent.HasMetadata) break;
            await Task.Delay(500, cts.Token);
        }

        if (!torrent.HasMetadata)
            throw new Exception("Metadata not received within 60s");
        if (torrent.Name != "Sintel")
            throw new Exception($"Expected name 'Sintel', got '{torrent.Name}'");
        await client.DisposeAsync();
    }

    [TestMethod]
    public async Task Network_LiveSwarm_DownloadsPieces()
    {
        if (OperatingSystem.IsBrowser())
            throw new UnsupportedTestException("Uses SipSorceryPeer — desktop only");
        var client = CreateIsolatedClient();
        client.PeerFactory = (initiator) => new SipSorceryPeer(initiator, trickle: false);
        var torrent = client.Add(SintelMagnet);

        // Wait for metadata
        using var metaCts = new CancellationTokenSource(60000);
        while (!metaCts.IsCancellationRequested && !torrent.HasMetadata)
            await Task.Delay(500, metaCts.Token);

        if (!torrent.HasMetadata)
            throw new Exception("Metadata not received");

        // Wait for at least one piece (32KB = one block from a peer)
        using var pieceCts = new CancellationTokenSource(60000);
        while (!pieceCts.IsCancellationRequested)
        {
            if (torrent.Downloaded > 0) break;
            await Task.Delay(500, pieceCts.Token);
        }

        if (torrent.Downloaded == 0)
            throw new Exception("No data downloaded from live swarm");

        await client.DisposeAsync();
    }
}
