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
    private readonly ConcurrentDictionary<string, DateTime> _offerTimestamps = new();
    private readonly byte[] _infoHash;
    private readonly List<Func<Torrent.TorrentSwarm, WireProtocol, WireExtension>> _extensionFactories = new();
    private bool _disposed;

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
        _ = CleanupStaleOffersAsync();
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
                // Skip self-offers and already-connected peers
                var myPeerId = TrackerEncoding.ToBinaryString(_client.PeerId);
                if (fromPeerId == myPeerId) return;
                if (_peers.ContainsKey(fromPeerId)) return;

                if (WebTorrentClient.VerboseLogging) Console.WriteLine($"[PeerCoordinator] Processing offer from {fromPeerId[..Math.Min(12, fromPeerId.Length)]}...");
                var (conn, answerSdp) = await _webRtc.HandleOfferAsync(fromPeerId, offer);
                if (WebTorrentClient.VerboseLogging) Console.WriteLine($"[PeerCoordinator] Answer created, sending back...");
                var answerJson = System.Text.Json.JsonSerializer.SerializeToElement(
                    new { type = answerSdp.Type, sdp = answerSdp.Sdp });
                await tracker.SendAnswerAsync(fromPeerId, answerJson, offerId);
                if (WebTorrentClient.VerboseLogging) Console.WriteLine($"[PeerCoordinator] Answer sent. Waiting for ICE...");

                _ = WaitAndSetupPeerAsync(conn);
            }
            catch (Exception ex)
            {
                if (WebTorrentClient.VerboseLogging) Console.WriteLine($"[PeerCoordinator] Offer handling FAILED: {ex.GetType().Name}: {ex.Message}");
            }
        };

        // Handle incoming WebRTC answers for our pre-generated offers
        tracker.OnAnswer += async (fromPeerId, offerId, answer) =>
        {
            try
            {
                if (WebTorrentClient.VerboseLogging) Console.WriteLine($"[PeerCoordinator] Processing answer from {fromPeerId[..Math.Min(12, fromPeerId.Length)]}, offerId={offerId[..Math.Min(8, offerId.Length)]}...");
                var conn = await _webRtc.HandleAnswerByOfferIdAsync(offerId, answer);
                if (conn == null)
                {
                    if (WebTorrentClient.VerboseLogging) Console.WriteLine($"[PeerCoordinator] No pending offer for offerId — trying by peerId...");
                    await _webRtc.HandleAnswerAsync(fromPeerId, answer);
                    return;
                }

                if (WebTorrentClient.VerboseLogging) Console.WriteLine($"[PeerCoordinator] Answer matched offer. ICE connecting...");
                _pendingOffers.TryRemove(offerId, out _);

                _ = WaitAndSetupPeerAsync(conn);
            }
            catch (Exception ex)
            {
                if (WebTorrentClient.VerboseLogging) Console.WriteLine($"[PeerCoordinator] Answer handling FAILED: {ex.GetType().Name}: {ex.Message}");
            }
        };

        _trackers.Add(tracker);

        // Pre-generate offers and send WITH the first announce (one message, not two)
        var offers = await GenerateOffersAsync(OffersPerAnnounce, ct);
        await tracker.StartAsync(_infoHash, 0, offers, offerFactory: null, ct);
    }

    /// <summary>
    /// Pre-generate N WebRTC offers for sending with tracker announce.
    /// Each offer is a fully formed RTCPeerConnection with data channel and SDP.
    /// Uses a per-offer timeout to avoid hanging on slow ICE gathering.
    /// </summary>
    private async Task<TrackerOffer[]> GenerateOffersAsync(int count, CancellationToken ct = default)
    {
        var offers = new List<TrackerOffer>();
        for (int i = 0; i < count; i++)
        {
            try
            {
                using var offerCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                offerCts.CancelAfter(15_000);
                var offerId = TrackerEncoding.ToBinaryString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(20));
                var (sdp, conn) = await _webRtc.CreateOfferAsync(offerId, offerCts.Token);
                _pendingOffers[offerId] = conn;
                _offerTimestamps[offerId] = DateTime.UtcNow;
                offers.Add(new TrackerOffer(sdp, offerId));
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                if (WebTorrentClient.VerboseLogging) Console.WriteLine($"[PeerCoordinator] Generate offer {i} timed out (15s)");
            }
            catch (Exception ex)
            {
                if (WebTorrentClient.VerboseLogging) Console.WriteLine($"[PeerCoordinator] Generate offer {i} failed: {ex.GetType().Name}: {ex.Message}");
            }
        }
        return offers.ToArray();
    }

    /// <summary>Wait for data channel to open, then setup the peer. Fire-and-forget — never blocks the tracker message loop.</summary>
    private async Task WaitAndSetupPeerAsync(IConnection conn)
    {
        try
        {
            using var openCts = new CancellationTokenSource(25000);
            if (conn is WebRtcConnection webRtcConn)
                await webRtcConn.WaitForOpenAsync(openCts.Token);
            else if (conn is SipSorceryWebRtcConnection sipConn)
                await sipConn.WaitForOpenAsync(openCts.Token);

            await SetupPeerAsync(conn);
        }
        catch (OperationCanceledException)
        {
            // ICE failed — this peer is unreachable. Silent cleanup.
            await conn.DisposeAsync();
        }
        catch (Exception ex)
        {
            if (WebTorrentClient.VerboseLogging) Console.WriteLine($"[PeerCoordinator] Peer setup failed: {ex.GetType().Name}: {ex.Message}");
            await conn.DisposeAsync();
        }
    }

    private async Task CleanupStaleOffersAsync()
    {
        while (!_disposed)
        {
            await Task.Delay(30000);
            var staleIds = _offerTimestamps
                .Where(kv => (DateTime.UtcNow - kv.Value).TotalSeconds > 60)
                .Select(kv => kv.Key).ToList();
            foreach (var id in staleIds)
            {
                if (_pendingOffers.TryRemove(id, out var conn))
                    await conn.DisposeAsync();
                _offerTimestamps.TryRemove(id, out _);
            }
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
        if (_disposed) return;
        _disposed = true;

        foreach (var tracker in _trackers.ToArray())
        {
            try { await tracker.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10)); }
            catch { }
        }
        _trackers.Clear();

        foreach (var peer in _peers.Values.ToArray())
        {
            try { await peer.Wire.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)); }
            catch { }
        }
        _peers.Clear();

        foreach (var conn in _pendingOffers.Values.ToArray())
        {
            try { await conn.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)); }
            catch { }
        }
        _pendingOffers.Clear();
        _offerTimestamps.Clear();
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
