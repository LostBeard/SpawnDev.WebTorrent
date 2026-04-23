using System.Security.Cryptography;

namespace SpawnDev.WebTorrent;

/// <summary>
/// BEP 52 (BitTorrent v2) Merkle tree hasher. Computes piece-layer and file-root hashes
/// for v2 torrents using SHA-256 over 16 KiB leaves. All operations are pure and thread-safe.
///
/// Spec: https://www.bittorrent.org/beps/bep_0052.html
///
/// Key invariants:
/// - Leaf size is always 16 KiB (<see cref="LeafSize"/>), regardless of piece size.
/// - Piece size must be a power-of-two multiple of the leaf size (16 KiB, 32 KiB, 64 KiB, 128 KiB, ...).
/// - Final leaf of a file is zero-padded to 16 KiB if the file does not end on a leaf boundary.
/// - Non-power-of-two leaf counts are padded to the next power of two using zero-propagated
///   pad hashes (see <see cref="PadHashAtLevel"/>).
/// </summary>
public static class MerkleHasher
{
    /// <summary>BEP 52 leaf size: 16 KiB.</summary>
    public const int LeafSize = 16384;

    /// <summary>SHA-256 digest length in bytes.</summary>
    public const int HashSize = 32;

    private static readonly byte[][] _padHashCache = new byte[64][]; // supports trees up to 2^64 leaves, way beyond practical

    /// <summary>
    /// Returns the canonical zero-pad hash for the given Merkle-tree level. Level 0 is the leaf
    /// level (SHA-256 of 16 KiB of zero bytes). Each higher level is SHA-256 of two copies of
    /// the previous level's pad hash concatenated. Values are memoized.
    /// </summary>
    public static byte[] PadHashAtLevel(int level)
    {
        if (level < 0) throw new ArgumentOutOfRangeException(nameof(level));
        if (level >= _padHashCache.Length) throw new ArgumentOutOfRangeException(nameof(level), "Tree level beyond practical range.");

        var cached = Volatile.Read(ref _padHashCache[level]);
        if (cached != null) return cached;

        byte[] computed;
        if (level == 0)
        {
            computed = SHA256.HashData(new byte[LeafSize]);
        }
        else
        {
            var below = PadHashAtLevel(level - 1);
            var concat = new byte[HashSize * 2];
            Buffer.BlockCopy(below, 0, concat, 0, HashSize);
            Buffer.BlockCopy(below, 0, concat, HashSize, HashSize);
            computed = SHA256.HashData(concat);
        }

        Volatile.Write(ref _padHashCache[level], computed);
        return computed;
    }

    /// <summary>
    /// Hashes a single 16 KiB leaf. If <paramref name="leafContent"/> is shorter than 16 KiB
    /// (only valid for the last leaf of a file), the remainder is zero-padded before hashing.
    /// </summary>
    public static byte[] HashLeaf(ReadOnlySpan<byte> leafContent)
    {
        if (leafContent.Length > LeafSize)
            throw new ArgumentException($"Leaf content exceeds {LeafSize} bytes.", nameof(leafContent));
        if (leafContent.Length == LeafSize)
            return SHA256.HashData(leafContent);

        Span<byte> padded = stackalloc byte[LeafSize];
        leafContent.CopyTo(padded);
        // Remaining bytes are already zero from stackalloc.
        return SHA256.HashData(padded);
    }

    /// <summary>
    /// Computes the Merkle root of a list of hashes, treating them as leaves at the given tree
    /// level. Pads the list to the next power of two using the level's zero-pad hash, then
    /// combines pairwise with SHA-256 until a single root remains.
    /// </summary>
    /// <param name="hashes">Input hashes, each exactly <see cref="HashSize"/> bytes.</param>
    /// <param name="level">The Merkle-tree level at which these hashes live. Level 0 means
    /// the inputs are leaf hashes (SHA-256 of 16 KiB blocks). A higher level is appropriate
    /// when the inputs are already piece-layer roots (level = log2(piece_size / leaf_size)).</param>
    public static byte[] ComputeRoot(IReadOnlyList<byte[]> hashes, int level)
    {
        if (hashes == null) throw new ArgumentNullException(nameof(hashes));
        if (hashes.Count == 0) throw new ArgumentException("At least one hash is required.", nameof(hashes));
        foreach (var h in hashes)
        {
            if (h == null || h.Length != HashSize)
                throw new ArgumentException($"All hashes must be exactly {HashSize} bytes.", nameof(hashes));
        }

        // Copy into a mutable working buffer padded to the next power of two at this level.
        int n = 1;
        while (n < hashes.Count) n <<= 1;

        var current = new byte[n][];
        for (int i = 0; i < hashes.Count; i++) current[i] = hashes[i];
        if (hashes.Count < n)
        {
            var padHash = PadHashAtLevel(level);
            for (int i = hashes.Count; i < n; i++) current[i] = padHash;
        }

        int currentLevel = level;
        while (current.Length > 1)
        {
            int half = current.Length / 2;
            var next = new byte[half][];
            var pair = new byte[HashSize * 2];
            for (int i = 0; i < half; i++)
            {
                Buffer.BlockCopy(current[2 * i], 0, pair, 0, HashSize);
                Buffer.BlockCopy(current[2 * i + 1], 0, pair, HashSize, HashSize);
                next[i] = SHA256.HashData(pair);
            }
            current = next;
            currentLevel++;
        }

        return current[0];
    }

    /// <summary>
    /// Computes the per-piece Merkle-root hashes (the "piece layer") for a file of the given
    /// content and piece size. Each piece contains exactly <c>pieceSize / 16 KiB</c> leaves;
    /// the final piece may cover less than <paramref name="pieceSize"/> bytes of actual file
    /// content (the last leaf is zero-padded to 16 KiB, and any remaining leaf slots in the
    /// final piece are filled with the level-0 zero-pad hash before the piece root is computed).
    /// </summary>
    /// <param name="fileContent">The raw file bytes.</param>
    /// <param name="pieceSize">Piece size in bytes. Must be a power-of-two multiple of
    /// <see cref="LeafSize"/>.</param>
    /// <returns>One hash per piece, in order.</returns>
    public static byte[][] ComputePieceLayer(ReadOnlySpan<byte> fileContent, int pieceSize)
    {
        ValidatePieceSize(pieceSize);
        int leavesPerPiece = pieceSize / LeafSize;
        int leafLevelLog2 = Log2(leavesPerPiece);

        if (fileContent.Length == 0) return Array.Empty<byte[]>();

        long totalPieces = (fileContent.Length + pieceSize - 1L) / pieceSize;
        var result = new byte[totalPieces][];

        for (long pieceIdx = 0; pieceIdx < totalPieces; pieceIdx++)
        {
            long pieceOffset = pieceIdx * pieceSize;
            long pieceEnd = Math.Min(pieceOffset + pieceSize, fileContent.Length);
            int pieceLen = (int)(pieceEnd - pieceOffset);

            // Leaves that have actual file content in this piece.
            int actualLeaves = (pieceLen + LeafSize - 1) / LeafSize;

            var pieceLeafHashes = new byte[leavesPerPiece][];
            for (int li = 0; li < actualLeaves; li++)
            {
                int leafOffset = li * LeafSize;
                int leafLen = Math.Min(LeafSize, pieceLen - leafOffset);
                pieceLeafHashes[li] = HashLeaf(fileContent.Slice((int)pieceOffset + leafOffset, leafLen));
            }
            // Pad any empty leaf slots within this piece with level-0 zero-pad hash.
            if (actualLeaves < leavesPerPiece)
            {
                var zeroLeaf = PadHashAtLevel(0);
                for (int li = actualLeaves; li < leavesPerPiece; li++) pieceLeafHashes[li] = zeroLeaf;
            }

            result[pieceIdx] = leavesPerPiece == 1
                ? pieceLeafHashes[0]
                : ComputeRoot(pieceLeafHashes, level: 0);
        }

        return result;
    }

    /// <summary>
    /// Computes the file Merkle root for BEP 52. If the file fits in a single piece (or less),
    /// the root is the Merkle root of its 16 KiB leaves (level-0 padding). Otherwise the root
    /// is the Merkle root of the piece-layer hashes (pieced-level padding).
    /// </summary>
    public static byte[] ComputeFileRoot(ReadOnlySpan<byte> fileContent, int pieceSize)
    {
        ValidatePieceSize(pieceSize);

        if (fileContent.Length == 0)
        {
            // By convention v2 represents an empty file as the leaf-level zero-pad hash.
            // (BEP 52 doesn't include zero-length files in the file tree, but defining a
            // sensible root here makes the helper total.)
            return (byte[])PadHashAtLevel(0).Clone();
        }

        if (fileContent.Length <= pieceSize)
        {
            // Single piece. File root is the Merkle root over the file's leaves, padded to
            // a power of two at level 0.
            int actualLeaves = (fileContent.Length + LeafSize - 1) / LeafSize;
            var leaves = new byte[actualLeaves][];
            for (int li = 0; li < actualLeaves; li++)
            {
                int leafOffset = li * LeafSize;
                int leafLen = Math.Min(LeafSize, fileContent.Length - leafOffset);
                leaves[li] = HashLeaf(fileContent.Slice(leafOffset, leafLen));
            }
            return actualLeaves == 1 ? leaves[0] : ComputeRoot(leaves, level: 0);
        }

        // Multi-piece file. Compute the piece layer, then take the Merkle root of those hashes
        // at the piece-layer level.
        var pieceRoots = ComputePieceLayer(fileContent, pieceSize);
        int leavesPerPiece = pieceSize / LeafSize;
        int pieceLevel = Log2(leavesPerPiece);
        return pieceRoots.Length == 1 ? pieceRoots[0] : ComputeRoot(pieceRoots, level: pieceLevel);
    }

    /// <summary>
    /// Create a new incremental BEP 52 Merkle hasher for streaming input. The hasher
    /// accepts data in any-sized chunks via <see cref="IncrementalMerkleHasher.Update"/>
    /// and produces the same file root + piece layer that
    /// <see cref="ComputeFileRoot"/> / <see cref="ComputePieceLayer"/> would produce on
    /// the full concatenation of those chunks. Memory usage is bounded (one piece worth
    /// of leaf hashes + the per-piece root list), suitable for multi-GiB files.
    /// </summary>
    public static IncrementalMerkleHasher CreateIncremental(int pieceSize) => new IncrementalMerkleHasher(pieceSize);

    private static void ValidatePieceSize(int pieceSize)
    {
        if (pieceSize < LeafSize)
            throw new ArgumentOutOfRangeException(nameof(pieceSize), $"Piece size must be at least {LeafSize} bytes (16 KiB).");
        if (pieceSize % LeafSize != 0)
            throw new ArgumentOutOfRangeException(nameof(pieceSize), $"Piece size must be a multiple of {LeafSize}.");
        int leavesPerPiece = pieceSize / LeafSize;
        if ((leavesPerPiece & (leavesPerPiece - 1)) != 0)
            throw new ArgumentOutOfRangeException(nameof(pieceSize), "Piece size divided by leaf size must be a power of two.");
    }

    private static int Log2(int powerOfTwo)
    {
        int log = 0;
        while ((1 << log) < powerOfTwo) log++;
        return log;
    }
}
