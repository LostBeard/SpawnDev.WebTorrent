using System.Security.Cryptography;

namespace SpawnDev.WebTorrent;

/// <summary>
/// BEP 52 Merkle-proof verifier. Given a file's <c>pieces root</c>, the range of hashes
/// delivered at a particular tree layer (e.g., by a BEP 52 peer "hashes" message), and
/// the sibling-hash "proof" path climbing toward the root, reconstructs the root hash
/// and reports whether it matches the expected value.
///
/// Spec references (BEP 52 §"Protocol extension", fetched 2026-04-23):
/// - "The base layer defines the lowest requested layer of the hash tree. It is the
///   number of layers above the leaf layer that the hash list should start at. A value
///   of zero indicates that leaf hashes are requested."  =&gt; layer 0 = leaves.
/// - "Hashes starts with the base layer and ends with the uncle hash closest to the
///   root."  =&gt; proof hashes are ordered bottom-up (leaf side first).
/// - "Index MUST be a multiple of length" and "Length MUST be equal-to-or-greater-than
///   two and a power of two."
/// - "A proof layer is omitted if the requested hashes include the entire child layer.
///   In other words, the first log2(length)-1 proof layers are omitted."
///
/// The verification algorithm:
///   1. Pair-reduce the <paramref name="baseLayerHashes"/> log2(length) times to reach a
///      single combined hash at layer <c>baseLayer + log2(length)</c>. No external
///      siblings are needed for these reductions - they are pure internal combinations
///      of the received range.
///   2. For each hash in <paramref name="proofHashes"/>, combine with the current climbing
///      hash by choosing left/right placement based on the parity of the current index
///      at that level, then right-shift the index to move up a layer.
///   3. The final hash after all proof hashes have been consumed must equal
///      <paramref name="piecesRoot"/>.
///
/// This is a PURE FUNCTION with no networking, state, or BitTorrent-session awareness.
/// The wire dispatch and state machine that plugs it into piece verification during
/// download is a separate Phase 2c step-2 deliverable (see <c>Plans/bep52-phase2-
/// execution.md</c>).
/// </summary>
public static class MerkleProofVerifier
{
    /// <summary>
    /// Verify a BEP 52 Merkle proof.
    /// </summary>
    /// <param name="piecesRoot">32-byte SHA-256 root hash for the file.</param>
    /// <param name="index">Index within <paramref name="baseLayerHashes"/>'s tree layer
    /// where the range starts. Must be a multiple of
    /// <paramref name="baseLayerHashes"/>.Length.</param>
    /// <param name="baseLayerHashes">The hashes at the base layer, in order. Length MUST
    /// be &gt;= 2 and a power of two (per spec), OR exactly 1 for the "whole tree is one
    /// hash" edge case (single-piece file whose root is the pieces root directly).</param>
    /// <param name="proofHashes">Sibling uncles climbing from the received range toward
    /// the root, bottom-up. Can be empty when the range covers the entire layer under
    /// pieces root (no siblings needed).</param>
    /// <returns><c>true</c> iff the reconstructed hash at the root level equals
    /// <paramref name="piecesRoot"/> byte-for-byte.</returns>
    public static bool Verify(
        byte[] piecesRoot,
        ulong index,
        byte[][] baseLayerHashes,
        byte[][] proofHashes)
    {
        if (piecesRoot == null || piecesRoot.Length != MerkleHasher.HashSize)
            throw new ArgumentException($"piecesRoot must be exactly {MerkleHasher.HashSize} bytes.", nameof(piecesRoot));
        if (baseLayerHashes == null) throw new ArgumentNullException(nameof(baseLayerHashes));
        if (proofHashes == null) throw new ArgumentNullException(nameof(proofHashes));

        int length = baseLayerHashes.Length;
        if (length == 0) return false;
        // Spec permits length = 1 only at the trivial boundary (single piece = root).
        // Otherwise length must be power-of-two and >= 2.
        if (length > 1 && (length & (length - 1)) != 0) return false;
        if (index % (ulong)length != 0) return false;

        foreach (var h in baseLayerHashes)
        {
            if (h == null || h.Length != MerkleHasher.HashSize) return false;
        }
        foreach (var h in proofHashes)
        {
            if (h == null || h.Length != MerkleHasher.HashSize) return false;
        }

        // Internal pair-reduction: collapse the baseLayerHashes range into a single hash.
        // This climbs log2(length) layers purely from the received data.
        var current = baseLayerHashes;
        ulong currentIndex = index;
        while (current.Length > 1)
        {
            var next = new byte[current.Length / 2][];
            for (int i = 0; i < next.Length; i++)
            {
                next[i] = HashPair(current[2 * i], current[2 * i + 1]);
            }
            current = next;
            currentIndex /= 2;
        }

        byte[] climbing = current[0];

        // External climb: apply each proof hash as a sibling, placing it left or right
        // based on the parity of the current index at that level.
        foreach (var sibling in proofHashes)
        {
            bool currentIsRight = (currentIndex & 1) == 1;
            climbing = currentIsRight
                ? HashPair(sibling, climbing)
                : HashPair(climbing, sibling);
            currentIndex /= 2;
        }

        return climbing.AsSpan().SequenceEqual(piecesRoot);
    }

    /// <summary>
    /// Convenience overload accepting a <see cref="Bep52WireMessages.Hashes"/> message.
    /// Splits the message's flat hash list into the Length base-layer hashes and the
    /// ProofLayers proof hashes in their documented order ("base layer first, uncle
    /// hashes toward the root after") before delegating to <see cref="Verify(byte[], ulong, byte[][], byte[][])"/>.
    /// </summary>
    public static bool Verify(Bep52WireMessages.Hashes msg)
    {
        int length = (int)msg.Length;
        int proof = (int)msg.ProofLayers;
        if (msg.HashList.Length != length + proof) return false;

        var baseLayerHashes = new byte[length][];
        Array.Copy(msg.HashList, 0, baseLayerHashes, 0, length);

        var proofHashes = new byte[proof][];
        Array.Copy(msg.HashList, length, proofHashes, 0, proof);

        return Verify(msg.PiecesRoot, msg.Index, baseLayerHashes, proofHashes);
    }

    private static byte[] HashPair(byte[] left, byte[] right)
    {
        Span<byte> concat = stackalloc byte[MerkleHasher.HashSize * 2];
        left.AsSpan().CopyTo(concat);
        right.AsSpan().CopyTo(concat[MerkleHasher.HashSize..]);
        return SHA256.HashData(concat);
    }
}
