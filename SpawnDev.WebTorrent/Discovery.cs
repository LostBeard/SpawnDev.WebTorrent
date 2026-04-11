namespace SpawnDev.WebTorrent;

/// <summary>
/// Peer discovery orchestrator — manages trackers (WebSocket, HTTP, UDP).
/// Direct 1:1 port of torrent-discovery/index.js.
/// </summary>
public class Discovery : IAsyncDisposable
{
    public const int DefaultIntervalMs = 15 * 60 * 1000;  // 15 minutes

    public string InfoHashHex { get; }
    public byte[] InfoHash { get; }
    public byte[] PeerId { get; }
    public bool Destroyed { get; private set; }

    private readonly List<WebSocketTracker> _wsTrackers = new();
    private readonly List<HttpTracker> _httpTrackers = new();
    private readonly List<UdpTrackerClient> _udpTrackers = new();
    private readonly Func<bool, SimplePeer> _createPeerFunc;
    private readonly HttpClient _http;

    // ========================
    // EVENTS
    // ========================

    /// <summary>Peer discovered (SimplePeer for WebRTC, or "ip:port" string for TCP).</summary>
    public event Action<SimplePeer>? OnWebRtcPeer;
    public event Action<string>? OnTcpPeer;  // "ip:port"
    public event Action? OnTrackerAnnounce;
    public event Action<string>? OnWarning;
    public event Action<TrackerUpdate>? OnTrackerUpdate;

    // ========================
    // CONSTRUCTOR
    // ========================

    public Discovery(byte[] infoHash, byte[] peerId, string[] announceUrls,
                     Func<bool, SimplePeer> createPeerFunc, HttpClient http)
    {
        InfoHash = infoHash;
        InfoHashHex = Convert.ToHexString(infoHash).ToLowerInvariant();
        PeerId = peerId;
        _createPeerFunc = createPeerFunc;
        _http = http;

        Console.WriteLine($"[Discovery] Creating for {InfoHashHex}, {announceUrls.Length} trackers");
        foreach (var url in announceUrls)
        {
            if (string.IsNullOrWhiteSpace(url)) continue;
            Console.WriteLine($"[Discovery] Tracker: {url}");

            if (url.StartsWith("wss://") || url.StartsWith("ws://"))
            {
                // Use shared tracker pool - one WebSocket per URL across all torrents
                var tracker = WebSocketTracker.GetOrCreate(url, peerId, createPeerFunc);
                // Subscribe this torrent's handlers to the shared connection
                tracker.Subscribe(infoHash,
                    onPeer: (peer) => OnWebRtcPeer?.Invoke(peer),
                    peerFactory: createPeerFunc,
                    onUpdate: (update) => OnTrackerUpdate?.Invoke(update),
                    onWarning: (msg) => OnWarning?.Invoke(msg));
                tracker.OnAnnounce += () =>
                {
                    // Re-announce triggered by tracker interval
                    _ = tracker.AnnounceAsync(infoHash, new AnnounceOptions(), peerId);
                    OnTrackerAnnounce?.Invoke();
                };
                _wsTrackers.Add(tracker);
            }
            else if (url.StartsWith("http://") || url.StartsWith("https://"))
            {
                var tracker = new HttpTracker(url, infoHash, peerId, http);
                tracker.OnPeer += (addr) => OnTcpPeer?.Invoke(addr);
                tracker.OnWarning += (msg) => OnWarning?.Invoke(msg);
                tracker.OnAnnounce += () =>
                {
                    _ = tracker.AnnounceAsync(new AnnounceOptions());
                    OnTrackerAnnounce?.Invoke();
                };
                _httpTrackers.Add(tracker);
            }
            else if (url.StartsWith("udp://") && !OperatingSystem.IsBrowser())
            {
                var tracker = new UdpTrackerClient(url, peerId);
                tracker.OnPeer += (addr) => OnTcpPeer?.Invoke(addr);
                tracker.OnWarning += (msg) => OnWarning?.Invoke(msg);
                tracker.OnAnnounce += () => OnTrackerAnnounce?.Invoke();
                _udpTrackers.Add(tracker);
            }
        }
    }

    // ========================
    // ANNOUNCE
    // ========================

    /// <summary>Send announce to all trackers.</summary>
    public async Task AnnounceAsync(AnnounceOptions? opts = null)
    {
        if (Destroyed) return;
        opts ??= new AnnounceOptions();

        var tasks = new List<Task>();
        foreach (var t in _wsTrackers)
            tasks.Add(t.AnnounceAsync(InfoHash, opts, PeerId));
        foreach (var t in _httpTrackers)
            tasks.Add(t.AnnounceAsync(opts));
        // UDP trackers use their own announce format — start them if not already running
        foreach (var t in _udpTrackers)
        {
            if (!t.IsConnected)
                tasks.Add(t.StartAsync(InfoHash, 6881));
        }

        await Task.WhenAll(tasks);
        OnTrackerAnnounce?.Invoke();
    }

    /// <summary>Announce completion to all trackers.</summary>
    public async Task CompleteAsync()
    {
        await AnnounceAsync(new AnnounceOptions { Event = "completed" });
    }

    /// <summary>Announce stopped to all trackers.</summary>
    public async Task StopAsync()
    {
        await AnnounceAsync(new AnnounceOptions { Event = "stopped" });
    }

    // ========================
    // DISPOSE
    // ========================

    public async ValueTask DisposeAsync()
    {
        if (Destroyed) return;
        Destroyed = true;

        foreach (var t in _wsTrackers)
            await t.DisposeAsync();
        foreach (var t in _httpTrackers)
            await t.DisposeAsync();
        foreach (var t in _udpTrackers)
            await t.DisposeAsync();

        _wsTrackers.Clear();
        _httpTrackers.Clear();
        _udpTrackers.Clear();
    }
}
