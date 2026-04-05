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
    // ========================
    // STATE (match JS torrent properties)
    // ========================

    public string? InfoHash { get; set; }
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
    public bool IsPrivate { get; set; }

    /// <summary>BEP 53: Selected file indices from magnet so= parameter. Null if not specified.</summary>
    public int[]? SelectedFileIndices { get; set; }

    // Pieces
    public Piece[] Pieces { get; set; } = Array.Empty<Piece>();
    public List<Wire> Wires { get; } = new();
    public TorrentFileInfo[]? Files { get; set; }

    // Peers
    private readonly Dictionary<string, Peer> _peers = new();
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

    /// <summary>Upload/download ratio.</summary>
    public double Ratio => Downloaded > 0 ? (double)UploadedTotal / Downloaded : 0;

    /// <summary>Number of completed (verified) pieces.</summary>
    public int CompletedPieces => Bitfield.Count(b => b);

    /// <summary>Total number of pieces.</summary>
    public int PieceCount => Bitfield.Length;

    /// <summary>Announce URLs from metadata (alias for AnnounceUrls).</summary>
    public string[] Announce => AnnounceUrls;

    /// <summary>Computed magnet URI with trackers and web seeds.</summary>
    public string ComputedMagnetUri
    {
        get
        {
            if (string.IsNullOrEmpty(InfoHash)) return "";
            var sb = new StringBuilder($"magnet:?xt=urn:btih:{InfoHash}");
            if (!string.IsNullOrEmpty(Name))
                sb.Append($"&dn={Uri.EscapeDataString(Name)}");
            foreach (var tr in AnnounceUrls)
                sb.Append($"&tr={Uri.EscapeDataString(tr)}");
            foreach (var ws in UrlList)
                sb.Append($"&ws={Uri.EscapeDataString(ws)}");
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
        _http = new HttpClient();
        PeerIdHex = client.PeerId;
        MagnetUri = magnetUri;
        Strategy = opts.Strategy;

        if (opts.Paused) Paused = true;
        if (opts.Deselect) _deselect = true;
        if (opts.MaxWebConns > 0) MaxWebConns = opts.MaxWebConns;

        ParseMagnet(magnetUri);
        if (string.IsNullOrEmpty(InfoHash))
            throw new Exception("Malformed magnet: no info hash");

        StartRechoke();
        StartDiscovery();
    }

    /// <summary>Initialize torrent from parsed metadata.</summary>
    public void InitFromMetadata(TorrentMetadata metadata, WebTorrentClient client, AddTorrentOptions opts)
    {
        _client = client;
        _http = new HttpClient();
        PeerIdHex = client.PeerId;
        Strategy = opts.Strategy;
        if (opts.Paused) Paused = true;
        if (opts.Deselect) _deselect = true;
        if (opts.MaxWebConns > 0) MaxWebConns = opts.MaxWebConns;

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

    /// <summary>Event fired when BEP 46 detects a new infohash for this mutable torrent.</summary>
    public event Action<string>? OnMutableUpdate; // new infohash hex

    /// <summary>Fire the mutable update event (called by WebTorrentClient when DHT detects new version).</summary>
    public void NotifyMutableUpdate(string newInfoHash) => OnMutableUpdate?.Invoke(newInfoHash);

    private void ParseMagnet(string magnetUri)
    {
        var queryStart = magnetUri.IndexOf('?');
        if (queryStart < 0) return;
        var query = System.Web.HttpUtility.ParseQueryString(magnetUri[(queryStart)..]);

        var xt = query["xt"];
        if (xt != null && xt.StartsWith("urn:btih:"))
        {
            var hashPart = xt["urn:btih:".Length..];
            if (hashPart.Length == 40)
                InfoHash = hashPart.ToLowerInvariant();
            else if (hashPart.Length == 32)
                InfoHash = Convert.ToHexString(Base32Decode(hashPart)).ToLowerInvariant();
        }

        // BEP 46: xs=urn:btpk:{public_key_hex} — mutable torrent via DHT
        var xs = query["xs"];
        if (xs != null && xs.StartsWith("urn:btpk:"))
        {
            var pkHex = xs["urn:btpk:".Length..];
            if (pkHex.Length == 64) // 32 bytes = 64 hex chars
                BtpkPublicKey = Convert.FromHexString(pkHex);
        }

        var trackers = query.GetValues("tr");
        if (trackers != null) AnnounceUrls = trackers;

        var webSeeds = query.GetValues("ws");
        if (webSeeds != null) UrlList = webSeeds;

        Name = query["dn"];

        // BEP 53: so= parameter for selecting specific file indices
        var so = query["so"];
        if (!string.IsNullOrEmpty(so))
        {
            try
            {
                SelectedFileIndices = so.Split(',')
                    .Select(s => s.Trim())
                    .Where(s => !string.IsNullOrEmpty(s))
                    .Select(int.Parse)
                    .ToArray();
            }
            catch { /* malformed so= — ignore */ }
        }
    }

    // ========================
    // METADATA
    // ========================

    /// <summary>Original .torrent file bytes for export/re-distribution.</summary>
    public byte[]? TorrentFileBytes { get; set; }

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
        Name = metadata.Name;
        PieceLength = metadata.PieceLength;
        Length = metadata.TotalLength;
        IsPrivate = metadata.IsPrivate;
        LastPieceLength = (int)(Length % PieceLength);
        if (LastPieceLength == 0) LastPieceLength = PieceLength;

        _hashes = metadata.PieceHashes;

        // Store optional metadata fields
        TorrentFileBytes = metadata.OriginalTorrentBytes;
        Comment = metadata.Comment;
        CreatedBy = metadata.CreatedBy;
        CreationDate = metadata.CreationDate;

        Pieces = new Piece[metadata.PieceCount];
        Bitfield = new bool[metadata.PieceCount];
        for (int i = 0; i < metadata.PieceCount; i++)
        {
            int len = (i == metadata.PieceCount - 1) ? LastPieceLength : PieceLength;
            Pieces[i] = new Piece(len);
        }

        Files = metadata.Files;
        // Set back-reference on files for per-file progress computation
        if (Files != null)
            foreach (var f in Files) f.Torrent = this;
        if (AnnounceUrls.Length == 0 && metadata.AnnounceUrls != null)
            AnnounceUrls = metadata.AnnounceUrls;
        if (UrlList.Length == 0 && metadata.UrlList != null)
            UrlList = metadata.UrlList;

        // Initialize chunk store if not already set (e.g., by SeedAsync)
        if (_store == null)
        {
            if (_client?.AsyncFileSystem != null && !string.IsNullOrEmpty(InfoHash))
                _store = new Storage.AsyncFSChunkStore(_client.AsyncFileSystem, $"webtorrent/{InfoHash}", PieceLength);
            else
                _store = new Storage.MemoryChunkStore(PieceLength);
        }

        _rarityMap = new RarityMap(this);

        if (_client?.EnableWebSeeds == true)
            foreach (var url in UrlList) AddWebSeed(url);

        // Select all pieces for download — unless paused or deselect mode
        if (Pieces.Length > 0 && !Paused && !_deselect)
            _selections.Insert(new SelectionItem { From = 0, To = Pieces.Length - 1, Priority = 0 });

        foreach (var wire in Wires.ToArray())
            OnWireWithMetadata(wire);

        StartSpeedTimer();

        // Persist .torrent metadata for restore after page reload
        if (_client?.AsyncFileSystem != null && TorrentFileBytes != null && !string.IsNullOrEmpty(InfoHash))
            _ = PersistMetadataAsync();

        Ready = true;
        OnMetadata?.Invoke();
        OnReady?.Invoke();
    }

    private async Task PersistMetadataAsync()
    {
        if (_client?.AsyncFileSystem == null || TorrentFileBytes == null || string.IsNullOrEmpty(InfoHash)) return;
        try
        {
            var fs = _client.AsyncFileSystem;
            var dir = "webtorrent/_state";
            if (!await fs.DirectoryExists(dir))
                await fs.CreateDirectory(dir);
            await fs.Write($"{dir}/{InfoHash}.torrent", TorrentFileBytes);
        }
        catch { /* Best-effort persistence */ }
    }

    // ========================
    // DISCOVERY
    // ========================

    private void StartDiscovery()
    {
        if (_discovery != null || Destroyed || _client == null || string.IsNullOrEmpty(InfoHash)) return;

        var infoHashBytes = Convert.FromHexString(InfoHash!);

        _discovery = new Discovery(
            infoHashBytes, _client.PeerIdBuffer, AnnounceUrls,
            (initiator) => _client.CreatePeer(initiator),
            _http ?? new HttpClient()
        );

        _discovery.OnWebRtcPeer += AddPeer;
        _discovery.OnTcpPeer += ConnectTcpPeer;
        _discovery.OnWarning += (msg) => OnWarning?.Invoke(msg);

        _ = _discovery.AnnounceAsync(new AnnounceOptions
        {
            Event = "started",
            Left = Math.Max(Length - Downloaded, 0),
        });
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

    public void AddPeer(SimplePeer simplePeer)
    {
        if (Destroyed) return;
        if (_peers.Count >= (_client?.MaxConns ?? 55)) return;

        var peer = Peer.CreateWebRTCPeer(simplePeer);
        peer.Swarm = this;
        if (_peers.ContainsKey(peer.Id)) return;
        _peers[peer.Id] = peer;

        simplePeer.OnConnect += () =>
        {
            peer.OnConnected();
            if (peer.WireInstance != null)
            {
                _client?.ApplyExtensions(peer.WireInstance);
                Wires.Add(peer.WireInstance);
                OnWire?.Invoke(peer.WireInstance, peer.Id);
                if (HasMetadata) OnWireWithMetadata(peer.WireInstance);

                peer.WireInstance.OnClose += () =>
                {
                    Wires.Remove(peer.WireInstance);
                    _peers.Remove(peer.Id);
                };
            }
        };

        simplePeer.OnError += (err) => { peer.Destroy(err); _peers.Remove(peer.Id); };
        simplePeer.OnClose += () => _peers.Remove(peer.Id);
        peer.StartConnectTimeout();
    }

    /// <summary>Remove a peer from the swarm by peer ID.</summary>
    public void RemovePeer(string peerId)
    {
        if (_peers.TryGetValue(peerId, out var peer))
        {
            peer.Destroy();
            _peers.Remove(peerId);
        }
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

        Done = Bitfield.All(b => b);
    }

    public void AddWebSeed(string url)
    {
        if (Destroyed || !HasMetadata || _http == null) return;
        if (_webConns.Any(wc => wc.Url == url)) return;

        var webConn = new WebConn(url, this, _http);
        _webConns.Add(webConn);
        Wires.Add(webConn.WireInstance);
        OnWire?.Invoke(webConn.WireInstance, url);
        if (HasMetadata) OnWireWithMetadata(webConn.WireInstance);
    }

    // ========================
    // PIECE SELECTION
    // ========================

    public void Select(int start, int end, int priority = 0)
    {
        _selections.Insert(new SelectionItem { From = start, To = end, Priority = priority });
        UpdateWires();
    }

    public void Deselect(int start, int end)
    {
        _selections.Remove(new SelectionItem { From = start, To = end });
    }

    public void Critical(int start, int end)
    {
        for (int i = start; i <= end; i++) _critical[i] = true;
    }

    // ========================
    // FILE READ (for streaming and download)
    // ========================

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

        // Auto-select file pieces and resume if needed — enables on-demand streaming
        if (Paused || !_selections.Any())
        {
            // Select this file's piece range so pieces will be requested
            Select(file.StartPiece, file.EndPiece, 1);
            if (Paused) Resume();
        }

        while (resultPos < length)
        {
            ct.ThrowIfCancellationRequested();

            int pieceIdx = (int)(absOffset / PieceLength);
            int pieceOffset = (int)(absOffset % PieceLength);
            int pieceSize = (pieceIdx == PieceCount - 1) ? LastPieceLength : PieceLength;
            int available = pieceSize - pieceOffset;
            int toRead = Math.Min(available, length - resultPos);

            // Wait for piece if not yet downloaded
            if (pieceIdx < Bitfield.Length && !Bitfield[pieceIdx])
            {
                // Mark as critical to prioritize download
                Critical(pieceIdx, pieceIdx);

                // Poll until piece arrives (100ms intervals)
                while (!Bitfield[pieceIdx] && !Destroyed)
                {
                    ct.ThrowIfCancellationRequested();
                    await Task.Delay(100, ct);
                }

                if (Destroyed) throw new OperationCanceledException("Torrent destroyed while waiting for piece");
            }

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
    }

    public void Resume()
    {
        Paused = false;
        UpdateWires();
    }

    // ========================
    // DESTROY
    // ========================

    public async ValueTask DisposeAsync()
    {
        if (Destroyed) return;
        Destroyed = true;

        _speedTimer?.Dispose();
        _rechokeTimer?.Dispose();
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
    /// Get a seekable .NET Stream for this file. Pieces download on demand as the
    /// stream is read. Works on both desktop and browser. Use like any System.IO.Stream.
    /// </summary>
    public Stream CreateReadStream(long start = 0) => new TorrentReadStream(this, start);

    /// <summary>Alias for Length (matches JS WebTorrent file.size).</summary>
    public long Size => Length;

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
}
