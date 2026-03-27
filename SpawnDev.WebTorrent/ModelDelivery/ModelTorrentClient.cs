using SpawnDev.WebTorrent.Storage;
using SpawnDev.WebTorrent.Torrent;
using SpawnDev.WebTorrent.Transports;

namespace SpawnDev.WebTorrent.ModelDelivery;

/// <summary>
/// High-level API for downloading ML model files via WebTorrent.
/// This is the integration point for SpawnDev.ILGPU.ML's ModelHub.
///
/// Usage:
///   var client = new ModelTorrentClient(options);
///   var stream = await client.GetModelStreamAsync("Xenova/distilgpt2", "onnx/model.onnx");
///   // stream supports random-access reads for weight loading
///
/// The client handles:
/// - Fetching .torrent metadata from the server
/// - Downloading pieces from peers + web seed fallback
/// - Random-access streaming for weight loading
/// - OPFS/disk caching for persistence across sessions
/// </summary>
public class ModelTorrentClient : IAsyncDisposable
{
    private readonly ModelTorrentOptions _options;
    private readonly HttpClient _httpClient;

    public ModelTorrentClient(ModelTorrentOptions? options = null)
    {
        _options = options ?? new ModelTorrentOptions();
        _httpClient = new HttpClient();
    }

    /// <summary>
    /// Download a model file and return its bytes.
    /// Uses web seed (HTTP range requests) for immediate availability.
    /// Pieces are cached for P2P seeding to other clients.
    /// </summary>
    public async Task<byte[]> DownloadModelAsync(string repoId, string filePath,
        IProgress<double>? progress = null, CancellationToken ct = default)
    {
        // 1. Get .torrent metadata from server
        var torrentUrl = $"{_options.ServerBaseUrl}/torrent/{repoId}/{filePath}";
        byte[] torrentBytes;
        try
        {
            torrentBytes = await _httpClient.GetByteArrayAsync(torrentUrl, ct);
        }
        catch (HttpRequestException)
        {
            // Fallback: direct download from HuggingFace
            var hfUrl = $"https://huggingface.co/{repoId}/resolve/main/{filePath}";
            return await _httpClient.GetByteArrayAsync(hfUrl, ct);
        }

        // 2. Parse torrent metadata
        var metadata = TorrentParser.Parse(torrentBytes);

        // 3. Create chunk store and piece manager
        await using var store = _options.StoreFactory?.Invoke(metadata.PieceLength)
            ?? new MemoryChunkStore(metadata.PieceLength);
        var pieceManager = new PieceManager(metadata, store);

        // 4. Download all pieces via web seed
        var webSeed = new WebSeedConnection(_httpClient,
            $"{_options.ServerBaseUrl}/hf/{repoId}", metadata);

        int totalPieces = metadata.PieceCount;
        for (int i = 0; i < totalPieces; i++)
        {
            var pieceData = await webSeed.DownloadPieceAsync(i, ct);
            if (pieceData != null)
            {
                pieceManager.GetNextBlock(i); // mark as requested
                await pieceManager.ReceiveBlockAsync(i, 0, pieceData);
            }
            progress?.Report((double)(i + 1) / totalPieces);
        }

        // 5. Assemble complete file from stored pieces
        var result = new byte[metadata.TotalLength];
        long offset = 0;
        for (int i = 0; i < totalPieces; i++)
        {
            var piece = await store.GetAsync(i);
            if (piece == null)
                throw new InvalidOperationException($"Piece {i} missing after download");
            int len = (int)Math.Min(piece.Length, metadata.TotalLength - offset);
            Array.Copy(piece, 0, result, offset, len);
            offset += len;
        }

        return result;
    }

    /// <summary>
    /// Get a random-access stream for a model file.
    /// Pieces download on demand as the stream is read.
    /// </summary>
    public async Task<ModelStream> GetModelStreamAsync(string repoId, string filePath,
        CancellationToken ct = default)
    {
        // Get .torrent metadata
        var torrentUrl = $"{_options.ServerBaseUrl}/torrent/{repoId}/{filePath}";
        var torrentBytes = await _httpClient.GetByteArrayAsync(torrentUrl, ct);
        var metadata = TorrentParser.Parse(torrentBytes);

        var store = _options.StoreFactory?.Invoke(metadata.PieceLength)
            ?? new MemoryChunkStore(metadata.PieceLength);
        var pieceManager = new PieceManager(metadata, store);

        var webSeed = new WebSeedConnection(_httpClient,
            $"{_options.ServerBaseUrl}/hf/{repoId}", metadata);

        return new ModelStream(metadata, store, pieceManager, webSeed);
    }

    /// <summary>
    /// Get magnet URI for a model file (for sharing with other clients).
    /// </summary>
    public async Task<string?> GetMagnetUriAsync(string repoId, string filePath,
        CancellationToken ct = default)
    {
        try
        {
            var magnetUrl = $"{_options.ServerBaseUrl}/magnet/{repoId}/{filePath}";
            var json = await _httpClient.GetStringAsync(magnetUrl, ct);
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            return doc.RootElement.GetProperty("magnetUri").GetString();
        }
        catch
        {
            return null;
        }
    }

    public ValueTask DisposeAsync()
    {
        _httpClient.Dispose();
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// Random-access stream over a torrent file.
/// Pieces download on demand when read. Supports seeking.
/// </summary>
public class ModelStream : IAsyncDisposable
{
    private readonly TorrentMetadata _metadata;
    private readonly IChunkStore _store;
    private readonly PieceManager _pieceManager;
    private readonly WebSeedConnection _webSeed;

    public long Length => _metadata.TotalLength;
    public int PieceLength => _metadata.PieceLength;
    public int PieceCount => _metadata.PieceCount;

    public ModelStream(TorrentMetadata metadata, IChunkStore store,
        PieceManager pieceManager, WebSeedConnection webSeed)
    {
        _metadata = metadata;
        _store = store;
        _pieceManager = pieceManager;
        _webSeed = webSeed;
    }

    /// <summary>
    /// Read bytes at a specific offset. Downloads pieces on demand.
    /// This is the API that ModelHub's weight loader calls.
    /// </summary>
    public async Task<byte[]> ReadAsync(long offset, int length, CancellationToken ct = default)
    {
        var result = new byte[length];
        int resultPos = 0;

        while (resultPos < length)
        {
            int pieceIndex = (int)(offset / _metadata.PieceLength);
            int pieceOffset = (int)(offset % _metadata.PieceLength);
            int bytesInPiece = Math.Min(_metadata.PieceLength - pieceOffset, length - resultPos);

            // Ensure piece is downloaded
            if (!_pieceManager.Bitfield[pieceIndex])
            {
                var pieceData = await _webSeed.DownloadPieceAsync(pieceIndex, ct);
                if (pieceData != null)
                {
                    _pieceManager.GetNextBlock(pieceIndex);
                    await _pieceManager.ReceiveBlockAsync(pieceIndex, 0, pieceData);
                }
            }

            // Read from store
            var data = await _store.GetAsync(pieceIndex, pieceOffset, bytesInPiece, ct);
            if (data != null)
                Array.Copy(data, 0, result, resultPos, bytesInPiece);

            resultPos += bytesInPiece;
            offset += bytesInPiece;
        }

        return result;
    }

    public async ValueTask DisposeAsync()
    {
        await _store.DisposeAsync();
    }
}

/// <summary>Configuration for ML model torrent delivery.</summary>
public class ModelTorrentOptions
{
    /// <summary>Base URL of the SpawnDev.WebTorrent server.</summary>
    public string ServerBaseUrl { get; set; } = "https://localhost:5560";

    /// <summary>Custom chunk store factory (default: MemoryChunkStore).</summary>
    public Func<int, IChunkStore>? StoreFactory { get; set; }
}
