using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace SpawnDev.WebTorrent;

/// <summary>
/// WebSocket BitTorrent tracker client.
/// Direct 1:1 port of bittorrent-tracker/lib/client/websocket-tracker.js.
/// Handles announce, offer/answer relay, reconnection, and offer generation.
/// </summary>
public class WebSocketTracker : IAsyncDisposable
{
    // Constants matching JS exactly
    public const int ReconnectMinimum = 10_000;
    public const int ReconnectMaximum = 3_600_000;     // 1 hour
    public const int ReconnectVariance = 300_000;       // 5 min
    public const int OfferTimeout = 50_000;             // 50s
    public const int DefaultAnnounceInterval = 120_000;  // 120s - matches typical tracker default
    public const int MaxOffers = 10;  // JS: MAX_ANNOUNCE_PEERS = 10

    // ========================
    // SHARED SOCKET POOL (matches JS socketPool pattern)
    // One WebSocket per tracker URL, shared across all torrents
    // ========================
    private static readonly Dictionary<string, WebSocketTracker> _socketPool = new();
    private static readonly object _poolLock = new();

    /// <summary>Clear all shared tracker connections. Use in tests that create multiple clients.</summary>
    public static void ClearPool()
    {
        lock (_poolLock)
        {
            foreach (var t in _socketPool.Values)
                _ = t.DisposeAsync();
            _socketPool.Clear();
        }
    }

    /// <summary>
    /// Get or create a shared WebSocketTracker for the given URL and peer ID.
    /// Keyed by (url + peerId) so different clients get separate connections.
    /// </summary>
    public static WebSocketTracker GetOrCreate(string announceUrl, byte[] peerId, Func<bool, SimplePeer> createPeerFunc)
    {
        lock (_poolLock)
        {
            var key = announceUrl + ":" + Convert.ToHexString(peerId);
            if (_socketPool.TryGetValue(key, out var existing) && !existing.Destroyed)
                return existing;
            var tracker = new WebSocketTracker(announceUrl, peerId, createPeerFunc);
            _socketPool[key] = tracker;
            return tracker;
        }
    }

    // ========================
    // STATE
    // ========================

    public string AnnounceUrl { get; }
    public bool Destroyed { get; private set; }
    public bool Reconnecting { get; private set; }
    public int Retries { get; private set; }

    private ClientWebSocket? _ws;
    private bool _connected;
    private readonly List<(byte[] infoHash, AnnounceOptions opts, byte[]? peerId)> _pendingAnnounces = new();
    private bool _expectingResponse;
    private string? _trackerId;
    private Timer? _reconnectTimer;
    private Timer? _announceTimer;
    private CancellationTokenSource? _readCts;

    // Peer tracking: offerId (hex) → (SimplePeer, timeout, infoHashBinary)
    private readonly Dictionary<string, (SimplePeer peer, Timer? timeout, string infoHashBinary)> _pendingOffers = new();

    // Binary strings for tracker protocol (match JS hex2bin encoding)
    private readonly byte[] _peerId;       // 20 bytes
    private readonly string _peerIdBinary;    // latin1 binary string

    // Per-info_hash event handlers and peer factories - routes responses to the correct torrent
    private readonly Dictionary<string, Action<SimplePeer>> _peerHandlers = new();
    private readonly Dictionary<string, Action<TrackerUpdate>> _updateHandlers = new();
    private readonly Dictionary<string, Action<string>> _warningHandlers = new();
    private readonly Dictionary<string, Func<bool, SimplePeer>> _peerFactories = new();

    // Default factory for creating SimplePeer instances (fallback)
    private readonly Func<bool, SimplePeer> _createPeerFunc;

    // ========================
    // EVENTS
    // ========================

    /// <summary>A WebRTC peer is ready (offer received and answered, or our offer was answered).</summary>
    public event Action<SimplePeer>? OnPeer;

    /// <summary>Tracker response with swarm stats.</summary>
    public event Action<TrackerUpdate>? OnUpdate;

    /// <summary>Non-fatal warning.</summary>
    public event Action<string>? OnWarning;

    /// <summary>Tracker announced (for Discovery to forward).</summary>
    public event Action? OnAnnounce;

    // ========================
    // CONSTRUCTOR
    // ========================

    private WebSocketTracker(string announceUrl, byte[] peerId, Func<bool, SimplePeer> createPeerFunc)
    {
        AnnounceUrl = announceUrl;
        _peerId = peerId;
        _peerIdBinary = ToBinaryString(peerId);
        _createPeerFunc = createPeerFunc;

        _ = OpenSocketAsync();
    }

    /// <summary>Subscribe to events for a specific info_hash on this shared tracker connection.</summary>
    public void Subscribe(byte[] infoHash, Action<SimplePeer> onPeer, Func<bool, SimplePeer>? peerFactory = null, Action<TrackerUpdate>? onUpdate = null, Action<string>? onWarning = null)
    {
        var key = ToBinaryString(infoHash);
        _peerHandlers[key] = onPeer;
        if (peerFactory != null) _peerFactories[key] = peerFactory;
        if (onUpdate != null) _updateHandlers[key] = onUpdate;
        if (onWarning != null) _warningHandlers[key] = onWarning;
    }

    /// <summary>Unsubscribe a torrent from this shared tracker connection.</summary>
    public void Unsubscribe(byte[] infoHash)
    {
        var key = ToBinaryString(infoHash);
        _peerHandlers.Remove(key);
        _updateHandlers.Remove(key);
        _warningHandlers.Remove(key);
    }

    // ========================
    // ANNOUNCE
    // ========================

    /// <summary>Send announce to tracker with WebRTC offers for a specific info_hash.</summary>
    public async Task AnnounceAsync(byte[] infoHash, AnnounceOptions opts, byte[]? peerId = null)
    {
        if (Destroyed || Reconnecting) { if (WebTorrentClient.VerboseLogging) Console.WriteLine($"[WSTracker] AnnounceAsync skipped: destroyed={Destroyed} reconnecting={Reconnecting}"); return; }
        var infoHashBinary = ToBinaryString(infoHash);
        if (!_connected)
        {
            // Not connected yet - queue for when socket connects (supports multiple torrents)
            if (WebTorrentClient.VerboseLogging) Console.WriteLine($"[WSTracker] AnnounceAsync queued (not yet connected to {AnnounceUrl})");
            _pendingAnnounces.Add((infoHash, opts, peerId));
            return;
        }
        if (WebTorrentClient.VerboseLogging) Console.WriteLine($"[WSTracker] AnnounceAsync to {AnnounceUrl}, infoHash={Convert.ToHexString(infoHash)[..8]}..., event={opts.Event ?? "none"}");

        var peerIdBin = peerId != null ? ToBinaryString(peerId) : _peerIdBinary;
        var msg = new Dictionary<string, object?>
        {
            ["action"] = "announce",
            ["info_hash"] = infoHashBinary,
            ["peer_id"] = peerIdBin,
            ["uploaded"] = opts.Uploaded,
            ["downloaded"] = opts.Downloaded,
            ["left"] = opts.Left,
        };

        if (!string.IsNullOrEmpty(opts.Event))
            msg["event"] = opts.Event;
        if (_trackerId != null)
            msg["trackerid"] = _trackerId;

        if (opts.Event == "stopped" || opts.Event == "completed")
        {
            // Don't include offers with stopped/completed
            await SendAsync(msg);
        }
        else
        {
            // Generate WebRTC offers (capped at 5, matching JS numwant cap)
            int numwant = Math.Min(opts.Numwant, MaxOffers);
            if (WebTorrentClient.VerboseLogging) Console.WriteLine($"[WSTracker] Generating {numwant} offers for {AnnounceUrl}...");
            var offers = await GenerateOffersAsync(numwant, infoHashBinary);
            if (WebTorrentClient.VerboseLogging) Console.WriteLine($"[WSTracker] Generated {offers.Count} offers, sending to {AnnounceUrl}");
            msg["numwant"] = numwant;
            msg["offers"] = offers;
            await SendAsync(msg);
        }
    }

    // ========================
    // SOCKET MANAGEMENT
    // ========================

    private async Task OpenSocketAsync()
    {
        Destroyed = false;
        try
        {
            Console.WriteLine($"[WSTracker] Connecting to {AnnounceUrl}...");
            _ws = new ClientWebSocket();
            _readCts = new CancellationTokenSource();
            await _ws.ConnectAsync(new Uri(AnnounceUrl), CancellationToken.None);
            _connected = true;
            Console.WriteLine($"[WSTracker] Connected to {AnnounceUrl}");

            if (Reconnecting)
            {
                Reconnecting = false;
                Retries = 0;
                OnAnnounce?.Invoke();
            }

            // Process all queued announces (supports multiple torrents on shared socket)
            if (_pendingAnnounces.Count > 0)
            {
                var pending = _pendingAnnounces.ToArray();
                _pendingAnnounces.Clear();
                if (WebTorrentClient.VerboseLogging) Console.WriteLine($"[WSTracker] Processing {pending.Length} queued announces to {AnnounceUrl}");
                foreach (var (hash, opts2, pid) in pending)
                    _ = AnnounceAsync(hash, opts2, pid);
            }

            // Start read loop
            _ = ReadLoopAsync(_readCts.Token);
        }
        catch (Exception ex)
        {
            OnWarning?.Invoke($"WebSocket connect failed: {ex.Message}");
            StartReconnectTimer();
        }
    }

    private async Task ReadLoopAsync(CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        try
        {
            while (!ct.IsCancellationRequested && _ws?.State == WebSocketState.Open)
            {
                var result = await _ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    OnSocketClose();
                    return;
                }

                // Collect full message (may span multiple frames)
                var ms = new MemoryStream();
                ms.Write(buffer, 0, result.Count);
                while (!result.EndOfMessage)
                {
                    result = await _ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
                    ms.Write(buffer, 0, result.Count);
                }

                var json = Encoding.UTF8.GetString(ms.ToArray());
                _expectingResponse = false;
                OnSocketData(json);
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException)
        {
            OnSocketClose();
        }
        catch (Exception ex)
        {
            OnWarning?.Invoke($"WebSocket read error: {ex.Message}");
            OnSocketClose();
        }
    }

    private void OnSocketData(string json)
    {
        if (Destroyed) return;

        if (WebTorrentClient.VerboseLogging)
        {
            var preview = json.Length > 200 ? json[..200] + "..." : json;
            Console.WriteLine($"[WSTracker] RECV from {AnnounceUrl}: {preview}");
        }

        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch
        {
            OnWarning?.Invoke("Invalid tracker response JSON");
            return;
        }

        var root = doc.RootElement;
        var action = root.TryGetProperty("action", out var actionProp) ? actionProp.GetString() : null;

        if (action == "announce")
            OnAnnounceResponse(root);
        else if (action == "scrape")
        {
            // Scrape response - emit for consumer
        }
        else
        {
            // Check for offer/answer by field presence (matches JS behavior)
            if (root.TryGetProperty("offer", out _) || root.TryGetProperty("answer", out _))
                OnAnnounceResponse(root);
            else
            {
                if (WebTorrentClient.VerboseLogging)
                    Console.WriteLine($"[WSTracker] Unknown action from {AnnounceUrl}: {action}");
            }
        }
    }

    private async void OnAnnounceResponse(JsonElement data)
    {
        // Get info_hash from response to route to correct torrent
        string responseInfoHash = "";
        if (data.TryGetProperty("info_hash", out var ihProp))
            responseInfoHash = ihProp.GetString() ?? "";

        // Ignore messages from ourselves
        if (data.TryGetProperty("peer_id", out var pidProp))
        {
            var responsePeerId = pidProp.GetString() ?? "";
            if (responsePeerId == _peerIdBinary) return;
        }

        // Failure reason
        if (data.TryGetProperty("failure reason", out var failProp))
        {
            if (_warningHandlers.TryGetValue(responseInfoHash, out var warnHandler))
                warnHandler(failProp.GetString() ?? "tracker failure");
            else
                OnWarning?.Invoke(failProp.GetString() ?? "tracker failure");
            return;
        }

        // Warning message
        if (data.TryGetProperty("warning message", out var warnProp))
        {
            if (_warningHandlers.TryGetValue(responseInfoHash, out var warnHandler))
                warnHandler(warnProp.GetString() ?? "");
            else
                OnWarning?.Invoke(warnProp.GetString() ?? "");
        }

        // Interval
        if (data.TryGetProperty("interval", out var intProp) && intProp.TryGetInt32(out var interval))
            SetAnnounceInterval(interval * 1000);
        else if (data.TryGetProperty("min interval", out var minIntProp) && minIntProp.TryGetInt32(out var minInterval))
            SetAnnounceInterval(minInterval * 1000);

        // Tracker ID
        if (data.TryGetProperty("tracker id", out var tidProp))
            _trackerId = tidProp.GetString();

        // Swarm stats - route to correct torrent by info_hash
        if (data.TryGetProperty("complete", out _))
        {
            var update = new TrackerUpdate
            {
                AnnounceUrl = AnnounceUrl,
                Complete = data.TryGetProperty("complete", out var cp) ? cp.GetInt32() : 0,
                Incomplete = data.TryGetProperty("incomplete", out var ip) ? ip.GetInt32() : 0,
            };
            if (_updateHandlers.TryGetValue(responseInfoHash, out var updateHandler))
                updateHandler(update);
            OnUpdate?.Invoke(update);
        }

        // Incoming offer from another peer - create answering peer
        if (data.TryGetProperty("offer", out var offerProp) && data.TryGetProperty("peer_id", out var offerPeerId))
        {
            // Use per-info_hash peer factory if available, otherwise default
            var factory = _peerFactories.TryGetValue(responseInfoHash, out var f) ? f : _createPeerFunc;
            var peer = factory(false); // responder
            await peer.InitAsync(); // Initialize RTCPeerConnection before signaling
            var offerId = data.TryGetProperty("offer_id", out var oidProp) ? oidProp.GetString() ?? "" : "";
            var remotePeerId = offerPeerId.GetString() ?? "";

            peer.OnSignal += (signal) =>
            {
                if (signal.Type != "answer") return;
                // Send answer back through tracker - use the info_hash from the incoming offer
                var answerMsg = new Dictionary<string, object?>
                {
                    ["action"] = "announce",
                    ["info_hash"] = responseInfoHash,
                    ["peer_id"] = _peerIdBinary,
                    ["to_peer_id"] = remotePeerId,
                    ["answer"] = new { type = signal.Type, sdp = signal.Sdp },
                    ["offer_id"] = offerId,
                };
                if (_trackerId != null) answerMsg["trackerid"] = _trackerId;
                _ = SendAsync(answerMsg);
            };

            // Route peer to the correct torrent by info_hash
            if (_peerHandlers.TryGetValue(responseInfoHash, out var peerHandler))
                peerHandler(peer);
            else
                OnPeer?.Invoke(peer);

            // Signal the offer to the peer (triggers answer generation)
            _ = peer.Signal(new SignalData { Type = "offer", Sdp = offerProp.TryGetProperty("sdp", out var sdpProp) ? sdpProp.GetString() : null });
        }

        // Incoming answer to one of our offers
        if (data.TryGetProperty("answer", out var answerProp) && data.TryGetProperty("offer_id", out var answerOidProp))
        {
            // Convert offer_id from binary string to hex for lookup (matches JS bin2hex)
            var offerIdBinary = answerOidProp.GetString() ?? "";
            var offerIdHex = BinaryStringToHex(offerIdBinary);

            if (_pendingOffers.TryGetValue(offerIdHex, out var entry))
            {
                var peer = entry.peer;
                var offerInfoHash = entry.infoHashBinary;
                entry.timeout?.Dispose();
                _pendingOffers.Remove(offerIdHex);

                // Route peer to the correct torrent by the info_hash stored with the offer
                if (_peerHandlers.TryGetValue(offerInfoHash, out var peerHandler))
                    peerHandler(peer);
                else
                    OnPeer?.Invoke(peer);

                var answerSdp = answerProp.TryGetProperty("sdp", out var aSdpProp) ? aSdpProp.GetString() : null;
                _ = peer.Signal(new SignalData { Type = "answer", Sdp = answerSdp });
            }
        }
    }

    private void OnSocketClose()
    {
        if (Destroyed) return;
        _connected = false;
        StartReconnectTimer();
    }

    // ========================
    // RECONNECTION (exponential backoff matching JS exactly)
    // ========================

    private void StartReconnectTimer()
    {
        // JS increments retries BEFORE calculating delay (exponential backoff)
        Retries++;
        var random = new Random();
        int ms = random.Next(ReconnectVariance) +
                 Math.Min((int)Math.Pow(2, Retries) * ReconnectMinimum, ReconnectMaximum);

        Reconnecting = true;
        _reconnectTimer?.Dispose();
        _reconnectTimer = new Timer(_ =>
        {
            _ = OpenSocketAsync();
        }, null, ms, Timeout.Infinite);
    }

    // ========================
    // OFFER GENERATION (matches JS _generateOffers exactly)
    // ========================

    private async Task<List<object>> GenerateOffersAsync(int numwant, string infoHashBinary)
    {
        // Generate all offers in parallel (matches JS Promise.all pattern)
        var tasks = new List<Task<object?>>();

        for (int i = 0; i < numwant; i++)
        {
            var offerIdBytes = System.Security.Cryptography.RandomNumberGenerator.GetBytes(20);
            var offerIdHex = Convert.ToHexString(offerIdBytes).ToLowerInvariant();

            // Use the per-info_hash factory if available
            var factory = _peerFactories.TryGetValue(infoHashBinary, out var f) ? f : _createPeerFunc;

            tasks.Add(Task.Run(async () =>
            {
                try
                {
                    var peer = factory(true);
                    var signalTcs = new TaskCompletionSource<SignalData>();
                    peer.OnSignal += (signal) =>
                    {
                        if (signal.Type == "offer")
                            signalTcs.TrySetResult(signal);
                    };

                    await peer.InitAsync();

                    var timeoutTask = Task.Delay(15_000);
                    var completedTask = await Task.WhenAny(signalTcs.Task, timeoutTask);

                    if (completedTask != signalTcs.Task) { await peer.DisposeAsync(); return null; }

                    var signal = signalTcs.Task.Result;
                    var offerTimer = new Timer(_ =>
                    {
                        if (_pendingOffers.Remove(offerIdHex, out var entry))
                            _ = entry.peer.DisposeAsync();
                    }, null, OfferTimeout, Timeout.Infinite);

                    _pendingOffers[offerIdHex] = (peer, offerTimer, infoHashBinary);

                    return (object?)new
                    {
                        offer = new { type = signal.Type, sdp = signal.Sdp },
                        offer_id = ToBinaryString(offerIdBytes),
                    };
                }
                catch { return null; }
            }));
        }

        var results = await Task.WhenAll(tasks);
        return results.Where(r => r != null).ToList()!;
    }

    // ========================
    // SEND
    // ========================

    private async Task SendAsync(object msg)
    {
        if (Destroyed || _ws?.State != WebSocketState.Open) return;
        _expectingResponse = true;

        var json = JsonSerializer.Serialize(msg, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
            // CRITICAL: Do NOT escape chars 0x80-0xFF as \uXXXX - JS JSON.stringify doesn't
            // escape them, and the tracker expects matching wire format for info_hash/peer_id
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        });
        var bytes = Encoding.UTF8.GetBytes(json);
        await _ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None);
    }

    // ========================
    // INTERVAL
    // ========================

    private void SetAnnounceInterval(int ms)
    {
        _announceTimer?.Dispose();
        _announceTimer = new Timer(_ => OnAnnounce?.Invoke(), null, ms, ms);
    }

    // ========================
    // BINARY STRING ENCODING (matches JS hex2bin/bin2hex exactly)
    // ========================

    /// <summary>
    /// Convert bytes to a "binary string" — each byte becomes a latin1 char.
    /// Matches JS hex2bin() / String.fromCharCode() encoding used by bittorrent-tracker.
    /// </summary>
    public static string ToBinaryString(byte[] bytes)
        => new string(bytes.Select(b => (char)b).ToArray());

    /// <summary>
    /// Convert a "binary string" back to hex.
    /// Matches JS bin2hex() encoding.
    /// </summary>
    public static string BinaryStringToHex(string binaryString)
        => Convert.ToHexString(binaryString.Select(c => (byte)c).ToArray()).ToLowerInvariant();

    // ========================
    // DISPOSE
    // ========================

    public async ValueTask DisposeAsync()
    {
        if (Destroyed) return;
        Destroyed = true;
        _connected = false;

        _reconnectTimer?.Dispose();
        _announceTimer?.Dispose();
        _readCts?.Cancel();

        foreach (var (_, entry) in _pendingOffers)
        {
            entry.timeout?.Dispose();
            await entry.peer.DisposeAsync();
        }
        _pendingOffers.Clear();

        if (_ws?.State == WebSocketState.Open)
        {
            try { await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None); }
            catch { }
        }
        _ws?.Dispose();
        _ws = null;
    }
}

// ========================
// SUPPORTING TYPES
// ========================

public class AnnounceOptions
{
    public long Uploaded { get; set; }
    public long Downloaded { get; set; }
    public long Left { get; set; }
    public string? Event { get; set; }
    public int Numwant { get; set; } = 10;  // JS: MAX_ANNOUNCE_PEERS = 10
}

public class TrackerUpdate
{
    public string AnnounceUrl { get; set; } = "";
    public int Complete { get; set; }
    public int Incomplete { get; set; }
}
