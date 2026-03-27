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
        _webSeeds.Add(new WebSeedConnection(httpClient, url, _metadata));
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
                        int piece = _pieceManager.SelectPiece(peer.Bitfield);
                        if (piece >= 0)
                            await RequestBlocksFromPeer(peer, piece);
                    }
                }

                // 2. Fall back to web seeds for priority pieces with no peer coverage
                foreach (var priorityPiece in _priorityPieces.ToArray())
                {
                    if (_pieceManager.Bitfield[priorityPiece]) continue;

                    bool peerHasIt = _activePeers.Any(p => !p.IsChoked
                        && p.Bitfield.Length > priorityPiece && p.Bitfield[priorityPiece]);

                    if (!peerHasIt)
                    {
                        await DownloadFromWebSeed(priorityPiece, ct);
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

            var data = await seed.DownloadPieceAsync(pieceIndex, ct);
            if (data != null)
            {
                // Feed the entire piece as one block
                await _pieceManager.ReceiveBlockAsync(pieceIndex, 0, data);
                return;
            }
        }
    }

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
