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

        // Wire up the transport: connection → wire → connection
        if (Conn is SimplePeer simplePeer)
        {
            // Data from WebRTC → wire parser
            simplePeer.OnData += (data) => wire.DataReceived(data);

            // Data from wire → WebRTC
            wire.SendRaw = async (data) =>
            {
                try { await simplePeer.Send(data); }
                catch { Destroy(null); }
            };

            // Connection close → destroy peer
            simplePeer.OnClose += () => Destroy(null);
            simplePeer.OnError += (err) => Destroy(err);
        }

        // Wire lifecycle
        wire.OnClose += () => Destroy(null);

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

        var infoHash = HexToBytes(Swarm.InfoHash ?? "");
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

        CancelConnectTimeout();
        CancelHandshakeTimeout();

        WireInstance?.Destroy();

        if (Conn is SimplePeer sp)
            _ = sp.DisposeAsync();

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
