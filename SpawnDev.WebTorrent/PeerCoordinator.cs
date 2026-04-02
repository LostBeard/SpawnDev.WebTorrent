using System.Collections.Concurrent;
using SpawnDev.WebTorrent.Discovery;
using SpawnDev.WebTorrent.Transports;
using SpawnDev.WebTorrent.Wire;

namespace SpawnDev.WebTorrent;

/// <summary>
/// Coordinates peer discovery, WebRTC signaling, and connection establishment.
///
/// Implements the WebTorrent tracker protocol: offers are pre-generated and sent
/// WITH the announce message. The tracker distributes them to existing peers,
/// who create answers and send them back. No "discover peer → create offer" race.
///
/// Flow:
///   1. Pre-generate N WebRTC offers (RTCPeerConnection + data channel + SDP)
///   2. Send offers WITH tracker announce
///   3. Tracker relays offers to existing peers in the swarm
///   4. Existing peers create answers, send back via tracker
///   5. We receive answers, match by offerId, complete ICE → data channel opens
///   6. Wire protocol handshake, peer added to swarm
/// </summary>
public class PeerCoordinator : IAsyncDisposable
{
    private readonly WebTorrentClient _client;
    private readonly IWebRtcTransport _webRtc;
    private readonly List<WebSocketTrackerClient> _trackers = new();
    private readonly ConcurrentDictionary<string, ConnectedPeer> _peers = new();
    private readonly ConcurrentDictionary<string, IConnection> _pendingOffers = new();
    private readonly byte[] _infoHash;
    private readonly List<Func<Torrent.TorrentSwarm, WireProtocol, WireExtension>> _extensionFactories = new();

    private const int OffersPerAnnounce = 5;

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
    }

    /// <summary>Add a tracker and start discovering peers.</summary>
    public async Task AddTrackerAsync(string trackerUrl, CancellationToken ct = default)
    {
        var tracker = new WebSocketTrackerClient(trackerUrl, _client.PeerId);

        tracker.OnAnnounceResponse += (s, l) => OnSwarmUpdate?.Invoke(s, l);

        // Handle incoming WebRTC offers relayed by the tracker
        // (from other peers who sent offers with THEIR announce)
        tracker.OnOffer += async (fromPeerId, offerId, offer) =>
        {
            try
            {
                var (conn, answerSdp) = await _webRtc.HandleOfferAsync(fromPeerId, offer);
                var answerJson = System.Text.Json.JsonSerializer.SerializeToElement(
                    new { type = answerSdp.Type, sdp = answerSdp.Sdp });
                await tracker.SendAnswerAsync(fromPeerId, answerJson, offerId);

                // Wait for the data channel to open
                using var openCts = new CancellationTokenSource(15000);
                if (conn is WebRtcConnection webRtcConn)
                    await webRtcConn.WaitForOpenAsync(openCts.Token);
                else if (conn is SipSorceryWebRtcConnection sipConn)
                    await sipConn.WaitForOpenAsync(openCts.Token);

                await SetupPeerAsync(conn);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[PeerCoordinator] Offer handling failed: {ex.GetType().Name}: {ex.Message}");
            }
        };

        // Handle incoming WebRTC answers for our pre-generated offers
        tracker.OnAnswer += async (fromPeerId, offerId, answer) =>
        {
            try
            {
                var conn = await _webRtc.HandleAnswerByOfferIdAsync(offerId, answer);
                if (conn == null)
                {
                    // Fallback: try matching by peerId (legacy)
                    await _webRtc.HandleAnswerAsync(fromPeerId, answer);
                    return;
                }

                // Remove from pending
                _pendingOffers.TryRemove(offerId, out _);

                // Wait for data channel to open
                using var openCts = new CancellationTokenSource(15000);
                if (conn is WebRtcConnection webRtcConn)
                    await webRtcConn.WaitForOpenAsync(openCts.Token);
                else if (conn is SipSorceryWebRtcConnection sipConn)
                    await sipConn.WaitForOpenAsync(openCts.Token);

                await SetupPeerAsync(conn);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[PeerCoordinator] Answer handling failed: {ex.GetType().Name}: {ex.Message}");
            }
        };

        _trackers.Add(tracker);

        // Pre-generate offers and announce with them
        var offers = await GenerateOffersAsync(OffersPerAnnounce, ct);
        await tracker.StartAsync(_infoHash, 0, ct);

        // Re-announce with fresh offers (StartAsync sends the first announce without offers,
        // so send a second announce immediately with offers)
        if (offers.Length > 0)
            await tracker.AnnounceAsync(_infoHash, 0, 0, 0, 0, offers, ct);
    }

    /// <summary>
    /// Pre-generate N WebRTC offers for sending with tracker announce.
    /// Each offer is a fully formed RTCPeerConnection with data channel and SDP.
    /// </summary>
    private async Task<TrackerOffer[]> GenerateOffersAsync(int count, CancellationToken ct = default)
    {
        var offers = new List<TrackerOffer>();
        for (int i = 0; i < count; i++)
        {
            try
            {
                var offerId = Guid.NewGuid().ToString("N");
                var (sdp, conn) = await _webRtc.CreateOfferAsync(offerId, ct);
                _pendingOffers[offerId] = conn;
                offers.Add(new TrackerOffer(sdp, offerId));
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[PeerCoordinator] Generate offer {i} failed: {ex.GetType().Name}: {ex.Message}");
            }
        }
        return offers.ToArray();
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
    }

    /// <summary>Re-announce to all trackers with fresh offers.</summary>
    public async Task ReannounceAsync(long uploaded, long downloaded, long left)
    {
        var offers = await GenerateOffersAsync(OffersPerAnnounce);
        foreach (var tracker in _trackers.ToArray())
            await tracker.AnnounceAsync(_infoHash, 0, uploaded, downloaded, left, offers);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var tracker in _trackers.ToArray())
            await tracker.DisposeAsync();
        _trackers.Clear();

        foreach (var peer in _peers.Values.ToArray())
            await peer.Wire.DisposeAsync();
        _peers.Clear();

        foreach (var conn in _pendingOffers.Values.ToArray())
            await conn.DisposeAsync();
        _pendingOffers.Clear();

        await _webRtc.DisposeAsync();
    }
}

/// <summary>A pre-generated WebRTC offer for sending with tracker announce.</summary>
public record TrackerOffer(SdpMessage Offer, string OfferId);

/// <summary>A fully connected peer with wire protocol active.</summary>
public class ConnectedPeer
{
    public IConnection Connection { get; init; } = null!;
    public WireProtocol Wire { get; init; } = null!;
    public string PeerId { get; init; } = "";
    public bool[] Bitfield { get; set; } = Array.Empty<bool>();
}
