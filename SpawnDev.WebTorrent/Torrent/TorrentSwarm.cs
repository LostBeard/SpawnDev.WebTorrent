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
    private IChunkStore? _store;

    /// <summary>Torrent metadata (available after initialization or metadata exchange).</summary>
    public TorrentMetadata? Metadata { get; private set; }

    /// <summary>20-byte info hash.</summary>
    public byte[] InfoHash { get; private set; } = Array.Empty<byte>();

    /// <summary>Whether metadata has been received and parsed.</summary>
    public bool HasMetadata => Metadata != null;

    /// <summary>Whether all selected pieces have been downloaded and verified.</summary>
    public bool Done { get; private set; }

    /// <summary>Download progress (0.0 to 1.0).</summary>
    public double Progress { get; private set; }

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
    public bool[]? Bitfield { get; private set; }

    /// <summary>Files in this torrent (available after metadata).</summary>
    public TorrentFileStream[] Files { get; private set; } = Array.Empty<TorrentFileStream>();

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
    }

    /// <summary>Initialize from magnet URI or info hash string.</summary>
    public Task InitializeAsync(string magnetOrInfoHash)
    {
        // Parse magnet URI: magnet:?xt=urn:btih:{infoHash}&dn={name}&tr={tracker}
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
            // Raw hex info hash
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
        Metadata = metadata;
        InfoHash = metadata.InfoHash;
        Bitfield = new bool[metadata.PieceCount];

        // Create chunk store
        _store = _options.StoreFactory?.Invoke(metadata.PieceLength)
            ?? new MemoryChunkStore(metadata.PieceLength);

        // Create file stream abstractions
        Files = metadata.Files.Select(f => new TorrentFileStream(this, f, _store)).ToArray();

        OnReady?.Invoke();
    }

    /// <summary>Add a discovered peer.</summary>
    public void AddPeer(PeerInfo info)
    {
        if (_peers.Count >= _client.Torrents.Count * 55) return; // max peers
        // TODO: connect to peer, perform handshake, add to _peers
    }

    /// <summary>Mark a piece as verified.</summary>
    internal void PieceVerified(int index, byte[] data)
    {
        if (Bitfield == null || Metadata == null) return;
        Bitfield[index] = true;
        Downloaded += data.Length;
        OnPieceVerified?.Invoke(index);
        OnDownload?.Invoke(data.Length);

        // Check if done
        int verified = Bitfield.Count(b => b);
        Progress = (double)verified / Metadata.PieceCount;
        if (verified == Metadata.PieceCount)
        {
            Done = true;
            OnDone?.Invoke();
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var peer in _peers.ToArray())
            await peer.DisposeAsync();
        _peers.Clear();

        if (_store != null)
            await _store.DisposeAsync();
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

    public PeerConnection(WireProtocol wire, PeerInfo info)
    {
        Wire = wire;
        Info = info;
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
            if (_swarm.Bitfield == null) return 0;
            int count = 0, total = _file.EndPiece - _file.StartPiece + 1;
            for (int i = _file.StartPiece; i <= _file.EndPiece; i++)
                if (_swarm.Bitfield[i]) count++;
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
            while (_swarm.Bitfield != null && !_swarm.Bitfield[pieceIndex])
            {
                // TODO: prioritize this piece for immediate download
                await Task.Delay(10, ct);
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
