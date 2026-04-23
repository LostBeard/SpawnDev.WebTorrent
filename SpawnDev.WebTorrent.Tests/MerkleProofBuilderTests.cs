using System.Security.Cryptography;
using NUnit.Framework;
using SpawnDev.WebTorrent;

namespace SpawnDev.WebTorrent.Tests;

/// <summary>
/// Tests for <see cref="MerkleProofBuilder"/> - the BEP 52 Merkle-proof BUILDER (inverse
/// of <see cref="MerkleProofVerifier"/>). These tests exist to prove round-trip
/// correctness: proofs we emit must be accepted by the verifier we already trust (and
/// by extension by any BEP 52-compliant peer).
///
/// The builder is used on the seed path: when a peer asks for a range of piece-layer
/// hashes via hash_request, we hand them to <see cref="MerkleProofBuilder.Build"/> to
/// produce the base-layer slice plus uncle path they need. If the response round-trips
/// through <see cref="MerkleProofVerifier.Verify(byte[], ulong, byte[][], byte[][])"/>
/// back to the advertised pieces_root, the seed path is correct.
/// </summary>
[TestFixture]
public class MerkleProofBuilderTests
{
    /// <summary>
    /// 4-piece file, piece size = 16 KiB (1 leaf per piece -> piece layer == leaf layer,
    /// level 0). Request length=2 index=0 -> the entire left half. Needs 1 proof hash
    /// (the right-half piece-layer ancestor).
    /// </summary>
    [Test]
    public void Build_4Pieces_Length2_Index0_VerifiesRoundTrip()
    {
        var leaves = BuildPieceLayer(4, seed: 1);
        var root = ComputeRootAtLevel(leaves, level: 0);

        var result = MerkleProofBuilder.Build(
            leaves, baseLayerLevel: 0, index: 0, length: 2, proofLayers: 1, expectedRoot: root);

        Assert.That(result, Is.Not.Null, "Build must succeed for a valid request");
        var (baseLayer, proof) = result!.Value;
        Assert.That(baseLayer.Length, Is.EqualTo(2));
        Assert.That(proof.Length, Is.EqualTo(1));
        Assert.That(MerkleProofVerifier.Verify(root, 0, baseLayer, proof), Is.True);
    }

    /// <summary>Same tree, request length=2 index=2 (right half). Sibling is the left half's
    /// combined hash, computed internally by the verifier when it reduces [l0,l1].</summary>
    [Test]
    public void Build_4Pieces_Length2_Index2_VerifiesRoundTrip()
    {
        var leaves = BuildPieceLayer(4, seed: 2);
        var root = ComputeRootAtLevel(leaves, level: 0);

        var result = MerkleProofBuilder.Build(
            leaves, baseLayerLevel: 0, index: 2, length: 2, proofLayers: 1, expectedRoot: root);

        Assert.That(result, Is.Not.Null);
        var (baseLayer, proof) = result!.Value;
        Assert.That(MerkleProofVerifier.Verify(root, 2, baseLayer, proof), Is.True);
    }

    /// <summary>8-piece file, request length=2 at index=4 (one leaf-pair in the middle).
    /// Needs 2 proof hashes - the adjacent pair's combined hash at level 1 and the far-
    /// side half's combined hash at level 2.</summary>
    [Test]
    public void Build_8Pieces_MiddleRange_Length2_Index4_VerifiesRoundTrip()
    {
        var leaves = BuildPieceLayer(8, seed: 3);
        var root = ComputeRootAtLevel(leaves, level: 0);

        var result = MerkleProofBuilder.Build(
            leaves, baseLayerLevel: 0, index: 4, length: 2, proofLayers: 2, expectedRoot: root);

        Assert.That(result, Is.Not.Null);
        var (baseLayer, proof) = result!.Value;
        Assert.That(proof.Length, Is.EqualTo(2));
        Assert.That(MerkleProofVerifier.Verify(root, 4, baseLayer, proof), Is.True);
    }

    /// <summary>Full-layer request: length equals the padded tree size, proof_layers = 0.
    /// Receiver internally reduces everything; no siblings needed.</summary>
    [Test]
    public void Build_8Pieces_FullLayer_ProofLayers0_VerifiesRoundTrip()
    {
        var leaves = BuildPieceLayer(8, seed: 4);
        var root = ComputeRootAtLevel(leaves, level: 0);

        var result = MerkleProofBuilder.Build(
            leaves, baseLayerLevel: 0, index: 0, length: 8, proofLayers: 0, expectedRoot: root);

        Assert.That(result, Is.Not.Null);
        var (baseLayer, proof) = result!.Value;
        Assert.That(baseLayer.Length, Is.EqualTo(8));
        Assert.That(proof.Length, Is.EqualTo(0));
        Assert.That(MerkleProofVerifier.Verify(root, 0, baseLayer, proof), Is.True);
    }

    /// <summary>Non-power-of-two piece count: pad-hash propagation must match between
    /// builder and verifier. 3 real pieces + 1 pad (level 0) -> 4 padded pieces.</summary>
    [Test]
    public void Build_3Pieces_PaddedToNextPow2_VerifiesRoundTrip()
    {
        var leaves = BuildPieceLayer(3, seed: 5);
        // Compute the root the way ComputeFileRoot does (next-pow2 with level-0 pad).
        var padded = new byte[4][];
        for (int i = 0; i < 3; i++) padded[i] = leaves[i];
        padded[3] = MerkleHasher.PadHashAtLevel(0);
        var root = ComputeRootAtLevel(padded, level: 0);

        var result = MerkleProofBuilder.Build(
            leaves, baseLayerLevel: 0, index: 0, length: 2, proofLayers: 1, expectedRoot: root);

        Assert.That(result, Is.Not.Null);
        var (baseLayer, proof) = result!.Value;
        Assert.That(MerkleProofVerifier.Verify(root, 0, baseLayer, proof), Is.True);
    }

    /// <summary>Piece-layer-level request (base_layer != 0). 8 pieces of 64 KiB (4 leaves
    /// each) -> piece layer at level 2. Request piece-layer hashes length=4 index=0 with
    /// proof_layers=1.</summary>
    [Test]
    public void Build_PieceLayer_Level2_VerifiesRoundTrip()
    {
        // Simulate a file where piece layer has 8 entries at level 2 (piece size = 64 KiB).
        var pieceLayerHashes = BuildPieceLayer(8, seed: 6);
        int level = 2;
        var root = ComputeRootAtLevel(pieceLayerHashes, level);

        var result = MerkleProofBuilder.Build(
            pieceLayerHashes, baseLayerLevel: level, index: 0, length: 4, proofLayers: 1, expectedRoot: root);

        Assert.That(result, Is.Not.Null);
        var (baseLayer, proof) = result!.Value;
        Assert.That(baseLayer.Length, Is.EqualTo(4));
        Assert.That(proof.Length, Is.EqualTo(1));
        Assert.That(MerkleProofVerifier.Verify(root, 0, baseLayer, proof), Is.True);
    }

    [Test]
    public void Build_ReturnsNull_ForLengthBelow2()
    {
        var leaves = BuildPieceLayer(4, seed: 7);
        var root = ComputeRootAtLevel(leaves, level: 0);
        Assert.That(MerkleProofBuilder.Build(leaves, 0, 0, 1, 2, root), Is.Null,
            "Spec requires length >= 2");
        Assert.That(MerkleProofBuilder.Build(leaves, 0, 0, 0, 2, root), Is.Null);
    }

    [Test]
    public void Build_ReturnsNull_ForNonPowerOfTwoLength()
    {
        var leaves = BuildPieceLayer(8, seed: 8);
        var root = ComputeRootAtLevel(leaves, level: 0);
        Assert.That(MerkleProofBuilder.Build(leaves, 0, 0, 3, 1, root), Is.Null,
            "Length must be a power of two");
        Assert.That(MerkleProofBuilder.Build(leaves, 0, 0, 6, 0, root), Is.Null);
    }

    [Test]
    public void Build_ReturnsNull_ForIndexNotMultipleOfLength()
    {
        var leaves = BuildPieceLayer(4, seed: 9);
        var root = ComputeRootAtLevel(leaves, level: 0);
        Assert.That(MerkleProofBuilder.Build(leaves, 0, 1, 2, 1, root), Is.Null,
            "Index must be a multiple of length");
    }

    [Test]
    public void Build_ReturnsNull_ForWrongProofLayerCount()
    {
        var leaves = BuildPieceLayer(4, seed: 10);
        var root = ComputeRootAtLevel(leaves, level: 0);
        Assert.That(MerkleProofBuilder.Build(leaves, 0, 0, 2, 0, root), Is.Null,
            "Should require exactly 1 proof layer for a 4-leaf tree with length=2");
        Assert.That(MerkleProofBuilder.Build(leaves, 0, 0, 2, 2, root), Is.Null,
            "Should reject too many proof layers");
    }

    [Test]
    public void Build_ReturnsNull_ForMismatchedExpectedRoot()
    {
        var leaves = BuildPieceLayer(4, seed: 11);
        var wrongRoot = new byte[32]; // zeros
        Assert.That(MerkleProofBuilder.Build(leaves, 0, 0, 2, 1, wrongRoot), Is.Null,
            "Builder self-check must refuse to emit a proof that does not climb to the claimed root");
    }

    [Test]
    public void Build_ReturnsNull_ForIndexBeyondPaddedLayer()
    {
        var leaves = BuildPieceLayer(4, seed: 12);
        var root = ComputeRootAtLevel(leaves, level: 0);
        // index=4 with length=2 would need positions [4,5] in a 4-element layer - out of range
        Assert.That(MerkleProofBuilder.Build(leaves, 0, 4, 2, 1, root), Is.Null);
    }

    /// <summary>
    /// End-to-end via MerkleHasher: build a real file, compute its root + piece layer,
    /// then use MerkleProofBuilder to answer a hash_request and verify the response.
    /// This is the shape that the Torrent.OnV2HashRequest seed path actually executes.
    /// </summary>
    [Test]
    public void Build_RealFile_PieceLayerRequest_VerifiesRoundTrip()
    {
        // 8 pieces of 64 KiB = 512 KiB file. Piece size 64 KiB = 4 leaves/piece -> piece layer
        // at level 2.
        int pieceSize = 65536;
        int fileLen = pieceSize * 8;
        var fileBytes = new byte[fileLen];
        new Random(13).NextBytes(fileBytes);

        var pieceLayer = MerkleHasher.ComputePieceLayer(fileBytes, pieceSize);
        var fileRoot = MerkleHasher.ComputeFileRoot(fileBytes, pieceSize);

        int pieceLayerLevel = 2; // log2(64 KiB / 16 KiB)

        // Peer asks for the first 4 piece-layer hashes with 1 proof layer (the right half's
        // ancestor hash).
        var result = MerkleProofBuilder.Build(
            pieceLayer, baseLayerLevel: pieceLayerLevel,
            index: 0, length: 4, proofLayers: 1,
            expectedRoot: fileRoot);

        Assert.That(result, Is.Not.Null);
        var (baseLayer, proof) = result!.Value;
        Assert.That(baseLayer.Length, Is.EqualTo(4));
        Assert.That(proof.Length, Is.EqualTo(1));
        Assert.That(MerkleProofVerifier.Verify(fileRoot, 0, baseLayer, proof), Is.True,
            "Proof emitted by the builder must be accepted by the verifier");
    }

    // ── Helpers ──

    private static byte[][] BuildPieceLayer(int count, int seed)
    {
        var rng = new Random(seed);
        var result = new byte[count][];
        for (int i = 0; i < count; i++)
        {
            var h = new byte[32];
            rng.NextBytes(h);
            result[i] = h;
        }
        return result;
    }

    private static byte[] ComputeRootAtLevel(byte[][] leaves, int level)
    {
        int n = 1;
        while (n < leaves.Length) n <<= 1;
        var current = new byte[n][];
        for (int i = 0; i < leaves.Length; i++) current[i] = leaves[i];
        for (int i = leaves.Length; i < n; i++) current[i] = MerkleHasher.PadHashAtLevel(level);

        while (current.Length > 1)
        {
            var next = new byte[current.Length / 2][];
            for (int i = 0; i < next.Length; i++)
                next[i] = HashPair(current[2 * i], current[2 * i + 1]);
            current = next;
        }
        return current[0];
    }

    private static byte[] HashPair(byte[] l, byte[] r)
    {
        var b = new byte[64];
        l.CopyTo(b, 0);
        r.CopyTo(b, 32);
        return SHA256.HashData(b);
    }
}
