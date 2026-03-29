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

    public int PeerCount => _peers.Count;

    public event Action<ConnectedPeer>? OnPeerConnected;
    public event Action<ConnectedPeer>? OnPeerDisconnected;
    public event Action<int, int>? OnSwarmUpdate; // seeders, leechers

    public PeerCoordinator(WebTorrentClient client, byte[] infoHash,
        IWebRtcTransport webRtc)
    {
        _client = client;
        _infoHash = infoHash;
        _webRtc = webRtc;

        // Wire up WebRTC offer creation → send via tracker
        _webRtc.OnOfferCreated += async (peerId, offer) =>
        {
            foreach (var tracker in _trackers)
            {
                var offerJson = System.Text.Json.JsonSerializer.SerializeToElement(offer);
                await tracker.SendOfferAsync(peerId, offerJson,
                    Guid.NewGuid().ToString("N"));
            }
        };
    }

    /// <summary>Add a tracker and start discovering peers.</summary>
    public async Task AddTrackerAsync(string trackerUrl, CancellationToken ct = default)
    {
        var tracker = new WebSocketTrackerClient(trackerUrl, _client.PeerId);

        tracker.OnPeer += HandleNewPeer;
        tracker.OnAnnounceResponse += (s, l) => OnSwarmUpdate?.Invoke(s, l);

        // Handle incoming WebRTC offers relayed by the tracker
        tracker.OnOffer += async (fromPeerId, offerId, offer) =>
        {
            var (conn, answer) = await _webRtc.HandleOfferAsync(fromPeerId, offer);
            var answerJson = System.Text.Json.JsonSerializer.SerializeToElement(answer);
            await tracker.SendAnswerAsync(fromPeerId, answerJson, offerId);

            // Start wire protocol on the new connection
            await SetupPeerAsync(conn);
        };

        // Handle incoming WebRTC answers relayed by the tracker
        tracker.OnAnswer += async (fromPeerId, offerId, answer) =>
        {
            await _webRtc.HandleAnswerAsync(fromPeerId, answer);
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
            // Initiate WebRTC connection
            var conn = await _webRtc.ConnectAsync(info.Address);
            await SetupPeerAsync(conn);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[PeerCoordinator] Failed to connect to {info.Address}: {ex.Message}");
        }
    }

    private async Task SetupPeerAsync(IConnection conn)
    {
        var wire = new WireProtocol(conn);

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

        // Start message read loop (runs until disconnect)
        _ = Task.Run(async () =>
        {
            try
            {
                await wire.RunAsync();
            }
            finally
            {
                _peers.TryRemove(peer.PeerId, out _);
                OnPeerDisconnected?.Invoke(peer);
            }
        });
    }

    /// <summary>Re-announce to all trackers (periodic or after state change).</summary>
    public async Task ReannounceAsync(long uploaded, long downloaded, long left)
    {
        foreach (var tracker in _trackers)
            await tracker.AnnounceAsync(_infoHash, 0, uploaded, downloaded, left);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var tracker in _trackers)
            await tracker.DisposeAsync();
        _trackers.Clear();

        foreach (var peer in _peers.Values.ToArray())
            await peer.Wire.DisposeAsync();
        _peers.Clear();
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
