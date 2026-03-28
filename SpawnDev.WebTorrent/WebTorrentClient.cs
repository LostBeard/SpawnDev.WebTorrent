using SpawnDev.WebTorrent.Discovery;
using SpawnDev.WebTorrent.Storage;
using SpawnDev.WebTorrent.Torrent;
using SpawnDev.WebTorrent.Transports;

namespace SpawnDev.WebTorrent;

/// <summary>
/// Pure C# WebTorrent client. Runs on desktop (.NET) and browser (Blazor WASM).
/// No JavaScript dependencies. Manages torrents, peers, and piece downloads.
///
/// Usage:
///   var client = new WebTorrentClient(options);
///   var torrent = await client.AddAsync(magnetUri);
///   var data = await torrent.Files[0].ReadAsync(offset, length);
/// </summary>
public class WebTorrentClient : IAsyncDisposable
{
    private readonly WebTorrentOptions _options;
    private readonly List<TorrentSwarm> _torrents = new();
    private readonly List<ITransport> _transports = new();
    private readonly List<IDiscovery> _discoveryProviders = new();
    private readonly byte[] _peerId;

    /// <summary>Active torrents.</summary>
    public IReadOnlyList<TorrentSwarm> Torrents => _torrents;

    /// <summary>This client's peer ID (20 bytes, Azureus-style).</summary>
    public byte[] PeerId => _peerId;

    /// <summary>Aggregate download speed in bytes/sec.</summary>
    public double DownloadSpeed => _torrents.Sum(t => t.DownloadSpeed);

    /// <summary>Aggregate upload speed in bytes/sec.</summary>
    public double UploadSpeed => _torrents.Sum(t => t.UploadSpeed);

    /// <summary>Overall progress across all active torrents (0.0 to 1.0).</summary>
    public double Progress => _torrents.Count > 0 ? _torrents.Average(t => t.Progress) : 0;

    /// <summary>Aggregate seed ratio (uploaded/downloaded).</summary>
    public double Ratio
    {
        get
        {
            long down = _torrents.Sum(t => t.Downloaded);
            long up = _torrents.Sum(t => t.Uploaded);
            return down > 0 ? (double)up / down : 0;
        }
    }

    /// <summary>Upload rate limiter.</summary>
    public RateLimiter UploadLimiter { get; } = new(-1);

    /// <summary>Download rate limiter.</summary>
    public RateLimiter DownloadLimiter { get; } = new(-1);

    /// <summary>Maximum upload rate in bytes/sec (-1 = unlimited, 0 = disabled).</summary>
    public long UploadLimit
    {
        get => UploadLimiter.Rate;
        set => UploadLimiter.Rate = value;
    }

    /// <summary>Maximum download rate in bytes/sec (-1 = unlimited).</summary>
    public long DownloadLimit
    {
        get => DownloadLimiter.Rate;
        set => DownloadLimiter.Rate = value;
    }

    // Events
    public event Action<TorrentSwarm>? OnTorrentAdd;
    public event Action<TorrentSwarm>? OnTorrentRemove;
    public event Action<TorrentSwarm>? OnTorrentReady;
    public event Action<TorrentSwarm>? OnTorrentDone;
    public event Action<Exception>? OnError;

    public WebTorrentClient(WebTorrentOptions? options = null)
    {
        _options = options ?? new WebTorrentOptions();
        UploadLimit = _options.UploadLimit;
        DownloadLimit = _options.DownloadLimit;

        // Generate peer ID: -SD0110- + 12 random bytes (Azureus-style)
        // SD = SpawnDev, 0110 = v1.1.0
        _peerId = new byte[20];
        "-SD0110-"u8.CopyTo(_peerId);
        Random.Shared.NextBytes(_peerId.AsSpan(8));
    }

    /// <summary>
    /// Register a transport (TCP, WebRTC, etc.).
    /// Call before adding torrents. Different transports for desktop vs browser.
    /// </summary>
    public void AddTransport(ITransport transport)
    {
        _transports.Add(transport);
        transport.OnConnection += HandleIncomingConnection;
    }

    /// <summary>
    /// Register a discovery provider (tracker, DHT, etc.).
    /// </summary>
    public void AddDiscovery(IDiscovery discovery)
    {
        _discoveryProviders.Add(discovery);
    }

    /// <summary>
    /// Add a torrent by magnet URI, info hash, or parsed metadata.
    /// </summary>
    public async Task<TorrentSwarm> AddAsync(string magnetOrInfoHash, AddTorrentOptions? options = null)
    {
        options ??= new AddTorrentOptions();

        var swarm = new TorrentSwarm(this, options);
        WireSwarmEvents(swarm);
        _torrents.Add(swarm);
        OnTorrentAdd?.Invoke(swarm);

        // Parse magnet URI or info hash
        await swarm.InitializeAsync(magnetOrInfoHash);

        // Start discovery
        foreach (var discovery in _discoveryProviders)
        {
            discovery.OnPeer += peer => swarm.AddPeer(peer);
            await discovery.StartAsync(swarm.InfoHash, 0);
        }

        return swarm;
    }

    /// <summary>
    /// Add a torrent from parsed metadata (e.g., from a .torrent file).
    /// </summary>
    public async Task<TorrentSwarm> AddAsync(TorrentMetadata metadata, AddTorrentOptions? options = null)
    {
        options ??= new AddTorrentOptions();

        var swarm = new TorrentSwarm(this, options);
        WireSwarmEvents(swarm);
        _torrents.Add(swarm);
        OnTorrentAdd?.Invoke(swarm);

        swarm.SetMetadata(metadata);

        foreach (var discovery in _discoveryProviders)
        {
            discovery.OnPeer += peer => swarm.AddPeer(peer);
            await discovery.StartAsync(metadata.InfoHash, 0);
        }

        return swarm;
    }

    private void WireSwarmEvents(TorrentSwarm swarm)
    {
        swarm.OnReady += () => OnTorrentReady?.Invoke(swarm);
        swarm.OnDone += () => OnTorrentDone?.Invoke(swarm);
        swarm.OnError += (ex) => OnError?.Invoke(ex);
    }

    /// <summary>Seed data by creating a torrent and making it available.</summary>
    public async Task<TorrentSwarm> SeedAsync(byte[] data, string name,
        TorrentCreatorOptions? createOptions = null, AddTorrentOptions? addOptions = null)
    {
        var (torrentBytes, metadata) = Torrent.TorrentCreator.CreateFromBytes(name, data, createOptions);
        var swarm = await AddAsync(metadata, addOptions);

        // Store all pieces in the chunk store so we can serve them
        if (swarm.Store != null && swarm.PieceManager != null)
        {
            for (int i = 0; i < metadata.PieceCount; i++)
            {
                int pieceStart = i * metadata.PieceLength;
                int pieceLen = Math.Min(metadata.PieceLength, data.Length - pieceStart);
                var pieceData = new byte[pieceLen];
                Array.Copy(data, pieceStart, pieceData, 0, pieceLen);
                await swarm.Store.PutAsync(i, pieceData);
                swarm.PieceManager.MarkComplete(i);
            }

            // Mark swarm as done since all pieces are seeded
            swarm.MarkDone();
        }

        return swarm;
    }

    /// <summary>Get a torrent by info hash (hex string or 20-byte array).</summary>
    public TorrentSwarm? Get(string infoHashHex)
    {
        var hash = infoHashHex.Length == 40
            ? Convert.FromHexString(infoHashHex)
            : System.Text.Encoding.ASCII.GetBytes(infoHashHex);
        return _torrents.FirstOrDefault(t => t.InfoHash.SequenceEqual(hash));
    }

    /// <summary>Get a torrent by info hash bytes.</summary>
    public TorrentSwarm? Get(byte[] infoHash)
        => _torrents.FirstOrDefault(t => t.InfoHash.SequenceEqual(infoHash));

    /// <summary>
    /// Create an HTTP server that serves torrent content with range request support.
    /// Access files at: http://localhost:{port}/{infoHash}/{filePath}
    /// Desktop only — browser uses blob URLs for streaming.
    /// </summary>
    public TorrentHttpServer CreateServer(int port = 8080)
    {
        var server = new TorrentHttpServer(this, port);
        server.Start();
        return server;
    }

    /// <summary>Remove a torrent and optionally destroy its data.</summary>
    public async Task RemoveAsync(TorrentSwarm torrent, bool destroyStore = false)
    {
        _torrents.Remove(torrent);
        OnTorrentRemove?.Invoke(torrent);
        await torrent.DisposeAsync();
    }

    /// <summary>
    /// Add a torrent from .torrent file bytes. Parses metadata and starts discovery.
    /// </summary>
    public async Task<TorrentSwarm> AddFromTorrentFileAsync(byte[] torrentFileBytes,
        AddTorrentOptions? options = null)
    {
        var metadata = Torrent.TorrentParser.Parse(torrentFileBytes);
        return await AddAsync(metadata, options);
    }

    /// <summary>
    /// Quick setup: create client with default tracker and add a magnet URI.
    /// Convenience method for the simplest use case.
    /// </summary>
    public static async Task<(WebTorrentClient client, TorrentSwarm swarm)> QuickStartAsync(
        string magnetUri, WebTorrentOptions? options = null)
    {
        var client = new WebTorrentClient(options);
        var swarm = await client.AddAsync(magnetUri);
        return (client, swarm);
    }

    private void HandleIncomingConnection(IConnection connection)
    {
        _ = HandleIncomingConnectionAsync(connection);
    }

    private async Task HandleIncomingConnectionAsync(IConnection connection)
    {
        try
        {
            // Perform handshake to determine which torrent this peer wants
            var wire = new Wire.WireProtocol(connection);

            // Receive their handshake first
            if (!await wire.ReceiveHandshakeAsync())
            {
                await connection.CloseAsync();
                return;
            }

            if (wire.RemoteInfoHash == null)
            {
                await connection.CloseAsync();
                return;
            }

            // Find the matching torrent
            var swarm = _torrents.FirstOrDefault(t => t.InfoHash.SequenceEqual(wire.RemoteInfoHash));
            if (swarm == null)
            {
                await connection.CloseAsync();
                return;
            }

            // Send our handshake back
            await wire.SendHandshakeAsync(swarm.InfoHash, _peerId);

            // Add as a connected peer
            var peerInfo = new Discovery.PeerInfo
            {
                Address = connection.RemoteId,
                Source = connection.TransportType,
            };
            await swarm.AddConnectedPeerAsync(wire, peerInfo);
        }
        catch
        {
            try { await connection.CloseAsync(); } catch { }
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var torrent in _torrents.ToArray())
            await torrent.DisposeAsync();
        _torrents.Clear();

        foreach (var transport in _transports)
            await transport.DisposeAsync();
        _transports.Clear();

        foreach (var discovery in _discoveryProviders)
            await discovery.DisposeAsync();
        _discoveryProviders.Clear();
    }
}

/// <summary>Client-level configuration.</summary>
public class WebTorrentOptions
{
    /// <summary>Maximum peers per torrent (default 55).</summary>
    public int MaxConns { get; set; } = 55;

    /// <summary>Upload rate limit in bytes/sec (-1 = unlimited, 0 = disabled).</summary>
    public long UploadLimit { get; set; } = -1;

    /// <summary>Download rate limit in bytes/sec (-1 = unlimited).</summary>
    public long DownloadLimit { get; set; } = -1;

    /// <summary>Tracker announce URLs.</summary>
    public string[] Trackers { get; set; } = new[]
    {
        "wss://hub.spawndev.com:44365/announce",
        "wss://tracker.openwebtorrent.com",
        "wss://tracker.files.fm:7073/announce",
    };
}

/// <summary>Per-torrent options.</summary>
public class AddTorrentOptions
{
    /// <summary>Start paused (don't connect to peers immediately).</summary>
    public bool Paused { get; set; }

    /// <summary>Don't select any files for download initially.</summary>
    public bool Deselect { get; set; }

    /// <summary>Web seed URLs (HTTP fallback for piece downloads).</summary>
    public string[] WebSeeds { get; set; } = Array.Empty<string>();

    /// <summary>Piece selection strategy: "rarest" or "sequential".</summary>
    public string Strategy { get; set; } = "rarest";

    /// <summary>Custom chunk store factory. If null, uses platform default.</summary>
    public Func<int, IChunkStore>? StoreFactory { get; set; }

    /// <summary>
    /// Async file system for persistent storage (OPFS in browser, native on desktop).
    /// If provided, AsyncFSChunkStore is used instead of MemoryChunkStore.
    /// </summary>
    public SpawnDev.AsyncFileSystem.IAsyncFS? AsyncFileSystem { get; set; }
}
