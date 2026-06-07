using SpawnDev.WebTorrent.Storage;
using System.Security.Cryptography;
using System.Text;

namespace SpawnDev.WebTorrent;

/// <summary>
/// Represents a single torrent being downloaded/seeded.
/// Direct 1:1 port of webtorrent/lib/torrent.js lifecycle.
/// Download engine is in Torrent.Download.cs (partial class).
/// </summary>
public partial class Torrent : IAsyncDisposable
{
    // Fallback hash engine for the rare path where _client is null (parser
    // unit tests, in-memory torrent reconstruction without a client). Real
    // production paths run with _client set.
    private static readonly IPieceHashEngine _defaultEngine = new SystemCryptoPieceHashEngine();

    // ========================
    // STATE (match JS torrent properties)
    // ========================

    public string? InfoHash { get; set; }

    /// <summary>
    /// BEP 52 v2 info hash: lowercase hex of SHA-256 of the info dict bytes. Populated by
    /// parsing a v2 or hybrid magnet URI (<c>xt=urn:btmh:</c>) or by <see cref="InitFromMetadata"/>
    /// when the underlying torrent is v2. Empty string for v1-only torrents.
    /// </summary>
    public string V2InfoHash { get; set; } = "";

    /// <summary>
    /// The 20-byte info-hash value used on the BitTorrent wire (handshake bytes 28..47) and
    /// in tracker announce <c>info_hash</c> parameters. For v1-only and hybrid torrents this
    /// is the v1 SHA-1 infohash. For pure-v2 torrents (InfoHash empty, V2InfoHash set) this
    /// is the FIRST 20 bytes of the v2 SHA-256 infohash — the cross-client convention used
    /// by libtorrent / qBittorrent / rqbit for wire-compat with the 20-byte-wide BitTorrent
    /// handshake. Returns an empty string if neither hash is set.
    /// </summary>
    public string WireInfoHashHex
    {
        get
        {
            if (!string.IsNullOrEmpty(InfoHash)) return InfoHash;
            if (!string.IsNullOrEmpty(V2InfoHash) && V2InfoHash.Length >= 40)
                return V2InfoHash[..40].ToLowerInvariant();
            return "";
        }
    }

    /// <summary>
    /// Human-friendly display label for this torrent. Returns <see cref="Name"/> if set,
    /// otherwise falls back to <see cref="WireInfoHashHex"/>. Never returns null or
    /// empty so UI code can consume it directly without <c>??</c> chains that miss
    /// pure-v2 torrents (which have an empty <see cref="InfoHash"/> before metadata
    /// arrives).
    /// </summary>
    public string DisplayName
    {
        get
        {
            if (!string.IsNullOrEmpty(Name)) return Name!;
            var w = WireInfoHashHex;
            return !string.IsNullOrEmpty(w) ? w : "unknown";
        }
    }

    /// <summary>
    /// Compact (&lt;= 12-char) label for narrow UI cells. Returns <see cref="Name"/>
    /// unmodified if set (name text is left alone even when long), otherwise the first
    /// 12 chars of the wire hash, otherwise <c>"unknown"</c>. Use when table columns
    /// are width-limited; for full labels use <see cref="DisplayName"/>.
    /// </summary>
    public string DisplayNameShort
    {
        get
        {
            if (!string.IsNullOrEmpty(Name)) return Name!;
            var w = WireInfoHashHex;
            if (string.IsNullOrEmpty(w)) return "unknown";
            return w.Length >= 12 ? w[..12] : w;
        }
    }

    /// <summary>
    /// BEP 52 torrent meta version (mirror of <see cref="TorrentMetadata.MetaVersion"/>).
    /// <c>0</c> = v1-only / Phase 1 (piece hashes are flat SHA-1 or flat SHA-256);
    /// <c>2</c> = BEP 52 v2 (piece hashes are Merkle roots over 16 KiB leaves, requiring
    /// Merkle-tree verification not a single flat hash). Drives piece verification in
    /// <see cref="Torrent.Download"/>.
    /// </summary>
    public int MetaVersion { get; set; }

    /// <summary>
    /// BEP 52 per-file Merkle piece-layer roots: one 32-byte root per file in the same order
    /// as <see cref="Files"/>. Empty for v1-only torrents. Required for serving BEP 52 peer-
    /// wire <c>hash_request</c> queries and for issuing our own hash_requests on v2-only
    /// magnet bootstrap paths.
    /// </summary>
    public byte[][] FileRoots { get; set; } = Array.Empty<byte[]>();

    /// <summary>
    /// BEP 52 piece-layer hashes indexed by per-file root. Each value is the concatenated
    /// piece-layer hashes for that file (one 32-byte hash per piece). Files whose length is
    /// less than or equal to <see cref="PieceLength"/> do not appear - their root IS their
    /// single piece-layer hash. Empty for v1-only torrents. Populated from
    /// <see cref="TorrentMetadata.PieceLayers"/> by <see cref="SetMetadata"/>.
    /// </summary>
    public Dictionary<byte[], byte[]> PieceLayers { get; set; }
        = new Dictionary<byte[], byte[]>(ByteArrayEqualityComparer.Instance);

    /// <summary>
    /// BEP 52 peer-wire hash-request/hashes/hash-reject correlation + verification state
    /// machine. Allocated on demand when a v2 or hybrid torrent parses metadata, shared
    /// across all peer Wires so the torrent can correlate a response on wire A to a request
    /// issued through wire B if the first peer times out. <c>null</c> for v1-only torrents.
    /// </summary>
    public V2HashRequestCoordinator? V2HashCoord { get; private set; }

    /// <summary>
    /// Per-global-piece length in bytes. For v1 and hybrid torrents this is
    /// [PieceLength, PieceLength, ..., LastPieceLength]. For pure-v2 multi-file torrents
    /// each file's LAST piece may be partial (shorter than PieceLength) because BEP 52
    /// requires implicit zero-padding between files so every file starts on a piece
    /// boundary in the virtual stream - so pieces that straddle a file's end are short
    /// even when they're not the final piece of the whole torrent. Populated by
    /// <see cref="SetMetadata"/>; consumed by <see cref="VerifyPieceHash"/> and the
    /// <c>Pieces[]</c> initializer.
    /// </summary>
    private int[] _pieceLengths = Array.Empty<int>();

    /// <summary>
    /// Compute the per-global-piece length array. Same logic as the old hard-coded
    /// `LastPieceLength for the final piece, PieceLength for all others` for v1 / hybrid,
    /// plus the pure-v2 multi-file variant where each file's last piece is sized from
    /// <c>file.Length % PieceLength</c>.
    /// </summary>
    private static int[] ComputePieceLengths(TorrentMetadata metadata)
    {
        int n = metadata.PieceCount;
        var result = new int[n];
        int pieceLen = metadata.PieceLength;

        // Pure-v2 multi-file path: derive per-piece length from per-file piece counts.
        bool isPureV2MultiFile = metadata.MetaVersion == 2
            && string.IsNullOrEmpty(metadata.InfoHash)  // pure v2 only, not hybrid
            && metadata.Files != null && metadata.Files.Length > 1;

        if (isPureV2MultiFile)
        {
            int globalIdx = 0;
            foreach (var file in metadata.Files!)
            {
                if (file.Length == 0) continue;
                int filePieceCount = (int)((file.Length + pieceLen - 1) / pieceLen);
                int lastPieceLen = (int)(file.Length % pieceLen);
                if (lastPieceLen == 0) lastPieceLen = pieceLen;
                for (int pi = 0; pi < filePieceCount; pi++)
                {
                    if (globalIdx >= n) break;
                    result[globalIdx++] = (pi == filePieceCount - 1) ? lastPieceLen : pieceLen;
                }
            }
            // Pad any remainder (shouldn't happen if PieceCount matches) with PieceLength.
            while (globalIdx < n) result[globalIdx++] = pieceLen;
            return result;
        }

        // v1 / hybrid / single-file-v2: every piece is PieceLength except the last.
        int lastLen = (int)(metadata.TotalLength % pieceLen);
        if (lastLen == 0) lastLen = pieceLen;
        for (int i = 0; i < n - 1; i++) result[i] = pieceLen;
        if (n > 0) result[n - 1] = lastLen;
        return result;
    }

    public string? PeerIdHex { get; set; }
    public string? Name { get; set; }
    public int PieceLength { get; set; }
    public int LastPieceLength { get; set; }
    public long Length { get; set; }
    public bool Paused { get; set; }
    public bool Destroyed { get; private set; }
    public string? MagnetUri { get; set; }

    // Metadata
    public bool HasMetadata => _hashes.Length > 0;
    public string[] AnnounceUrls { get; set; } = Array.Empty<string>();
    public string[] UrlList { get; set; } = Array.Empty<string>();
    /// <summary>BEP 17 Hoffman-style HTTP seed URLs.</summary>
    public string[] HttpSeeds { get; set; } = Array.Empty<string>();
    public bool IsPrivate { get; set; }

    /// <summary>BEP 53: Selected file indices from magnet so= parameter. Null if not specified.</summary>
    public int[]? SelectedFileIndices { get; set; }

    // Pieces
    public Piece[] Pieces { get; set; } = Array.Empty<Piece>();
    public List<Wire> Wires { get; } = new();

    /// <summary>Diagnostic: last-piece completion outcome (OK / verify-FAIL / THREW / flush=null). For streaming triage.</summary>
    public string? LastCompletionNote;

    /// <summary>Diagnostic: WebRTC peers the factory was asked to create (signaling attempts) vs peers that
    /// actually reached AddPeer (connected). The gap reveals discovery-found-none vs attempted-but-never-connected.</summary>
    public int PeersAttempted;
    public int PeersConnected;
    public TorrentFileInfo[]? Files { get; set; }

    // Peers
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, Peer> _peers = new();
    public int NumPeers => Wires.Count;

    // Stats
    public long Received { get; set; }
    public long UploadedTotal { get; set; }
    public double Progress => Length > 0 ? (double)Downloaded / Length : 0;
    public long Downloaded
    {
        get
        {
            if (Bitfield.Length == 0) return 0;
            long dl = 0;
            for (int i = 0; i < Pieces.Length; i++)
            {
                if (i < Bitfield.Length && Bitfield[i])
                    dl += (i == Pieces.Length - 1) ? LastPieceLength : PieceLength;
                else if (Pieces[i] != null && Pieces[i].Length > 0)
                    dl += Pieces[i].Length - Pieces[i].Missing;
            }
            return dl;
        }
    }

    /// <summary>Alias for UploadedTotal (matches original TorrentSwarm API).</summary>
    public long Uploaded => UploadedTotal;

    /// <summary>InfoHash as hex string (already stored as hex in _Alt).</summary>
    public string InfoHashHex => InfoHash ?? "";

    /// <summary>Number of connected peers (wire protocol connections).</summary>
    public int PeerCount => Wires.Count;

    /// <summary>Number of connected web seeds.</summary>
    public int WebSeedCount => _webConns.Count;

    /// <summary>Per-tracker swarm stats (seeders/leechers) from announce responses.</summary>
    public Dictionary<string, TrackerUpdate> TrackerStats { get; } = new();

    /// <summary>Upload/download ratio.</summary>
    public double Ratio => Downloaded > 0 ? (double)UploadedTotal / Downloaded : 0;

    /// <summary>Number of completed (verified) pieces.</summary>
    public int CompletedPieces => Bitfield.Count(b => b);

    /// <summary>Total number of pieces.</summary>
    public int PieceCount => Bitfield.Length;

    /// <summary>Announce URLs from metadata (alias for AnnounceUrls).</summary>
    public string[] Announce => AnnounceUrls;

    /// <summary>
    /// Computed magnet URI with trackers and web seeds. Emits xt=urn:btih: for v1,
    /// xt=urn:btmh:1220&lt;digest&gt; for v2, and both for hybrid torrents. Returns
    /// empty string when no infohash of either kind is available.
    /// </summary>
    public string ComputedMagnetUri
    {
        get
        {
            bool hasV1 = !string.IsNullOrEmpty(InfoHash);
            bool hasV2 = !string.IsNullOrEmpty(V2InfoHash);
            if (!hasV1 && !hasV2) return "";

            var sb = new StringBuilder("magnet:");
            bool first = true;
            void AppendParam(string key, string value)
            {
                sb.Append(first ? '?' : '&');
                first = false;
                sb.Append(key);
                sb.Append('=');
                sb.Append(value);
            }

            if (hasV1) AppendParam("xt", $"urn:btih:{InfoHash}");
            if (hasV2) AppendParam("xt", $"urn:btmh:1220{V2InfoHash}");
            if (!string.IsNullOrEmpty(Name))
                AppendParam("dn", Uri.EscapeDataString(Name));
            foreach (var tr in AnnounceUrls)
                AppendParam("tr", Uri.EscapeDataString(tr));
            foreach (var ws in UrlList)
                AppendParam("ws", Uri.EscapeDataString(ws));
            return sb.ToString();
        }
    }

    // Speed tracking
    private long _lastDownloaded;
    private long _lastUploaded;
    private DateTime _lastSpeedSample = DateTime.UtcNow;

    /// <summary>Current download speed in bytes/sec (updated by speed sampling).</summary>
    public double DownloadSpeed { get; private set; }

    /// <summary>Current upload speed in bytes/sec (updated by speed sampling).</summary>
    public double UploadSpeed { get; private set; }

    /// <summary>Download speed history (for sparkline graphs). Each entry = bytes/sec at sample time.</summary>
    public List<double> DownloadSpeedHistory { get; } = new();

    /// <summary>Upload speed history (for sparkline graphs).</summary>
    public List<double> UploadSpeedHistory { get; } = new();

    /// <summary>Estimated time remaining in milliseconds. -1 if unknown.</summary>
    public double TimeRemaining => Done ? 0 : DownloadSpeed > 0 ? (Length - Downloaded) / DownloadSpeed * 1000 : -1;

    private const int MaxSpeedHistoryLength = 60;
    private Timer? _speedTimer;
    private Timer? _noPeersTimer;

    /// <summary>Sample current speed. Called periodically by speed timer.</summary>
    internal void SampleSpeed()
    {
        var now = DateTime.UtcNow;
        var elapsed = (now - _lastSpeedSample).TotalSeconds;
        if (elapsed < 0.1) return;

        var dl = Downloaded;
        var ul = UploadedTotal;

        DownloadSpeed = (dl - _lastDownloaded) / elapsed;
        UploadSpeed = (ul - _lastUploaded) / elapsed;

        _lastDownloaded = dl;
        _lastUploaded = ul;
        _lastSpeedSample = now;

        DownloadSpeedHistory.Add(DownloadSpeed);
        UploadSpeedHistory.Add(UploadSpeed);
        if (DownloadSpeedHistory.Count > MaxSpeedHistoryLength) DownloadSpeedHistory.RemoveAt(0);
        if (UploadSpeedHistory.Count > MaxSpeedHistoryLength) UploadSpeedHistory.RemoveAt(0);
    }

    /// <summary>Start speed sampling timer (1 second interval).</summary>
    internal void StartSpeedTimer()
    {
        _lastDownloaded = Downloaded;
        _lastUploaded = UploadedTotal;
        _lastSpeedSample = DateTime.UtcNow;
        _speedTimer?.Dispose();
        _speedTimer = new Timer(_ => SampleSpeed(), null, 1000, 1000);
    }

    /// <summary>Seconds between "no peers" event checks. Default 30.</summary>
    private int _noPeersIntervalTime = 30;

    /// <summary>Start noPeers timer. Fires OnNoPeers for each enabled source with zero peers.</summary>
    private void StartNoPeersTimer()
    {
        var intervalMs = _noPeersIntervalTime * 1000;
        _noPeersTimer?.Dispose();
        _noPeersTimer = new Timer(_ => CheckNoPeers(), null, intervalMs, intervalMs);
    }

    private void CheckNoPeers()
    {
        if (Destroyed || Done) return;
        if (_peers.Count == 0)
        {
            // Fire for each enabled source — matches JS behavior
            if (AnnounceUrls.Length > 0) OnNoPeers?.Invoke("tracker");
            if (_discovery != null) OnNoPeers?.Invoke("dht");
        }
    }

    // Discovery
    private Discovery? _discovery;

    // Store
    internal IChunkStore? _store;

    // Web seeds
    private readonly List<WebConn> _webConns = new();

    // Client reference
    private WebTorrentClient? _client;
    private HttpClient? _http;
    private bool _deselect;

    // Events
    public event Action<Wire, string>? OnWire;
    public event Action? OnMetadata;
    public event Action? OnReady;
    public event Action? OnInfoHash;
    public event Action<int>? OnDownload;  // bytes downloaded in this chunk
    public event Action<int>? OnUpload;    // bytes uploaded in this chunk
    public event Action? OnIdle;           // no active selections, now seeding
    public event Action<string>? OnNoPeers; // no peers found via tracker/dht/lsd/ut_pex

    // ========================
    // INITIALIZATION
    // ========================

    /// <summary>Initialize torrent from a magnet URI.</summary>
    public async Task InitFromMagnetAsync(string magnetUri, WebTorrentClient client, AddTorrentOptions opts)
    {
        _client = client;
        _http = client._http;
        PeerIdHex = client.PeerId;
        MagnetUri = magnetUri;
        Strategy = opts.Strategy;

        if (opts.Paused) Paused = true;
        if (opts.Deselect) _deselect = true;
        if (opts.MaxWebConns > 0) MaxWebConns = opts.MaxWebConns;
        if (opts.NoPeersIntervalTime > 0) _noPeersIntervalTime = opts.NoPeersIntervalTime;

        ParseMagnet(magnetUri);
        if (string.IsNullOrEmpty(InfoHash) && string.IsNullOrEmpty(V2InfoHash))
            throw new Exception("Malformed magnet: no info hash");
        // Pure-v2 magnet URIs (urn:btmh: only) are supported as of 3.1.3-rc.13:
        // the tracker announce + BT wire handshake use the first 20 bytes of the v2
        // SHA-256 hash (cross-client convention, see WireInfoHashHex). Metadata
        // retrieval for v2-only magnets still relies on peers running an extension
        // that can serve the v2 info dict — BEP 9 ut_metadata is v1-only; v2
        // metadata discovery is peer-to-peer via BEP 52 hash_request messages once
        // at least one peer already has the torrent file. If no such peer is
        // reachable the swarm will stall on metadata exchange, but the tracker
        // connection + WebRTC signaling layer now works for pure-v2 swarms.

        // Merge client's default trackers for maximum peer discovery
        if (_client?.DefaultTrackers?.Length > 0)
            AnnounceUrls = AnnounceUrls.Union(_client.DefaultTrackers).ToArray();

        OnInfoHash?.Invoke();

        // Peer-free metadata bootstrap: if the magnet carried an HTTP(S) exact-source (xs=)
        // URL to the full .torrent, fetch it directly instead of waiting for a peer to serve
        // ut_metadata. This is what makes a web-seed-only swarm work for the FIRST client —
        // e.g. the HuggingFace proxy / a CDN cache server hands out magnets with xs=/torrent/...
        // and there is no peer in the swarm yet. Runs concurrently with discovery; whichever
        // resolves metadata first wins (SetMetadata is idempotent via the HasMetadata guard).
        if (!HasMetadata && !string.IsNullOrEmpty(ExactSourceUrl))
            _ = FetchMetadataFromExactSourceAsync(ExactSourceUrl!);

        StartRechoke();
        StartDiscovery();
    }

    /// <summary>
    /// Fetch the full .torrent from the magnet's HTTP(S) exact-source (xs=) URL and resolve
    /// metadata from it — no peers required. The fetched info dict is verified against the
    /// magnet's info hash before it is trusted, so a hostile exact source cannot inject a
    /// mismatched torrent. Failures are non-fatal: discovery + ut_metadata remain the fallback.
    /// </summary>
    private async Task FetchMetadataFromExactSourceAsync(string url)
    {
        try
        {
            if (_http == null || Destroyed || HasMetadata) return;

            var torrentBytes = await _http.GetByteArrayAsync(url);
            if (Destroyed || HasMetadata) return;

            var metadata = TorrentParser.Parse(torrentBytes);

            // Verify the fetched .torrent actually matches the magnet's info hash. Compare on
            // whichever identifier the magnet carried (v1 InfoHash, else pure-v2 V2InfoHash).
            bool hashOk;
            if (!string.IsNullOrEmpty(InfoHash))
                hashOk = string.Equals(metadata.InfoHash, InfoHash, StringComparison.OrdinalIgnoreCase);
            else if (!string.IsNullOrEmpty(V2InfoHash))
                hashOk = string.Equals(metadata.V2InfoHash, V2InfoHash, StringComparison.OrdinalIgnoreCase);
            else
                hashOk = false;

            if (!hashOk)
            {
                OnWarning?.Invoke(
                    $"xs= exact-source .torrent info hash mismatch (magnet={WireInfoHashHex}, " +
                    $"fetched v1={metadata.InfoHash}, v2={metadata.V2InfoHash}); ignoring {url}");
                return;
            }

            if (Destroyed || HasMetadata) return;
            metadata.OriginalTorrentBytes = torrentBytes;
            SetMetadata(metadata);
        }
        catch (Exception ex)
        {
            // Non-fatal — fall back to ut_metadata via peers / web-seed discovery.
            OnWarning?.Invoke($"xs= exact-source metadata fetch failed for {url}: {ex.Message}");
        }
    }

    /// <summary>Initialize torrent from parsed metadata.</summary>
    public void InitFromMetadata(TorrentMetadata metadata, WebTorrentClient client, AddTorrentOptions opts)
    {
        _client = client;
        _http = client._http;
        PeerIdHex = client.PeerId;
        Strategy = opts.Strategy;
        if (opts.Paused) Paused = true;
        if (opts.Deselect) _deselect = true;
        if (opts.MaxWebConns > 0) MaxWebConns = opts.MaxWebConns;
        if (opts.NoPeersIntervalTime > 0) _noPeersIntervalTime = opts.NoPeersIntervalTime;

        SetMetadata(metadata);
        StartRechoke();
        StartDiscovery();
    }

    // ========================
    // MAGNET PARSING
    // ========================

    /// <summary>BEP 46 public key (from xs=urn:btpk: magnet param). Null if not a mutable torrent.</summary>
    public byte[]? BtpkPublicKey { get; set; }

    /// <summary>Whether this torrent was added via a BEP 46 btpk magnet URI.</summary>
    public bool IsMutableTorrent => BtpkPublicKey != null;

    /// <summary>
    /// HTTP(S) exact-source URL (the <c>xs=</c> magnet parameter) pointing at the full
    /// <c>.torrent</c> metainfo. When present, metadata can be bootstrapped with a single
    /// HTTP GET — no peers required. This is how a web-seed-only swarm (e.g. the HuggingFace
    /// proxy / a CDN cache server) lets the first client resolve metadata without anyone in
    /// the swarm to serve ut_metadata. Null if the magnet carries no HTTP exact-source.
    /// </summary>
    public string? ExactSourceUrl { get; private set; }

    /// <summary>Event fired when BEP 46 detects a new infohash for this mutable torrent.</summary>
    public event Action<string>? OnMutableUpdate; // new infohash hex

    /// <summary>Fire the mutable update event (called by WebTorrentClient when DHT detects new version).</summary>
    public void NotifyMutableUpdate(string newInfoHash) => OnMutableUpdate?.Invoke(newInfoHash);

    /// <summary>
    /// Parse a magnet URI and populate the torrent's info-hash / v2-info-hash / trackers /
    /// web-seeds / display-name fields. Exposed <c>internal</c> primarily for testing; normal
    /// callers go through <see cref="InitFromMagnetAsync"/>.
    /// </summary>
    internal void ParseMagnet(string magnetUri)
    {
        var queryStart = magnetUri.IndexOf('?');
        if (queryStart < 0) return;
        var query = System.Web.HttpUtility.ParseQueryString(magnetUri[(queryStart)..]);

        // A magnet URI can carry multiple xt values (BEP 52 hybrid magnets carry both
        // urn:btih: v1 and urn:btmh: v2 identifiers). ParseQueryString returns them
        // comma-separated; split and handle each.
        var xtValues = query["xt"];
        if (xtValues != null)
        {
            foreach (var xt in xtValues.Split(','))
            {
                if (xt.StartsWith("urn:btih:"))
                {
                    var hashPart = xt["urn:btih:".Length..];
                    if (hashPart.Length == 40)
                        InfoHash = hashPart.ToLowerInvariant();
                    else if (hashPart.Length == 32)
                        InfoHash = Convert.ToHexString(Base32Decode(hashPart)).ToLowerInvariant();
                }
                else if (xt.StartsWith("urn:btmh:"))
                {
                    // BEP 52 v2 multihash: <code-varint><length-varint><digest>. For SHA-256
                    // code = 0x12, length = 0x20 = 32 bytes, digest = 32 bytes. Encoded as hex
                    // the prefix is "1220" and the total string is 68 hex chars (4 prefix + 64
                    // digest). Other multihash codecs could appear in theory but v2 currently
                    // mandates SHA-256 so we only accept that.
                    var multihashHex = xt["urn:btmh:".Length..];
                    if (multihashHex.Length == 68 && multihashHex.StartsWith("1220", StringComparison.OrdinalIgnoreCase))
                    {
                        V2InfoHash = multihashHex[4..].ToLowerInvariant();
                    }
                }
            }
        }

        // A magnet may carry multiple xs= (exact source) params. Two flavors we handle:
        //   - xs=urn:btpk:{public_key_hex}  → BEP 46 mutable torrent via DHT
        //   - xs=http(s)://.../file.torrent → exact source: the full .torrent over HTTP,
        //     used to bootstrap metadata peer-free (see ExactSourceUrl / FetchMetadataFromExactSourceAsync).
        var xsValues = query.GetValues("xs");
        if (xsValues != null)
        {
            foreach (var xsVal in xsValues)
            {
                if (xsVal.StartsWith("urn:btpk:"))
                {
                    var pkHex = xsVal["urn:btpk:".Length..];
                    if (pkHex.Length == 64) // 32 bytes = 64 hex chars
                        BtpkPublicKey = Convert.FromHexString(pkHex);
                }
                else if (xsVal.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                      || xsVal.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                {
                    // First HTTP exact source wins; the proxy emits exactly one.
                    ExactSourceUrl ??= xsVal;
                }
            }
        }

        var trackers = query.GetValues("tr");
        if (trackers != null) AnnounceUrls = trackers;

        var webSeeds = query.GetValues("ws");
        if (webSeeds != null) UrlList = webSeeds;

        Name = query["dn"];

        // BEP 53: so= parameter for selecting specific file indices
        // Supports both individual indices and ranges: so=0,2,4 or so=0-4,6,8-10
        var so = query["so"];
        if (!string.IsNullOrEmpty(so))
        {
            var indices = new List<int>();
            foreach (var part in so.Split(','))
            {
                try
                {
                    var trimmed = part.Trim();
                    if (string.IsNullOrEmpty(trimmed)) continue;
                    var dashIdx = trimmed.IndexOf('-');
                    if (dashIdx > 0 && dashIdx < trimmed.Length - 1)
                    {
                        // Range: "0-4" means files 0,1,2,3,4
                        int start = int.Parse(trimmed[..dashIdx]);
                        int end = int.Parse(trimmed[(dashIdx + 1)..]);
                        for (int i = start; i <= end; i++)
                            indices.Add(i);
                    }
                    else
                    {
                        indices.Add(int.Parse(trimmed));
                    }
                }
                catch { /* malformed segment - skip it, keep valid ones */ }
            }
            if (indices.Count > 0)
                SelectedFileIndices = indices.Distinct().Order().ToArray();
        }
    }

    // ========================
    // METADATA
    // ========================

    /// <summary>Original .torrent file bytes for export/re-distribution.</summary>
    public byte[]? TorrentFileBytes { get; set; }

    /// <summary>Raw bencoded info dict — what this torrent serves to magnet peers over ut_metadata (BEP 9).</summary>
    public byte[]? InfoDictBytes { get; set; }

    /// <summary>
    /// Get the .torrent file as a JS Blob (browser) for zero-copy download links.
    /// Returns null if TorrentFileBytes is null or not in browser.
    /// The caller owns the Blob and must dispose it.
    /// </summary>
    public SpawnDev.BlazorJS.JSObjects.Blob? TorrentFileBlob
    {
        get
        {
            if (TorrentFileBytes == null || !OperatingSystem.IsBrowser()) return null;
            return new SpawnDev.BlazorJS.JSObjects.Blob(
                new[] { TorrentFileBytes },
                new SpawnDev.BlazorJS.JSObjects.BlobOptions { Type = "application/x-bittorrent" });
        }
    }

    /// <summary>Comment embedded in .torrent file.</summary>
    public string? Comment { get; set; }

    /// <summary>Creator identification string.</summary>
    public string? CreatedBy { get; set; }

    /// <summary>Torrent creation timestamp.</summary>
    public DateTimeOffset? CreationDate { get; set; }

    /// <summary>Set metadata from parsed torrent info.</summary>
    public void SetMetadata(TorrentMetadata metadata)
    {
        if (HasMetadata) return;

        InfoHash = metadata.InfoHash;
        V2InfoHash = metadata.V2InfoHash;
        MetaVersion = metadata.MetaVersion;
        OnInfoHash?.Invoke();
        Name = metadata.Name;
        PieceLength = metadata.PieceLength;
        Length = metadata.TotalLength;
        IsPrivate = metadata.IsPrivate;
        LastPieceLength = (int)(Length % PieceLength);
        if (LastPieceLength == 0) LastPieceLength = PieceLength;

        _hashes = metadata.PieceHashes;

        // BEP 52: retain per-file roots and piece-layer map so we can serve peer-wire
        // hash_request queries (seed path) and correlate any outbound hash_requests we issue
        // to fetch missing piece layers (magnet bootstrap path).
        FileRoots = metadata.FileRoots ?? Array.Empty<byte[]>();
        PieceLayers = metadata.PieceLayers ?? new Dictionary<byte[], byte[]>(ByteArrayEqualityComparer.Instance);

        // Pre-compute per-global-piece lengths. For hybrid + v1 torrents every piece is
        // PieceLength except the LAST (LastPieceLength). For pure-v2 multi-file torrents
        // each file's last piece may be partial - a piece is short whenever it straddles
        // the tail of a file in the padded virtual stream. VerifyPieceHash and the Pieces[]
        // initialization both consult this array.
        _pieceLengths = ComputePieceLengths(metadata);

        // Allocate the coordinator exactly once per v2 torrent. It is shared across all peer
        // wires (requests and responses can be attributed to any peer in the swarm - the
        // RequestKey is root+baseLayer+index+length, not peer-id).
        if (metadata.MetaVersion == 2)
            V2HashCoord ??= new V2HashRequestCoordinator();

        // Store optional metadata fields
        TorrentFileBytes = metadata.OriginalTorrentBytes;
        // Raw bencoded info dict — exactly what ut_metadata (BEP 9) serves to magnet peers.
        // Without retaining this, a seeded torrent advertises no metadata_size and a magnet
        // downloader can never pull the info dict from us (the two-SpawnDev-client stall).
        InfoDictBytes = metadata.InfoDictBytes;
        Comment = metadata.Comment;
        CreatedBy = metadata.CreatedBy;
        CreationDate = metadata.CreationDate;

        Pieces = new Piece[metadata.PieceCount];
        Bitfield = new bool[metadata.PieceCount];
        for (int i = 0; i < metadata.PieceCount; i++)
        {
            Pieces[i] = new Piece(_pieceLengths[i]);
        }

        Files = metadata.Files;
        // Set back-reference on files for per-file progress computation
        if (Files != null)
            foreach (var f in Files) f.Torrent = this;
        if (AnnounceUrls.Length == 0 && metadata.AnnounceUrls != null)
            AnnounceUrls = metadata.AnnounceUrls;
        if (UrlList.Length == 0 && metadata.UrlList != null)
            UrlList = metadata.UrlList;
        if (HttpSeeds.Length == 0 && metadata.HttpSeeds != null)
            HttpSeeds = metadata.HttpSeeds;

        // Initialize chunk store if not already set (e.g., by SeedAsync)
        if (_store == null)
        {
            // Use WireInfoHashHex so pure-v2 torrents get a stable non-empty OPFS dir
            // (first 20 bytes of v2 hash). v1 / hybrid torrents keep their v1 hash dir
            // unchanged, so existing persisted data restores without relocation.
            var storeHash = WireInfoHashHex;
            if (_client?.AsyncFileSystem != null && !string.IsNullOrEmpty(storeHash))
                _store = new Storage.AsyncFSChunkStore(_client.AsyncFileSystem, $"webtorrent/{storeHash}", PieceLength);
            else
                _store = new Storage.MemoryChunkStore(PieceLength);
        }

        _rarityMap = new RarityMap(this);

        if (_client?.EnableWebSeeds == true)
        {
            // BEP 19: GetRight-style web seeds (url-list)
            foreach (var url in UrlList) AddWebSeed(url);
            // BEP 17: Hoffman-style HTTP seeds (httpseeds)
            foreach (var url in HttpSeeds) AddHttpSeed(url);
        }

        // Select pieces for download - unless paused or deselect mode
        if (Pieces.Length > 0 && !Paused && !_deselect)
        {
            // BEP 53: If SelectedFileIndices is set, only select pieces for those files
            if (SelectedFileIndices != null && Files != null && SelectedFileIndices.Length > 0)
            {
                foreach (var fileIdx in SelectedFileIndices)
                {
                    if (fileIdx >= 0 && fileIdx < Files.Length)
                        _selections.Insert(new SelectionItem { From = Files[fileIdx].StartPiece, To = Files[fileIdx].EndPiece, Priority = 0 });
                }
            }
            else
            {
                _selections.Insert(new SelectionItem { From = 0, To = Pieces.Length - 1, Priority = 0 });
            }

            // Drive the request loop now that pieces are selected. The public Select() does
            // this (Insert + UpdateWires); this internal default-select must too. Real peers
            // self-start their request cycle from their bitfield/unchoke handshake, but a web
            // seed has no handshake — without this, a web-seed-only swarm (e.g. the HuggingFace
            // proxy with no peers) selects every piece yet never issues a single request.
            UpdateWires();
        }

        // BEP 27: Propagate IsPrivate to PEX extensions on wires that connected
        // before metadata arrived (magnet link flow: peers connect during ut_metadata fetch,
        // IsPrivate isn't known until info dict is parsed)
        if (IsPrivate)
        {
            foreach (var wire in Wires.ToArray())
            {
                var pex = wire.GetExtension<UtPexExtension>();
                if (pex != null) pex.IsPrivate = true;
            }
        }

        foreach (var wire in Wires.ToArray())
            OnWireWithMetadata(wire);

        StartSpeedTimer();
        StartNoPeersTimer();

        // Persist .torrent metadata for restore after page reload
        if (_client?.AsyncFileSystem != null && TorrentFileBytes != null && !string.IsNullOrEmpty(WireInfoHashHex))
            _ = PersistMetadataAsync();

        Ready = true;
        OnMetadata?.Invoke();
        OnReady?.Invoke();
    }

    private async Task PersistMetadataAsync()
    {
        var key = WireInfoHashHex;
        if (_client?.AsyncFileSystem == null || TorrentFileBytes == null || string.IsNullOrEmpty(key)) return;
        try
        {
            var fs = _client.AsyncFileSystem;
            var dir = "webtorrent/_state";
            if (!await fs.DirectoryExists(dir))
                await fs.CreateDirectory(dir);
            await fs.Write($"{dir}/{key}.torrent", TorrentFileBytes);
            await PersistStateAsync();
        }
        catch { /* Best-effort persistence */ }
    }

    /// <summary>Persist torrent state (paused, selected files) to companion JSON file.</summary>
    internal async Task PersistStateAsync()
    {
        var key = WireInfoHashHex;
        if (_client?.AsyncFileSystem == null || string.IsNullOrEmpty(key)) return;
        try
        {
            var fs = _client.AsyncFileSystem;
            var dir = "webtorrent/_state";
            if (!await fs.DirectoryExists(dir))
                await fs.CreateDirectory(dir);
            var state = new Dictionary<string, object>();
            if (Paused) state["paused"] = true;
            var json = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(state);
            await fs.Write($"{dir}/{key}.state.json", json);
        }
        catch { }
    }

    // ========================
    // DISCOVERY
    // ========================

    private void StartDiscovery()
    {
        // Discovery uses the 20-byte wire info hash — v1 hash if present, else first
        // 20 bytes of the v2 SHA-256 hash for pure-v2 torrents. WireInfoHashHex handles
        // the fallback.
        var wireHex = WireInfoHashHex;
        if (_discovery != null || Destroyed || _client == null || string.IsNullOrEmpty(wireHex)) return;

        var infoHashBytes = Convert.FromHexString(wireHex);

        // Tracker-based discovery (always allowed, even for private torrents per BEP 27)
        _discovery = new Discovery(
            infoHashBytes, _client.PeerIdBuffer, AnnounceUrls,
            (initiator) => { PeersAttempted++; return _client.CreatePeer(initiator); },
            _http!
        );

        _discovery.OnWebRtcPeer += AddPeer;
        _discovery.OnTcpPeer += ConnectTcpPeer;
        _discovery.OnWarning += (msg) => OnWarning?.Invoke(msg);
        _discovery.OnTrackerUpdate += (update) => TrackerStats[update.AnnounceUrl] = update;

        _ = _discovery.AnnounceAsync(new AnnounceOptions
        {
            Event = "started",
            Left = Math.Max(Length - Downloaded, 0),
            // Advertise our TCP listener port to mainline trackers so other
            // clients can dial us in. 0 if no advertising enabled / no listener
            // bound; keeps prior behavior on the legacy code path.
            Port = _client.AdvertisedTcpPort,
        });

        // BEP 27: Private torrents MUST NOT use DHT, PEX, or LSD.
        // PEX is handled per-wire in UtPexExtension.IsPrivate.
        // DHT and LSD guards go here when per-torrent DHT/LSD is wired up.
        if (IsPrivate) return;

        // Future: DHT get_peers / announce_peer for this torrent
        // Future: LSD multicast announce for this torrent
    }

    // ========================
    // PEER MANAGEMENT
    // ========================

    /// <summary>Connect to a TCP peer by address. Desktop only — browser has no TCP.</summary>
    private void ConnectTcpPeer(string address)
    {
        if (Destroyed || OperatingSystem.IsBrowser()) return;
        if (_peers.Count >= (_client?.MaxConns ?? 55)) return;

        _ = Task.Run(async () =>
        {
            try
            {
                var tcpPeer = new TcpPeer(initiator: true);
                await tcpPeer.ConnectAsync(address);
                if (!tcpPeer.Connected || Destroyed) { await tcpPeer.DisposeAsync(); return; }
                AddPeer(tcpPeer);
            }
            catch { /* Connection failed — silently discard */ }
        });
    }

    /// <summary>
    /// Register the ut_metadata (BEP 9) wire extension for THIS torrent on a wire. When the torrent
    /// already has metadata (a seed, or a magnet that finished bootstrapping) the extension is built
    /// with the raw info dict so its extended handshake advertises <c>metadata_size</c> and it serves
    /// piece requests. Otherwise it is a fetcher that pulls the info dict from the peer and feeds it
    /// to <see cref="SetMetadata"/>. This is registered PER-TORRENT (not via the client-global
    /// extension factory) so each wire advertises the correct torrent's metadata — the old global
    /// factory only ever created fetchers, so a SpawnDev.WebTorrent seed advertised no ut_metadata and
    /// a magnet peer could never bootstrap from it.
    /// </summary>
    private void SetupMetadataExtension(Wire wire)
    {
        bool isPureV2 = string.IsNullOrEmpty(InfoHash) && !string.IsNullOrEmpty(V2InfoHash);
        var ext = (HasMetadata && InfoDictBytes != null)
            ? new UtMetadataExtension(InfoDictBytes)   // serve our info dict to magnet peers
            : new UtMetadataExtension();               // fetch the info dict from this peer
        if (isPureV2)
        {
            ext.MetadataVersion = 2;
            ext.V2InfoHashHex = V2InfoHash;
        }
        ext.SetWire(wire);
        ext.OnMetadata += (infoDictBytes) =>
        {
            if (HasMetadata) return;
            TorrentMetadata? metadata = isPureV2
                ? TorrentParser.ParseInfoDictV2(infoDictBytes, Convert.FromHexString(V2InfoHash ?? ""))
                : TorrentParser.ParseInfoDict(infoDictBytes, Convert.FromHexString(InfoHash ?? ""));
            if (metadata != null) SetMetadata(metadata);
        };
        ext.OnWarning += (msg) => OnWarning?.Invoke(msg);
        wire.Use(ext);
    }

    public void AddPeer(SimplePeer simplePeer)
    {
        if (Destroyed) return;
        if (_peers.Count >= (_client?.MaxConns ?? 55)) return;

        PeersConnected++;
        var peer = Peer.CreateWebRTCPeer(simplePeer);
        peer.Swarm = this;
        if (!_peers.TryAdd(peer.Id, peer)) return;

        // Capture all of the post-connect wire-up in a local delegate so we can
        // either subscribe it to the OnConnect event OR invoke it inline if the
        // peer already finished connecting before AddPeer was called. Critical
        // for TCP peers where ConnectAsync fires EmitConnect synchronously -
        // the caller's `AddPeer(tcpPeer)` after a completed `ConnectAsync` would
        // otherwise miss the already-fired OnConnect and the BitTorrent
        // handshake would never start. Same story for any other SimplePeer
        // variant that transitions to Connected synchronously before AddPeer.
        Action runOnConnected = () =>
        {
            peer.OnConnected();
            if (peer.WireInstance != null)
            {
                _client?.ApplyExtensions(peer.WireInstance);

                // Register ut_metadata (BEP 9) for THIS torrent: SERVE our info dict if we have
                // metadata (seed / post-bootstrap), else FETCH it from this peer (magnet bootstrap).
                // Per-torrent so the wire advertises this torrent's real metadata_size.
                SetupMetadataExtension(peer.WireInstance);

                // Set remote address from transport for peer display
                peer.WireInstance.RemoteAddress = simplePeer.RemoteAddress;

                // BEP 27: Propagate private flag to PEX extension
                if (IsPrivate)
                {
                    var pex = peer.WireInstance.GetExtension<UtPexExtension>();
                    if (pex != null) pex.IsPrivate = true;
                }

                Wires.Add(peer.WireInstance);

                // Bubble wire download/upload events → peer → torrent → client
                peer.WireInstance.OnDownload += (bytes) =>
                {
                    peer.EmitDownload(bytes);
                    OnDownload?.Invoke(bytes);
                    _client?.EmitDownload(bytes);
                };
                peer.WireInstance.OnUpload += (bytes) =>
                {
                    peer.EmitUpload(bytes);
                    OnUpload?.Invoke(bytes);
                    _client?.EmitUpload(bytes);
                };

                // Validate handshake: detect self-connections and duplicates.
                //
                // Duplicate-peer convergence rule: when two clients form multiple
                // simultaneous WebRTC connections (tracker fans out ~N offers per announce),
                // both sides can complete BT handshakes on N wires in arbitrary order
                // relative to each other. The per-side ordering of "existing" vs "newcomer"
                // is timing-dependent and NOT cross-side stable — if each side picks based
                // on its own local ordering, coord keeps wire-A while worker keeps wire-B,
                // each side's remote close kills the other's survivor, both swarms → 0.
                //
                // Fix (rc.12, Geordi's diagnosis): compare an identifier that's IDENTICAL
                // on both endpoints of the same WebRTC connection — the data channel's
                // Label. The initiator creates the channel with a random label; the
                // responder receives that same label via OnDataChannel. Both sides ranking
                // wires by channel Label pick the SAME physical connection to keep.
                //
                // Rule: when duplicate detected, keep the wire whose underlying channel
                // label is lexicographically SMALLER. Both sides converge.
                peer.WireInstance.OnHandshake += (infoHash, peerId, exts) =>
                {
                    // Self-connection detection
                    if (peerId == _client?.PeerId)
                    {
                        if (WebTorrentClient.VerboseLogging)
                            Console.WriteLine($"[Torrent.OnHandshake] SELF-CONNECTION detected: remote peerId={peerId} == client.PeerId → Destroy peer {peer.Id}");
                        peer.Destroy(); return;
                    }
                    // Duplicate peer detection (same remote peerId already connected).
                    //
                    // Phantom-wire filter: rc.19 per Geordi's DUP-DIAG finding on rc.15.
                    // `Peer.Destroy` triggers `Wire.Destroy` → wire.OnClose → the
                    // `Wires.Remove` subscription, BUT in certain destroy-race paths the
                    // wire stays in `Wires` while `_peers` has already been cleaned.
                    // Examples: the `simplePeer.OnClose` handler at line 914 fires
                    // independently and only removes from `_peers`, leaving the wire in
                    // `Wires` with `PeerId` still set. Matching these phantoms makes the
                    // tiebreaker see empty `existingLabel` (no backing Peer → no Conn →
                    // no ChannelName), and every real wire gets destroyed against the
                    // phantom's empty string. Cause of the two-popup peerCount=0 bug.
                    //
                    // Belt-and-suspenders filter:
                    //   !w.Destroyed — skip wires that already tore down cleanly but
                    //     stayed in the collection.
                    //   _peers.Values.Any(p => p.WireInstance == w) — require a live
                    //     Peer backing this wire; otherwise it's an orphan from a
                    //     destroy-race and matching it is always wrong.
                    var existingWire = Wires.ToArray().FirstOrDefault(w =>
                        w != peer.WireInstance
                        && w.PeerId == peerId
                        && !w.Destroyed
                        && _peers.Values.Any(p => p.WireInstance == w));
                    if (existingWire != null)
                    {
                        // Cross-side-stable tiebreaker: channel Label.
                        var existingPeer = _peers.Values.FirstOrDefault(p => p.WireInstance == existingWire);
                        string newLabel = (peer.Conn as SimplePeer)?.ChannelName ?? peer.Id;
                        string existingLabel = (existingPeer?.Conn as SimplePeer)?.ChannelName ?? existingPeer?.Id ?? "";
                        if (WebTorrentClient.VerboseLogging)
                        {
                            // Dump full state so we can diagnose the phantom-existing-wire
                            // scenario Geordi hit (rc.12 two-popup test: existing wire has
                            // PeerId set but its SimplePeer.ChannelName is empty, suggesting
                            // the wire is in Wires but its Peer isn't in _peers anymore).
                            var wiresDump = string.Join(" | ", Wires.ToArray().Select(w =>
                            {
                                var wp = _peers.Values.FirstOrDefault(p => p.WireInstance == w);
                                var wlab = (wp?.Conn as SimplePeer)?.ChannelName ?? "<noConn>";
                                return $"PeerId={w.PeerId ?? "<null>"}|peerInMap={(wp != null)}|label={wlab}";
                            }));
                            Console.WriteLine(
                                $"[Torrent.OnHandshake] DUP-DIAG: incomingPeerId={peerId} newPeer.Id={peer.Id} newLabel='{newLabel}' " +
                                $"existingPeer.Id={existingPeer?.Id ?? "<null>"} existingPeer.Conn={(existingPeer?.Conn?.GetType().Name ?? "<null>")} " +
                                $"existingLabel='{existingLabel}' Wires.Count={Wires.Count} _peers.Count={_peers.Count} " +
                                $"Wires=[{wiresDump}]");
                        }

                        // ALWAYS keep both wires alive when a duplicate handshake is observed.
                        //
                        // Rationale (2026-05-03, after the RenderMandelbrot bug repro on Chrome
                        // Stable + Canary): proactively calling Destroy() on the "loser" peer
                        // closes its underlying RTCPeerConnection, which empirically causes
                        // Chromium to emit `sctp-failure | User-Initiated Abort | sctpCauseCode=12`
                        // on the SURVIVOR's data channel. Both sides observe the cascade — the
                        // worker destroys its loser, the coord destroys its loser, and BOTH lose
                        // their winner immediately after. The two PCs are spec'd-independent
                        // (separate ufrag, DTLS fingerprint, UDP port) so this shouldn't happen,
                        // but it does, on every Chromium version we've tested. Filing-upstream
                        // is its own task; the fix here is to stop triggering the cascade.
                        //
                        // The bridge layer (`SpawnDev.ILGPU.P2P.P2PWebRtcBridge`) already dedupes
                        // by canonical BT peerId — `_wiresByBtPeerId[canonical]` holds the set of
                        // wires for a logical peer, and `UnregisterPeer` only fires when the LAST
                        // wire's `wire.OnClose` fires AND the bridge filter (Destroyed ||
                        // IsTransportDead) is empty. With both wires alive, the bridge sees one
                        // logical peer with N wires; consumer-level state stays correct. When the
                        // remote eventually disconnects, both wires close naturally, bridge cleans
                        // up. Cost: a small amount of duplicate keepalive traffic over the lifetime
                        // of the connection (one canonical peer with 2 PCs). Net traffic impact
                        // is negligible compared to actual dispatch payloads.
                        //
                        // Self-connection check above (line 894) is unaffected — that's a real
                        // self-loop hazard, not a duplicate-of-the-same-remote.
                        if (WebTorrentClient.VerboseLogging)
                            Console.WriteLine(
                                $"[Torrent.OnHandshake] DUP-OBSERVED: incomingPeerId={peerId[..Math.Min(16, peerId.Length)]}... " +
                                $"newPeer.Id={peer.Id} newLabel='{newLabel?[..Math.Min(8, newLabel?.Length ?? 0)]}...' " +
                                $"existingPeer.Id={existingPeer?.Id} existingLabel='{existingLabel?[..Math.Min(8, existingLabel?.Length ?? 0)]}...'. " +
                                $"BOTH wires kept alive; bridge layer will dedupe by canonical peerId. " +
                                $"Wires.Count={Wires.Count} _peers.Count={_peers.Count}.");
                        return;
                    }
                    if (WebTorrentClient.VerboseLogging)
                        Console.WriteLine($"[Torrent.OnHandshake] OK: peer {peer.Id} remote={peerId[..Math.Min(16, peerId.Length)]}... accepted (Wires count={Wires.Count})");
                };

                OnWire?.Invoke(peer.WireInstance, peer.Id);
                if (HasMetadata) OnWireWithMetadata(peer.WireInstance);

                peer.WireInstance.OnClose += () =>
                {
                    Wires.Remove(peer.WireInstance);
                    _peers.TryRemove(peer.Id, out _);
                };
            }
        };

        // Subscribe for the normal case (peer is still connecting), and if the
        // peer already transitioned to Connected before we got here, run the
        // wire-up inline so we don't miss the already-fired OnConnect.
        simplePeer.OnConnect += runOnConnected;
        if (simplePeer.Connected)
        {
            // Safe to run now: everything above just wires up event handlers +
            // creates the Wire; none of it depends on being inside the OnConnect
            // handler's callback stack.
            runOnConnected();
        }

        simplePeer.OnError += (err) => { peer.Destroy(err); _peers.TryRemove(peer.Id, out _); };
        simplePeer.OnClose += () => _peers.TryRemove(peer.Id, out _);
        peer.StartConnectTimeout();
    }

    /// <summary>Remove a peer from the swarm by peer ID.</summary>
    public void RemovePeer(string peerId)
    {
        if (_peers.TryRemove(peerId, out var peer))
            peer.Destroy();
    }

    /// <summary>Remove a peer by Wire reference.</summary>
    public void RemovePeer(Wire wire)
    {
        wire.Destroy();
        Wires.Remove(wire);
    }

    /// <summary>Re-verify all pieces against the chunk store and update the bitfield.</summary>
    public async Task RescanFilesAsync(CancellationToken ct = default)
    {
        if (_store == null || !HasMetadata) return;

        for (int i = 0; i < PieceCount; i++)
        {
            ct.ThrowIfCancellationRequested();
            var pieceData = await _store.GetAsync(i, ct: ct);
            if (pieceData == null) { Bitfield[i] = false; continue; }

            // Verify hash
            var hash = _hashes.Length > 0 && _hashes[0].Length == 32
                ? System.Security.Cryptography.SHA256.HashData(pieceData)
                : System.Security.Cryptography.SHA1.HashData(pieceData);
            Bitfield[i] = i < _hashes.Length && hash.SequenceEqual(_hashes[i]);
        }

        // BEP 53: When partial selection is active, "done" means all selected pieces verified
        if (SelectedFileIndices != null && Files != null && SelectedFileIndices.Length > 0)
        {
            Done = true;
            foreach (var fileIdx in SelectedFileIndices)
            {
                if (fileIdx < 0 || fileIdx >= Files.Length) continue;
                for (int p = Files[fileIdx].StartPiece; p <= Files[fileIdx].EndPiece && p < Bitfield.Length; p++)
                {
                    if (!Bitfield[p]) { Done = false; break; }
                }
                if (!Done) break;
            }
        }
        else
        {
            Done = Bitfield.All(b => b);
        }
    }

    public void AddWebSeed(string url)
    {
        if (Destroyed || !HasMetadata || _http == null) return;
        if (_webConns.Any(wc => wc.Url == url)) return;

        var webConn = new WebConn(url, this, _http);
        var wire = webConn.WireInstance;
        wire.RemoteAddress = url;

        // Web seed SendRaw: intercept outbound request messages (id=6) and route them
        // directly to the WebConn's HTTP handler, bypassing wire protocol parsing entirely.
        wire.SendRaw = async (data) =>
        {
            try
            {
                // Request message: 4-byte length + 1-byte id(6) + 4-byte index + 4-byte offset + 4-byte length = 17 bytes
                if (data.Length >= 17 && data[4] == 6)
                {
                    int pieceIndex = (data[5] << 24) | (data[6] << 16) | (data[7] << 8) | data[8];
                    int offset = (data[9] << 24) | (data[10] << 16) | (data[11] << 8) | data[12];
                    int length = (data[13] << 24) | (data[14] << 16) | (data[15] << 8) | data[16];

                    if (WebTorrentClient.VerboseLogging)
                        Console.WriteLine($"[WebSeed SendRaw] {url} request piece={pieceIndex} offset={offset} length={length} (Requests.Count={wire.Requests.Count})");

                    // Fire-and-forget HTTP request - don't block the send pipeline
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            var pendingReq = wire.Requests.ToArray().FirstOrDefault(r => r.Piece == pieceIndex && r.Offset == offset);
                            if (pendingReq == null)
                            {
                                if (WebTorrentClient.VerboseLogging)
                                    Console.WriteLine($"[WebSeed SendRaw] piece={pieceIndex} offset={offset}: NO pendingReq in wire.Requests (dropped)");
                                return;
                            }

                            await webConn.HandleRequestAsync(pieceIndex, offset, length, (err, buf) =>
                            {
                                try
                                {
                                    if (WebTorrentClient.VerboseLogging)
                                        Console.WriteLine($"[WebSeed respond] piece={pieceIndex} err={(err?.GetType().Name ?? "null")} bytes={(buf?.Length ?? -1)}");
                                    if (buf != null)
                                    {
                                        Interlocked.Add(ref wire._downloadedSinceLastCheck, buf.Length);
                                        wire.Downloaded += buf.Length;
                                    }
                                    wire.Requests.Remove(pendingReq);
                                    pendingReq.Callback(err, buf);
                                }
                                catch (Exception cbEx)
                                {
                                    if (WebTorrentClient.VerboseLogging)
                                        Console.WriteLine($"[WebSeed respond] callback threw: {cbEx}");
                                }
                            });
                        }
                        catch (Exception hrEx)
                        {
                            if (WebTorrentClient.VerboseLogging)
                                Console.WriteLine($"[WebSeed SendRaw] HandleRequest threw: {hrEx}");
                        }
                    });
                }
            }
            catch { }
        };

        _webConns.Add(webConn);
        Wires.Add(wire);

        // Set web seed wire state directly - bypass wire protocol handshake
        wire.PeerHasAll = true;
        wire.PeerChoking = false;
        wire.AmInterested = true;

        OnWire?.Invoke(wire, url);
        if (HasMetadata) OnWireWithMetadata(wire);
    }

    /// <summary>Add a BEP 17 Hoffman-style HTTP seed. Uses ?info_hash=X&piece=N query format.</summary>
    public void AddHttpSeed(string seedUrl)
    {
        if (Destroyed || !HasMetadata || _http == null || string.IsNullOrEmpty(InfoHash)) return;
        if (_webConns.Any(wc => wc.Url == seedUrl)) return;

        var webConn = new WebConn(seedUrl, this, _http);
        var wire = webConn.WireInstance;
        wire.RemoteAddress = seedUrl;

        // BEP 17: intercept request messages and fetch via ?info_hash=X&piece=N&ranges=offset-length
        var infoHashBytes = Convert.FromHexString(InfoHash);
        wire.SendRaw = async (data) =>
        {
            try
            {
                if (data.Length >= 17 && data[4] == 6)
                {
                    int pieceIndex = (data[5] << 24) | (data[6] << 16) | (data[7] << 8) | data[8];
                    int offset = (data[9] << 24) | (data[10] << 16) | (data[11] << 8) | data[12];
                    int length = (data[13] << 24) | (data[14] << 16) | (data[15] << 8) | data[16];

                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            var pendingReq = wire.Requests.ToArray().FirstOrDefault(r => r.Piece == pieceIndex && r.Offset == offset);
                            if (pendingReq == null) return;

                            // BEP 17 URL: seedUrl?info_hash=XXXX&piece=N&ranges=offset-length
                            var encodedHash = Uri.EscapeDataString(System.Text.Encoding.Latin1.GetString(infoHashBytes));
                            var requestUrl = $"{seedUrl}?info_hash={encodedHash}&piece={pieceIndex}";
                            if (offset > 0 || length < PieceLength)
                                requestUrl += $"&ranges={offset}-{offset + length - 1}";

                            using var cts = new CancellationTokenSource(60_000);
                            var pieceData = await _http!.GetByteArrayAsync(requestUrl, cts.Token);

                            // Extract the requested range from the response
                            byte[] block;
                            if (offset > 0 || pieceData.Length > length)
                            {
                                block = new byte[Math.Min(length, pieceData.Length - offset)];
                                Array.Copy(pieceData, offset, block, 0, block.Length);
                            }
                            else
                            {
                                block = pieceData;
                            }

                            wire.Downloaded += block.Length;
                            Interlocked.Add(ref wire._downloadedSinceLastCheck, block.Length);
                            wire.Requests.Remove(pendingReq);
                            pendingReq.Callback(null, block);
                        }
                        catch (Exception ex)
                        {
                            var pendingReq = wire.Requests.ToArray().FirstOrDefault(r => r.Piece == pieceIndex && r.Offset == offset);
                            if (pendingReq != null)
                            {
                                wire.Requests.Remove(pendingReq);
                                pendingReq.Callback(ex, null);
                            }
                        }
                    });
                }
            }
            catch { }
        };

        _webConns.Add(webConn);
        Wires.Add(wire);

        wire.PeerHasAll = true;
        wire.PeerChoking = false;
        wire.AmInterested = true;

        OnWire?.Invoke(wire, seedUrl);
        if (HasMetadata) OnWireWithMetadata(wire);
    }

    // ========================
    // PIECE SELECTION
    // ========================

    public void Select(int start, int end, int priority = 0, bool isStreamSelection = false)
    {
        // isStreamSelection=true keeps this range a SEPARATE entry (not concatenated into the whole-file
        // selection), so its priority is preserved instead of smeared across every piece — required for a
        // streaming media player's small high-priority tail (moov) request to actually outrank the front.
        _selections.Insert(new SelectionItem { From = start, To = end, Priority = priority, IsStreamSelection = isStreamSelection });
        UpdateWires();
    }

    public void Deselect(int start, int end, bool isStreamSelection = false)
    {
        // Selections.Remove only matches entries whose IsStreamSelection flag equals the item's, so a
        // streaming priority range (added with isStreamSelection: true) is ONLY removed when this is passed
        // true. Default false preserves existing whole-file/regular Deselect callers. Re-run the picker so an
        // abandoned stream's pieces stop being prioritized.
        _selections.Remove(new SelectionItem { From = start, To = end, IsStreamSelection = isStreamSelection });
        UpdateWires();
    }

    public void Critical(int start, int end)
    {
        for (int i = start; i <= end; i++) _critical.TryAdd(i, true);
        // Kick the request loop immediately so read-awaited pieces are fetched now.
        // Real peers self-trigger requests from piece-completion / unchoke events, but a
        // read can mark a piece critical when nothing else is driving UpdateWires (a cold
        // streaming read over a web seed, which has no handshake) - without this the
        // critical piece waits for the next unrelated trigger. (Select() already does this.)
        UpdateWires();
    }

    /// <summary>Diagnostic snapshot of the live piece-selection + critical state (for streaming triage).</summary>
    public string DebugSelectionState()
    {
        var sels = new List<string>();
        for (int i = 0; i < _selections.Length; i++)
        {
            var s = _selections.Get(i);
            if (s != null)
            {
                int h = 0;
                for (int p = s.From; p <= s.To && p < Bitfield.Length; p++) if (Bitfield[p]) h++;
                sels.Add($"[{s.From}-{s.To}:p{s.Priority}:have{h}/{s.To - s.From + 1}]");
            }
        }
        var crit = new List<int>(_critical.Keys);
        crit.Sort();
        int have = 0; for (int i = 0; i < Bitfield.Length; i++) if (Bitfield[i]) have++;
        var last8 = new System.Text.StringBuilder();
        for (int p = Math.Max(0, Bitfield.Length - 8); p < Bitfield.Length; p++) last8.Append(Bitfield[p] ? '1' : '0');
        // Targeted last-piece triage: a partial last piece (e.g. a non-faststart moov ending in it) not
        // downloading even with a healthy swarm -> is it received at all (missing<len), do wires advertise it,
        // and is a web seed (which always has every piece) present to fall back on?
        int lpi = PieceCount - 1;
        var lp = (lpi >= 0 && lpi < Pieces.Length) ? Pieces[lpi] : null;
        var wiresArr = Wires.ToArray();
        int wiresWithLast = 0;
        foreach (var w in wiresArr) { try { if (w.PeerHasPiece(lpi)) wiresWithLast++; } catch { } }
        var trackerHosts = new List<string>();
        foreach (var k in TrackerStats.Keys) { try { trackerHosts.Add(new Uri(k).Host); } catch { trackerHosts.Add(k); } }
        var lastInfo = $" lastPiece[{lpi}]:len={(lp != null ? lp.Length : -1)},missing={(lp != null ? lp.Missing : -1)},wiresHave={wiresWithLast}/{wiresArr.Length},webSeeds={WebSeedCount} note='{LastCompletionNote}'" +
            $" peers[att={PeersAttempted},conn={PeersConnected}] trackers={TrackerStats.Count}[{string.Join(",", trackerHosts)}] peerDrops=[{string.Join(" | ", Peer.RecentDrops)}]" +
            $" zc={ZeroCopyPiecesVerified}";
        return $"pieces={Bitfield.Length} have={have} last8={last8} sel={{{string.Join(" ", sels)}}} crit={{{string.Join(",", crit)}}}{lastInfo}";
    }

    // ========================
    // FILE READ (for streaming and download)
    // ========================

    // Ensure a range read's pieces will actually download: select the file's piece range (unless in
    // deselect / inspect mode, where per-piece Critical() drives on-demand fetching) and resume if paused.
    // Shared by the byte[] and Uint8Array readers so both prioritize identically. In deselect mode do NOT
    // select the whole file — Critical() (marked per-piece) + critical-first picking fetch only the pieces a
    // read touches, so structure inspection of a multi-GB checkpoint never pulls weights.
    private void EnsureReadSelection(TorrentFileInfo file)
    {
        if (!_deselect && (Paused || !_selections.Any()))
            Select(file.StartPiece, file.EndPiece, 1);
        if (Paused) Resume();
    }

    // Mark a piece critical (jump the picker queue) and poll until it arrives. Shared on-demand
    // prioritization for range reads — without it a read would block on pieces nobody requested.
    private async Task EnsurePieceAsync(int pieceIdx, CancellationToken ct)
    {
        if (pieceIdx < Bitfield.Length && !Bitfield[pieceIdx])
        {
            Critical(pieceIdx, pieceIdx);
            try
            {
                while (!Bitfield[pieceIdx] && !Destroyed)
                {
                    ct.ThrowIfCancellationRequested();
                    await Task.Delay(100, ct);
                }
            }
            finally
            {
                // Always un-mark Critical, even when the read is CANCELLED (a media element seeking away cancels
                // its prior read). Without the finally, ThrowIfCancellationRequested throws before this line and
                // the piece leaks into _critical forever - the critical-first pass then burns every pass on stale
                // pieces and the sort grows unbounded.
                _critical.TryRemove(pieceIdx, out _);
            }
            if (Destroyed) throw new OperationCanceledException("Torrent destroyed while waiting for piece");
        }
    }

    /// <summary>
    /// Read bytes from a file within the torrent. Assembles data across piece boundaries.
    /// If pieces are not yet downloaded, waits for them (supports streaming while downloading).
    /// Marks needed pieces as critical to prioritize their download.
    /// </summary>
    /// <param name="fileIndex">Index into the Files array.</param>
    /// <param name="offset">Byte offset within the file.</param>
    /// <param name="length">Number of bytes to read.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The requested bytes.</returns>
    public async Task<byte[]> ReadFileAsync(int fileIndex, long offset, int length, CancellationToken ct = default)
    {
        if (Files == null || fileIndex < 0 || fileIndex >= Files.Length)
            throw new ArgumentOutOfRangeException(nameof(fileIndex));
        if (_store == null) throw new InvalidOperationException("No chunk store available");

        var file = Files[fileIndex];
        if (offset + length > file.Length)
            length = (int)(file.Length - offset);
        if (length <= 0) return Array.Empty<byte>();

        // Convert file offset to absolute torrent offset
        long absOffset = file.Offset + offset;
        var result = new byte[length];
        int resultPos = 0;

        EnsureReadSelection(file);

        // Mark the ENTIRE read range critical UP FRONT so its pieces download in PARALLEL. Without this the
        // loop below awaits EnsurePieceAsync one piece at a time (serial), so a multi-piece read - e.g. the
        // tail moov of a non-faststart mp4 spanning several pieces - fetches piece-by-piece and loses the race
        // to a concurrent read's critical pieces (the element's autoplay front read), stalling loadedmetadata
        // on the moov's last piece. Critical() is idempotent + EnsurePieceAsync removes each as it is consumed.
        {
            int firstReadPiece = (int)(absOffset / PieceLength);
            int lastReadPiece = (int)((absOffset + (long)length - 1) / PieceLength);
            if (lastReadPiece > firstReadPiece) Critical(firstReadPiece, lastReadPiece);
        }

        while (resultPos < length)
        {
            ct.ThrowIfCancellationRequested();

            int pieceIdx = (int)(absOffset / PieceLength);
            int pieceOffset = (int)(absOffset % PieceLength);
            int pieceSize = (pieceIdx == PieceCount - 1) ? LastPieceLength : PieceLength;
            int available = pieceSize - pieceOffset;
            int toRead = Math.Min(available, length - resultPos);

            await EnsurePieceAsync(pieceIdx, ct);

            if (pieceIdx < Bitfield.Length && Bitfield[pieceIdx])
            {
                var data = await _store.GetAsync(pieceIdx, pieceOffset, toRead);
                if (data != null)
                {
                    Array.Copy(data, 0, result, resultPos, data.Length);
                    resultPos += data.Length;
                    absOffset += data.Length;
                    continue;
                }
            }

            // Piece verified but data missing from store (shouldn't happen)
            throw new InvalidOperationException($"Piece {pieceIdx} marked as verified but data not in store");
        }

        return result;
    }

    /// <summary>
    /// Read a byte range from a file as a JS <c>Uint8Array</c>, staying in JS memory (no .NET
    /// <c>byte[]</c> hop) when the chunk store is OPFS-backed. Selects + prioritizes (Critical) the needed
    /// pieces exactly like <see cref="ReadFileAsync(int,long,int,CancellationToken)"/>, so the read is
    /// fulfilled on demand. Intended for zero-copy GPU upload (<c>writeBuffer</c>) — the bytes never leave JS.
    /// </summary>
    public async Task<SpawnDev.BlazorJS.JSObjects.Uint8Array> ReadFileUint8ArrayAsync(int fileIndex, long offset, int length, CancellationToken ct = default)
    {
        // Returns a JS Uint8Array (for zero-copy browser GPU upload), so it needs a JS runtime.
        // Desktop has none — use ReadFileAsync (byte[]) there instead. Fail fast with a clear message
        // rather than a cryptic JS-object-creation error.
        if (!OperatingSystem.IsBrowser())
            throw new PlatformNotSupportedException(
                "ReadFileUint8ArrayAsync returns a JS Uint8Array and requires a browser runtime; use ReadFileAsync on desktop.");
        if (Files == null || fileIndex < 0 || fileIndex >= Files.Length)
            throw new ArgumentOutOfRangeException(nameof(fileIndex));
        if (_store == null) throw new InvalidOperationException("No chunk store available");

        var file = Files[fileIndex];
        if (offset + length > file.Length)
            length = (int)(file.Length - offset);
        if (length <= 0) return new SpawnDev.BlazorJS.JSObjects.Uint8Array(0);

        long absOffset = file.Offset + offset;
        var result = new SpawnDev.BlazorJS.JSObjects.Uint8Array(length);            // JS-side result buffer
        int resultPos = 0;
        // Zero-copy JS path only when the store is OPFS-backed and can hand back Uint8Arrays.
        var opfs = _store as Storage.AsyncFSChunkStore;
        bool jsPath = opfs != null && opfs.SupportsUint8Array;

        EnsureReadSelection(file);

        // Mark the ENTIRE read range critical UP FRONT so its pieces download in PARALLEL. Without this the
        // loop below awaits EnsurePieceAsync one piece at a time (serial), so a multi-piece read - e.g. the
        // tail moov of a non-faststart mp4 spanning several pieces - fetches piece-by-piece and loses the race
        // to a concurrent read's critical pieces (the element's autoplay front read), stalling loadedmetadata
        // on the moov's last piece. Critical() is idempotent + EnsurePieceAsync removes each as it is consumed.
        {
            int firstReadPiece = (int)(absOffset / PieceLength);
            int lastReadPiece = (int)((absOffset + (long)length - 1) / PieceLength);
            if (lastReadPiece > firstReadPiece) Critical(firstReadPiece, lastReadPiece);
        }

        while (resultPos < length)
        {
            ct.ThrowIfCancellationRequested();

            int pieceIdx = (int)(absOffset / PieceLength);
            int pieceOffset = (int)(absOffset % PieceLength);
            int pieceSize = (pieceIdx == PieceCount - 1) ? LastPieceLength : PieceLength;
            int available = pieceSize - pieceOffset;
            int toRead = Math.Min(available, length - resultPos);

            await EnsurePieceAsync(pieceIdx, ct);

            if (pieceIdx < Bitfield.Length && Bitfield[pieceIdx])
            {
                if (jsPath)
                {
                    // Read ONLY the needed sub-range directly from the OPFS file (File.slice → ArrayBuffer),
                    // never the whole piece — memory-bounded, and the bytes stay JS-side (zero-copy).
                    using var slice = await opfs!.GetUint8ArrayAsync(pieceIdx, pieceOffset, toRead, ct);
                    if (slice != null)
                    {
                        int got = (int)slice.Length;
                        result.Set(slice, resultPos);
                        resultPos += got;
                        absOffset += got;
                        continue;
                    }
                }
                else
                {
                    // Desktop / non-OPFS fallback: read .NET bytes and copy them into the JS buffer.
                    // (No zero-copy win here, but desktop isn't feeding a browser GPU anyway.)
                    var data = await _store.GetAsync(pieceIdx, pieceOffset, toRead);
                    if (data != null)
                    {
                        result.Set(data, resultPos);
                        resultPos += data.Length;
                        absOffset += data.Length;
                        continue;
                    }
                }
            }

            result.Dispose();
            throw new InvalidOperationException($"Piece {pieceIdx} marked as verified but data not in store");
        }

        return result;
    }

    /// <summary>
    /// Read the entire file data.
    /// </summary>
    public async Task<byte[]> ReadFileAsync(int fileIndex, CancellationToken ct = default)
    {
        if (Files == null || fileIndex < 0 || fileIndex >= Files.Length)
            throw new ArgumentOutOfRangeException(nameof(fileIndex));
        return await ReadFileAsync(fileIndex, 0, (int)Files[fileIndex].Length, ct);
    }

    // ========================
    // PAUSE / RESUME
    // ========================

    public void Pause()
    {
        Paused = true;
        // Choke all wires so peers stop sending blocks, cancel outstanding requests
        foreach (var wire in Wires.ToArray())
        {
            wire.Choke();
            foreach (var req in wire.Requests.ToArray())
                wire.Cancel(req.Piece, req.Offset, req.Length);
        }
        _ = PersistStateAsync();
    }

    public void Resume()
    {
        Paused = false;
        UpdateWires();
        _ = PersistStateAsync();
    }

    // ========================
    // DESTROY
    // ========================

    public async ValueTask DisposeAsync()
    {
        if (Destroyed) return;
        Destroyed = true;

        // Timer.Dispose() does NOT wait for in-flight callbacks. Rechoke iterates
        // Wires in a LINQ OrderBy; if a callback is mid-iteration when the Wires
        // collection is cleared below, it dereferences a null wire and NREs on
        // the thread-pool — crashing the testhost during back-to-back test teardown.
        // DisposeAsync drains in-flight callbacks first.
        _speedTimer?.Dispose();
        _noPeersTimer?.Dispose();
        if (_rechokeTimer != null) await _rechokeTimer.DisposeAsync();
        _rarityMap?.Destroy();

        if (_discovery != null)
        {
            try { await _discovery.StopAsync(); } catch { }
            await _discovery.DisposeAsync();
        }

        foreach (var peer in _peers.Values.ToArray()) peer.Destroy();
        _peers.Clear();

        foreach (var wc in _webConns) await wc.DisposeAsync();
        _webConns.Clear();

        foreach (var wire in Wires.ToArray()) wire.Destroy();
        Wires.Clear();
    }

    // ========================
    // HELPERS
    // ========================

    private static byte[] Base32Decode(string input)
    {
        input = input.ToUpperInvariant();
        var output = new byte[input.Length * 5 / 8];
        int bitIndex = 0, inputIndex = 0;
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        while (inputIndex < input.Length)
        {
            int byteIndex = bitIndex / 8, bitOffset = bitIndex % 8;
            int val = alphabet.IndexOf(input[inputIndex]);
            if (val < 0) { inputIndex++; continue; }
            if (bitOffset <= 3) { if (byteIndex < output.Length) output[byteIndex] |= (byte)(val << (3 - bitOffset)); }
            else
            {
                if (byteIndex < output.Length) output[byteIndex] |= (byte)(val >> (bitOffset - 3));
                if (byteIndex + 1 < output.Length) output[byteIndex + 1] |= (byte)(val << (11 - bitOffset));
            }
            bitIndex += 5; inputIndex++;
        }
        return output;
    }
}

/// <summary>File info within a torrent.</summary>
public class TorrentFileInfo
{
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public long Length { get; set; }
    public long Offset { get; set; }

    /// <summary>MIME type (derived from file extension).</summary>
    public string Type => MimeTypes.GetMimeType(Name);

    /// <summary>Reference to parent torrent for computing per-file progress.</summary>
    internal Torrent? Torrent { get; set; }

    /// <summary>First piece index that contains data for this file.</summary>
    public int StartPiece => Torrent?.PieceLength > 0 ? (int)(Offset / Torrent.PieceLength) : 0;

    /// <summary>Last piece index that contains data for this file.</summary>
    public int EndPiece => Torrent?.PieceLength > 0 ? (int)((Offset + Length - 1) / Torrent.PieceLength) : 0;

    /// <summary>Bytes downloaded for this file.</summary>
    public long Downloaded
    {
        get
        {
            if (Torrent == null || Torrent.Bitfield.Length == 0) return 0;
            long dl = 0;
            for (int i = StartPiece; i <= EndPiece && i < Torrent.Bitfield.Length; i++)
            {
                if (!Torrent.Bitfield[i]) continue;
                int pieceSize = (i == Torrent.PieceCount - 1) ? Torrent.LastPieceLength : Torrent.PieceLength;
                long pieceStart = (long)i * Torrent.PieceLength;
                long overlapStart = Math.Max(pieceStart, Offset);
                long overlapEnd = Math.Min(pieceStart + pieceSize, Offset + Length);
                if (overlapEnd > overlapStart) dl += overlapEnd - overlapStart;
            }
            return dl;
        }
    }

    /// <summary>Download progress for this file (0.0 to 1.0).</summary>
    public double Progress => Length > 0 ? (double)Downloaded / Length : 0;

    /// <summary>Whether this file is fully downloaded.</summary>
    public bool Done => Length > 0 && Downloaded >= Length;

    /// <summary>Select this file for download.</summary>
    public void Select() => Torrent?.Select(StartPiece, EndPiece, 1);

    /// <summary>Deselect this file from download.</summary>
    public void Deselect() => Torrent?.Deselect(StartPiece, EndPiece);

    /// <summary>
    /// Get the streaming URL for this file (served by the service worker).
    /// Point a video/audio/img element's src at this URL for streaming with seeking.
    /// Pieces download on demand as the media plays.
    /// </summary>
    public string? StreamURL => Torrent != null ? ServiceWorkerStreamHandler.GetStreamUrl(Torrent, this) : null;

    /// <summary>
    /// Set the src of an HTML media element to this file's streaming URL.
    /// Supports streaming, seeking, and all browser codecs.
    /// Pieces download on demand as the media plays.
    /// </summary>
    public void StreamTo(SpawnDev.BlazorJS.JSObjects.HTMLMediaElement elem)
    {
        var url = StreamURL;
        if (url != null) elem.Src = url;
    }

    /// <summary>
    /// Get the entire file as a byte array. Blocks until all pieces are downloaded.
    /// For streaming, use StreamURL or CreateReadStream() instead.
    /// </summary>
    public Task<byte[]> GetArrayBufferAsync(CancellationToken ct = default)
        => Torrent?.ReadFileAsync(Array.IndexOf(Torrent.Files!, this), 0, (int)Length, ct)
           ?? Task.FromResult(Array.Empty<byte>());

    /// <summary>
    /// Read a byte range from this file. Waits for needed pieces to download.
    /// Works during active download — pieces are fetched on demand.
    /// </summary>
    public Task<byte[]> ReadAsync(long offset, int length, CancellationToken ct = default)
        => Torrent?.ReadFileAsync(Array.IndexOf(Torrent.Files!, this), offset, length, ct)
           ?? Task.FromResult(Array.Empty<byte>());

    /// <summary>
    /// Read a byte range as a JS <c>Uint8Array</c> — stays in JS memory on OPFS-backed stores (no .NET
    /// <c>byte[]</c> hop). Selects + prioritizes the needed pieces like <see cref="ReadAsync"/>. Ideal for
    /// zero-copy GPU upload (<c>writeBuffer</c>): read a weight range, hand the Uint8Array straight to the GPU.
    /// </summary>
    public Task<SpawnDev.BlazorJS.JSObjects.Uint8Array> ReadUint8ArrayAsync(long offset, int length, CancellationToken ct = default)
        => Torrent?.ReadFileUint8ArrayAsync(Array.IndexOf(Torrent.Files!, this), offset, length, ct)
           ?? Task.FromResult(new SpawnDev.BlazorJS.JSObjects.Uint8Array(0));

    /// <summary>
    /// Get a seekable .NET Stream for this file. Pieces download on demand as the
    /// stream is read. Works on both desktop and browser. Use like any System.IO.Stream.
    /// </summary>
    public Stream CreateReadStream(long start = 0) => new TorrentReadStream(this, start);

    /// <summary>Alias for Length (matches JS WebTorrent file.size).</summary>
    public long Size => Length;

    /// <summary>
    /// Get the file as a JS Blob. In browser with OPFS, uses zero-copy Uint8Array path
    /// (data stays in JS land, no .NET→JS round-trip). On desktop, falls back to byte[].
    /// The caller owns the Blob and must dispose it.
    /// </summary>
    public async Task<SpawnDev.BlazorJS.JSObjects.Blob?> BlobAsync(CancellationToken ct = default)
    {
        if (Torrent == null || !Done) return null;

        // Try zero-copy path: assemble Blob from Uint8Array pieces directly in JS
        if (Torrent._store is Storage.AsyncFSChunkStore opfsStore && opfsStore.SupportsUint8Array)
        {
            var parts = new List<SpawnDev.BlazorJS.JSObjects.Uint8Array>();
            try
            {
                for (int i = StartPiece; i <= EndPiece; i++)
                {
                    var uint8 = await opfsStore.GetUint8ArrayAsync(i, ct);
                    if (uint8 == null) { DisposeParts(parts); return null; }

                    // Trim to file boundaries for first/last piece
                    if (i == StartPiece || i == EndPiece)
                    {
                        long pieceStart = (long)i * Torrent.PieceLength;
                        int sliceStart = i == StartPiece ? (int)(Offset - pieceStart) : 0;
                        int sliceEnd = i == EndPiece ? (int)(Offset + Length - pieceStart) : (int)uint8.Length;
                        if (sliceStart > 0 || sliceEnd < uint8.Length)
                        {
                            var sliced = uint8.Slice(sliceStart, sliceEnd);
                            uint8.Dispose();
                            uint8 = sliced;
                        }
                    }
                    parts.Add(uint8);
                }

                // Build the Blob DIRECTLY from the JS Uint8Array parts. The JS Blob constructor accepts
                // ArrayBufferView (TypedArray) parts, so the pieces are concatenated in JS and the bytes
                // NEVER enter the .NET/WASM heap — the genuine zero-copy this method always claimed.
                // (Previously it called ReadBytes() to pull every Uint8Array into a .NET byte[] and then
                // shipped the byte[][] back to JS to build the Blob — a full JS->.NET->JS round-trip.)
                var blob = new SpawnDev.BlazorJS.JSObjects.Blob(
                    parts,
                    new SpawnDev.BlazorJS.JSObjects.BlobOptions { Type = Type });
                return blob;
            }
            finally
            {
                DisposeParts(parts);
            }
        }

        // Fallback: read full file as byte[], create Blob from that
        var data = await GetArrayBufferAsync(ct);
        return new SpawnDev.BlazorJS.JSObjects.Blob(
            new[] { data },
            new SpawnDev.BlazorJS.JSObjects.BlobOptions { Type = Type });
    }

    private static void DisposeParts(List<SpawnDev.BlazorJS.JSObjects.Uint8Array> parts)
    {
        foreach (var p in parts) p.Dispose();
        parts.Clear();
    }

    /// <summary>Check if a piece index contains data from this file.</summary>
    public bool Includes(int pieceIndex) => pieceIndex >= StartPiece && pieceIndex <= EndPiece;

    /// <summary>
    /// Get a seekable .NET Stream for a range of this file.
    /// Pieces download on demand as the stream is read.
    /// </summary>
    public Stream CreateReadStream(long start, long end)
    {
        return new TorrentReadStream(this, start, end);
    }

    /// <summary>
    /// Get a byte range as ArrayBuffer (byte[]). Waits for needed pieces.
    /// Matches JS file.arrayBuffer({start, end}).
    /// </summary>
    public Task<byte[]> ArrayBufferAsync(long start = 0, long end = -1, CancellationToken ct = default)
    {
        if (end < 0) end = Length - 1;
        return ReadAsync(start, (int)(end - start + 1), ct);
    }

    /// <summary>
    /// Async enumerable for streaming file data in chunks.
    /// Pieces download on demand. Matches JS file[Symbol.asyncIterator].
    /// </summary>
    public async IAsyncEnumerable<byte[]> StreamAsync(long start = 0, long end = -1, int chunkSize = 65536,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        if (end < 0) end = Length - 1;
        long pos = start;
        while (pos <= end)
        {
            ct.ThrowIfCancellationRequested();
            int toRead = (int)Math.Min(chunkSize, end - pos + 1);
            var chunk = await ReadAsync(pos, toRead, ct);
            if (chunk.Length == 0) break;
            yield return chunk;
            pos += chunk.Length;
        }
    }

    // Events
    public event Action? OnDone;
    internal void CheckDone() { if (Done) OnDone?.Invoke(); }
}

/// <summary>Simple MIME type lookup from file extension.</summary>
internal static class MimeTypes
{
    private static readonly Dictionary<string, string> _types = new(StringComparer.OrdinalIgnoreCase)
    {
        { ".mp4", "video/mp4" }, { ".webm", "video/webm" }, { ".mkv", "video/x-matroska" },
        { ".avi", "video/x-msvideo" }, { ".mov", "video/quicktime" },
        { ".mp3", "audio/mpeg" }, { ".ogg", "audio/ogg" }, { ".flac", "audio/flac" },
        { ".wav", "audio/wav" }, { ".aac", "audio/aac" },
        { ".jpg", "image/jpeg" }, { ".jpeg", "image/jpeg" }, { ".png", "image/png" },
        { ".gif", "image/gif" }, { ".webp", "image/webp" }, { ".svg", "image/svg+xml" },
        { ".pdf", "application/pdf" }, { ".zip", "application/zip" },
        { ".txt", "text/plain" }, { ".html", "text/html" }, { ".css", "text/css" },
        { ".js", "text/javascript" }, { ".json", "application/json" },
        { ".onnx", "application/octet-stream" }, { ".bin", "application/octet-stream" },
        { ".torrent", "application/x-bittorrent" },
    };

    public static string GetMimeType(string fileName)
    {
        var ext = System.IO.Path.GetExtension(fileName);
        return _types.TryGetValue(ext, out var type) ? type : "application/octet-stream";
    }
}

/// <summary>Parsed torrent metadata.</summary>
public class TorrentMetadata
{
    public string InfoHash { get; set; } = "";
    public string Name { get; set; } = "";
    public int PieceLength { get; set; }
    public int PieceCount { get; set; }
    public long TotalLength { get; set; }
    public bool IsPrivate { get; set; }
    public byte[][] PieceHashes { get; set; } = Array.Empty<byte[]>();
    public TorrentFileInfo[] Files { get; set; } = Array.Empty<TorrentFileInfo>();
    public string[]? UrlList { get; set; }
    /// <summary>BEP 17 Hoffman-style HTTP seed URLs (httpseeds key in .torrent).</summary>
    public string[]? HttpSeeds { get; set; }
    public string[]? AnnounceUrls { get; set; }
    /// <summary>Raw bencoded info dictionary bytes (for computing info hash without re-encoding).</summary>
    public byte[]? InfoDictBytes { get; set; }
    /// <summary>Original raw .torrent file bytes (for export/re-distribution).</summary>
    public byte[]? OriginalTorrentBytes { get; set; }
    /// <summary>Creator identification string.</summary>
    public string? CreatedBy { get; set; }
    /// <summary>Torrent creation timestamp.</summary>
    public DateTimeOffset? CreationDate { get; set; }
    /// <summary>Comment embedded in .torrent file.</summary>
    public string? Comment { get; set; }

    /// <summary>
    /// Piece hash algorithm derived from <see cref="PieceHashes"/>. 32-byte hashes are
    /// SHA-256 (BEP 52 Phase 1 compatibility); 20-byte hashes are classic v1 SHA-1.
    /// Returns <c>"SHA-256"</c>, <c>"SHA-1"</c>, or <c>null</c> when pieces haven't
    /// been parsed yet.
    /// </summary>
    public string? PieceHashAlgorithm => PieceHashes.Length == 0 ? null
        : PieceHashes[0].Length == 32 ? "SHA-256"
        : PieceHashes[0].Length == 20 ? "SHA-1"
        : null;

    /// <summary>
    /// BEP 52 torrent meta version. <c>0</c> means this torrent does not carry a
    /// <c>meta version</c> key (classic v1); <c>2</c> means the info dict is BEP 52
    /// v2-shaped (has <c>file tree</c>, <c>piece layers</c>, SHA-256 info hash). A
    /// hybrid v1+v2 torrent reports <c>2</c> and also populates <see cref="InfoHash"/>
    /// with the v1 SHA-1 infohash while <see cref="V2InfoHash"/> carries the v2 SHA-256.
    /// </summary>
    public int MetaVersion { get; set; }

    /// <summary>
    /// BEP 52 v2 info hash: lowercase hex of SHA-256 of the info dictionary bytes.
    /// Empty for v1-only torrents. For v2-only torrents, <see cref="InfoHash"/> will
    /// be empty string and this field carries the canonical identity. For hybrid
    /// torrents both are populated.
    /// </summary>
    public string V2InfoHash { get; set; } = "";

    /// <summary>
    /// BEP 52 per-file Merkle roots at the piece layer. One 32-byte root per file in
    /// the same order as <see cref="Files"/>. Empty for v1-only torrents.
    /// </summary>
    public byte[][] FileRoots { get; set; } = Array.Empty<byte[]>();

    /// <summary>
    /// Same semantics as <see cref="Torrent.WireInfoHashHex"/>: v1 hash when present
    /// (v1-only or hybrid), else the first 20 bytes of the v2 SHA-256 hash (pure v2).
    /// Used as the canonical 40-char hex identity for OPFS persistence paths so that
    /// pure-v2 torrents get a stable non-empty directory.
    /// </summary>
    public string WireInfoHashHex
    {
        get
        {
            if (!string.IsNullOrEmpty(InfoHash)) return InfoHash;
            if (!string.IsNullOrEmpty(V2InfoHash) && V2InfoHash.Length >= 40)
                return V2InfoHash[..40].ToLowerInvariant();
            return "";
        }
    }

    /// <summary>
    /// BEP 52 <c>piece layers</c> dict. Keys are per-file root hashes (32 bytes) for
    /// files whose length exceeds <see cref="PieceLength"/>; values are the concatenated
    /// piece-layer hashes for that file (one 32-byte entry per piece). Files smaller
    /// than or equal to the piece size do not appear here - their root equals their
    /// single piece-layer hash. Empty for v1-only torrents.
    /// </summary>
    public Dictionary<byte[], byte[]> PieceLayers { get; set; } = new Dictionary<byte[], byte[]>(ByteArrayEqualityComparer.Instance);
}

/// <summary>
/// Byte-array equality + hash comparer for use as dictionary keys where the keys are
/// raw binary data (e.g. SHA-256 piece-layer roots per BEP 52). Provides
/// content-based equality; the default reference equality on byte[] is useless here.
/// </summary>
public sealed class ByteArrayEqualityComparer : IEqualityComparer<byte[]>
{
    public static readonly ByteArrayEqualityComparer Instance = new();

    public bool Equals(byte[]? x, byte[]? y)
    {
        if (ReferenceEquals(x, y)) return true;
        if (x is null || y is null) return false;
        return x.AsSpan().SequenceEqual(y);
    }

    public int GetHashCode(byte[] obj)
    {
        if (obj is null) return 0;
        // FNV-1a 32-bit hash over all bytes - stable across process runs (unlike
        // HashCode.Combine on a long array which hashes structure, not content).
        unchecked
        {
            uint hash = 2166136261u;
            foreach (var b in obj)
            {
                hash = (hash ^ b) * 16777619u;
            }
            return (int)hash;
        }
    }
}
