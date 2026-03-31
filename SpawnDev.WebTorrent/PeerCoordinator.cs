using System.Collections.Concurrent;
using SpawnDev.WebTorrent.Discovery;
using SpawnDev.WebTorrent.Transports;
using SpawnDev.WebTorrent.Wire;

namespace SpawnDev.WebTorrent;

/// <summary>
/// Coordinates peer discovery, WebRTC signaling, and connection establishment.
/// Ties the tracker client to the WebRTC transport, handling the full flow:
///   1. Tracker discovers peers
///   2. WebRTC offers/answers relay through tracker
///   3. Data channels open
///   4. Wire protocol handshake
///   5. Peer added to torrent swarm
///
/// This is the "glue" that makes P2P work in the browser.
/// </summary>
public class PeerCoordinator : IAsyncDisposable
{
    private readonly WebTorrentClient _client;
    private readonly IWebRtcTransport _webRtc;
    private readonly List<WebSocketTrackerClient> _trackers = new();
    private readonly ConcurrentDictionary<string, ConnectedPeer> _peers = new();
    private readonly byte[] _infoHash;
    private readonly List<Func<Torrent.TorrentSwarm, WireProtocol, WireExtension>> _extensionFactories = new();

    public int PeerCount => _peers.Count;

    public event Action<ConnectedPeer>? OnPeerConnected;
    public event Action<ConnectedPeer>? OnPeerDisconnected;
    public event Action<int, int>? OnSwarmUpdate; // seeders, leechers

    /// <summary>The swarm this coordinator belongs to (set by TorrentSwarm).</summary>
    internal Torrent.TorrentSwarm? Swarm { get; set; }

    public PeerCoordinator(WebTorrentClient client, byte[] infoHash,
        IWebRtcTransport webRtc)
    {
        _client = client;
        _infoHash = infoHash;
        _webRtc = webRtc;

        // Wire up WebRTC offer creation → send via tracker
        _webRtc.OnOfferCreated += async (peerId, offer) =>
        {
            try
            {
                foreach (var tracker in _trackers)
                {
                    var offerJson = System.Text.Json.JsonSerializer.SerializeToElement(offer);
                    await tracker.SendOfferAsync(peerId, offerJson,
                        Guid.NewGuid().ToString("N"));
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[PeerCoordinator] SendOffer failed: {ex.GetType().Name}: {ex.Message}");
            }
        };
    }

    /// <summary>Add a tracker and start discovering peers.</summary>
    public async Task AddTrackerAsync(string trackerUrl, CancellationToken ct = default)
    {
        var tracker = new WebSocketTrackerClient(trackerUrl, _client.PeerId);

        tracker.OnPeer += HandleNewPeer;
        tracker.OnAnnounceResponse += (s, l) =>
        {
            OnSwarmUpdate?.Invoke(s, l);
        };

        // Handle incoming WebRTC offers relayed by the tracker
        tracker.OnOffer += async (fromPeerId, offerId, offer) =>
        {
            try
            {
                var (conn, answer) = await _webRtc.HandleOfferAsync(fromPeerId, offer);
                var answerJson = System.Text.Json.JsonSerializer.SerializeToElement(answer);
                await tracker.SendAnswerAsync(fromPeerId, answerJson, offerId);

                // Wait for the data channel to open (initiator processes our answer → ICE → open)
                using var openCts = new CancellationTokenSource(15000);
                if (conn is WebRtcConnection webRtcConn)
                    await webRtcConn.WaitForOpenAsync(openCts.Token);

                await SetupPeerAsync(conn);
            }
            catch (Exception ex)
            {
                OnPeerDisconnected?.Invoke(new ConnectedPeer { PeerId = fromPeerId });
                Console.WriteLine($"[PeerCoordinator] Offer handling failed: {ex.GetType().Name}: {ex.Message}");
            }
        };

        // Handle incoming WebRTC answers relayed by the tracker
        tracker.OnAnswer += async (fromPeerId, offerId, answer) =>
        {
            try
            {
                await _webRtc.HandleAnswerAsync(fromPeerId, answer);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[PeerCoordinator] Answer handling failed: {ex.GetType().Name}: {ex.Message}");
            }
        };

        _trackers.Add(tracker);
        await tracker.StartAsync(_infoHash, 0, ct);
    }

    private async void HandleNewPeer(PeerInfo info)
    {
        if (_peers.ContainsKey(info.Address)) return; // already connected
        if (_peers.Count >= 55) return; // max peers

        try
        {
            var conn = await _webRtc.ConnectAsync(info.Address);
            await SetupPeerAsync(conn);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[PeerCoordinator] Connect to {info.Address} failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>Register a wire extension factory. Extensions are created for every new peer BEFORE the BEP 10 handshake.</summary>
    public void UseExtension(Func<Torrent.TorrentSwarm, WireProtocol, WireExtension> factory) => _extensionFactories.Add(factory);

    private async Task SetupPeerAsync(IConnection conn)
    {
        var wire = new WireProtocol(conn);

        // Register extensions BEFORE handshake so they're included in BEP 10 negotiation
        foreach (var factory in _extensionFactories)
            wire.Extensions.Register(factory(Swarm!, wire));

        // Perform BitTorrent handshake
        await wire.SendHandshakeAsync(_infoHash, _client.PeerId);
        if (!await wire.ReceiveHandshakeAsync())
        {
            await conn.CloseAsync();
            return;
        }

        // Verify info hash matches
        if (wire.RemoteInfoHash == null || !wire.RemoteInfoHash.SequenceEqual(_infoHash))
        {
            await conn.CloseAsync();
            return;
        }

        // Send BEP 10 extended handshake if both sides support it
        if (wire.SupportsExtensions && wire.Extensions.Count > 0)
        {
            var extHandshake = wire.Extensions.BuildHandshake();
            var encoded = SpawnDev.WebTorrent.Bencode.BencodeEncoder.Encode(
                extHandshake.ToDictionary(kv => kv.Key, kv => kv.Value));
            await wire.SendExtensionMessageAsync(0, encoded);
        }

        var peer = new ConnectedPeer
        {
            Connection = conn,
            Wire = wire,
            PeerId = wire.RemotePeerId != null
                ? Convert.ToHexString(wire.RemotePeerId).ToLowerInvariant()
                : conn.RemoteId,
        };

        _peers[peer.PeerId] = peer;
        OnPeerConnected?.Invoke(peer);
        // Message read loop is started by TorrentSwarm.AddConnectedPeerAsync — not here.
        // Having two RunAsync loops on the same wire causes messages to be split randomly.
    }

    /// <summary>Re-announce to all trackers (periodic or after state change).</summary>
    public async Task ReannounceAsync(long uploaded, long downloaded, long left)
    {
        foreach (var tracker in _trackers.ToArray())
            await tracker.AnnounceAsync(_infoHash, 0, uploaded, downloaded, left);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var tracker in _trackers.ToArray())
            await tracker.DisposeAsync();
        _trackers.Clear();

        foreach (var peer in _peers.Values.ToArray())
            await peer.Wire.DisposeAsync();
        _peers.Clear();

        await _webRtc.DisposeAsync();
    }
}

/// <summary>A fully connected peer with wire protocol active.</summary>
public class ConnectedPeer
{
    public IConnection Connection { get; init; } = null!;
    public WireProtocol Wire { get; init; } = null!;
    public string PeerId { get; init; } = "";
    public bool[] Bitfield { get; set; } = Array.Empty<bool>();
}
