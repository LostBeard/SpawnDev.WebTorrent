using System.Collections.Concurrent;
using System.Security.Cryptography;
using SpawnDev.RTC.Signaling;
using RtcAnnounceOptions = SpawnDev.RTC.Signaling.AnnounceOptions;

namespace SpawnDev.WebTorrent;

/// <summary>
/// WebSocket BitTorrent tracker client. Thin adapter over
/// <see cref="TrackerSignalingClient"/> from <c>SpawnDev.RTC.Signaling</c>.
/// Keeps the source-compatible pre-3.1.0 public surface (<see cref="GetOrCreate"/>,
/// <see cref="Subscribe"/>, <see cref="AnnounceAsync"/>, events) but delegates the
/// wire protocol, socket pool, reconnect logic, and announce framing to the
/// generic signaling client. WebTorrent-specific logic (<see cref="SimplePeer"/>
/// factories, offer_id/peer bookkeeping) lives in <see cref="SimplePeerRoomHandler"/>.
/// </summary>
public class WebSocketTracker : IAsyncDisposable
{
    // Wire constants - kept for source-compat with consumers that reference them.
    public const int ReconnectMinimum = TrackerSignalingClient.ReconnectMinimum;
    public const int ReconnectMaximum = TrackerSignalingClient.ReconnectMaximum;
    public const int ReconnectVariance = TrackerSignalingClient.ReconnectVariance;
    public const int OfferTimeout = TrackerSignalingClient.OfferTimeout;
    public const int DefaultAnnounceInterval = TrackerSignalingClient.DefaultAnnounceInterval;
    public const int MaxOffers = TrackerSignalingClient.MaxOffers;

    // ========================
    // SHARED POOL
    // One WebSocketTracker per (announceUrl, peerId). Wraps one shared
    // TrackerSignalingClient (which has its own pool - we do not double-pool the
    // underlying socket).
    // ========================
    private static readonly ConcurrentDictionary<string, WebSocketTracker> _pool = new();

    /// <summary>Dispose and drop every pooled tracker client. Also clears the
    /// underlying <see cref="TrackerSignalingClient"/> pool.</summary>
    public static void ClearPool()
    {
        foreach (var t in _pool.Values)
            _ = t.DisposeAsync();
        _pool.Clear();
        TrackerSignalingClient.ClearPool();
    }

    /// <summary>
    /// Get or create a shared <see cref="WebSocketTracker"/> for the given URL and
    /// peer id. The first call with a given (url, peerId) pair wins - subsequent
    /// calls return the cached instance, and the <paramref name="createPeerFunc"/>
    /// from later calls is ignored (matches pre-3.1.0 behaviour).
    /// </summary>
    public static WebSocketTracker GetOrCreate(string announceUrl, byte[] peerId, Func<bool, SimplePeer> createPeerFunc)
    {
        var key = announceUrl + ":" + Convert.ToHexString(peerId);
        while (true)
        {
            if (_pool.TryGetValue(key, out var existing) && !existing.Destroyed)
                return existing;
            var created = new WebSocketTracker(announceUrl, peerId, createPeerFunc);
            if (_pool.TryAdd(key, created)) return created;
            _ = created.DisposeAsync();
        }
    }

    // ========================
    // STATE
    // ========================
    public string AnnounceUrl => _signal.AnnounceUrl;
    public bool Destroyed => _signal.Destroyed;
    public bool Reconnecting => _signal.Reconnecting;
    public int Retries => _signal.Retries;

    private readonly TrackerSignalingClient _signal;
    private readonly Func<bool, SimplePeer> _defaultFactory;
    private readonly ConcurrentDictionary<string, SimplePeerRoomHandler> _handlers = new();

    // ========================
    // EVENTS
    // ========================
    /// <summary>A WebRTC peer is ready (offer received and answered, or our offer was answered).</summary>
    public event Action<SimplePeer>? OnPeer;

    /// <summary>Tracker response with swarm stats.</summary>
    public event Action<TrackerUpdate>? OnUpdate;

    /// <summary>Non-fatal warning.</summary>
    public event Action<string>? OnWarning;

    /// <summary>Socket (re)connected - a good moment to announce.</summary>
    public event Action? OnAnnounce;

    // ========================
    // CONSTRUCTOR
    // ========================
    private WebSocketTracker(string announceUrl, byte[] peerId, Func<bool, SimplePeer> createPeerFunc)
    {
        _defaultFactory = createPeerFunc;
        _signal = TrackerSignalingClient.GetOrCreate(announceUrl, peerId);

        _signal.OnConnected += () => OnAnnounce?.Invoke();
        _signal.OnWarning += w => OnWarning?.Invoke(w);
        _signal.OnSwarmStats += (room, stats) =>
        {
            var update = new TrackerUpdate
            {
                AnnounceUrl = announceUrl,
                Complete = stats.Complete,
                Incomplete = stats.Incomplete,
            };
            if (_handlers.TryGetValue(room.ToWireString(), out var h))
                h.RaiseUpdate(update);
            OnUpdate?.Invoke(update);
        };
    }

    // ========================
    // SUBSCRIBE / UNSUBSCRIBE (per-torrent)
    // ========================
    /// <summary>Subscribe to events for a specific info_hash on this shared tracker connection.</summary>
    public void Subscribe(byte[] infoHash, Action<SimplePeer> onPeer, Func<bool, SimplePeer>? peerFactory = null, Action<TrackerUpdate>? onUpdate = null, Action<string>? onWarning = null)
    {
        var room = RoomKey.FromBytes(infoHash);
        var wire = room.ToWireString();
        var handler = new SimplePeerRoomHandler(peerFactory ?? _defaultFactory, onPeer, onUpdate, onWarning, OnWarning);
        _handlers[wire] = handler;
        _signal.Subscribe(room, handler);
    }

    /// <summary>Unsubscribe a torrent from this shared tracker connection.</summary>
    public void Unsubscribe(byte[] infoHash)
    {
        var room = RoomKey.FromBytes(infoHash);
        var wire = room.ToWireString();
        if (_handlers.TryRemove(wire, out var handler))
            handler.DisposePending();
        _signal.Unsubscribe(room);
    }

    // ========================
    // ANNOUNCE
    // ========================
    /// <summary>Send announce to tracker with WebRTC offers for a specific info_hash.</summary>
    public async Task AnnounceAsync(byte[] infoHash, AnnounceOptions opts, byte[]? peerId = null)
    {
        // peerId arg is ignored - the pre-3.1.0 implementation accepted it but always
        // used the tracker's constructor peerId on the wire. Keeping the parameter for
        // source-compat with any consumer that passes it explicitly.
        _ = peerId;

        var room = RoomKey.FromBytes(infoHash);
        var signalingOpts = new RtcAnnounceOptions
        {
            Event = opts.Event,
            NumWant = opts.Numwant,
            Uploaded = opts.Uploaded,
            Downloaded = opts.Downloaded,
            // WebTorrent's AnnounceOptions.Left is non-nullable long - a negative value
            // historically meant "don't send" but opts.Left is always >= 0 in practice.
            Left = opts.Left >= 0 ? opts.Left : null,
        };
        await _signal.AnnounceAsync(room, signalingOpts).ConfigureAwait(false);
    }

    // ========================
    // BINARY STRING ENCODING (source-compat helpers)
    // ========================
    /// <summary>
    /// Convert bytes to a "binary string" - each byte becomes a latin1 char. Matches
    /// JS <c>hex2bin()</c> / <c>String.fromCharCode()</c> used by bittorrent-tracker.
    /// </summary>
    public static string ToBinaryString(byte[] bytes)
        => new string(bytes.Select(b => (char)b).ToArray());

    /// <summary>Convert a "binary string" back to hex.</summary>
    public static string BinaryStringToHex(string binaryString)
        => Convert.ToHexString(binaryString.Select(c => (byte)c).ToArray()).ToLowerInvariant();

    // ========================
    // DISPOSE
    // ========================
    public async ValueTask DisposeAsync()
    {
        foreach (var h in _handlers.Values) h.DisposePending();
        _handlers.Clear();
        await _signal.DisposeAsync().ConfigureAwait(false);
    }

    // ========================
    // SIGNALING ROOM HANDLER
    // Maps the generic ISignalingRoomHandler callbacks onto WebTorrent's SimplePeer flow.
    // Each Subscribe() creates one of these; it owns the per-torrent pending-offer table.
    // ========================
    private sealed class SimplePeerRoomHandler : ISignalingRoomHandler
    {
        private readonly Func<bool, SimplePeer> _factory;
        private readonly Action<SimplePeer> _onPeer;
        private readonly Action<TrackerUpdate>? _onUpdate;
        private readonly Action<string>? _onWarning;
        private readonly Action<string>? _trackerOnWarning;

        // Outstanding offers WE have generated. offer-id hex → (peer, timeout).
        // Concurrent because multiple offer-generation tasks race to add entries and
        // timer callbacks race to remove them.
        private readonly ConcurrentDictionary<string, (SimplePeer peer, Timer? timer)> _pendingOffers = new();

        public SimplePeerRoomHandler(Func<bool, SimplePeer> factory, Action<SimplePeer> onPeer, Action<TrackerUpdate>? onUpdate, Action<string>? onWarning, Action<string>? trackerOnWarning)
        {
            _factory = factory;
            _onPeer = onPeer;
            _onUpdate = onUpdate;
            _onWarning = onWarning;
            _trackerOnWarning = trackerOnWarning;
        }

        public void RaiseUpdate(TrackerUpdate update) => _onUpdate?.Invoke(update);

        public void DisposePending()
        {
            foreach (var (_, entry) in _pendingOffers)
            {
                entry.timer?.Dispose();
                _ = entry.peer.DisposeAsync();
            }
            _pendingOffers.Clear();
        }

        public async Task<IReadOnlyList<SignalingOffer>> CreateOffersAsync(int count, CancellationToken ct)
        {
            // Match JS WebTorrent: generate all offers in parallel.
            var tasks = new Task<SignalingOffer?>[count];
            for (int i = 0; i < count; i++)
                tasks[i] = GenerateOneAsync(ct);

            var results = await Task.WhenAll(tasks).ConfigureAwait(false);
            return results.Where(r => r is not null).Cast<SignalingOffer>().ToList();
        }

        private async Task<SignalingOffer?> GenerateOneAsync(CancellationToken ct)
        {
            SimplePeer? peer = null;
            try
            {
                peer = _factory(true); // initiator
                var offerIdBytes = RandomNumberGenerator.GetBytes(20);
                var offerIdHex = Convert.ToHexString(offerIdBytes).ToLowerInvariant();

                var tcs = new TaskCompletionSource<SignalData>(TaskCreationOptions.RunContinuationsAsynchronously);
                peer.OnSignal += OnSignal;
                void OnSignal(SignalData s)
                {
                    if (s.Type == "offer") tcs.TrySetResult(s);
                }

                await peer.InitAsync().ConfigureAwait(false);

                using var timeout = new CancellationTokenSource(15_000);
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
                var signal = await tcs.Task.WaitAsync(linked.Token).ConfigureAwait(false);

                if (string.IsNullOrEmpty(signal.Sdp))
                {
                    await peer.DisposeAsync().ConfigureAwait(false);
                    return null;
                }

                var offerTimer = new Timer(static s =>
                {
                    var (dict, hex) = ((ConcurrentDictionary<string, (SimplePeer, Timer?)>, string))s!;
                    if (dict.TryRemove(hex, out var entry))
                        _ = entry.Item1.DisposeAsync();
                }, (_pendingOffers, offerIdHex), OfferTimeout, Timeout.Infinite);

                _pendingOffers[offerIdHex] = (peer, offerTimer);
                return new SignalingOffer(offerIdBytes, signal.Sdp);
            }
            catch (OperationCanceledException)
            {
                if (peer is not null) await peer.DisposeAsync().ConfigureAwait(false);
                return null;
            }
            catch (Exception ex)
            {
                _onWarning?.Invoke($"Offer generation failed: {ex.Message}");
                _trackerOnWarning?.Invoke($"Offer generation failed: {ex.Message}");
                if (peer is not null) await peer.DisposeAsync().ConfigureAwait(false);
                return null;
            }
        }

        public async Task<string?> HandleOfferAsync(byte[] remotePeerId, byte[] offerId, string offerSdp, CancellationToken ct)
        {
            SimplePeer? peer = null;
            try
            {
                peer = _factory(false); // responder

                var answerTcs = new TaskCompletionSource<SignalData>(TaskCreationOptions.RunContinuationsAsynchronously);
                peer.OnSignal += OnSignal;
                void OnSignal(SignalData s)
                {
                    if (s.Type == "answer") answerTcs.TrySetResult(s);
                }

                await peer.InitAsync().ConfigureAwait(false);

                // Route the peer to the consumer BEFORE signaling the offer so the
                // consumer can wire up OnData / OnConnect handlers before traffic
                // starts flowing.
                _onPeer(peer);

                _ = peer.Signal(new SignalData { Type = "offer", Sdp = offerSdp });

                using var timeout = new CancellationTokenSource(15_000);
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
                var signal = await answerTcs.Task.WaitAsync(linked.Token).ConfigureAwait(false);
                return signal.Sdp;
            }
            catch (OperationCanceledException)
            {
                return null;
            }
            catch (Exception ex)
            {
                _onWarning?.Invoke($"Answer generation failed: {ex.Message}");
                _trackerOnWarning?.Invoke($"Answer generation failed: {ex.Message}");
                if (peer is not null) await peer.DisposeAsync().ConfigureAwait(false);
                return null;
            }
        }

        public Task HandleAnswerAsync(byte[] remotePeerId, byte[] offerId, string answerSdp, CancellationToken ct)
        {
            var offerIdHex = Convert.ToHexString(offerId).ToLowerInvariant();
            if (!_pendingOffers.TryRemove(offerIdHex, out var entry))
                return Task.CompletedTask;

            entry.timer?.Dispose();
            _onPeer(entry.peer);
            _ = entry.peer.Signal(new SignalData { Type = "answer", Sdp = answerSdp });
            return Task.CompletedTask;
        }
    }
}

// ========================
// SUPPORTING TYPES (public surface preserved)
// ========================

public class AnnounceOptions
{
    public long Uploaded { get; set; }
    public long Downloaded { get; set; }
    public long Left { get; set; }
    public string? Event { get; set; }
    public int Numwant { get; set; } = 10; // JS: MAX_ANNOUNCE_PEERS = 10
}

public class TrackerUpdate
{
    public string AnnounceUrl { get; set; } = "";
    public int Complete { get; set; }
    public int Incomplete { get; set; }
}
