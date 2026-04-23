using System.Security.Cryptography;

namespace SpawnDev.WebTorrent;

/// <summary>
/// BEP 52 Merkle-proof builder. Produces the <c>hashes</c>-message payload (base-layer
/// range + uncle-proof list) that answers a peer's <c>hash_request</c> from the full
/// layer of hashes we hold for a given file root.
///
/// Symmetric counterpart to <see cref="MerkleProofVerifier"/>: where the verifier takes
/// <c>(pieces_root, index, baseLayerHashes, proofHashes)</c> and re-climbs to the root,
/// the builder takes <c>(fullLayerHashes, baseLayerLevel, index, length, proofLayers)</c>
/// and emits exactly the <c>baseLayerHashes</c> + <c>proofHashes</c> pair that the
/// verifier would accept. The algorithm is the exact inverse:
///
///   1. Pad <paramref name="fullLayerHashes"/> at its tree level to the next power of two
///      using <see cref="MerkleHasher.PadHashAtLevel"/>.
///   2. Slice out the L hashes at positions <c>[index .. index+length-1]</c> as the base-
///      layer payload (caller will verify-reduce these to one hash internally).
///   3. Iteratively halve the padded layer upward (pure pair hashing), tracking the
///      position of the request block. At each level, once we've crossed the internal-
///      reduction boundary, emit the sibling at <c>groupIndex ^ 1</c> as a proof hash.
///   4. After <paramref name="proofLayers"/> external climbs we must have reached the root.
///
/// The returned base+proof arrays are ready to drop into a
/// <see cref="Bep52WireMessages.Hashes"/> message by concatenating
/// <c>[baseLayer... proof...]</c>, matching the wire format documented on
/// <see cref="MerkleProofVerifier.Verify(Bep52WireMessages.Hashes)"/>.
///
/// Pure function, no I/O, no BitTorrent-session awareness. Integration glue that calls
/// this from a peer <c>hash_request</c> handler lives in <c>Torrent.Download.cs</c>.
/// </summary>
public static class MerkleProofBuilder
{
    /// <summary>
    /// Build the base-layer + proof lists that answer a <c>hash_request</c> for the
    /// given range. Returns <c>null</c> if the parameters are illegal or if the
    /// reconstructed root does not match <paramref name="expectedRoot"/> (defensive
    /// self-check - prevents sending a cryptographically inconsistent response).
    /// </summary>
    /// <param name="fullLayerHashes">Every hash we hold at <paramref name="baseLayerLevel"/>
    /// for this file, in tree order. For a piece-layer response this is the concatenated
    /// piece-layer hashes unpacked into one byte[] per piece.</param>
    /// <param name="baseLayerLevel">Tree level of <paramref name="fullLayerHashes"/>. 0 is
    /// the leaf level; piece-layer level is <c>log2(pieceSize / leafSize)</c>.</param>
    /// <param name="index">Tree-layer index where the requested range begins. Must be a
    /// multiple of <paramref name="length"/>.</param>
    /// <param name="length">Number of base-layer hashes requested. Must be >= 2, a power
    /// of two, and <c>index + length &lt;= paddedLayerSize</c>.</param>
    /// <param name="proofLayers">Number of proof hashes the caller expects. Must equal
    /// <c>log2(paddedLayerSize) - log2(length)</c>.</param>
    /// <param name="expectedRoot">Expected file root. Self-check that our reconstructed
    /// climb produces this value before returning anything; guards against stale/wrong
    /// input data.</param>
    /// <returns>Tuple (base-layer slice, proof siblings) ready for
    /// <see cref="Bep52WireMessages.Hashes"/> construction, or <c>null</c> if the request
    /// is malformed or our data is inconsistent.</returns>
    public static (byte[][] baseLayer, byte[][] proof)? Build(
        IReadOnlyList<byte[]> fullLayerHashes,
        int baseLayerLevel,
        ulong index,
        int length,
        int proofLayers,
        byte[] expectedRoot)
    {
        if (fullLayerHashes == null || fullLayerHashes.Count == 0) return null;
        if (expectedRoot == null || expectedRoot.Length != MerkleHasher.HashSize) return null;
        if (length < 2 || (length & (length - 1)) != 0) return null;
        if (proofLayers < 0) return null;
        if (baseLayerLevel < 0) return null;
        if (index % (ulong)length != 0) return null;

        // Pad layer to next power of two with the level's canonical pad hash.
        int paddedCount = 1;
        while (paddedCount < fullLayerHashes.Count) paddedCount <<= 1;
        if (paddedCount < length) return null;
        if (index + (ulong)length > (ulong)paddedCount) return null;

        // proofLayers must exactly land us at the root.
        int treeDepth = IntLog2(paddedCount);
        int internalClimbs = IntLog2(length);
        if (treeDepth - internalClimbs != proofLayers) return null;

        var padHash = MerkleHasher.PadHashAtLevel(baseLayerLevel);
        var layer = new byte[paddedCount][];
        for (int i = 0; i < fullLayerHashes.Count; i++)
        {
            var h = fullLayerHashes[i];
            if (h == null || h.Length != MerkleHasher.HashSize) return null;
            layer[i] = h;
        }
        for (int i = fullLayerHashes.Count; i < paddedCount; i++) layer[i] = padHash;

        // Extract the base-layer payload from the padded layer.
        var baseLayer = new byte[length][];
        for (int i = 0; i < length; i++) baseLayer[i] = layer[(int)index + i];

        // Climb internally through log2(length) levels (no proof hashes emitted - the
        // sibling at each level is already part of the base-layer payload).
        var currentLayer = layer;
        long currentGroupIndex = (long)index;
        int currentLevel = baseLayerLevel;

        for (int c = 0; c < internalClimbs; c++)
        {
            currentLayer = ReduceLayer(currentLayer, currentLevel);
            currentGroupIndex /= 2;
            currentLevel++;
        }

        // External climb: each level emits the sibling of our climbing node, then
        // combines and advances upward.
        var proof = new byte[proofLayers][];
        for (int p = 0; p < proofLayers; p++)
        {
            long siblingIdx = currentGroupIndex ^ 1L;
            if (siblingIdx < 0 || siblingIdx >= currentLayer.Length) return null;
            proof[p] = currentLayer[(int)siblingIdx];

            currentLayer = ReduceLayer(currentLayer, currentLevel);
            currentGroupIndex /= 2;
            currentLevel++;
        }

        if (currentLayer.Length != 1) return null;
        if (!currentLayer[0].AsSpan().SequenceEqual(expectedRoot)) return null;

        return (baseLayer, proof);
    }

    private static byte[][] ReduceLayer(byte[][] layer, int level)
    {
        // If layer has odd count we implicitly pair the last element with the level pad
        // hash. (Shouldn't happen for already-padded layers, but defensive.)
        int n = layer.Length;
        var next = new byte[(n + 1) / 2][];
        var pad = MerkleHasher.PadHashAtLevel(level);
        for (int i = 0; i < next.Length; i++)
        {
            byte[] left = layer[2 * i];
            byte[] right = 2 * i + 1 < n ? layer[2 * i + 1] : pad;
            next[i] = HashPair(left, right);
        }
        return next;
    }

    private static byte[] HashPair(byte[] left, byte[] right)
    {
        Span<byte> buf = stackalloc byte[MerkleHasher.HashSize * 2];
        left.AsSpan().CopyTo(buf);
        right.AsSpan().CopyTo(buf[MerkleHasher.HashSize..]);
        return SHA256.HashData(buf);
    }

    private static int IntLog2(int powerOfTwo)
    {
        int log = 0;
        while ((1 << log) < powerOfTwo) log++;
        return log;
    }
}
