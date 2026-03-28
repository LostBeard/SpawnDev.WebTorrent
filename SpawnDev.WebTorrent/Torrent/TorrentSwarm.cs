using SpawnDev.WebTorrent.Discovery;
using SpawnDev.WebTorrent.Storage;
using SpawnDev.WebTorrent.Wire;

namespace SpawnDev.WebTorrent.Torrent;

/// <summary>
/// Manages a single torrent's swarm: peers, pieces, downloads, and uploads.
/// Coordinates piece selection, request scheduling, and choke/unchoke rotation.
/// </summary>
public class TorrentSwarm : IAsyncDisposable
{
    private readonly WebTorrentClient _client;
    private readonly AddTorrentOptions _options;
    private readonly List<PeerConnection> _peers = new();
    private readonly HashSet<string> _knownPeerAddresses = new();
    private readonly SemaphoreSlim _peerLock = new(1, 1);
    private IChunkStore? _store;
    private PieceManager? _pieceManager;
    private DownloadCoordinator? _coordinator;

    /// <summary>Torrent metadata (available after initialization or metadata exchange).</summary>
    public TorrentMetadata? Metadata { get; private set; }

    /// <summary>20-byte info hash.</summary>
    public byte[] InfoHash { get; private set; } = Array.Empty<byte>();

    /// <summary>Whether metadata has been received and parsed.</summary>
    public bool HasMetadata => Metadata != null;

    /// <summary>Whether all selected pieces have been downloaded and verified.</summary>
    public bool Done { get; private set; }

    /// <summary>Download progress (0.0 to 1.0).</summary>
    public double Progress => _pieceManager?.Progress ?? 0;

    /// <summary>Current download speed in bytes/sec.</summary>
    public double DownloadSpeed { get; private set; }

    /// <summary>Current upload speed in bytes/sec.</summary>
    public double UploadSpeed { get; private set; }

    /// <summary>Total bytes downloaded (verified).</summary>
    public long Downloaded { get; private set; }

    /// <summary>Total bytes uploaded.</summary>
    public long Uploaded { get; private set; }

    /// <summary>Connected peer count.</summary>
    public int PeerCount => _peers.Count;

    /// <summary>Bitfield of verified pieces.</summary>
    public bool[]? Bitfield => _pieceManager?.Bitfield;

    /// <summary>Files in this torrent (available after metadata).</summary>
    public TorrentFileStream[] Files { get; private set; } = Array.Empty<TorrentFileStream>();

    /// <summary>The piece manager (available after metadata is set).</summary>
    public PieceManager? PieceManager => _pieceManager;

    /// <summary>The download coordinator (available after metadata is set).</summary>
    public DownloadCoordinator? Coordinator => _coordinator;

    /// <summary>The chunk store (available after metadata is set).</summary>
    public IChunkStore? Store => _store;

    /// <summary>Whether the swarm is paused (not connecting to new peers).</summary>
    public bool Paused { get; private set; }

    // Events
    public event Action? OnReady;
    public event Action? OnDone;
    public event Action<long>? OnDownload;
    public event Action<long>? OnUpload;
    public event Action<int>? OnPieceVerified;
    public event Action<PeerConnection>? OnPeerConnect;
    public event Action<PeerConnection>? OnPeerDisconnect;
    public event Action<Exception>? OnError;

    public TorrentSwarm(WebTorrentClient client, AddTorrentOptions options)
    {
        _client = client;
        _options = options;
        Paused = options.Paused;
    }

    /// <summary>Initialize from magnet URI or info hash string.</summary>
    public Task InitializeAsync(string magnetOrInfoHash)
    {
        if (magnetOrInfoHash.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase))
        {
            var uri = new Uri(magnetOrInfoHash);
            var query = System.Web.HttpUtility.ParseQueryString(uri.Query.Length > 0 ? uri.Query : magnetOrInfoHash.Substring(magnetOrInfoHash.IndexOf('?')));

            var xt = query["xt"];
            if (xt != null && xt.StartsWith("urn:btih:"))
            {
                var hashStr = xt.Substring("urn:btih:".Length);
                InfoHash = hashStr.Length == 40
                    ? Convert.FromHexString(hashStr)
                    : throw new ArgumentException($"Invalid info hash length: {hashStr.Length}");
            }
        }
        else if (magnetOrInfoHash.Length == 40)
        {
            InfoHash = Convert.FromHexString(magnetOrInfoHash);
        }
        else
        {
            throw new ArgumentException("Expected magnet URI or 40-char hex info hash");
        }

        return Task.CompletedTask;
    }

    /// <summary>Set metadata (from .torrent file parse or ut_metadata exchange).</summary>
    public void SetMetadata(TorrentMetadata metadata)
    {
        if (Metadata != null) return; // already set

        Metadata = metadata;
        InfoHash = metadata.InfoHash;

        // Create chunk store
        _store = _options.StoreFactory?.Invoke(metadata.PieceLength)
            ?? new MemoryChunkStore(metadata.PieceLength);

        // Create piece manager
        _pieceManager = new PieceManager(metadata, _store);
        _pieceManager.OnPieceComplete += HandlePieceComplete;

        // Create download coordinator
        _coordinator = new DownloadCoordinator(_pieceManager, metadata);
        _coordinator.OnDownloadComplete += () =>
        {
            Done = true;
            OnDone?.Invoke();
        };
        _coordinator.OnError += (ex) => OnError?.Invoke(ex);

        // Create file stream abstractions
        Files = metadata.Files.Select(f => new TorrentFileStream(this, f, _store)).ToArray();

        // Add any already-connected peers to the coordinator
        foreach (var peer in _peers)
        {
            _coordinator.AddPeer(peer.Wire, peer.PeerBitfield);
        }

        OnReady?.Invoke();
    }

    /// <summary>Add a discovered peer and initiate connection.</summary>
    public void AddPeer(PeerInfo info)
    {
        if (Paused) return;
        if (_peers.Count >= 55) return;

        // Deduplicate
        if (!_knownPeerAddresses.Add(info.Address)) return;

        // Connect asynchronously — don't block the caller
        _ = ConnectToPeerAsync(info);
    }

    private async Task ConnectToPeerAsync(PeerInfo info)
    {
        try
        {
            // Find a suitable transport from the client
            var transport = FindTransport(info);
            if (transport == null) return;

            var conn = await transport.ConnectAsync(info.Address);

            // Perform BitTorrent handshake
            var wire = new WireProtocol(conn);
            await wire.SendHandshakeAsync(InfoHash, _client.PeerId);

            if (!await wire.ReceiveHandshakeAsync())
            {
                await conn.CloseAsync();
                return;
            }

            // Verify info hash matches
            if (wire.RemoteInfoHash == null || !wire.RemoteInfoHash.SequenceEqual(InfoHash))
            {
                await conn.CloseAsync();
                return;
            }

            await AddConnectedPeerAsync(wire, info);
        }
        catch (Exception ex)
        {
            OnError?.Invoke(new Exception($"Failed to connect to {info.Address}: {ex.Message}"));
        }
    }

    /// <summary>Add a peer that has already completed the handshake (from PeerCoordinator or incoming).</summary>
    public async Task AddConnectedPeerAsync(WireProtocol wire, PeerInfo info)
    {
        await _peerLock.WaitAsync();
        try
        {
            if (_peers.Count >= 55)
            {
                await wire.DisposeAsync();
                return;
            }

            var peer = new PeerConnection(wire, info);

            // Wire up events
            wire.OnBitfield += (bf) =>
            {
                peer.PeerBitfield = new bool[bf.Length * 8];
                for (int i = 0; i < bf.Length; i++)
                    for (int bit = 0; bit < 8; bit++)
                        if (i * 8 + bit < peer.PeerBitfield.Length)
                            peer.PeerBitfield[i * 8 + bit] = (bf[i] & (1 << (7 - bit))) != 0;

                // Add to coordinator if metadata is available
                _coordinator?.AddPeer(wire, peer.PeerBitfield);
            };

            wire.OnHave += (pieceIndex) =>
            {
                if (pieceIndex < peer.PeerBitfield.Length)
                    peer.PeerBitfield[pieceIndex] = true;
            };

            wire.OnRequest += async (pieceIndex, offset, length) =>
            {
                // Seeding: respond to piece requests
                if (_store != null && _pieceManager != null && _pieceManager.Bitfield[pieceIndex])
                {
                    var data = await _store.GetAsync(pieceIndex, offset, length);
                    if (data != null)
                    {
                        await wire.SendPieceAsync(pieceIndex, offset, data);
                        Uploaded += data.Length;
                        OnUpload?.Invoke(data.Length);
                    }
                }
            };

            _peers.Add(peer);
            OnPeerConnect?.Invoke(peer);

            // Send interested + unchoke
            await wire.SendMessageAsync(MessageType.Interested);
            await wire.SendMessageAsync(MessageType.Unchoke);

            // Send our bitfield if we have metadata and any pieces
            if (_pieceManager != null && _pieceManager.Bitfield.Any(b => b))
            {
                await wire.SendBitfieldAsync(BoolBitfieldToBytes(_pieceManager.Bitfield));
            }

            // Run the message read loop in background
            _ = RunPeerAsync(peer);
        }
        finally
        {
            _peerLock.Release();
        }
    }

    private async Task RunPeerAsync(PeerConnection peer)
    {
        try
        {
            await peer.Wire.RunAsync();
        }
        catch { }
        finally
        {
            await _peerLock.WaitAsync();
            try
            {
                _peers.Remove(peer);
                _knownPeerAddresses.Remove(peer.Info.Address);
            }
            finally
            {
                _peerLock.Release();
            }
            OnPeerDisconnect?.Invoke(peer);
        }
    }

    /// <summary>Convert bool[] bitfield to packed byte[] for wire protocol.</summary>
    private static byte[] BoolBitfieldToBytes(bool[] bitfield)
    {
        int byteCount = (bitfield.Length + 7) / 8;
        var bytes = new byte[byteCount];
        for (int i = 0; i < bitfield.Length; i++)
            if (bitfield[i])
                bytes[i / 8] |= (byte)(1 << (7 - (i % 8)));
        return bytes;
    }

    private Transports.ITransport? FindTransport(PeerInfo info)
    {
        // For now, use the first available transport
        // TCP addresses look like "ip:port", WebRTC peer IDs are hex strings
        // The transport selection can be made smarter later
        return null; // Peers come through PeerCoordinator which handles transport
    }

    private void HandlePieceComplete(int pieceIndex)
    {
        if (_pieceManager == null || Metadata == null) return;

        int pieceLength = (pieceIndex == Metadata.PieceCount - 1)
            ? (int)(Metadata.TotalLength - (long)pieceIndex * Metadata.PieceLength)
            : Metadata.PieceLength;

        Downloaded += pieceLength;
        OnPieceVerified?.Invoke(pieceIndex);
        OnDownload?.Invoke(pieceLength);

        // Notify all peers we have this piece
        foreach (var peer in _peers.ToArray())
        {
            _ = peer.Wire.SendHaveAsync(pieceIndex);
        }

        if (_pieceManager.IsComplete)
        {
            Done = true;
            OnDone?.Invoke();
        }
    }

    /// <summary>Start the download coordinator.</summary>
    public void StartDownload()
    {
        _coordinator?.Start();
    }

    /// <summary>Stop the download coordinator.</summary>
    public void StopDownload()
    {
        _coordinator?.Stop();
    }

    /// <summary>Pause — stop connecting to new peers.</summary>
    public void Pause()
    {
        Paused = true;
    }

    /// <summary>Resume — allow connecting to new peers.</summary>
    public void Resume()
    {
        Paused = false;
    }

    /// <summary>Add a web seed URL.</summary>
    public void AddWebSeed(string url)
    {
        _coordinator?.AddWebSeed(new HttpClient { Timeout = TimeSpan.FromSeconds(30) }, url);
    }

    public async ValueTask DisposeAsync()
    {
        _coordinator?.Stop();

        foreach (var peer in _peers.ToArray())
            await peer.DisposeAsync();
        _peers.Clear();

        if (_store != null)
            await _store.DisposeAsync();

        _peerLock.Dispose();
    }
}

/// <summary>
/// Wrapper around a peer connection with its wire protocol state.
/// </summary>
public class PeerConnection : IAsyncDisposable
{
    public WireProtocol Wire { get; }
    public PeerInfo Info { get; }
    public bool[] PeerBitfield { get; set; } = Array.Empty<bool>();
    public bool IsChoked { get; set; } = true;

    public PeerConnection(WireProtocol wire, PeerInfo info)
    {
        Wire = wire;
        Info = info;

        wire.OnChoke += () => IsChoked = true;
        wire.OnUnchoke += () => IsChoked = false;
    }

    public async ValueTask DisposeAsync()
    {
        await Wire.DisposeAsync();
    }
}

/// <summary>
/// Random-access file stream over torrent pieces.
/// Supports byte-range reads for ML model weight streaming.
/// </summary>
public class TorrentFileStream
{
    private readonly TorrentSwarm _swarm;
    private readonly TorrentFile _file;
    private readonly IChunkStore _store;

    public string Name => _file.Name;
    public string Path => _file.Path;
    public long Length => _file.Length;
    public long Offset => _file.Offset;

    /// <summary>Download progress for this specific file (0.0 to 1.0).</summary>
    public double Progress
    {
        get
        {
            var bitfield = _swarm.Bitfield;
            if (bitfield == null) return 0;
            int count = 0, total = _file.EndPiece - _file.StartPiece + 1;
            for (int i = _file.StartPiece; i <= _file.EndPiece; i++)
                if (bitfield[i]) count++;
            return total > 0 ? (double)count / total : 0;
        }
    }

    public TorrentFileStream(TorrentSwarm swarm, TorrentFile file, IChunkStore store)
    {
        _swarm = swarm;
        _file = file;
        _store = store;
    }

    /// <summary>
    /// Read bytes from this file at a specific offset.
    /// Blocks until the requested pieces are available.
    /// This is the key API for ML model weight streaming.
    /// </summary>
    public async Task<byte[]> ReadAsync(long offset, int length, CancellationToken ct = default)
    {
        if (_swarm.Metadata == null) throw new InvalidOperationException("Metadata not available");
        var meta = _swarm.Metadata;

        var result = new byte[length];
        int resultOffset = 0;
        long fileAbsOffset = _file.Offset + offset;

        while (resultOffset < length)
        {
            int pieceIndex = (int)(fileAbsOffset / meta.PieceLength);
            int pieceOffset = (int)(fileAbsOffset % meta.PieceLength);
            int bytesInPiece = Math.Min(meta.PieceLength - pieceOffset, length - resultOffset);

            // Wait for this piece to be available
            var bitfield = _swarm.Bitfield;
            while (bitfield != null && !bitfield[pieceIndex])
            {
                // Signal the coordinator to prioritize this piece
                _swarm.Coordinator?.Prioritize(pieceIndex);
                await Task.Delay(10, ct);
                bitfield = _swarm.Bitfield;
            }

            var pieceData = await _store.GetAsync(pieceIndex, pieceOffset, bytesInPiece, ct);
            if (pieceData != null)
            {
                Array.Copy(pieceData, 0, result, resultOffset, bytesInPiece);
            }

            resultOffset += bytesInPiece;
            fileAbsOffset += bytesInPiece;
        }

        return result;
    }
}
