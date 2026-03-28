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
public class DownloadCoordinator
{
    private readonly PieceManager _pieceManager;
    private readonly TorrentMetadata _metadata;
    private readonly List<WebSeedConnection> _webSeeds = new();
    private readonly List<ActivePeer> _activePeers = new();
    private readonly SemaphoreSlim _updateLock = new(1);
    private CancellationTokenSource? _cts;

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
        _webSeeds.Add(seed);
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

        _activePeers.Add(peer);
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
                // 1. Request from peers
                foreach (var peer in _activePeers)
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
                if (_webSeeds.Count > 0)
                {
                    // Priority pieces first (from file read requests)
                    foreach (var priorityPiece in _priorityPieces.ToArray())
                    {
                        if (_pieceManager.Bitfield[priorityPiece]) continue;

                        bool peerHasIt = _activePeers.Any(p => !p.IsChoked
                            && p.Bitfield.Length > priorityPiece && p.Bitfield[priorityPiece]);

                        if (!peerHasIt)
                            await DownloadFromWebSeed(priorityPiece, ct);
                    }

                    // When no peers are available, proactively download via web seeds
                    bool hasPeers = _activePeers.Any(p => !p.IsChoked);
                    if (!hasPeers)
                    {
                        for (int i = 0; i < _pieceManager.PieceCount; i++)
                        {
                            if (!_pieceManager.Bitfield[i])
                            {
                                await DownloadFromWebSeed(i, ct);
                                break; // one piece per tick to stay responsive
                            }
                        }
                    }
                    else
                    {
                        // Suppress verbose logging after first few ticks
                    }
                }

                // 3. Endgame mode — when few pieces remain, request from ALL peers
                int remaining = _pieceManager.PieceCount - _pieceManager.CompletedCount;
                if (remaining > 0 && remaining <= EndgameThreshold && _activePeers.Count > 1)
                {
                    if (!EndgameMode)
                    {
                        EndgameMode = true;
                        OnLog?.Invoke($"Endgame mode: {remaining} pieces remaining");
                    }

                    for (int i = 0; i < _pieceManager.PieceCount; i++)
                    {
                        if (_pieceManager.Bitfield[i]) continue;
                        foreach (var peer in _activePeers)
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

    private async Task DownloadFromWebSeed(int pieceIndex, CancellationToken ct)
    {
        foreach (var seed in _webSeeds)
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

        // Send Have to all peers
        foreach (var peer in _activePeers)
        {
            _ = peer.Wire.SendHaveAsync(pieceIndex);
        }
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
