namespace SpawnDev.WebTorrent;

/// <summary>
/// Represents a peer in the torrent swarm.
/// Direct 1:1 port of webtorrent/lib/peer.js.
/// Manages connection lifecycle, wire creation, handshake, and timeouts.
/// </summary>
public class Peer
{
    // ========================
    // CONSTANTS (match JS exactly)
    // ========================
    public const int ConnectTimeoutTcp = 5_000;
    public const int ConnectTimeoutUtp = 5_000;
    public const int ConnectTimeoutWebrtc = 25_000;
    public const int HandshakeTimeoutMs = 25_000;

    /// <summary>Diagnostic ring buffer of recent peer-drop reasons (why peers leave) for streaming/RTC triage.
    /// Captured in <see cref="Destroy"/> independent of VerboseLogging (PMT drops high-volume console output).</summary>
    public static readonly System.Collections.Concurrent.ConcurrentQueue<string> RecentDrops = new();

    // Peer types
    public const string TypeTcpIncoming = "tcpIncoming";
    public const string TypeTcpOutgoing = "tcpOutgoing";
    public const string TypeUtpIncoming = "utpIncoming";
    public const string TypeUtpOutgoing = "utpOutgoing";
    public const string TypeWebrtc = "webrtc";
    public const string TypeWebSeed = "webSeed";

    // Discovery sources
    public const string SourceManual = "manual";
    public const string SourceTracker = "tracker";
    public const string SourceDht = "dht";
    public const string SourceLsd = "lsd";
    public const string SourceUtPex = "ut_pex";

    // ========================
    // STATE (match JS peer properties)
    // ========================

    /// <summary>Peer identifier: "ip:port", peer ID (WebRTC), or URL (web seed).</summary>
    public string Id { get; }

    /// <summary>Connection type.</summary>
    public string Type { get; }

    /// <summary>IP:port address (if known).</summary>
    public string? Addr { get; set; }

    /// <summary>Underlying connection (SimplePeer for WebRTC, TcpClient for TCP).</summary>
    public object? Conn { get; set; }

    /// <summary>Parent torrent.</summary>
    public Torrent? Swarm { get; set; }

    /// <summary>BitTorrent wire protocol instance.</summary>
    public Wire? WireInstance { get; set; }

    /// <summary>Discovery source.</summary>
    public string? Source { get; set; }

    /// <summary>Is the connection established?</summary>
    public bool Connected { get; set; }

    /// <summary>Is this peer destroyed?</summary>
    public bool Destroyed { get; set; }

    /// <summary>Connection retry count (outgoing TCP).</summary>
    public int Retries { get; set; }

    /// <summary>Has handshake been sent?</summary>
    public bool SentHandshake { get; set; }

    // Timeouts
    private CancellationTokenSource? _connectTimeoutCts;
    private CancellationTokenSource? _handshakeTimeoutCts;

    // ========================
    // EVENTS
    // ========================
    public event Action? OnConnect;
    public event Action<Exception?>? OnDisconnect;
    public event Action<long>? OnDownload;  // bytes
    public event Action<long>? OnUpload;    // bytes

    /// <summary>Fire download event (called by Torrent when wire data arrives).</summary>
    internal void EmitDownload(long bytes) => OnDownload?.Invoke(bytes);

    /// <summary>Fire upload event (called by Torrent when wire data is sent).</summary>
    internal void EmitUpload(long bytes) => OnUpload?.Invoke(bytes);

    // ========================
    // CONSTRUCTOR
    // ========================

    public Peer(string id, string type)
    {
        Id = id;
        Type = type;
    }

    // ========================
    // STATIC FACTORY METHODS (match JS Peer.createX)
    // ========================

    public static Peer CreateWebRTCPeer(SimplePeer conn)
    {
        var peer = new Peer(Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(10)).ToLowerInvariant(), TypeWebrtc);
        peer.Conn = conn;
        return peer;
    }

    public static Peer CreateWebSeedPeer(string url)
    {
        var peer = new Peer(url, TypeWebSeed);
        return peer;
    }

    // ========================
    // CONNECTION LIFECYCLE
    // ========================

    /// <summary>
    /// Called when the peer connection is established.
    /// Creates the Wire, pipes data between connection and wire, starts handshake.
    /// </summary>
    public void OnConnected()
    {
        if (Destroyed) return;
        Connected = true;
        OnConnect?.Invoke();

        CancelConnectTimeout();

        // Create the wire
        var wire = new Wire(Type);
        WireInstance = wire;

        // Back-reference so the wire (and consumers walking torrent.Wires, e.g.
        // SpawnDev.ILGPU.P2P.P2PWebRtcBridge) can read transport-level liveness via
        // simplePeer.IsTransportDead without needing to look the peer up by scanning a
        // collection. Set unconditionally — null is fine for non-SimplePeer transports.
        wire.SimplePeer = Conn as SimplePeer;

        // Wire up the transport: connection → wire → connection
        if (Conn is SimplePeer simplePeer)
        {
            // Data from WebRTC → wire parser
            simplePeer.OnData += (data) => wire.DataReceived(data);

            // Data from wire → WebRTC
            wire.SendRaw = async (data) =>
            {
                try { await simplePeer.Send(data); }
                catch (Exception ex)
                {
                    if (WebTorrentClient.VerboseLogging)
                        Console.WriteLine($"[Peer] SendRaw failed ({data.Length} bytes): {ex.GetType().Name}: {ex.Message}");
                    Destroy(null);
                }
            };

            // Connection close → destroy peer
            simplePeer.OnClose += () =>
            {
                if (WebTorrentClient.VerboseLogging)
                    Console.WriteLine($"[Peer] SimplePeer OnClose fired → Destroy");
                Destroy(null);
            };
            simplePeer.OnError += (err) =>
            {
                if (WebTorrentClient.VerboseLogging)
                    Console.WriteLine($"[Peer] SimplePeer OnError: {err?.Message ?? "null"} → Destroy");
                Destroy(err);
            };
        }

        // Wire lifecycle
        wire.OnClose += () =>
        {
            if (WebTorrentClient.VerboseLogging)
                Console.WriteLine($"[Peer] Wire OnClose fired → Destroy");
            Destroy(null);
        };

        // Wire handshake handler
        wire.OnHandshake += (infoHash, peerId, extensions) =>
        {
            OnHandshake(infoHash, peerId);
        };

        StartHandshakeTimeout();

        // For outgoing connections, send handshake immediately
        if (Swarm != null && Type != TypeTcpIncoming && !SentHandshake)
        {
            _ = SendHandshake();
        }
    }

    /// <summary>Send the BitTorrent handshake.</summary>
    public async Task SendHandshake()
    {
        if (WireInstance == null || Swarm == null) return;
        if (SentHandshake) return;
        SentHandshake = true;

        // Use the wire info hash — v1 when present, else first 20 bytes of v2 per the
        // cross-client pure-v2 wire convention. Covers v1-only, hybrid, and pure-v2
        // torrents uniformly.
        var infoHash = HexToBytes(Swarm.WireInfoHashHex);
        var peerId = HexToBytes(Swarm.PeerIdHex ?? "");
        await WireInstance.Handshake(infoHash, peerId, dht: true, fast: true);
    }

    /// <summary>Handle incoming handshake from remote peer.</summary>
    private void OnHandshake(string infoHash, string peerId)
    {
        CancelHandshakeTimeout();

        // If we haven't sent our handshake yet (incoming connection), send it now
        if (!SentHandshake && Swarm != null)
            _ = SendHandshake();
    }

    // ========================
    // TIMEOUTS
    // ========================

    public void StartConnectTimeout()
    {
        int timeoutMs = Type switch
        {
            TypeTcpIncoming or TypeTcpOutgoing => ConnectTimeoutTcp,
            TypeUtpIncoming or TypeUtpOutgoing => ConnectTimeoutUtp,
            TypeWebrtc => ConnectTimeoutWebrtc,
            _ => ConnectTimeoutWebrtc
        };

        _connectTimeoutCts = new CancellationTokenSource();
        var ct = _connectTimeoutCts.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(timeoutMs, ct);
                if (!ct.IsCancellationRequested && !Connected && !Destroyed)
                    Destroy(new TimeoutException($"Connect timeout ({Type})"));
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                // Never let this fire-and-forget timeout task fault into an UNOBSERVED task exception (the
                // browser surfaces that as an uncaught console error with a confusing async stack).
                if (WebTorrentClient.VerboseLogging)
                    Console.WriteLine($"[Peer] connect-timeout task error: {ex.GetType().Name}: {ex.Message}");
            }
        });
    }

    public void StartHandshakeTimeout()
    {
        _handshakeTimeoutCts = new CancellationTokenSource();
        var ct = _handshakeTimeoutCts.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(HandshakeTimeoutMs, ct);
                if (!ct.IsCancellationRequested && !Destroyed)
                    Destroy(new TimeoutException("Handshake timeout"));
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                if (WebTorrentClient.VerboseLogging)
                    Console.WriteLine($"[Peer] handshake-timeout task error: {ex.GetType().Name}: {ex.Message}");
            }
        });
    }

    private void CancelConnectTimeout() => _connectTimeoutCts?.Cancel();
    private void CancelHandshakeTimeout() => _handshakeTimeoutCts?.Cancel();

    // ========================
    // DESTROY
    // ========================

    public void Destroy(Exception? err = null)
    {
        if (Destroyed) return;
        Destroyed = true;
        Connected = false;

        // Record WHY this peer dropped (non-verbose) so peer-stability triage can see it without console logs.
        {
            var dr = err != null ? $"{err.GetType().Name}:{err.Message}" : "null";
            var idShort = !string.IsNullOrEmpty(Id) ? Id[..Math.Min(6, Id.Length)] : "?";
            RecentDrops.Enqueue($"{idShort}/{Type}={dr}");
            while (RecentDrops.Count > 24) RecentDrops.TryDequeue(out _);
        }

        if (WebTorrentClient.VerboseLogging)
        {
            var reason = err != null ? $"{err.GetType().Name}: {err.Message}" : "null";
            // Connect/handshake timeouts are EXPECTED on a public swarm (most peers are unreachable behind
            // NAT) - log them as a concise one-liner. Reserve the stack dump for UNEXPECTED destroys, where
            // "who destroyed this peer" is the actual question (the 2026-05-03 sctp-cascade investigation).
            if (err is TimeoutException)
                Console.WriteLine($"[Peer] Destroy(Id={Id}, err={reason})");
            else
                Console.WriteLine($"[Peer] Destroy(Id={Id}, err={reason})\n{new System.Diagnostics.StackTrace(1, false)}");
        }

        CancelConnectTimeout();
        CancelHandshakeTimeout();

        WireInstance?.Destroy();

        // Do NOT call sp.DisposeAsync() here.
        //
        // SimplePeer.DisposeAsync (RtcPeer.DisposeAsync line 551) calls `_pc?.Close()`
        // on the underlying RTCPeerConnection. Closing a PC while another PC to the
        // same remote peer is alive triggers Chromium's
        // `sctp-failure | User-Initiated Abort | sctpCauseCode=12` cascade onto the
        // surviving sibling PC's data channel. Both sides observe the cascade
        // simultaneously and the entire peer-to-peer connection drops.
        //
        // This is the root cause of the 2026-05-03 RenderMandelbrot live repro that
        // survived the rc.1 BT-layer "don't destroy on duplicate" fix and the rc.2-rc.5
        // signaling-layer dedup experiments. Captain reproduced it with 1 worker, no
        // user input, no clicking; the cascade fired within seconds of BT handshake
        // completion. Chain: HandshakeTimeout (25s) on a duplicate wire whose
        // OnHandshake race-cancelled too late → Peer.Destroy(TimeoutException) →
        // sp.DisposeAsync → _pc.Close() → cascade onto the survivor.
        //
        // Letting the underlying PC live past Peer.Destroy:
        //  - Normal disconnect (remote closed): SimplePeer.OnClose fires from the
        //    REMOTE side; the PC is already closed; no cascade because the close
        //    didn't originate from us. The PC's IDisposable releases via GC.
        //  - Timeout / error destroy: Peer is gone from the BT layer, but the PC
        //    sits alive briefly. Chromium's internal idle/SCTP-heartbeat timeout
        //    (~30s no traffic) closes it from the inside without firing the
        //    User-Initiated Abort signal. No cascade.
        //
        // Cost: a brief resource leak between Peer.Destroy and the PC's natural
        // expiration. Net negligible (one PC, ~100KB) compared to the cascade
        // catastrophe (entire swarm goes to peerCount=0).

        OnDisconnect?.Invoke(err);
    }

    // ========================
    // HELPERS
    // ========================

    private static byte[] HexToBytes(string hex)
    {
        if (string.IsNullOrEmpty(hex)) return Array.Empty<byte>();
        return Convert.FromHexString(hex);
    }
}
