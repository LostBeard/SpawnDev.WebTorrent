using System.Security.Cryptography;
using System.Text;

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
    /// Configurable via WebTorrentClientOptions.DefaultTrackers. Set to empty array to disable.
    /// </summary>
    public string[] DefaultTrackers { get; set; } = new[]
    {
        "wss://tracker.openwebtorrent.com",
        "wss://tracker.webtorrent.dev",
        "wss://tracker.btorrent.xyz",
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
        if (opts.UploadLimit >= 0) UploadRateLimiter.Rate = opts.UploadLimit;
        if (opts.Blocklist != null) foreach (var ip in opts.Blocklist) Blocklist.Add(ip);

        _http = opts.HttpClient ?? new HttpClient();
        AsyncFileSystem = opts.AsyncFileSystem;

        // Wire up stream handler if provided
        if (opts.StreamHandler != null)
            RegisterStreamHandler(opts.StreamHandler);

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
    /// Add a torrent to download. Accepts magnet URI or info hash hex.
    /// Returns the Torrent immediately (metadata may still be resolving via ut_metadata).
    /// </summary>
    public Torrent Add(string magnetOrInfoHash, AddTorrentOptions? opts = null)
    {
        if (Destroyed) throw new InvalidOperationException("Client is destroyed");
        opts ??= new AddTorrentOptions();

        var torrent = new Torrent();

        // Register ut_metadata extension — this is how magnet links get metadata
        UseExtension((wire) =>
        {
            var ext = new UtMetadataExtension();
            ext.SetWire(wire);
            ext.OnMetadata += (infoDictBytes) =>
            {
                if (torrent.HasMetadata) return;
                // Parse the received info dict
                var infoHashBytes = Convert.FromHexString(torrent.InfoHash ?? "");
                var metadata = TorrentParser.ParseInfoDict(infoDictBytes, infoHashBytes);
                if (metadata != null)
                    torrent.SetMetadata(metadata);
            };
            ext.OnWarning += (msg) => OnWarning?.Invoke(msg);
            return ext;
        });

        // Start the torrent — parse magnet and begin discovery
        // Default trackers are merged inside InitFromMagnetAsync before StartDiscovery
        _ = torrent.InitFromMagnetAsync(magnetOrInfoHash, this, opts);

        // Check for duplicate after InfoHash is set
        var existing = Torrents.FirstOrDefault(t => t.InfoHash == torrent.InfoHash && t != torrent);
        if (existing != null)
        {
            OnWarning?.Invoke($"Duplicate torrent: {torrent.InfoHash}");
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
    public async Task<Torrent?> AddBtpkAsync(byte[] publicKey, byte[]? salt = null,
        IDhtSigner? signer = null, AddTorrentOptions? opts = null, CancellationToken ct = default)
    {
        if (Destroyed) throw new InvalidOperationException("Client is destroyed");
        if (Dht == null && !OperatingSystem.IsBrowser())
        {
            // Auto-start DHT if not running
            Dht = new DhtDiscovery();
            await Dht.StartAsync(new byte[20], ct: ct);
        }

        if (Dht == null)
        {
            OnWarning?.Invoke("BEP 46 requires DHT (desktop only) or tracker relay (browser)");
            return null;
        }

        // Resolve the current infohash from DHT
        var items = signer != null ? Dht.CreateMutableItems(signer) : Dht.CreateMutableItems(new NoOpSigner(publicKey));
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

        var existing = Torrents.FirstOrDefault(t => t.InfoHash == torrent.InfoHash && t != torrent);
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

                    // Skip if already loaded
                    if (Torrents.Any(t => t.InfoHash == metadata.InfoHash)) continue;

                    // Read state BEFORE initializing so Paused is set before discovery starts
                    var restoreOpts = new AddTorrentOptions();
                    try
                    {
                        var stateFile = $"{stateDir}/{metadata.InfoHash}.state.json";
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

    /// <summary>Get a torrent by info hash hex.</summary>
    public Torrent? Get(string infoHash)
    {
        return Torrents.FirstOrDefault(t =>
            string.Equals(t.InfoHash, infoHash, StringComparison.OrdinalIgnoreCase));
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

        // Check for duplicate
        var existing = Torrents.FirstOrDefault(t => t.InfoHash == torrent.InfoHash && t != torrent);
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

        var torrent = Torrents.FirstOrDefault(t =>
            t.HasMetadata && t.InfoHashHex == request.InfoHash);

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
        var infoHash = torrent.InfoHash;

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

    /// <summary>Remove a torrent by info hash.</summary>
    public async Task RemoveAsync(string infoHash)
    {
        var torrent = Torrents.FirstOrDefault(t => t.InfoHash == infoHash);
        if (torrent != null) await RemoveAsync(torrent);
    }

    /// <summary>Remove a torrent and delete all associated data from storage.</summary>
    public async Task RemoveWithDataAsync(Torrent torrent)
    {
        var infoHash = torrent.InfoHash;
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

    /// <summary>Remove a torrent by info hash and delete all associated data.</summary>
    public async Task RemoveWithDataAsync(string infoHash)
    {
        var torrent = Torrents.FirstOrDefault(t => t.InfoHash == infoHash);
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

        // Default: SipSorcery for desktop (NUnit/console), BrowserPeer for Blazor WASM
        // Runtime detection: if SIPSorcery types are available, use them
        return new SipSorceryPeer(initiator, IceServers, trickle: false);
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
    public HttpClient? HttpClient { get; set; }
    /// <summary>Async file system for persistent storage (OPFS in browser, native FS on desktop).</summary>
    public SpawnDev.AsyncFileSystem.IAsyncFS? AsyncFileSystem { get; set; }
    /// <summary>Service worker stream handler for media streaming.</summary>
    public ServiceWorkerStreamHandler? StreamHandler { get; set; }
    /// <summary>IP addresses to block from connecting.</summary>
    public HashSet<string>? Blocklist { get; set; }
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
    public int MaxWebConns { get; set; } = 4;
    /// <summary>Seconds between "no peers" event checks. Default 30.</summary>
    public int NoPeersIntervalTime { get; set; } = 30;
}
