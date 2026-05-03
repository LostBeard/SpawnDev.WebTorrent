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
    /// underlying <see cref="TrackerSignalingClient"/> pool and the cross-tracker
    /// dedup registry.</summary>
    public static void ClearPool()
    {
        foreach (var t in _pool.Values)
            _ = t.DisposeAsync();
        _pool.Clear();
        TrackerSignalingClient.ClearPool();
        CrossTrackerDedupRegistry.ClearPool();
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
    private readonly string _localPeerIdHex;

    private WebSocketTracker(string announceUrl, byte[] peerId, Func<bool, SimplePeer> createPeerFunc)
    {
        _defaultFactory = createPeerFunc;
        _localPeerIdHex = Convert.ToHexString(peerId).ToLowerInvariant();
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
        // Cross-tracker dedup: one shared registry per (info_hash, local_peer_id).
        // Every WebSocketTracker for the same torrent (e.g. when DefaultTrackers
        // contains multiple URLs) consults the same registry, so a SimplePeer for
        // a given remote is only created ONCE across all trackers - not once per
        // tracker. Without this, two trackers connecting the same swarm produce
        // 2x the RTCPeerConnection count for every logical peer pair.
        var infoHashHex = Convert.ToHexString(infoHash).ToLowerInvariant();
        var dedup = CrossTrackerDedupRegistry.GetOrCreate(infoHashHex, _localPeerIdHex);
        var handler = new SimplePeerRoomHandler(dedup, peerFactory ?? _defaultFactory, onPeer, onUpdate, onWarning, OnWarning);
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
        // Cross-tracker dedup registry shared with every other WebSocketTracker for
        // the same (info_hash, local_peer_id). Holds:
        //   * the local peer_id hex (for the cross-side-stable lex-compare tiebreaker:
        //     larger peer_id is canonical answerer-side, smaller is canonical
        //     offerer-side), and
        //   * the set of remote peer_ids we have already accepted an offer from OR
        //     sent an answer to.
        // Together this prevents:
        //   (1) the same logical remote occupying two RTCPeerConnections after one
        //       announce round (the JS tracker can pair the same candidate against
        //       multiple of our positional offers when surplus offers exist),
        //   (2) cross-side mismatch when both peers announce simultaneously and
        //       race HandleOfferAsync vs HandleAnswerAsync to opposite PCs (peerCount
        //       stays 0/0 - failure mode of rc.2's TryAdd-only dedup), AND
        //   (3) cross-tracker duplication when the same logical remote announces to
        //       multiple trackers we are subscribed to - each tracker's offer-relay
        //       would otherwise produce a fresh PC if the registry were per-tracker.
        // Without offer-relay dedup we collapse duplicates at the BT-handshake layer
        // (Torrent.cs:891+), and that collapse triggers Chromium's
        // `sctp-failure | User-Initiated Abort` cascade on the SURVIVING PC, killing
        // the entire peer-to-peer connection (verified 2026-05-03 Stable + Canary).
        // Mirrors and extends the pattern in
        // `SpawnDev.RTC.Signaling.RtcPeerConnectionRoomHandler.HandleOfferAsync:111`.
        // See `Docs/protocol-reference/08-offer-pairing-and-dedup.md`.
        private readonly CrossTrackerDedupRegistry _dedup;
        private readonly Func<bool, SimplePeer> _factory;
        private readonly Action<SimplePeer> _onPeer;
        private readonly Action<TrackerUpdate>? _onUpdate;
        private readonly Action<string>? _onWarning;
        private readonly Action<string>? _trackerOnWarning;

        // Outstanding offers WE have generated. offer-id hex → (peer, timeout).
        // Concurrent because multiple offer-generation tasks race to add entries and
        // timer callbacks race to remove them.
        private readonly ConcurrentDictionary<string, (SimplePeer peer, Timer? timer)> _pendingOffers = new();

        public SimplePeerRoomHandler(CrossTrackerDedupRegistry dedup, Func<bool, SimplePeer> factory, Action<SimplePeer> onPeer, Action<TrackerUpdate>? onUpdate, Action<string>? onWarning, Action<string>? trackerOnWarning)
        {
            _dedup = dedup;
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
            var remoteHex = Convert.ToHexString(remotePeerId).ToLowerInvariant();

            // Allocate the responder peer first so the registry can stash a reference
            // in the slot at claim time (needed for the replace-on-conflict path).
            // _factory is synchronous; the expensive WebRTC init happens later via
            // peer.InitAsync. If the slot rejects us we dispose immediately, paying a
            // SimplePeer allocation but no PC negotiation.
            var newPeer = _factory(false); // responder

            // First-claim wins; only on conflict does the cross-side-stable tiebreaker
            // (LARGER peer_id is canonical answerer-side) apply. See
            // CrossTrackerDedupRegistry XML doc for the full state machine.
            if (!_dedup.TryAcceptOffer(remoteHex, newPeer, out var toDispose))
            {
                await newPeer.DisposeAsync().ConfigureAwait(false);
                return null;
            }

            // We replaced an existing offerer-side peer (rare: simultaneous-announce
            // race won by us-as-answerer per tiebreaker). Dispose async; the consumer
            // already saw the replaced peer via _onPeer earlier and will clean up its
            // bookkeeping when its OnClose fires (Release at that point is a no-op
            // since the slot now holds newPeer, not the replaced peer).
            if (toDispose != null) _ = toDispose.DisposeAsync();

            try
            {
                var answerTcs = new TaskCompletionSource<SignalData>(TaskCreationOptions.RunContinuationsAsynchronously);
                newPeer.OnSignal += OnSignal;
                void OnSignal(SignalData s)
                {
                    if (s.Type == "answer") answerTcs.TrySetResult(s);
                }

                // Cleanup: when the SimplePeer disconnects, free the slot — but ONLY
                // if newPeer is still the slot's current owner (Release is no-op if
                // we were replaced, which is what we want).
                newPeer.OnClose += () => _dedup.Release(remoteHex, newPeer);

                await newPeer.InitAsync().ConfigureAwait(false);

                // Route the peer to the consumer BEFORE signaling the offer so the
                // consumer can wire up OnData / OnConnect handlers before traffic
                // starts flowing.
                _onPeer(newPeer);

                _ = newPeer.Signal(new SignalData { Type = "offer", Sdp = offerSdp });

                using var timeout = new CancellationTokenSource(15_000);
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
                var signal = await answerTcs.Task.WaitAsync(linked.Token).ConfigureAwait(false);
                return signal.Sdp;
            }
            catch (OperationCanceledException)
            {
                _dedup.Release(remoteHex, newPeer);
                return null;
            }
            catch (Exception ex)
            {
                _dedup.Release(remoteHex, newPeer);
                _onWarning?.Invoke($"Answer generation failed: {ex.Message}");
                _trackerOnWarning?.Invoke($"Answer generation failed: {ex.Message}");
                await newPeer.DisposeAsync().ConfigureAwait(false);
                return null;
            }
        }

        public Task HandleAnswerAsync(byte[] remotePeerId, byte[] offerId, string answerSdp, CancellationToken ct)
        {
            var offerIdHex = Convert.ToHexString(offerId).ToLowerInvariant();
            if (!_pendingOffers.TryRemove(offerIdHex, out var entry))
                return Task.CompletedTask;

            entry.timer?.Dispose();

            var remoteHex = Convert.ToHexString(remotePeerId).ToLowerInvariant();

            // Mirror of HandleOfferAsync: first-claim wins; tiebreaker only on conflict.
            // SMALLER peer_id is canonical offerer-side.
            if (!_dedup.TryAcceptAnswer(remoteHex, entry.peer, out var toDispose))
            {
                _ = entry.peer.DisposeAsync();
                return Task.CompletedTask;
            }

            // Replaced an answerer-side peer (we won as offerer per tiebreaker).
            if (toDispose != null) _ = toDispose.DisposeAsync();

            entry.peer.OnClose += () => _dedup.Release(remoteHex, entry.peer);

            _onPeer(entry.peer);
            _ = entry.peer.Signal(new SignalData { Type = "answer", Sdp = answerSdp });
            return Task.CompletedTask;
        }
    }
}

// ========================
// CROSS-TRACKER DEDUP REGISTRY
// ========================
/// <summary>
/// Per-(info_hash, local_peer_id) registry that holds at most ONE
/// <see cref="SimplePeer"/> per logical remote peer, shared across every
/// <see cref="WebSocketTracker"/> subscribed to the same torrent.
/// </summary>
/// <remarks>
/// <para>Two competing claim paths exist per remote: <see cref="TryAcceptOffer"/>
/// fires when an offer-relay arrives (we are responder-side), and
/// <see cref="TryAcceptAnswer"/> fires when an answer to our pending offer arrives
/// (we are offerer-side). Each path also fires at most once per (tracker, remote)
/// — multiple trackers all consult this same registry, so the cross-tracker dedup
/// is a "first-claim wins" race against the tiebreaker.</para>
///
/// <para>The first-arriving claim ALWAYS wins (slot empty → claim succeeds). This
/// is the asymmetric-announce case: only one peer announces, only one direction
/// of pairing exists, only one handler fires; we MUST accept it or no connection
/// forms. The previous (rc.3+rc.4 first-cut) "drop unconditionally if tiebreaker
/// loses" rule broke this: when only the larger peer announced, the smaller peer
/// dropped the offer-relay, and the larger peer's pending offer timed out unanswered.
/// `P2PSwarm.TwoTab_PeerDiscovery` failed ~50% of the time because that side
/// happened to be the smaller peer.</para>
///
/// <para>The cross-side-stable tiebreaker (LARGER peer_id is canonical answerer-side,
/// SMALLER is canonical offerer-side) only applies on CONFLICT — when the slot is
/// already claimed by the OTHER path:</para>
/// <list type="bullet">
///   <item><b>HandleOfferAsync arrives, slot held by HandleAnswerAsync:</b> the
///   simultaneous-announce race fired. If WE are the larger (answerer-side per
///   tiebreaker), REPLACE — dispose the existing offerer-side peer, accept this
///   offer. If smaller, KEEP the existing offerer-side peer, drop the offer.</item>
///   <item><b>HandleAnswerAsync arrives, slot held by HandleOfferAsync:</b> mirror.
///   If WE are smaller (offerer-side per tiebreaker), REPLACE. If larger, KEEP
///   existing answerer-side peer, dispose this pending peer.</item>
///   <item><b>Same-type conflict</b> (e.g. two trackers each delivered an offer-relay
///   for the same remote): the first-arriving is already correct; second-arriving
///   drops with no replacement.</item>
/// </list>
///
/// <para>Both peers apply the same comparison so they converge on the SAME PC
/// pair (offerer-side at the smaller peer, answerer-side at the larger).</para>
///
/// <para>Without offer-relay dedup we collapse duplicates at the BT-handshake layer
/// (Torrent.cs:891+), and that collapse triggers Chromium's
/// <c>sctp-failure | User-Initiated Abort</c> cascade on the SURVIVING PC,
/// killing the entire peer-to-peer connection (verified Chrome Stable + Canary,
/// 2026-05-03 against the live RenderMandelbrot demo on lostbeard.github.io).</para>
///
/// <para>See <c>Docs/protocol-reference/08-offer-pairing-and-dedup.md</c>.</para>
/// </remarks>
internal sealed class CrossTrackerDedupRegistry
{
    public enum SlotType { Offer, Answer }

    private sealed class Slot
    {
        public SlotType Type;
        public SimplePeer Peer = default!;
    }

    private static readonly ConcurrentDictionary<string, CrossTrackerDedupRegistry> _pool = new();

    /// <summary>Get or create the registry for the given (info_hash hex, local peer_id hex) pair.</summary>
    public static CrossTrackerDedupRegistry GetOrCreate(string infoHashHex, string localPeerIdHex)
    {
        var key = infoHashHex + ":" + localPeerIdHex;
        return _pool.GetOrAdd(key, _ => new CrossTrackerDedupRegistry(localPeerIdHex));
    }

    /// <summary>Drop every pooled registry. Called from <see cref="WebSocketTracker.ClearPool"/>.</summary>
    public static void ClearPool() => _pool.Clear();

    public string LocalPeerIdHex { get; }
    private readonly Dictionary<string, Slot> _slots = new();
    private readonly object _lock = new();

    private CrossTrackerDedupRegistry(string localPeerIdHex) { LocalPeerIdHex = localPeerIdHex; }

    /// <summary>Try to claim the slot for an incoming offer-relay. Returns false if
    /// the slot is held by another path that wins the tiebreaker; in that case the
    /// caller MUST dispose <paramref name="newPeer"/>. Returns true otherwise; if
    /// <paramref name="toDispose"/> is non-null, an existing peer was REPLACED and
    /// the caller MUST dispose it (the caller has already wired the new peer into
    /// the consumer via <c>_onPeer</c> by this point, so the consumer's bookkeeping
    /// will see the new peer).</summary>
    public bool TryAcceptOffer(string remoteHex, SimplePeer newPeer, out SimplePeer? toDispose)
    {
        toDispose = null;
        lock (_lock)
        {
            if (!_slots.TryGetValue(remoteHex, out var existing))
            {
                _slots[remoteHex] = new Slot { Type = SlotType.Offer, Peer = newPeer };
                return true;
            }
            if (existing.Type == SlotType.Answer)
            {
                // Conflict: HandleAnswerAsync wired our pending offer to this remote
                // first. Tiebreaker: LARGER peer_id is canonical answerer-side.
                if (string.CompareOrdinal(LocalPeerIdHex, remoteHex) > 0)
                {
                    toDispose = existing.Peer;
                    existing.Type = SlotType.Offer;
                    existing.Peer = newPeer;
                    return true;
                }
                return false; // we are smaller, keep the offerer-side peer
            }
            // Same-type conflict: another tracker already delivered an offer for this remote.
            return false;
        }
    }

    /// <summary>Mirror of <see cref="TryAcceptOffer"/> for incoming answers.</summary>
    public bool TryAcceptAnswer(string remoteHex, SimplePeer pendingPeer, out SimplePeer? toDispose)
    {
        toDispose = null;
        lock (_lock)
        {
            if (!_slots.TryGetValue(remoteHex, out var existing))
            {
                _slots[remoteHex] = new Slot { Type = SlotType.Answer, Peer = pendingPeer };
                return true;
            }
            if (existing.Type == SlotType.Offer)
            {
                // Conflict: HandleOfferAsync accepted this remote's offer-relay first.
                // Tiebreaker: SMALLER peer_id is canonical offerer-side.
                if (string.CompareOrdinal(LocalPeerIdHex, remoteHex) < 0)
                {
                    toDispose = existing.Peer;
                    existing.Type = SlotType.Answer;
                    existing.Peer = pendingPeer;
                    return true;
                }
                return false; // we are larger, keep the answerer-side peer
            }
            // Same-type conflict: another tracker already delivered an answer.
            return false;
        }
    }

    /// <summary>Free the slot for the given remote IFF <paramref name="ownerPeer"/>
    /// is still the slot's current owner. Wired from each accepted SimplePeer's
    /// <see cref="SimplePeer.OnClose"/>; if the peer was REPLACED before disconnect,
    /// the slot is held by the replacement and we must NOT free it on the old peer's
    /// OnClose.</summary>
    public void Release(string remoteHex, SimplePeer ownerPeer)
    {
        lock (_lock)
        {
            if (_slots.TryGetValue(remoteHex, out var existing) && ReferenceEquals(existing.Peer, ownerPeer))
                _slots.Remove(remoteHex);
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
    // JS bittorrent-tracker reference caps client-side numwant at 5
    // (lib/client/websocket-tracker.js:61: `numwant = Math.min(opts.numwant, 5)`).
    // Our previous default of 10 was double and inflated duplicate-PC formation 2x.
    // See `Docs/protocol-reference/08-offer-pairing-and-dedup.md` for the protocol details.
    public int Numwant { get; set; } = 5;
    /// <summary>
    /// TCP listener port to advertise to the tracker. Mainline trackers
    /// (HTTP/UDP) include this in their compact peer list so other clients can
    /// dial in by IP+port. <c>0</c> = no TCP listener (default; legacy behavior
    /// where <see cref="HttpTracker"/> hardcoded <c>port=0</c> and
    /// <see cref="UdpTrackerClient"/> hardcoded <c>6881</c>). WebRTC tracker
    /// signaling ignores this field. Set automatically by <see cref="Torrent"/>
    /// when <see cref="WebTorrentClientOptions.AdvertiseTcpListenerToTrackers"/>
    /// is true and a <see cref="TcpListenerService"/> is running.
    /// </summary>
    public int Port { get; set; }
}

public class TrackerUpdate
{
    public string AnnounceUrl { get; set; } = "";
    public int Complete { get; set; }
    public int Incomplete { get; set; }
}
