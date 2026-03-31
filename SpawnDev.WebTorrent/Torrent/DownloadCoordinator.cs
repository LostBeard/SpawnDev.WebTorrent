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
    private int _tickCount;
    private readonly List<ActivePeer> _activePeers = new();
    private readonly object _peersLock = new();
    private readonly object _seedsLock = new();
    private readonly SemaphoreSlim _updateLock = new(1);
    private CancellationTokenSource? _cts;
    private bool _disposed;

    /// <summary>Pieces that are high-priority (requested by file reads).</summary>
    private readonly HashSet<int> _priorityPieces = new();

    /// <summary>Maximum outstanding requests per peer.</summary>
    public int MaxRequestsPerPeer { get; set; } = 6;

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

    /// <summary>Add an active peer with its wire protocol and bitfield.</summary>
    public void AddPeer(WireProtocol wire, bool[] peerBitfield)
    {
        var peer = new ActivePeer
        {
            Wire = wire,
            Bitfield = peerBitfield,
            OutstandingRequests = new List<(int piece, int offset, int length)>(),
        };

        wire.OnPiece += async (pieceIdx, offset, data) =>
        {
            peer.OutstandingRequests.RemoveAll(r => r.piece == pieceIdx && r.offset == offset);
            await _pieceManager.ReceiveBlockAsync(pieceIdx, offset, data);
        };

        wire.OnChoke += () => peer.IsChoked = true;
        wire.OnUnchoke += () => peer.IsChoked = false;

        // If the remote already sent Unchoke before we subscribed, start unchoked.
        if (!wire.PeerChoking)
            peer.IsChoked = false;

        Console.Error.WriteLine($"[Coordinator] AddPeer: choked={peer.IsChoked}, bitfield={peerBitfield.Count(b => b)}/{peerBitfield.Length} pieces");

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
        _priorityPieces.Add(pieceIndex);
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
                WebSeedConnection[] seeds;
                lock (_seedsLock) seeds = _webSeeds.ToArray();

                // Debug: log coordinator state each tick (first 10 ticks only)
                if (_tickCount++ < 10)
                    Console.Error.WriteLine($"[Coordinator] tick {_tickCount}: {peers.Length} peers ({peers.Count(p => !p.IsChoked)} unchoked), {seeds.Length} seeds, complete={_pieceManager.CompletedCount}/{_pieceManager.PieceCount}");

                // 1. Request from peers
                foreach (var peer in peers)
                {
                    if (peer.IsChoked || peer.OutstandingRequests.Count >= MaxRequestsPerPeer)
                        continue;

                    // Try priority pieces first
                    foreach (var priorityPiece in _priorityPieces.ToArray())
                    {
                        if (!_pieceManager.Bitfield[priorityPiece] && peer.Bitfield.Length > priorityPiece && peer.Bitfield[priorityPiece])
                        {
                            await RequestBlocksFromPeer(peer, priorityPiece);
                        }
                    }

                    // Then normal selection
                    if (peer.OutstandingRequests.Count < MaxRequestsPerPeer)
                    {
                        int piece = _pieceManager.SelectPiece(peer.Bitfield, Strategy);
                        if (piece >= 0)
                            await RequestBlocksFromPeer(peer, piece);
                    }
                }

                // 2. Web seed downloads
                if (seeds.Length > 0)
                {
                    // Priority pieces first (from file read requests)
                    foreach (var priorityPiece in _priorityPieces.ToArray())
                    {
                        if (_pieceManager.Bitfield[priorityPiece]) continue;

                        bool peerHasIt = peers.Any(p => !p.IsChoked
                            && p.Bitfield.Length > priorityPiece && p.Bitfield[priorityPiece]);

                        if (!peerHasIt)
                            await DownloadFromWebSeed(priorityPiece, seeds, ct);
                    }

                    // When no peers are available, start background bulk stream + serve priority pieces
                    bool hasPeers = peers.Any(p => !p.IsChoked);
                    if (!hasPeers && seeds.Length > 0)
                    {
                        // Start bulk stream in background (fills cache sequentially)
                        if (_bulkDownloadTask == null || _bulkDownloadTask.IsCompleted)
                            _bulkDownloadTask = DownloadBulkFromWebSeed(seeds, ct);

                        // Serve any priority pieces via individual range requests (seeking)
                        foreach (var priorityPiece in _priorityPieces.ToArray())
                        {
                            if (!_pieceManager.Bitfield[priorityPiece])
                                await DownloadFromWebSeed(priorityPiece, seeds, ct);
                        }
                    }
                    else
                    {
                        // Suppress verbose logging after first few ticks
                    }
                }

                // 3. Endgame mode — when few pieces remain, request from ALL peers
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
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            OnLog?.Invoke($"Download loop crashed: {ex.Message}");
            OnError?.Invoke(ex);
        }
        OnLog?.Invoke("Download loop ended");
    }

    private async Task RequestBlocksFromPeer(ActivePeer peer, int pieceIndex)
    {
        while (peer.OutstandingRequests.Count < MaxRequestsPerPeer)
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
            // Multi-file — download per piece (existing path)
            for (int i = 0; i < _pieceManager.PieceCount; i++)
            {
                if (!_pieceManager.Bitfield[i])
                {
                    await DownloadFromWebSeed(i, seeds, ct);
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
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
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
        _priorityPieces.Remove(pieceIndex);
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

            _ = peer.Wire.SendHaveAsync(pieceIndex);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cts?.Cancel();
        _cts?.Dispose();
        _updateLock.Dispose();
    }
}

/// <summary>An active peer connection with download state.</summary>
public class ActivePeer
{
    public WireProtocol Wire { get; set; } = null!;
    public bool[] Bitfield { get; set; } = Array.Empty<bool>();
    public bool IsChoked { get; set; } = true;
    public List<(int piece, int offset, int length)> OutstandingRequests { get; set; } = new();
}
