using System.Security.Cryptography;
using SpawnDev.WebTorrent.Storage;

namespace SpawnDev.WebTorrent.Torrent;

/// <summary>
/// Manages piece lifecycle: selection, block tracking, verification, and storage.
/// The core download coordinator — decides what to request from which peer.
///
/// Piece states: Missing → Downloading (blocks tracked) → Verifying → Complete
/// Block size: 16KB (standard BitTorrent block size).
/// </summary>
public class PieceManager
{
    private readonly TorrentMetadata _metadata;
    private readonly IChunkStore _store;
    private readonly PieceState[] _pieces;
    private readonly BlazorJS.Cryptography.IPortableCrypto? _crypto;

    /// <summary>Standard block size: 16KB.</summary>
    public const int BlockSize = 16384;

    /// <summary>Total number of pieces.</summary>
    public int PieceCount => _pieces.Length;

    /// <summary>Number of verified (complete) pieces.</summary>
    public int CompletedCount { get; private set; }

    /// <summary>Whether all pieces are complete.</summary>
    public bool IsComplete => CompletedCount == PieceCount;

    /// <summary>Download progress (0.0 to 1.0).</summary>
    public double Progress => PieceCount > 0 ? (double)CompletedCount / PieceCount : 0;

    /// <summary>Bitfield of completed pieces (for sending to peers).</summary>
    public bool[] Bitfield { get; }

    // Events
    public event Action<int>? OnPieceComplete;
    public event Action<int, int>? OnBlockReceived; // pieceIndex, blockOffset

    public PieceManager(TorrentMetadata metadata, IChunkStore store,
        BlazorJS.Cryptography.IPortableCrypto? crypto = null)
    {
        _metadata = metadata;
        _store = store;
        _crypto = crypto;
        _pieces = new PieceState[metadata.PieceCount];
        Bitfield = new bool[metadata.PieceCount];

        for (int i = 0; i < _pieces.Length; i++)
        {
            int pieceLength = (i == _pieces.Length - 1)
                ? (int)(_metadata.TotalLength - (long)i * _metadata.PieceLength)
                : _metadata.PieceLength;
            int blockCount = (pieceLength + BlockSize - 1) / BlockSize;
            _pieces[i] = new PieceState(pieceLength, blockCount);
        }
    }

    /// <summary>
    /// Select a piece to request from a peer, given their bitfield.
    /// Uses rarest-first strategy by default.
    /// </summary>
    public int SelectPiece(bool[] peerBitfield, string strategy = "rarest")
    {
        // Find pieces the peer has that we don't
        var candidates = new List<int>();
        for (int i = 0; i < PieceCount; i++)
        {
            if (!Bitfield[i] && i < peerBitfield.Length && peerBitfield[i]
                && _pieces[i].State != DownloadState.Verifying)
            {
                candidates.Add(i);
            }
        }

        if (candidates.Count == 0) return -1;

        if (strategy == "sequential")
            return candidates[0]; // first missing piece

        // Rarest-first: for now, pick randomly (rarity tracking TBD)
        return candidates[Random.Shared.Next(candidates.Count)];
    }

    /// <summary>
    /// Get the next block to request from a piece.
    /// Returns (offset, length) or (-1, 0) if no blocks needed.
    /// </summary>
    public (int offset, int length) GetNextBlock(int pieceIndex)
    {
        var piece = _pieces[pieceIndex];
        if (piece.State == DownloadState.Complete) return (-1, 0);

        piece.State = DownloadState.Downloading;

        for (int b = 0; b < piece.BlockCount; b++)
        {
            if (!piece.BlockReceived[b] && !piece.BlockRequested[b])
            {
                piece.BlockRequested[b] = true;
                int offset = b * BlockSize;
                int length = Math.Min(BlockSize, piece.PieceLength - offset);
                return (offset, length);
            }
        }

        return (-1, 0); // all blocks requested or received
    }

    /// <summary>
    /// Process a received block. Returns true if the piece is now complete and verified.
    /// </summary>
    public async Task<bool> ReceiveBlockAsync(int pieceIndex, int offset, byte[] data)
    {
        if (pieceIndex < 0 || pieceIndex >= PieceCount) return false;
        var piece = _pieces[pieceIndex];
        if (piece.State == DownloadState.Complete) return false;

        int blockIdx = offset / BlockSize;
        if (blockIdx >= piece.BlockCount) return false;

        piece.BlockReceived[blockIdx] = true;
        piece.BlockData[blockIdx] = data;

        OnBlockReceived?.Invoke(pieceIndex, offset);

        // Check if all blocks received
        if (!piece.BlockReceived.All(b => b)) return false;

        // Assemble complete piece
        piece.State = DownloadState.Verifying;
        var pieceData = new byte[piece.PieceLength];
        int pos = 0;
        for (int b = 0; b < piece.BlockCount; b++)
        {
            var block = piece.BlockData[b];
            if (block == null) { piece.State = DownloadState.Missing; return false; }
            Array.Copy(block, 0, pieceData, pos, block.Length);
            pos += block.Length;
        }

        // Verify hash — use async crypto when available (native SubtleCrypto in browser)
        bool verified = _crypto != null
            ? await _metadata.VerifyPieceAsync(pieceIndex, pieceData, _crypto)
            : _metadata.VerifyPiece(pieceIndex, pieceData);

        if (verified)
        {
            // Store verified piece
            await _store.PutAsync(pieceIndex, pieceData);
            piece.State = DownloadState.Complete;
            piece.ClearBlockData(); // free memory
            Bitfield[pieceIndex] = true;
            CompletedCount++;
            OnPieceComplete?.Invoke(pieceIndex);
            return true;
        }
        else
        {
            // Hash mismatch — discard and re-request
            piece.Reset();
            return false;
        }
    }

    /// <summary>
    /// Receive a complete piece (all bytes at once). Used by web seed downloads
    /// where the entire piece arrives as a single HTTP range response.
    /// Bypasses block tracking — verifies hash and stores directly.
    /// </summary>
    public async Task<bool> ReceiveCompletePieceAsync(int pieceIndex, byte[] pieceData)
    {
        if (pieceIndex < 0 || pieceIndex >= PieceCount) return false;
        var piece = _pieces[pieceIndex];
        if (piece.State == DownloadState.Complete) return true;

        bool verified = _crypto != null
            ? await _metadata.VerifyPieceAsync(pieceIndex, pieceData, _crypto)
            : _metadata.VerifyPiece(pieceIndex, pieceData);

        if (verified)
        {
            await _store.PutAsync(pieceIndex, pieceData);
            piece.State = DownloadState.Complete;
            piece.ClearBlockData();
            Bitfield[pieceIndex] = true;
            CompletedCount++;
            OnPieceComplete?.Invoke(pieceIndex);
            return true;
        }
        else
        {
            piece.Reset();
            return false;
        }
    }

    /// <summary>
    /// Cancel a block request (e.g., peer disconnected).
    /// </summary>
    public void CancelBlock(int pieceIndex, int offset)
    {
        if (pieceIndex < 0 || pieceIndex >= PieceCount) return;
        int blockIdx = offset / BlockSize;
        var piece = _pieces[pieceIndex];
        if (blockIdx < piece.BlockCount)
            piece.BlockRequested[blockIdx] = false;
    }

    /// <summary>
    /// Mark a piece as already complete (e.g., from existing storage).
    /// </summary>
    public void MarkComplete(int pieceIndex)
    {
        if (pieceIndex < 0 || pieceIndex >= PieceCount) return;
        if (_pieces[pieceIndex].State == DownloadState.Complete) return; // idempotent
        _pieces[pieceIndex].State = DownloadState.Complete;
        Bitfield[pieceIndex] = true;
        CompletedCount++;
    }
}

/// <summary>Download state of a single piece.</summary>
public enum DownloadState
{
    Missing,
    Downloading,
    Verifying,
    Complete,
}

/// <summary>Tracks blocks within a piece during download.</summary>
public class PieceState
{
    public int PieceLength { get; }
    public int BlockCount { get; }
    public DownloadState State { get; set; } = DownloadState.Missing;
    public bool[] BlockReceived { get; }
    public bool[] BlockRequested { get; }
    public byte[]?[] BlockData { get; }

    public PieceState(int pieceLength, int blockCount)
    {
        PieceLength = pieceLength;
        BlockCount = blockCount;
        BlockReceived = new bool[blockCount];
        BlockRequested = new bool[blockCount];
        BlockData = new byte[blockCount][];
    }

    public void Reset()
    {
        State = DownloadState.Missing;
        Array.Clear(BlockReceived);
        Array.Clear(BlockRequested);
        Array.Clear(BlockData);
    }

    public void ClearBlockData()
    {
        Array.Clear(BlockData);
    }
}
