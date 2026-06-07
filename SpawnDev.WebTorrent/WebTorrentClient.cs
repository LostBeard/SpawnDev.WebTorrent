using System.Security.Cryptography;
using System.Text;
using SpawnDev.BlazorJS.Cryptography;

namespace SpawnDev.WebTorrent;

/// <summary>
/// WebTorrent client — manages torrents, peer connections, and trackers.
/// Direct 1:1 port of webtorrent/index.js.
/// </summary>
public class WebTorrentClient : IAsyncDisposable
{
    // ========================
    // CONSTANTS (match JS exactly)
    // ========================
    public const int DefaultMaxConns = 55;

    /// <summary>
    /// Default WSS trackers merged into every torrent for peer discovery.
    /// openwebtorrent is the most reliable public WSS tracker observed; tracker.webtorrent.dev
    /// is fickle and blocks some origins so it is not included by default.
    /// Configurable via WebTorrentClientOptions.DefaultTrackers. Set to empty array to disable.
    /// </summary>
    public string[] DefaultTrackers { get; set; } = new[]
    {
        "wss://tracker.openwebtorrent.com",
        "wss://hub.spawndev.com:44365/announce",
    };

    /// <summary>
    /// Version prefix for peer ID. WebTorrent uses Azureus-style:
    /// -WW0102- followed by random bytes.
    /// </summary>
    private const string VersionPrefix = "-WW0208-";

    // ========================
    // CONFIGURATION
    // ========================

    /// <summary>Peer ID as hex string (40 chars).</summary>
    public string PeerId { get; }

    /// <summary>Peer ID as 20 bytes.</summary>
    public byte[] PeerIdBuffer { get; }

    /// <summary>Max peer connections across all torrents.</summary>
    public int MaxConns { get; set; } = DefaultMaxConns;

    /// <summary>Enable web seeds (BEP 19). Default true.</summary>
    public bool EnableWebSeeds { get; set; } = true;

    /// <summary>Is the client destroyed?</summary>
    public bool Destroyed { get; private set; }

    /// <summary>Is the client ready?</summary>
    public bool Ready { get; private set; }

    /// <summary>Active torrents.</summary>
    public List<Torrent> Torrents { get; } = new();

    /// <summary>Aggregate download speed across all torrents (bytes/sec).</summary>
    public double DownloadSpeed => Torrents.Sum(t => t.DownloadSpeed);

    /// <summary>Aggregate upload speed across all torrents (bytes/sec).</summary>
    public double UploadSpeed => Torrents.Sum(t => t.UploadSpeed);

    /// <summary>Aggregate download progress across all active torrents (0.0 to 1.0).</summary>
    public double Progress
    {
        get
        {
            var total = Torrents.Sum(t => t.Length);
            return total > 0 ? Torrents.Sum(t => (double)t.Downloaded) / total : 0;
        }
    }

    /// <summary>Aggregate seed ratio (uploaded / downloaded).</summary>
    public double Ratio
    {
        get
        {
            var dl = Torrents.Sum(t => t.Downloaded);
            return dl > 0 ? Torrents.Sum(t => t.Uploaded) / (double)dl : 0;
        }
    }

    /// <summary>Verbose logging flag — gate all Console output behind this.</summary>
    public static bool VerboseLogging { get; set; }

    // ICE servers for WebRTC
    public string[] IceServers { get; set; } = SimplePeer.DefaultIceServers;

    // HTTP client for web seeds
    internal readonly HttpClient _http;

    /// <summary>Portable crypto for Ed25519 operations (BEP 44/46). Null if not configured.</summary>
    public IPortableCrypto? Crypto { get; set; }

    /// <summary>
    /// Hash engine used by piece verification. Defaults to
    /// <see cref="SystemCryptoPieceHashEngine"/> (System.Security.Cryptography,
    /// hardware-accelerated SHA-NI on modern CPUs). Replace with a GPU-backed
    /// engine (e.g. <c>SpawnDev.WebTorrent.GpuHash</c> when shipped) for
    /// recheck-heavy workloads where amortized GPU dispatch wins on large
    /// torrents.
    /// </summary>
    public IPieceHashEngine PieceHashEngine { get; set; } = new SystemCryptoPieceHashEngine();

    /// <summary>
    /// Active upload bandwidth policy. Mirrors
    /// <see cref="WebTorrentClientOptions.BandwidthPolicy"/>; mutating this
    /// property alone does NOT retroactively change <c>UploadRateLimiter.Rate</c>.
    /// Use <see cref="ApplyBandwidthPolicy(BandwidthPolicy)"/> to update both at
    /// once. Surfaces the operator's intent for UI / telemetry.
    /// </summary>
    public BandwidthPolicy BandwidthPolicy { get; set; } = BandwidthPolicy.Unlimited;

    /// <summary>
    /// Apply <paramref name="policy"/> to <see cref="UploadRateLimiter.Rate"/>
    /// at runtime. Updates <see cref="BandwidthPolicy"/> simultaneously so the
    /// two can't drift. Pass <see cref="BandwidthPolicy.Custom"/> only if
    /// you're also calling <see cref="ThrottleUpload"/> with the exact rate -
    /// otherwise it's a no-op (the existing rate stays).
    /// </summary>
    public void ApplyBandwidthPolicy(BandwidthPolicy policy)
    {
        BandwidthPolicy = policy;
        if (policy != BandwidthPolicy.Custom)
            UploadRateLimiter.Rate = policy.ToUploadBytesPerSec();
    }

    /// <summary>Async file system for persistent storage.</summary>
    public SpawnDev.AsyncFileSystem.IAsyncFS? AsyncFileSystem { get; set; }

    /// <summary>Upload rate limiter. Rate = -1 for unlimited, 0 for paused, positive for bytes/sec.</summary>
    public RateLimiter UploadRateLimiter { get; } = new RateLimiter(-1);

    /// <summary>Download rate limiter. Rate = -1 for unlimited, 0 for paused, positive for bytes/sec.</summary>
    public RateLimiter DownloadRateLimiter { get; } = new RateLimiter(-1);

    /// <summary>DHT discovery instance (desktop only). Null in browser.</summary>
    public DhtDiscovery? Dht { get; private set; }

    /// <summary>Service worker stream handler for media streaming. Set via options or RegisterStreamHandler.</summary>
    public ServiceWorkerStreamHandler? StreamHandler { get; private set; }

    /// <summary>Whether trackers are enabled.</summary>
    public bool EnableTrackers { get; set; } = true;

    /// <summary>Whether DHT is enabled (desktop only).</summary>
    public bool EnableDht { get; set; } = true;

    /// <summary>Whether Local Service Discovery is enabled (desktop only).</summary>
    public bool EnableLsd { get; set; } = true;

    /// <summary>Whether Peer Exchange (ut_pex) is enabled.</summary>
    public bool EnableUtPex { get; set; } = true;

    /// <summary>IP addresses to block.</summary>
    public HashSet<string> Blocklist { get; } = new();

    /// <summary>Set max download speed (bytes/sec). -1 to disable limit.</summary>
    public void ThrottleDownload(long rate) => DownloadRateLimiter.Rate = rate;

    /// <summary>Set max upload speed (bytes/sec). -1 to disable limit.</summary>
    public void ThrottleUpload(long rate) => UploadRateLimiter.Rate = rate;

    /// <summary>Event fired when a BEP 46 mutable torrent updates to a new infohash.</summary>
    public event Action<Torrent, string>? OnMutableUpdate; // torrent, new infohash

    // Extension factories — registered before torrent creation
    private readonly List<Func<Wire, IWireExtension>> _extensionFactories = new();

    // ========================
    // EVENTS
    // ========================

    public event Action<Torrent>? OnAdd;
    public event Action<Torrent>? OnRemove;
    public event Action<Torrent>? OnTorrentReady;
    public event Action<int>? OnDownload;   // bytes downloaded across any torrent
    public event Action<int>? OnUpload;     // bytes uploaded across any torrent
    public event Action<string>? OnWarning;
    public event Action<Exception>? OnError;

    /// <summary>Called by Torrent when wire download event bubbles up.</summary>
    internal void EmitDownload(int bytes) => OnDownload?.Invoke(bytes);

    /// <summary>Called by Torrent when wire upload event bubbles up.</summary>
    internal void EmitUpload(int bytes) => OnUpload?.Invoke(bytes);

    // ========================
    // CONSTRUCTOR
    // ========================

    public WebTorrentClient(WebTorrentClientOptions? opts = null)
    {
        opts ??= new WebTorrentClientOptions();

        // Generate peer ID (Azureus-style, matches JS)
        if (!string.IsNullOrEmpty(opts.PeerId))
        {
            PeerId = opts.PeerId;
        }
        else
        {
            var randomSuffix = Convert.ToBase64String(RandomNumberGenerator.GetBytes(9))
                .Replace("+", "").Replace("/", "").Replace("=", "");
            var peerIdStr = VersionPrefix + randomSuffix;
            // Peer ID must be exactly 20 bytes
            PeerIdBuffer = Encoding.ASCII.GetBytes(peerIdStr[..Math.Min(20, peerIdStr.Length)]);
            if (PeerIdBuffer.Length < 20)
            {
                var padded = new byte[20];
                PeerIdBuffer.CopyTo(padded, 0);
                RandomNumberGenerator.Fill(padded.AsSpan(PeerIdBuffer.Length));
                PeerIdBuffer = padded;
            }
            PeerId = Convert.ToHexString(PeerIdBuffer).ToLowerInvariant();
        }

        PeerIdBuffer ??= Convert.FromHexString(PeerId);

        if (opts.MaxConns > 0) MaxConns = opts.MaxConns;
        EnableWebSeeds = opts.EnableWebSeeds;
        EnableTrackers = opts.EnableTrackers;
        EnableDht = opts.EnableDht;
        EnableLsd = opts.EnableLsd;
        EnableUtPex = opts.EnableUtPex;
        if (opts.IceServers != null) IceServers = opts.IceServers;
        if (opts.DefaultTrackers != null) DefaultTrackers = opts.DefaultTrackers;
        if (opts.DownloadLimit >= 0) DownloadRateLimiter.Rate = opts.DownloadLimit;
        // UploadLimit (explicit bytes/sec) wins over BandwidthPolicy when set
        // (>= 0). Otherwise the policy translates to a rate via the helper -
        // Unlimited stays at -1 (legacy default), Conservative/Metered apply
        // their ceilings, SeedingDisabled pins rate=0.
        if (opts.UploadLimit >= 0)
            UploadRateLimiter.Rate = opts.UploadLimit;
        else
            UploadRateLimiter.Rate = opts.BandwidthPolicy.ToUploadBytesPerSec();
        BandwidthPolicy = opts.BandwidthPolicy;
        if (opts.Blocklist != null) foreach (var ip in opts.Blocklist) Blocklist.Add(ip);

        _http = opts.HttpClient ?? new HttpClient();
        AsyncFileSystem = opts.AsyncFileSystem;
        Crypto = opts.Crypto;

        // Wire up stream handler if provided
        if (opts.StreamHandler != null)
            RegisterStreamHandler(opts.StreamHandler);

        // Auto-start the inbound TCP listener if a port was requested. Bind +
        // accept-loop start are async so this is fire-and-forget; consumers who
        // need to know the kernel-assigned port should await EnsureTcpListenerAsync
        // explicitly instead of relying on the option.
        if (opts.TcpListenPort.HasValue && !OperatingSystem.IsBrowser())
            _ = EnsureTcpListenerAsync(opts.TcpListenPort.Value, opts.TcpListenAddress);
        AdvertiseTcpListenerToTrackers = opts.AdvertiseTcpListenerToTrackers;
        if (opts.PieceHashEngine != null) PieceHashEngine = opts.PieceHashEngine;

        Ready = true; // No blocklist to load in C# version

        // Register ut_pex extension (BEP 11) — peer exchange for all wires
        UseExtension((wire) =>
        {
            var ext = new UtPexExtension();
            ext.SetWire(wire);
            return ext;
        });
    }

    // ========================
    // EXTENSION REGISTRATION (matches JS client.use / torrent.use pattern)
    // ========================

    /// <summary>Register a wire extension factory. Extensions are created per-wire before handshake.</summary>
    public void UseExtension(Func<Wire, IWireExtension> factory)
    {
        _extensionFactories.Add(factory);
    }

    /// <summary>Apply all registered extension factories to a wire.</summary>
    internal void ApplyExtensions(Wire wire)
    {
        foreach (var factory in _extensionFactories)
        {
            var ext = factory(wire);
            wire.Use(ext);
        }
    }

    // ========================
    // ADD TORRENT
    // ========================

    /// <summary>
    /// Add a torrent from a magnet URI (or info hash hex) and await until its metadata is
    /// resolved, returning the ready <see cref="Torrent"/>. Metadata resolves via the magnet's
    /// HTTP(S) exact-source (<c>xs=</c>) <c>.torrent</c> when present (one GET, no peers — the
    /// HuggingFace-proxy / CDN-cache case), otherwise via ut_metadata from peers in the swarm.
    /// Pass a <paramref name="ct"/> to bound the wait; without one the task completes only when
    /// metadata arrives (so always supply a timeout token for peer-only magnets).
    /// </summary>
    public async Task<Torrent> AddAsync(string magnetOrInfoHash, AddTorrentOptions? opts = null,
        CancellationToken ct = default)
    {
        var torrent = Add(magnetOrInfoHash, opts);
        if (torrent.HasMetadata) return torrent;

        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnReady() => tcs.TrySetResult();
        torrent.OnReady += OnReady;
        try
        {
            // Re-check after subscribing — metadata may have resolved between Add() and the
            // event hookup (e.g. xs= fetch already completed, or a duplicate add returned a
            // torrent that was already ready).
            if (torrent.HasMetadata) return torrent;
            using (ct.Register(static s => ((TaskCompletionSource)s!).TrySetCanceled(), tcs))
                await tcs.Task.ConfigureAwait(false);
        }
        finally
        {
            torrent.OnReady -= OnReady;
        }
        return torrent;
    }

    /// <summary>
    /// Add a torrent to download. Accepts magnet URI or info hash hex.
    /// Returns the Torrent immediately (metadata may still be resolving via ut_metadata
    /// from peers, or via an HTTP exact-source <c>xs=</c> fetch if the magnet carried one).
    /// Use <see cref="AddAsync(string, AddTorrentOptions?, CancellationToken)"/> to await readiness.
    /// </summary>
    public Torrent Add(string magnetOrInfoHash, AddTorrentOptions? opts = null)
    {
        if (Destroyed) throw new InvalidOperationException("Client is destroyed");
        opts ??= new AddTorrentOptions();

        var torrent = new Torrent();

        // ut_metadata (BEP 9) is registered PER-TORRENT in Torrent.SetupMetadataExtension (called from
        // Torrent.AddPeer), NOT client-global. The old client-global factory captured a single
        // torrent's closure (wrong once a client holds >1 torrent) and only ever created FETCHERS — so
        // a seeded torrent advertised no ut_metadata and a magnet peer could never pull its info dict
        // (two SpawnDev.WebTorrent clients stalled at hasMetadata=false). The per-torrent registration
        // serves the info dict when the torrent has it and fetches when it doesn't.

        // Start the torrent — parse magnet and begin discovery
        // Default trackers are merged inside InitFromMagnetAsync before StartDiscovery
        _ = torrent.InitFromMagnetAsync(magnetOrInfoHash, this, opts);

        // Check for duplicate after WireInfoHashHex is set. WireInfoHashHex resolves
        // to v1 InfoHash when present, else truncated V2InfoHash — so pure-v2 magnets
        // dedup correctly where the pre-rc.20 plain-InfoHash match would miss them.
        var existing = Torrents.FirstOrDefault(t =>
            t != torrent &&
            !string.IsNullOrEmpty(torrent.WireInfoHashHex) &&
            string.Equals(t.WireInfoHashHex, torrent.WireInfoHashHex, StringComparison.OrdinalIgnoreCase));
        if (existing != null)
        {
            OnWarning?.Invoke($"Duplicate torrent: {torrent.WireInfoHashHex}");
            return existing;
        }

        Torrents.Add(torrent);
        torrent.OnReady += () => OnTorrentReady?.Invoke(torrent);
        OnAdd?.Invoke(torrent);

        return torrent;
    }

    /// <summary>
    /// Add a BEP 46 mutable torrent by public key. Resolves the current infohash from the DHT,
    /// downloads it, and subscribes for updates. When the publisher pushes a new version,
    /// OnMutableUpdate fires and the client can transition to the new torrent.
    /// </summary>
    /// <param name="publicKey">Publisher's Ed25519 public key (32 bytes).</param>
    /// <param name="salt">Optional salt for multi-channel publishers.</param>
    /// <param name="signer">Signer for verifying received items. Required for signature checks.</param>
    /// <summary>
    /// Eagerly initialize the DHT with optional custom <see cref="DhtOptions"/>. Without calling this,
    /// the DHT is lazy-initialized on the first BEP 46 operation using default options (port 6881),
    /// which blocks running two clients on a single host (port collision). Call this when:
    /// <list type="bullet">
    ///   <item>Running multiple clients on one host (tests / local relays / embedded workers) and they
    ///         need distinct DHT ports.</item>
    ///   <item>Pre-warming the routing table before the first BEP 46 op so the get/put latency doesn't
    ///         include bootstrap time.</item>
    ///   <item>Overriding bootstrap nodes (e.g. private tracker / isolated DHT network).</item>
    /// </list>
    /// <para>
    /// Idempotent: returns immediately if the DHT is already running, or if running in the browser
    /// where UDP sockets are unavailable (browser DHT is not implemented - BEP 46 over tracker-relay
    /// is a future item).
    /// </para>
    /// </summary>
    /// <param name="options">Optional DHT configuration. When null, uses defaults (port 6881, standard
    /// bootstrap nodes). Setting <see cref="DhtOptions.Port"/> to a unique value per instance enables
    /// multi-client loopback testing.</param>
    /// <param name="ct">Cancellation token for the initial bind + routing-table bootstrap.</param>
    public async Task EnsureDhtAsync(DhtOptions? options = null, CancellationToken ct = default)
    {
        if (Destroyed) throw new InvalidOperationException("Client is destroyed");
        if (OperatingSystem.IsBrowser()) return;
        if (Dht != null) return;

        var opts = options ?? new DhtOptions();
        Dht = new DhtDiscovery(opts);
        await Dht.StartAsync(new byte[20], opts.Port, ct);
    }

    /// <summary>The TCP peer-wire listener, if one is running. Set by
    /// <see cref="EnsureTcpListenerAsync"/> (or auto-start via
    /// <see cref="WebTorrentClientOptions.TcpListenPort"/>). Inspect
    /// <c>TcpListener.LocalEndPoint.Port</c> to get the actually-bound port
    /// when the requested port was 0 (kernel-assigned ephemeral). Always
    /// null in the browser - listening sockets are desktop-only.</summary>
    public TcpListenerService? TcpListener { get; private set; }

    /// <summary>
    /// Whether tracker announces should advertise our <see cref="TcpListener"/>
    /// port. When <c>true</c> AND a listener is bound, every HTTP/UDP tracker
    /// announce includes the listener's actual port in the BEP 3 <c>port=</c>
    /// field; trackers then put us in their compact peer list and other
    /// clients dial us in. Mirrors
    /// <see cref="WebTorrentClientOptions.AdvertiseTcpListenerToTrackers"/>;
    /// can be flipped at runtime.
    /// </summary>
    public bool AdvertiseTcpListenerToTrackers { get; set; }

    /// <summary>The port to advertise to HTTP/UDP trackers, or 0 if no
    /// advertising should happen. Returns 0 in the browser, when no listener
    /// is bound, or when <see cref="AdvertiseTcpListenerToTrackers"/> is false.
    /// Used by <see cref="Torrent"/> at announce time.</summary>
    public int AdvertisedTcpPort
    {
        get
        {
            if (!AdvertiseTcpListenerToTrackers) return 0;
            if (TcpListener == null) return 0;
            return TcpListener.LocalEndPoint.Port;
        }
    }

    /// <summary>
    /// Bind a TCP peer-wire listener and start accepting inbound BitTorrent
    /// connections. Idempotent; calling twice with the same port is a no-op.
    /// Closes the seed-C# / leech-mainline interop path - mainline clients
    /// (qBittorrent, libtorrent, Transmission) can dial in by IP+port and
    /// our listener routes by info_hash to the matching torrent. Desktop
    /// only - browser cannot bind a listening socket.
    /// </summary>
    /// <param name="port">TCP port to bind. 0 = kernel-assigned ephemeral
    /// (read <see cref="TcpListener"/>.LocalEndPoint.Port back to learn the
    /// actual port); &gt;0 = specific port.</param>
    /// <param name="address">Local address to bind. Defaults to
    /// <see cref="System.Net.IPAddress.Any"/> so external peers can reach
    /// the listener; pass <see cref="System.Net.IPAddress.Loopback"/> for
    /// localhost-only test harnesses.</param>
    public Task EnsureTcpListenerAsync(int port = 0, System.Net.IPAddress? address = null)
    {
        if (Destroyed) throw new InvalidOperationException("Client is destroyed");
        if (OperatingSystem.IsBrowser()) return Task.CompletedTask;
        if (TcpListener != null) return Task.CompletedTask;

        var bindAddr = address ?? System.Net.IPAddress.Any;
        TcpListener = new TcpListenerService(this, bindAddr, port);
        return TcpListener.StartAsync();
    }

    public async Task<Torrent?> AddBtpkAsync(byte[] publicKey, byte[]? salt = null,
        IDhtSigner? signer = null, AddTorrentOptions? opts = null, CancellationToken ct = default)
    {
        if (Destroyed) throw new InvalidOperationException("Client is destroyed");
        if (Dht == null && !OperatingSystem.IsBrowser())
        {
            // Auto-start DHT if not running. For callers that need a non-default
            // port (e.g. running multiple clients on one host) call EnsureDhtAsync
            // with a DhtOptions first - this lazy path uses default port 6881.
            await EnsureDhtAsync(ct: ct);
        }

        if (Dht == null)
        {
            OnWarning?.Invoke("BEP 46 requires DHT (desktop only) or tracker relay (browser)");
            return null;
        }

        // Create signer for verification - prefer explicit signer, then ReadOnlyEd25519Verifier, then NoOpSigner (test only)
        IDhtSigner resolvedSigner;
        if (signer != null)
        {
            resolvedSigner = signer;
        }
        else if (Crypto != null)
        {
            resolvedSigner = await ReadOnlyEd25519Verifier.CreateAsync(Crypto, publicKey);
        }
        else
        {
            if (VerboseLogging)
                Console.WriteLine("[WebTorrentClient] WARNING: No IPortableCrypto configured - BEP 46 signature verification disabled. Set WebTorrentClientOptions.Crypto for production use.");
            resolvedSigner = new NoOpSigner(publicKey);
        }

        // Resolve the current infohash from DHT
        var items = Dht.CreateMutableItems(resolvedSigner);
        items.AsyncFileSystem = AsyncFileSystem;
        await items.RestoreSequenceAsync();
        var result = await items.GetAsync(publicKey, salt, ct);

        Torrent? torrent = null;
        if (result.HasValue)
        {
            var infoHashHex = Convert.ToHexString(result.Value.value).ToLowerInvariant();
            torrent = Add(infoHashHex, opts);
            torrent.BtpkPublicKey = publicKey;
        }

        // Subscribe for updates — fires OnMutableUpdate when publisher pushes new version
        _ = Task.Run(async () =>
        {
            items.OnValueUpdated += (key, value, seq) =>
            {
                var newInfoHash = Convert.ToHexString(value).ToLowerInvariant();
                if (torrent != null && newInfoHash != torrent.InfoHash)
                {
                    torrent.NotifyMutableUpdate(newInfoHash);
                    OnMutableUpdate?.Invoke(torrent, newInfoHash);
                }
            };
            await items.SubscribeAsync(publicKey, salt, ct: ct);
        }, ct);

        return torrent;
    }

    /// <summary>
    /// Add a torrent from a .torrent file (byte array).
    /// Metadata is immediately available — no ut_metadata needed.
    /// </summary>
    public Torrent Add(byte[] torrentBytes, AddTorrentOptions? opts = null)
    {
        if (Destroyed) throw new InvalidOperationException("Client is destroyed");
        opts ??= new AddTorrentOptions();

        var metadata = TorrentParser.Parse(torrentBytes);
        // Merge default trackers for maximum peer discovery
        if (metadata.AnnounceUrls != null)
            metadata.AnnounceUrls = metadata.AnnounceUrls.Union(DefaultTrackers).ToArray();
        else
            metadata.AnnounceUrls = DefaultTrackers.ToArray();

        var torrent = new Torrent();
        torrent.InitFromMetadata(metadata, this, opts);

        // Duplicate check: match by WireInfoHashHex so pure-v2 torrents (empty v1
        // InfoHash) still dedup. Previous code matched only on InfoHash which meant
        // two calls with the same pure-v2 .torrent bytes would create two Torrents.
        var existing = Torrents.FirstOrDefault(t =>
            t != torrent &&
            !string.IsNullOrEmpty(torrent.WireInfoHashHex) &&
            string.Equals(t.WireInfoHashHex, torrent.WireInfoHashHex, StringComparison.OrdinalIgnoreCase));
        if (existing != null) return existing;

        Torrents.Add(torrent);
        torrent.OnReady += () => OnTorrentReady?.Invoke(torrent);
        OnAdd?.Invoke(torrent);
        return torrent;
    }

    // ========================
    // PERSISTENCE (restore from OPFS)
    // ========================

    /// <summary>
    /// Restore previously persisted torrents from async file system storage.
    /// Call after construction if using persistent storage.
    /// </summary>
    public async Task RestoreFromStorageAsync()
    {
        if (AsyncFileSystem == null) return;
        var stateDir = "webtorrent/_state";
        try
        {
            if (!await AsyncFileSystem.DirectoryExists(stateDir)) return;
            var files = await AsyncFileSystem.GetFiles(stateDir);
            foreach (var file in files)
            {
                if (!file.EndsWith(".torrent")) continue;
                try
                {
                    var torrentBytes = await AsyncFileSystem.ReadBytes($"{stateDir}/{file}");
                    if (torrentBytes == null || torrentBytes.Length == 0) continue;
                    var metadata = TorrentParser.Parse(torrentBytes);
                    metadata.OriginalTorrentBytes = torrentBytes;

                    // Skip if already loaded (compare on wire hash so pure-v2 torrents don't
                    // collide on empty v1 InfoHash)
                    var metaKey = metadata.WireInfoHashHex;
                    if (string.IsNullOrEmpty(metaKey)) continue;
                    if (Torrents.Any(t => t.WireInfoHashHex == metaKey)) continue;

                    // Read state BEFORE initializing so Paused is set before discovery starts
                    var restoreOpts = new AddTorrentOptions();
                    try
                    {
                        var stateFile = $"{stateDir}/{metaKey}.state.json";
                        if (await AsyncFileSystem.FileExists(stateFile))
                        {
                            var stateBytes = await AsyncFileSystem.ReadBytes(stateFile);
                            if (stateBytes != null)
                            {
                                var state = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, System.Text.Json.JsonElement>>(stateBytes);
                                if (state != null && state.TryGetValue("paused", out var pausedEl) && pausedEl.GetBoolean())
                                    restoreOpts.Paused = true;
                            }
                        }
                    }
                    catch { }

                    var torrent = new Torrent();
                    torrent.InitFromMetadata(metadata, this, restoreOpts);

                    // Check which pieces are already stored
                    if (torrent._store is Storage.AsyncFSChunkStore)
                    {
                        for (int i = 0; i < torrent.PieceCount; i++)
                        {
                            var piece = await torrent._store.GetAsync(i);
                            if (piece != null)
                            {
                                torrent.Bitfield[i] = true;
                                torrent.Pieces[i] = new Piece(0);
                            }
                        }
                        if (torrent.Bitfield.All(b => b))
                            torrent.Done = true;
                    }

                    Torrents.Add(torrent);
                    OnAdd?.Invoke(torrent);
                    if (VerboseLogging) Console.WriteLine($"[WebTorrentClient] Restored: {torrent.Name} ({torrent.CompletedPieces}/{torrent.PieceCount} pieces, paused={torrent.Paused})");
                }
                catch (Exception ex)
                {
                    if (VerboseLogging) Console.WriteLine($"[WebTorrentClient] Failed to restore {file}: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            if (VerboseLogging) Console.WriteLine($"[WebTorrentClient] Restore error: {ex.Message}");
        }
    }

    // ========================
    // SEED TORRENT
    // ========================

    /// <summary>
    /// Create a torrent from in-memory data and begin seeding it.
    /// Returns the Torrent immediately, already marked as Done with all pieces stored.
    /// </summary>
    public async Task<Torrent> SeedAsync(string name, byte[] data, TorrentCreatorOptions? options = null, AddTorrentOptions? addOpts = null)
    {
        if (Destroyed) throw new InvalidOperationException("Client is destroyed");
        options ??= new TorrentCreatorOptions();
        addOpts ??= new AddTorrentOptions();

        var (torrentBytes, metadata) = TorrentCreator.CreateFromBytes(name, data, options);
        return await SeedFromMetadataAsync(metadata, new[] { data }, addOpts);
    }

    /// <summary>
    /// Create a multi-file torrent and begin seeding it.
    /// </summary>
    public async Task<Torrent> SeedAsync(string torrentName, (string path, byte[] data)[] files, TorrentCreatorOptions? options = null, AddTorrentOptions? addOpts = null)
    {
        if (Destroyed) throw new InvalidOperationException("Client is destroyed");
        options ??= new TorrentCreatorOptions();
        addOpts ??= new AddTorrentOptions();

        var (torrentBytes, metadata) = TorrentCreator.CreateFromMultipleFiles(torrentName, files, options);

        // Concatenate all file data for piece storage
        var allData = new byte[files.Sum(f => (long)f.data.Length)];
        long offset = 0;
        foreach (var file in files)
        {
            Array.Copy(file.data, 0, allData, offset, file.data.Length);
            offset += file.data.Length;
        }

        return await SeedFromMetadataAsync(metadata, new[] { allData }, addOpts);
    }

    /// <summary>
    /// Add a torrent from .torrent file bytes and begin seeding if we have data.
    /// </summary>
    public Torrent AddFromTorrentFile(byte[] torrentFileBytes, AddTorrentOptions? opts = null)
    {
        return Add(torrentFileBytes, opts);
    }

    /// <summary>
    /// Get a torrent by info hash hex. Matches on v1 InfoHash when present; for pure-v2
    /// torrents (v1 empty) matches against the first 20 bytes of V2InfoHash — the wire-
    /// compat convention used by incoming BT handshakes and tracker announce routing.
    /// Also matches exact V2InfoHash (64 hex chars) so callers with only the v2 hash can
    /// look up their pure-v2 torrent.
    /// </summary>
    public Torrent? Get(string infoHash)
    {
        if (string.IsNullOrEmpty(infoHash)) return null;
        return Torrents.FirstOrDefault(t =>
            string.Equals(t.InfoHash, infoHash, StringComparison.OrdinalIgnoreCase)
            || string.Equals(t.V2InfoHash, infoHash, StringComparison.OrdinalIgnoreCase)
            || string.Equals(t.WireInfoHashHex, infoHash, StringComparison.OrdinalIgnoreCase));
    }

    private async Task<Torrent> SeedFromMetadataAsync(TorrentMetadata metadata, byte[][] dataChunks, AddTorrentOptions addOpts)
    {
        var torrent = new Torrent();
        torrent.InitFromMetadata(metadata, this, addOpts);

        // Write all pieces to the store
        var data = dataChunks[0]; // For single-file, this is the full data
        for (int i = 0; i < metadata.PieceCount; i++)
        {
            int pieceOffset = i * metadata.PieceLength;
            int pieceLen = (i == metadata.PieceCount - 1)
                ? (int)(metadata.TotalLength - (long)i * metadata.PieceLength)
                : metadata.PieceLength;

            if (pieceOffset + pieceLen <= data.Length)
            {
                await torrent._store!.PutAsync(i, data.AsMemory(pieceOffset, pieceLen));
            }

            // Mark piece as verified and done
            torrent.Bitfield[i] = true;
            torrent.Pieces[i] = new Piece(0);
        }

        torrent.Done = true;

        // Check for duplicate (WireInfoHashHex handles pure-v2 dedup)
        var existing = Torrents.FirstOrDefault(t =>
            t != torrent &&
            !string.IsNullOrEmpty(torrent.WireInfoHashHex) &&
            string.Equals(t.WireInfoHashHex, torrent.WireInfoHashHex, StringComparison.OrdinalIgnoreCase));
        if (existing != null) return existing;

        Torrents.Add(torrent);
        torrent.OnReady += () => OnTorrentReady?.Invoke(torrent);
        OnAdd?.Invoke(torrent);

        return torrent;
    }

    // ========================
    // SERVICE WORKER STREAMING
    // ========================

    /// <summary>Register a ServiceWorkerStreamHandler to enable media streaming via service worker.</summary>
    public void RegisterStreamHandler(ServiceWorkerStreamHandler handler)
    {
        StreamHandler = handler;
        handler.OnRequest += HandleStreamRequest;
    }

    /// <summary>Handle incoming stream requests from the service worker.</summary>
    private void HandleStreamRequest(StreamRequest request)
    {
        if (request.Handled) return;

        // GetStreamUrl emits WireInfoHashHex (v1 when present, first 20 bytes of v2
        // for pure-v2), so the lookup here must match on WireInfoHashHex too.
        var torrent = Torrents.FirstOrDefault(t =>
            t.HasMetadata && string.Equals(t.WireInfoHashHex, request.InfoHash, StringComparison.OrdinalIgnoreCase));

        if (torrent?.Files == null || request.FileIndex < 0 || request.FileIndex >= torrent.Files.Length)
            return;

        request.RespondWithStream(torrent, request.FileIndex);
    }

    // ========================
    // REMOVE TORRENT
    // ========================

    /// <summary>Remove a torrent from the client.</summary>
    public async Task RemoveAsync(Torrent torrent)
    {
        Torrents.Remove(torrent);
        // WireInfoHashHex so pure-v2 torrents hit the correct OPFS path
        // (v1 hash for v1/hybrid, first 20 bytes of v2 hash for pure-v2).
        var infoHash = torrent.WireInfoHashHex;

        // Delete persisted files FIRST (before dispose which might throw on WebSocket teardown)
        if (AsyncFileSystem != null && !string.IsNullOrEmpty(infoHash))
        {
            try
            {
                var stateFile = $"webtorrent/_state/{infoHash}.state.json";
                if (await AsyncFileSystem.FileExists(stateFile))
                    await AsyncFileSystem.Remove(stateFile);
                var metaFile = $"webtorrent/_state/{infoHash}.torrent";
                if (await AsyncFileSystem.FileExists(metaFile))
                    await AsyncFileSystem.Remove(metaFile);
            }
            catch { }
        }

        try { if (!torrent.Destroyed) await torrent.DisposeAsync(); } catch { }
        OnRemove?.Invoke(torrent);
    }

    /// <summary>Remove a torrent by info hash (v1, full v2, or wire truncation).</summary>
    public async Task RemoveAsync(string infoHash)
    {
        var torrent = Get(infoHash);
        if (torrent != null) await RemoveAsync(torrent);
    }

    /// <summary>Remove a torrent and delete all associated data from storage.</summary>
    public async Task RemoveWithDataAsync(Torrent torrent)
    {
        // WireInfoHashHex so pure-v2 data dir (webtorrent/<v2-prefix>) is actually removed
        var infoHash = torrent.WireInfoHashHex;
        await RemoveAsync(torrent);
        if (AsyncFileSystem != null && !string.IsNullOrEmpty(infoHash))
        {
            // Delete piece data
            var dataDir = $"webtorrent/{infoHash}";
            if (await AsyncFileSystem.DirectoryExists(dataDir))
                await AsyncFileSystem.Remove(dataDir, recursive: true);
            // Delete persisted .torrent metadata
            var metaFile = $"webtorrent/_state/{infoHash}.torrent";
            if (await AsyncFileSystem.FileExists(metaFile))
                await AsyncFileSystem.Remove(metaFile);
        }
    }

    /// <summary>Remove a torrent (v1, full v2, or wire truncation) and delete all associated data.</summary>
    public async Task RemoveWithDataAsync(string infoHash)
    {
        var torrent = Get(infoHash);
        if (torrent != null) await RemoveWithDataAsync(torrent);
    }

    // ========================
    // PEER FACTORY (for tracker to create WebRTC peers)
    // ========================

    /// <summary>Factory for creating WebRTC peers. Override for platform-specific implementations.</summary>
    public Func<bool, SimplePeer>? PeerFactory { get; set; }

    /// <summary>Create a SimplePeer instance for use by trackers.</summary>
    public SimplePeer CreatePeer(bool initiator)
    {
        if (PeerFactory != null)
            return PeerFactory(initiator);

        // RtcPeer is cross-platform via SpawnDev.RTC - works on both browser and desktop
        return new RtcPeer(initiator, IceServers, trickle: false);
    }

    // ========================
    // DESTROY
    // ========================

    public async ValueTask DisposeAsync()
    {
        if (Destroyed) return;
        Destroyed = true;

        foreach (var torrent in Torrents.ToArray())
        {
            try { await torrent.DisposeAsync(); } catch { }
        }
        Torrents.Clear();

        // Release the DHT UDP socket + routing-table worker. Without this,
        // the socket leaks across tests on the same port, and any call to
        // EnsureDhtAsync from a subsequent client collides on bind.
        if (Dht != null)
        {
            try { await Dht.DisposeAsync(); } catch { }
            Dht = null;
        }

        // Release the inbound TCP listener if one was started. Same rationale
        // as DHT: the listening socket leaks across tests if not explicitly
        // closed and the next bind on the same port fails.
        if (TcpListener != null)
        {
            try { await TcpListener.DisposeAsync(); } catch { }
            TcpListener = null;
        }

        _http.Dispose();
    }
}

// ========================
// OPTIONS
// ========================

public class WebTorrentClientOptions
{
    public string? PeerId { get; set; }
    public int MaxConns { get; set; } = WebTorrentClient.DefaultMaxConns;
    /// <summary>Enable BEP 19 web seeds. Default true.</summary>
    public bool EnableWebSeeds { get; set; } = true;
    /// <summary>Enable trackers (WebSocket + HTTP + UDP). Default true.</summary>
    public bool EnableTrackers { get; set; } = true;
    /// <summary>Enable DHT peer discovery (BEP 5, desktop only). Default true.</summary>
    public bool EnableDht { get; set; } = true;
    /// <summary>Enable Local Service Discovery (BEP 14, desktop only). Default true.</summary>
    public bool EnableLsd { get; set; } = true;
    /// <summary>Enable Peer Exchange (BEP 11). Default true.</summary>
    public bool EnableUtPex { get; set; } = true;
    /// <summary>DHT node ID (20 bytes). Auto-generated if null.</summary>
    public byte[]? NodeId { get; set; }
    /// <summary>Download speed limit in bytes/sec. -1 = unlimited.</summary>
    public long DownloadLimit { get; set; } = -1;
    /// <summary>Upload speed limit in bytes/sec. -1 = unlimited.</summary>
    public long UploadLimit { get; set; } = -1;
    public string[]? IceServers { get; set; }
    /// <summary>Default WSS trackers merged into every torrent for peer discovery. Set to empty array to disable.</summary>
    public string[]? DefaultTrackers { get; set; }
    /// <summary>
    /// Bind a TCP peer-wire listener at this port and auto-start it on construction.
    /// `null` (default) = no listener (back-compat). `0` = kernel-assigned ephemeral
    /// port; read <see cref="WebTorrentClient.TcpListener"/>.LocalEndPoint.Port back to
    /// learn the actual port. `&gt;0` = bind that specific port. Desktop only -
    /// browser-side ignores. Lets mainline clients (qBittorrent, libtorrent,
    /// Transmission) dial in by IP+port to leech torrents we're seeding.
    /// </summary>
    public int? TcpListenPort { get; set; }
    /// <summary>Local address to bind <see cref="TcpListenPort"/> to. Defaults to
    /// <see cref="System.Net.IPAddress.Any"/> so external peers can reach the
    /// listener. Pass <see cref="System.Net.IPAddress.Loopback"/> for localhost-only
    /// test harnesses. Ignored when <see cref="TcpListenPort"/> is null.</summary>
    public System.Net.IPAddress? TcpListenAddress { get; set; }
    /// <summary>
    /// When <c>true</c> AND a <see cref="WebTorrentClient.TcpListener"/> is
    /// running, every HTTP / UDP tracker announce includes the listener's
    /// actual port in the BEP 3 <c>port=</c> field so trackers put us in their
    /// compact peer list and other clients (mainline + ours) can dial in.
    /// Default <c>false</c> = legacy behavior (HTTP tracker advertised
    /// <c>port=0</c>, UDP tracker advertised <c>6881</c>). WebSocket/WebRTC
    /// tracker signaling is unaffected. Desktop only - has no effect in the
    /// browser since browsers can't bind a listener anyway. Only relevant when
    /// the listener is reachable from outside (port-forwarded, public IP, or
    /// local subnet) - advertising a port that nobody can reach is harmless
    /// but useless.
    /// </summary>
    public bool AdvertiseTcpListenerToTrackers { get; set; }
    public HttpClient? HttpClient { get; set; }
    /// <summary>Async file system for persistent storage (OPFS in browser, native FS on desktop).</summary>
    public SpawnDev.AsyncFileSystem.IAsyncFS? AsyncFileSystem { get; set; }
    /// <summary>Service worker stream handler for media streaming.</summary>
    public ServiceWorkerStreamHandler? StreamHandler { get; set; }
    /// <summary>IP addresses to block from connecting.</summary>
    public HashSet<string>? Blocklist { get; set; }
    /// <summary>Portable crypto for Ed25519 verification (BEP 44/46). Required for secure mutable DHT operations.</summary>
    public IPortableCrypto? Crypto { get; set; }
    /// <summary>Optional piece-hash engine override. Default
    /// <see cref="SystemCryptoPieceHashEngine"/> (System.Security.Cryptography).
    /// Provide a custom <see cref="IPieceHashEngine"/> here to route piece
    /// verification through a GPU / batched implementation.</summary>
    public IPieceHashEngine? PieceHashEngine { get; set; }

    /// <summary>
    /// High-level upload bandwidth policy. <see cref="BandwidthPolicy.Unlimited"/>
    /// (default) preserves legacy behavior. Other values translate to a
    /// concrete <see cref="WebTorrentClient.UploadRateLimiter"/> rate via
    /// <see cref="BandwidthPolicyExtensions.ToUploadBytesPerSec"/>. When
    /// <see cref="UploadLimit"/> is set explicitly (>= 0), it wins over the
    /// policy - the policy is the convenience layer for callers who want to
    /// say "be reasonable on a metered connection" without picking a number.
    /// </summary>
    public BandwidthPolicy BandwidthPolicy { get; set; } = BandwidthPolicy.Unlimited;
}

public class AddTorrentOptions
{
    public bool DisableWebSeeds { get; set; }
    public string Strategy { get; set; } = "rarest";
    public string? Path { get; set; }
    /// <summary>
    /// Add torrent in paused state. Metadata will download (via ut_metadata),
    /// but no piece data downloads until files are selected or a read/stream is requested.
    /// </summary>
    public bool Paused { get; set; }
    /// <summary>
    /// Start with no pieces selected (but still connect to peers, unlike Paused).
    /// Files must be individually selected before their pieces download.
    /// </summary>
    public bool Deselect { get; set; }
    /// <summary>Skip piece verification on start (trust existing store data).</summary>
    public bool SkipVerify { get; set; }
    /// <summary>Max simultaneous web seed connections for this torrent.</summary>
    public int MaxWebConns { get; set; } = 4; // parallel HTTP web-seed range requests. Kept <= the browser's ~6/origin HTTP/1.1 cap to avoid queueing; browser throughput is bounded by single-thread per-piece processing, not connection count.
    /// <summary>Seconds between "no peers" event checks. Default 30.</summary>
    public int NoPeersIntervalTime { get; set; } = 30;
}
