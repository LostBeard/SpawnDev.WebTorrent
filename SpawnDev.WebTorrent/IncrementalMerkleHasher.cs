using System.Security.Cryptography;

namespace SpawnDev.WebTorrent;

/// <summary>
/// Streaming BEP 52 Merkle-tree hasher. Accepts data in any-sized chunks via
/// <see cref="Update"/>, and on <see cref="Finish"/> returns the same file root +
/// piece layer that <see cref="MerkleHasher.ComputeFileRoot"/> /
/// <see cref="MerkleHasher.ComputePieceLayer"/> would produce on the full concatenated
/// input.
///
/// Memory footprint is bounded per file: one 16 KiB leaf buffer, the current piece's
/// leaf hashes (at most <c>pieceSize / 16 KiB</c> 32-byte entries), and the growing
/// piece-root list (one 32-byte entry per completed piece). A 1 GiB file with 256 KiB
/// pieces needs ~128 KiB of piece-root state plus 512 B of current-piece leaf hashes.
///
/// Not thread-safe - a single hasher must be driven by one producer. Create one per
/// file.
/// </summary>
public sealed class IncrementalMerkleHasher
{
    private readonly int _pieceSize;
    private readonly int _leavesPerPiece;
    private readonly int _leavesPerPieceLog2;
    private readonly byte[] _currentLeaf = new byte[MerkleHasher.LeafSize];
    private int _currentLeafFill;
    private readonly List<byte[]> _currentPieceLeafHashes = new();
    private readonly List<byte[]> _pieceRoots = new();
    private long _totalBytes;
    private bool _finished;

    internal IncrementalMerkleHasher(int pieceSize)
    {
        // Same validation as MerkleHasher - piece size must be a power-of-two multiple of 16 KiB.
        if (pieceSize < MerkleHasher.LeafSize)
            throw new ArgumentOutOfRangeException(nameof(pieceSize), $"Piece size must be at least {MerkleHasher.LeafSize} bytes (16 KiB).");
        if (pieceSize % MerkleHasher.LeafSize != 0)
            throw new ArgumentOutOfRangeException(nameof(pieceSize), $"Piece size must be a multiple of {MerkleHasher.LeafSize}.");
        int leavesPerPiece = pieceSize / MerkleHasher.LeafSize;
        if ((leavesPerPiece & (leavesPerPiece - 1)) != 0)
            throw new ArgumentOutOfRangeException(nameof(pieceSize), "Piece size divided by leaf size must be a power of two.");

        _pieceSize = pieceSize;
        _leavesPerPiece = leavesPerPiece;
        int log = 0;
        while ((1 << log) < leavesPerPiece) log++;
        _leavesPerPieceLog2 = log;
    }

    /// <summary>Total bytes fed via <see cref="Update"/> so far.</summary>
    public long TotalBytesHashed => _totalBytes;

    /// <summary>Piece roots computed so far (excludes any in-progress partial piece).</summary>
    public int CompletedPieceCount => _pieceRoots.Count;

    /// <summary>
    /// Feed arbitrary bytes into the hasher. Internally accumulates leaves (16 KiB each)
    /// and rolls each completed piece up into a piece root, discarding the underlying
    /// leaf hashes to keep memory bounded.
    /// </summary>
    public void Update(ReadOnlySpan<byte> data)
    {
        if (_finished) throw new InvalidOperationException($"Cannot {nameof(Update)} after {nameof(Finish)}.");
        if (data.Length == 0) return;

        int pos = 0;
        while (pos < data.Length)
        {
            int toCopy = Math.Min(MerkleHasher.LeafSize - _currentLeafFill, data.Length - pos);
            data.Slice(pos, toCopy).CopyTo(_currentLeaf.AsSpan(_currentLeafFill));
            _currentLeafFill += toCopy;
            pos += toCopy;
            _totalBytes += toCopy;

            if (_currentLeafFill == MerkleHasher.LeafSize)
            {
                // Full leaf - hash it and add to current piece.
                _currentPieceLeafHashes.Add(SHA256.HashData(_currentLeaf));
                _currentLeafFill = 0;

                if (_currentPieceLeafHashes.Count == _leavesPerPiece)
                {
                    // Full piece - roll up to a piece root and clear the leaves to free memory.
                    _pieceRoots.Add(MerkleHasher.ComputeRoot(_currentPieceLeafHashes, level: 0));
                    _currentPieceLeafHashes.Clear();
                }
            }
        }
    }

    /// <summary>
    /// Finalize the hasher. Returns <c>(fileRoot, pieceLayer)</c> matching BEP 52
    /// <see cref="MerkleHasher.ComputeFileRoot"/> / <see cref="MerkleHasher.ComputePieceLayer"/>
    /// semantics. Once called the hasher cannot be updated further.
    /// </summary>
    public (byte[] fileRoot, byte[][] pieceLayer) Finish()
    {
        if (_finished) throw new InvalidOperationException($"{nameof(Finish)} may only be called once.");
        _finished = true;

        // 1. Finalize the partial last leaf, if any, with zero padding out to 16 KiB.
        if (_currentLeafFill > 0)
        {
            _currentLeaf.AsSpan(_currentLeafFill).Clear();
            _currentPieceLeafHashes.Add(SHA256.HashData(_currentLeaf));
            _currentLeafFill = 0;
        }

        // 2. Empty-file edge case. BEP 52 doesn't define a canonical empty-file root; we
        //    return the level-0 zero-pad hash for consistency with MerkleHasher.ComputeFileRoot.
        if (_totalBytes == 0)
        {
            return (MerkleHasher.PadHashAtLevel(0), Array.Empty<byte[]>());
        }

        // 3. Single-piece file (length <= piece size). BEP 52: piece layer is empty, file
        //    root is the Merkle hash of the file's leaves padded to the next power of two
        //    at the LEAF level (NOT padded to leavesPerPiece). The two sub-cases correspond
        //    to "exactly piece size" (piece root already computed during Update) and
        //    "less than piece size" (leaves still in the partial-piece buffer).
        if (_totalBytes <= _pieceSize)
        {
            byte[] fileRoot;
            if (_currentPieceLeafHashes.Count > 0)
            {
                int actualLeaves = _currentPieceLeafHashes.Count;
                fileRoot = actualLeaves == 1
                    ? _currentPieceLeafHashes[0]
                    : MerkleHasher.ComputeRoot(_currentPieceLeafHashes, level: 0);
            }
            else
            {
                // file length == pieceSize exactly; the single piece was rolled up during Update.
                fileRoot = _pieceRoots[0];
            }
            return (fileRoot, Array.Empty<byte[]>());
        }

        // 4. Multi-piece file. Finalize the last partial piece by padding the remaining
        //    leaf slots to leavesPerPiece with the level-0 zero-pad hash and computing the
        //    root over them. Then roll piece roots up into the file root at the piece level.
        if (_currentPieceLeafHashes.Count > 0)
        {
            var zeroLeaf = MerkleHasher.PadHashAtLevel(0);
            while (_currentPieceLeafHashes.Count < _leavesPerPiece)
                _currentPieceLeafHashes.Add(zeroLeaf);
            _pieceRoots.Add(MerkleHasher.ComputeRoot(_currentPieceLeafHashes, level: 0));
            _currentPieceLeafHashes.Clear();
        }

        var fileRootMulti = _pieceRoots.Count == 1
            ? _pieceRoots[0]
            : MerkleHasher.ComputeRoot(_pieceRoots, _leavesPerPieceLog2);
        return (fileRootMulti, _pieceRoots.ToArray());
    }
}
