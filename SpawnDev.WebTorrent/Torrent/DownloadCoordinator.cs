using SpawnDev.WebTorrent.Transports;
using SpawnDev.WebTorrent.Wire;

namespace SpawnDev.WebTorrent.Torrent;

/// <summary>
/// Coordinates downloads across peers and web seeds.
/// The main download loop: selects pieces, requests blocks, handles responses,
/// falls back to web seeds when peers don't have what we need.
///
/// This is the engine that makes TorrentFileStream.ReadAsync work —
/// when a file read requests a piece, the coordinator prioritizes it.
/// </summary>
public class DownloadCoordinator : IDisposable
{
    private readonly PieceManager _pieceManager;
    private readonly TorrentMetadata _metadata;
    private readonly List<WebSeedConnection> _webSeeds = new();
    private Task? _bulkDownloadTask;
    private HttpClient? _streamHttp;
    private int _tickCount;
    private readonly List<ActivePeer> _activePeers = new();
    private readonly object _peersLock = new();
    private readonly object _seedsLock = new();
    private readonly SemaphoreSlim _updateLock = new(1);
    private CancellationTokenSource? _cts;
    private bool _disposed;

    /// <summary>Pieces that are high-priority (requested by file reads).</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<int, bool> _priorityPieces = new();

    /// <summary>Minimum outstanding requests per peer.</summary>
    public int MinRequestsPerPeer { get; set; } = 2;

    /// <summary>Maximum outstanding requests per peer (hard cap).</summary>
    public int MaxRequestsPerPeer { get; set; } = 64;

    /// <summary>Interval between update ticks in milliseconds.</summary>
    public int UpdateIntervalMs { get; set; } = 100;

    /// <summary>Whether endgame mode is active (last few pieces, request from all peers).</summary>
    public bool EndgameMode { get; private set; }

    /// <summary>Threshold: enter endgame when this many pieces remain.</summary>
    public int EndgameThreshold { get; set; } = 5;

    /// <summary>Piece selection strategy: "rarest" or "sequential".</summary>
    public string Strategy { get; set; } = "rarest";

    /// <summary>Number of configured web seeds.</summary>
    public int WebSeedCount { get { lock (_seedsLock) return _webSeeds.Count; } }

    /// <summary>Number of active peers.</summary>
    public int PeerCount { get { lock (_peersLock) return _activePeers.Count; } }

    // Events
    public event Action<int>? OnPieceComplete;
    public event Action? OnDownloadComplete;
    public event Action<double>? OnProgressChanged;

    public DownloadCoordinator(PieceManager pieceManager, TorrentMetadata metadata)
    {
        _pieceManager = pieceManager;
        _metadata = metadata;
        _pieceManager.OnPieceComplete += HandlePieceComplete;
    }

    /// <summary>Add a web seed URL for CDN fallback.</summary>
    public void AddWebSeed(HttpClient httpClient, string url)
    {
        var seed = new WebSeedConnection(httpClient, url, _metadata);
        seed.OnLog += (msg) => OnLog?.Invoke(msg);
        lock (_seedsLock) _webSeeds.Add(seed);
    }

    /// <summary>Add an active peer with its wire protocol and bitfield.
    /// If the wire already exists, updates its bitfield instead of creating a duplicate.</summary>
    public void AddPeer(WireProtocol wire, bool[] peerBitfield)
    {
        lock (_peersLock)
        {
            var existing = _activePeers.Find(p => p.Wire == wire);
            if (existing != null)
            {
                // Update bitfield — don't create duplicate ActivePeer or re-subscribe events
                existing.Bitfield = peerBitfield;
                return;
            }
        }

        if (WebTorrentClient.VerboseLogging)
            Console.WriteLine($"[DL] AddPeer: bitfield={peerBitfield.Length}, pieces={peerBitfield.Count(b => b)}, PeerChoking={wire.PeerChoking}");
        var peer = new ActivePeer
        {
            Wire = wire,
            Bitfield = peerBitfield,
            OutstandingRequests = new List<(int piece, int offset, int length)>(),
        };

        wire.OnPiece += async (pieceIdx, offset, data) =>
        {
            try
            {
                peer.RecordReceived(data.Length);
                peer.OutstandingRequests.RemoveAll(r => r.piece == pieceIdx && r.offset == offset);
                await _pieceManager.ReceiveBlockAsync(pieceIdx, offset, data);
            }
            catch (Exception ex)
            {
                OnError?.Invoke(ex);
            }
        };

        wire.OnChoke += () => peer.IsChoked = true;
        wire.OnUnchoke += () => peer.IsChoked = false;

        // If the remote already sent Unchoke before we subscribed, start unchoked.
        if (!wire.PeerChoking)
            peer.IsChoked = false;

        lock (_peersLock) _activePeers.Add(peer);
    }

    /// <summary>Remove a disconnected peer.</summary>
    public void RemovePeer(WireProtocol wire)
    {
        lock (_peersLock) _activePeers.RemoveAll(p => p.Wire == wire);
    }

    /// <summary>Request a specific piece with high priority (for file read).</summary>
    public void Prioritize(int pieceIndex)
    {
        _priorityPieces.TryAdd(pieceIndex, true);
    }

    /// <summary>Start the download loop.</summary>
    public void Start()
    {
        _cts = new CancellationTokenSource();
        _ = DownloadLoopAsync(_cts.Token);
    }

    /// <summary>Stop the download loop.</summary>
    public void Stop()
    {
        _cts?.Cancel();
    }

    private async Task DownloadLoopAsync(CancellationToken ct)
    {
        if (WebTorrentClient.VerboseLogging) Console.WriteLine("[DL] Download loop started");
        OnLog?.Invoke("Download loop started");
        try
        {
        while (!ct.IsCancellationRequested && !_pieceManager.IsComplete)
        {
            await _updateLock.WaitAsync(ct);
            try
            {
                // Snapshot collections for safe iteration
                ActivePeer[] peers;
                lock (_peersLock) peers = _activePeers.ToArray();
                if (WebTorrentClient.VerboseLogging && _tickCount % 10 == 0)
                    Console.WriteLine($"[DL] tick={_tickCount}, peers={peers.Length}, choked={peers.Count(p => p.IsChoked)}, seeds={_webSeeds.Count}, complete={_pieceManager.CompletedCount}/{_pieceManager.PieceCount}");
                WebSeedConnection[] seeds;
                lock (_seedsLock) seeds = _webSeeds.ToArray();

                _tickCount++;

                // 1. Request from peers
                foreach (var peer in peers)
                {
                    int pipelineDepth = Math.Min(peer.PipelineDepth, MaxRequestsPerPeer);
                    if (peer.IsChoked || peer.OutstandingRequests.Count >= pipelineDepth)
                        continue;

                    // Try priority pieces first
                    foreach (var priorityPiece in _priorityPieces.Keys.ToArray())
                    {
                        if (!_pieceManager.Bitfield[priorityPiece] && peer.Bitfield.Length > priorityPiece && peer.Bitfield[priorityPiece])
                        {
                            await RequestBlocksFromPeer(peer, pieceIndex: priorityPiece, maxRequests: pipelineDepth);
                        }
                    }

                    // Then normal selection — keep filling until pipeline is full
                    while (peer.OutstandingRequests.Count < pipelineDepth)
                    {
                        int piece = _pieceManager.SelectPiece(peer.Bitfield, Strategy);
                        if (piece >= 0)
                            await RequestBlocksFromPeer(peer, piece, pipelineDepth);
                        else
                            break; // no more pieces available from this peer
                    }
                }

                // 2. Web seed downloads — always active when pieces are missing
                if (seeds.Length > 0 && !_pieceManager.IsComplete)
                {
                    // Priority pieces first
                    foreach (var priorityPiece in _priorityPieces.Keys.ToArray())
                    {
                        if (!_pieceManager.Bitfield[priorityPiece])
                            await DownloadFromWebSeed(priorityPiece, seeds, ct);
                    }

                    // Bulk stream download — always runs if not already in progress
                    if (_bulkDownloadTask == null || _bulkDownloadTask.IsCompleted)
                        _bulkDownloadTask = DownloadBulkFromWebSeed(seeds, ct);
                }

                // 3. Timeout check — destroy peers that have outstanding requests but no data for 30s
                foreach (var peer in peers)
                {
                    if (peer.IsTimedOut)
                    {
                        OnLog?.Invoke($"Peer timed out (30s no data, {peer.OutstandingRequests.Count} pending)");
                        try { await peer.Wire.DisposeAsync(); } catch { }
                        RemovePeer(peer.Wire);
                    }
                }

                // 4. Endgame mode — when few pieces remain, request from ALL peers
                int remaining = _pieceManager.PieceCount - _pieceManager.CompletedCount;
                if (remaining > 0 && remaining <= EndgameThreshold && peers.Length > 1)
                {
                    if (!EndgameMode)
                    {
                        EndgameMode = true;
                        OnLog?.Invoke($"Endgame mode: {remaining} pieces remaining");
                    }

                    for (int i = 0; i < _pieceManager.PieceCount; i++)
                    {
                        if (_pieceManager.Bitfield[i]) continue;
                        foreach (var peer in peers)
                        {
                            if (!peer.IsChoked && peer.Bitfield.Length > i && peer.Bitfield[i])
                                await RequestBlocksFromPeer(peer, i);
                        }
                    }
                }
            }
            finally
            {
                _updateLock.Release();
            }

            await Task.Delay(UpdateIntervalMs, ct);
        }
        }
        catch (OperationCanceledException) { Console.WriteLine("[DL] Loop cancelled"); }
        catch (Exception ex)
        {
            Console.WriteLine($"[DL] LOOP CRASHED: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
            OnLog?.Invoke($"Download loop crashed: {ex.Message}");
            OnError?.Invoke(ex);
        }
        OnLog?.Invoke("Download loop ended");
    }

    private async Task RequestBlocksFromPeer(ActivePeer peer, int pieceIndex, int maxRequests = 6)
    {
        while (peer.OutstandingRequests.Count < maxRequests)
        {
            var (offset, length) = _pieceManager.GetNextBlock(pieceIndex);
            if (offset < 0) break;

            peer.OutstandingRequests.Add((pieceIndex, offset, length));
            await peer.Wire.SendRequestAsync(pieceIndex, offset, length);
        }
    }

    /// <summary>
    /// Download all missing pieces via web seed. For single-file torrents,
    /// downloads the entire file in one HTTP request and splits into pieces locally.
    /// For multi-file torrents, downloads per-file with range requests.
    /// </summary>
    private async Task DownloadBulkFromWebSeed(WebSeedConnection[] seeds, CancellationToken ct)
    {
        if (_metadata.Files.Length == 1)
        {
            // Single file — stream download, verify each piece as it arrives
            var fileName = _metadata.Files[0].Path.Contains('/')
                ? _metadata.Files[0].Path : _metadata.Name;

            foreach (var seed in seeds)
            {
                if (!seed.IsAvailable) continue;
                if (await StreamDownloadAndVerify(seed, fileName, ct))
                    return; // success
            }
        }
        else
        {
            // Multi-file — stream each file individually, split into pieces.
            // One HTTP request per file instead of one per piece.
            foreach (var file in _metadata.Files)
            {
                // Check if all pieces for this file are already complete
                bool allDone = true;
                for (int p = file.StartPiece; p <= file.EndPiece; p++)
                    if (!_pieceManager.Bitfield[p]) { allDone = false; break; }
                if (allDone) continue;

                foreach (var seed in seeds)
                {
                    if (!seed.IsAvailable || ct.IsCancellationRequested) continue;
                    if (await StreamDownloadFileAndVerify(seed, file, ct))
                        break; // success with this seed
                }
            }
        }
    }

    /// <summary>
    /// Stream a single-file download, verifying each piece as its bytes arrive.
    /// No buffering the entire file — reads pieceLength bytes at a time from the HTTP stream.
    /// </summary>
    private async Task<bool> StreamDownloadAndVerify(WebSeedConnection seed, string fileName, CancellationToken ct)
    {
        try
        {
            var url = $"{seed.BaseUrl}/{fileName}";
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            _streamHttp ??= new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
            var http = _streamHttp;
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);

            if (!response.IsSuccessStatusCode) return false;

            using var stream = await response.Content.ReadAsStreamAsync(ct);

            for (int p = 0; p < _metadata.PieceCount; p++)
            {
                int pieceLen = (p == _metadata.PieceCount - 1)
                    ? (int)(_metadata.TotalLength - (long)p * _metadata.PieceLength)
                    : _metadata.PieceLength;

                if (_pieceManager.Bitfield[p])
                {
                    // Already have this piece — skip its bytes in the stream
                    int skipped = 0;
                    var skipBuf = new byte[Math.Min(pieceLen, 65536)];
                    while (skipped < pieceLen)
                    {
                        int toSkip = Math.Min(skipBuf.Length, pieceLen - skipped);
                        int read = await stream.ReadAsync(skipBuf.AsMemory(0, toSkip), ct);
                        if (read == 0) return false;
                        skipped += read;
                    }
                    continue;
                }

                // Read exactly one piece worth of bytes
                var pieceData = new byte[pieceLen];
                int filled = 0;
                while (filled < pieceLen)
                {
                    int read = await stream.ReadAsync(pieceData.AsMemory(filled, pieceLen - filled), ct);
                    if (read == 0) return false; // unexpected end of stream
                    filled += read;
                }

                // Verify and store immediately — don't wait for the rest of the file
                await _pieceManager.ReceiveCompletePieceAsync(p, pieceData);
            }

            return true;
        }
        catch (Exception ex)
        {
            OnLog?.Invoke($"Stream download failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Stream a single file from a web seed, splitting into pieces and verifying as they arrive.
    /// One HTTP request per file — handles pieces that span file boundaries by accumulating bytes.
    /// </summary>
    private async Task<bool> StreamDownloadFileAndVerify(WebSeedConnection seed, TorrentFile file, CancellationToken ct)
    {
        try
        {
            var filePath = file.Path.Contains('/') ? file.Path : $"{_metadata.Name}/{file.Path}";
            var url = $"{seed.BaseUrl}/{WebSeedConnection.EscapePathPublic(filePath)}";
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            _streamHttp ??= new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
            var http = _streamHttp;
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);

            if (!response.IsSuccessStatusCode) return false;

            using var stream = await response.Content.ReadAsStreamAsync(ct);

            // Stream through the file's bytes, splitting into pieces
            long filePos = 0;
            for (int p = file.StartPiece; p <= file.EndPiece && !ct.IsCancellationRequested; p++)
            {
                int pieceLen = (p == _metadata.PieceCount - 1)
                    ? (int)(_metadata.TotalLength - (long)p * _metadata.PieceLength)
                    : _metadata.PieceLength;

                // How many bytes of this piece are in this file?
                long pieceStart = (long)p * _metadata.PieceLength;
                long overlapStart = Math.Max(pieceStart, file.Offset);
                long overlapEnd = Math.Min(pieceStart + pieceLen, file.Offset + file.Length);
                int bytesInFile = (int)(overlapEnd - overlapStart);

                if (_pieceManager.Bitfield[p])
                {
                    // Skip bytes we already have
                    int skipped = 0;
                    var skipBuf = new byte[Math.Min(bytesInFile, 65536)];
                    while (skipped < bytesInFile)
                    {
                        int toSkip = Math.Min(skipBuf.Length, bytesInFile - skipped);
                        int read = await stream.ReadAsync(skipBuf.AsMemory(0, toSkip), ct);
                        if (read == 0) return false;
                        skipped += read;
                    }
                    continue;
                }

                // For pieces that are entirely within this file, download and verify directly
                if (overlapStart == pieceStart && bytesInFile == pieceLen)
                {
                    var pieceData = new byte[pieceLen];
                    int filled = 0;
                    while (filled < pieceLen)
                    {
                        int read = await stream.ReadAsync(pieceData.AsMemory(filled, pieceLen - filled), ct);
                        if (read == 0) return false;
                        filled += read;
                    }
                    await _pieceManager.ReceiveCompletePieceAsync(p, pieceData);
                }
                else
                {
                    // Piece spans file boundary — read our portion, let the next file fill the rest
                    // Fall back to individual piece download for boundary pieces
                    int skipped = 0;
                    var skipBuf = new byte[Math.Min(bytesInFile, 65536)];
                    while (skipped < bytesInFile)
                    {
                        int toSkip = Math.Min(skipBuf.Length, bytesInFile - skipped);
                        int read = await stream.ReadAsync(skipBuf.AsMemory(0, toSkip), ct);
                        if (read == 0) return false;
                        skipped += read;
                    }
                    // Download this boundary piece via individual range request
                    var pieceData = await seed.DownloadPieceAsync(p, ct);
                    if (pieceData != null) await _pieceManager.ReceiveCompletePieceAsync(p, pieceData);
                }
            }
            return true;
        }
        catch (Exception ex)
        {
            OnLog?.Invoke($"Stream file download failed: {ex.Message}");
            return false;
        }
    }

    private async Task DownloadFromWebSeed(int pieceIndex, WebSeedConnection[] seeds, CancellationToken ct)
    {
        foreach (var seed in seeds)
        {
            if (!seed.IsAvailable) continue;

            try
            {
                var data = await seed.DownloadPieceAsync(pieceIndex, ct);
                if (data != null)
                {
                    var ok = await _pieceManager.ReceiveCompletePieceAsync(pieceIndex, data);
                    if (ok) return;
                }
            }
            catch (Exception ex)
            {
                OnLog?.Invoke($"Web seed: piece {pieceIndex} exception: {ex.Message}");
                OnError?.Invoke(ex);
            }
        }
    }

    /// <summary>Fired on errors (for logging).</summary>
    public event Action<Exception>? OnError;

    /// <summary>Fired for diagnostic log messages.</summary>
    public event Action<string>? OnLog;

    private void HandlePieceComplete(int pieceIndex)
    {
        _priorityPieces.TryRemove(pieceIndex, out _);
        OnPieceComplete?.Invoke(pieceIndex);
        OnProgressChanged?.Invoke(_pieceManager.Progress);

        if (_pieceManager.IsComplete)
            OnDownloadComplete?.Invoke();

        // Send Have to all peers + cancel duplicate endgame requests
        ActivePeer[] peers;
        lock (_peersLock) peers = _activePeers.ToArray();
        foreach (var peer in peers)
        {
            // Cancel outstanding requests for this completed piece (endgame cleanup)
            peer.OutstandingRequests.RemoveAll(r => r.piece == pieceIndex);

            try { _ = peer.Wire.SendHaveAsync(pieceIndex); }
            catch (Exception ex) { OnLog?.Invoke($"SendHave failed: {ex.Message}"); }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cts?.Cancel();
        _cts?.Dispose();
        _updateLock.Dispose();
        _streamHttp?.Dispose();
    }
}

/// <summary>An active peer connection with download state.</summary>
public class ActivePeer
{
    public WireProtocol Wire { get; set; } = null!;
    public bool[] Bitfield { get; set; } = Array.Empty<bool>();
    public bool IsChoked { get; set; } = true;
    public List<(int piece, int offset, int length)> OutstandingRequests { get; set; } = new();

    // Speed tracking for adaptive pipeline
    private long _bytesReceived;
    private DateTime _lastSpeedReset = DateTime.UtcNow;
    private DateTime _connectionStart = DateTime.UtcNow;

    /// <summary>Last time we received any data from this peer.</summary>
    public DateTime LastDataReceived { get; private set; } = DateTime.UtcNow;

    /// <summary>Record bytes received from this peer (for speed calculation).</summary>
    public void RecordReceived(int bytes)
    {
        _bytesReceived += bytes;
        LastDataReceived = DateTime.UtcNow;
    }

    /// <summary>Whether this peer has timed out (30s with outstanding requests and no data).</summary>
    public bool IsTimedOut => OutstandingRequests.Count > 0
        && (DateTime.UtcNow - LastDataReceived).TotalSeconds > 30;

    /// <summary>Download speed in bytes/sec over the connection lifetime.</summary>
    public double DownloadSpeed
    {
        get
        {
            var elapsed = (DateTime.UtcNow - _connectionStart).TotalSeconds;
            return elapsed > 0 ? _bytesReceived / elapsed : 0;
        }
    }

    /// <summary>Adaptive pipeline depth: 2 + ceil(elapsed * speed / 16384). Matches JS WebTorrent.</summary>
    public int PipelineDepth
    {
        get
        {
            var elapsed = (DateTime.UtcNow - _connectionStart).TotalSeconds;
            var depth = 2 + (int)Math.Ceiling(elapsed * DownloadSpeed / 16384);
            return Math.Clamp(depth, 2, 64);
        }
    }
}
