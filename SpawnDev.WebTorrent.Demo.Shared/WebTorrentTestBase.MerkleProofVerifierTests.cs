using System.Security.Cryptography;
using SpawnDev.UnitTesting;
using SpawnDev.WebTorrent;

namespace SpawnDev.WebTorrent.Demo.Shared;

/// <summary>
/// BEP 52 Merkle-proof verification tests. Build synthetic Merkle trees locally,
/// extract known-good proofs, verify they pass, then tamper with each element and
/// verify they fail. Complements Bep52V2 codec tests which cover serialization only.
/// Migrated from NUnit MerkleProofVerifierTests.cs.
/// </summary>
public abstract partial class WebTorrentTestBase
{
    [TestMethod]
    public async Task MerkleProofVerifier_TwoLeafTree_WithEmptyProof_MatchesRoot()
    {
        var l0 = MerkleProofVerifierTests_LeafHash(0);
        var l1 = MerkleProofVerifierTests_LeafHash(1);
        var root = MerkleProofVerifierTests_HashPair(l0, l1);

        var ok = MerkleProofVerifier.Verify(
            piecesRoot: root,
            index: 0,
            baseLayerHashes: new[] { l0, l1 },
            proofHashes: Array.Empty<byte[]>());
        if (!ok) throw new Exception("two-leaf tree with empty proof should verify against root");
        await Task.CompletedTask;
    }

    [TestMethod]
    public async Task MerkleProofVerifier_FourLeafTree_LeftHalf_NeedsOneProofHash()
    {
        var leaves = Enumerable.Range(0, 4).Select(MerkleProofVerifierTests_LeafHash).ToArray();
        var h01 = MerkleProofVerifierTests_HashPair(leaves[0], leaves[1]);
        var h23 = MerkleProofVerifierTests_HashPair(leaves[2], leaves[3]);
        var root = MerkleProofVerifierTests_HashPair(h01, h23);

        var ok = MerkleProofVerifier.Verify(
            piecesRoot: root,
            index: 0,
            baseLayerHashes: new[] { leaves[0], leaves[1] },
            proofHashes: new[] { h23 });
        if (!ok) throw new Exception("4-leaf left-half should verify with 1 proof hash (h23 sibling)");
        await Task.CompletedTask;
    }

    [TestMethod]
    public async Task MerkleProofVerifier_FourLeafTree_RightHalf_SiblingPlacedOnLeft()
    {
        // Tests that when the target leaves are on the right half, the verifier places the
        // sibling on the LEFT of the climbing hash. Easy off-by-one / wrong-side bug surface.
        var leaves = Enumerable.Range(0, 4).Select(MerkleProofVerifierTests_LeafHash).ToArray();
        var h01 = MerkleProofVerifierTests_HashPair(leaves[0], leaves[1]);
        var h23 = MerkleProofVerifierTests_HashPair(leaves[2], leaves[3]);
        var root = MerkleProofVerifierTests_HashPair(h01, h23);

        var ok = MerkleProofVerifier.Verify(
            piecesRoot: root,
            index: 2,
            baseLayerHashes: new[] { leaves[2], leaves[3] },
            proofHashes: new[] { h01 });
        if (!ok) throw new Exception("4-leaf right-half verify must place sibling on the left");
        await Task.CompletedTask;
    }

    [TestMethod]
    public async Task MerkleProofVerifier_FourLeafTree_AllLeaves_NoProofNeeded()
    {
        var leaves = Enumerable.Range(0, 4).Select(MerkleProofVerifierTests_LeafHash).ToArray();
        var h01 = MerkleProofVerifierTests_HashPair(leaves[0], leaves[1]);
        var h23 = MerkleProofVerifierTests_HashPair(leaves[2], leaves[3]);
        var root = MerkleProofVerifierTests_HashPair(h01, h23);

        var ok = MerkleProofVerifier.Verify(
            piecesRoot: root,
            index: 0,
            baseLayerHashes: leaves,
            proofHashes: Array.Empty<byte[]>());
        if (!ok) throw new Exception("all-leaves request should verify with no proof layers");
        await Task.CompletedTask;
    }

    [TestMethod]
    public async Task MerkleProofVerifier_EightLeafTree_MiddleRange_UsesCorrectSiblingOrder()
    {
        // Proof order per BEP 52: leaf-side first, root-side last. [h67, h0123].
        var leaves = Enumerable.Range(0, 8).Select(MerkleProofVerifierTests_LeafHash).ToArray();
        var h01 = MerkleProofVerifierTests_HashPair(leaves[0], leaves[1]);
        var h23 = MerkleProofVerifierTests_HashPair(leaves[2], leaves[3]);
        var h45 = MerkleProofVerifierTests_HashPair(leaves[4], leaves[5]);
        var h67 = MerkleProofVerifierTests_HashPair(leaves[6], leaves[7]);
        var h0123 = MerkleProofVerifierTests_HashPair(h01, h23);
        var h4567 = MerkleProofVerifierTests_HashPair(h45, h67);
        var root = MerkleProofVerifierTests_HashPair(h0123, h4567);

        var ok = MerkleProofVerifier.Verify(
            piecesRoot: root,
            index: 4,
            baseLayerHashes: new[] { leaves[4], leaves[5] },
            proofHashes: new[] { h67, h0123 });
        if (!ok) throw new Exception("8-leaf middle-range verify requires leaf-side-first proof order");
        await Task.CompletedTask;
    }

    [TestMethod]
    public async Task MerkleProofVerifier_TamperedLeaf_Fails()
    {
        var leaves = Enumerable.Range(0, 4).Select(MerkleProofVerifierTests_LeafHash).ToArray();
        var h01 = MerkleProofVerifierTests_HashPair(leaves[0], leaves[1]);
        var h23 = MerkleProofVerifierTests_HashPair(leaves[2], leaves[3]);
        var root = MerkleProofVerifierTests_HashPair(h01, h23);

        var tampered = (byte[])leaves[0].Clone();
        tampered[0] ^= 0x01;

        var ok = MerkleProofVerifier.Verify(
            piecesRoot: root,
            index: 0,
            baseLayerHashes: new[] { tampered, leaves[1] },
            proofHashes: new[] { h23 });
        if (ok) throw new Exception("tampered leaf must fail verification");
        await Task.CompletedTask;
    }

    [TestMethod]
    public async Task MerkleProofVerifier_TamperedProofHash_Fails()
    {
        var leaves = Enumerable.Range(0, 4).Select(MerkleProofVerifierTests_LeafHash).ToArray();
        var h01 = MerkleProofVerifierTests_HashPair(leaves[0], leaves[1]);
        var h23 = MerkleProofVerifierTests_HashPair(leaves[2], leaves[3]);
        var root = MerkleProofVerifierTests_HashPair(h01, h23);

        var tamperedSibling = (byte[])h23.Clone();
        tamperedSibling[^1] ^= 0x80;

        var ok = MerkleProofVerifier.Verify(
            piecesRoot: root,
            index: 0,
            baseLayerHashes: new[] { leaves[0], leaves[1] },
            proofHashes: new[] { tamperedSibling });
        if (ok) throw new Exception("tampered proof hash must fail verification");
        await Task.CompletedTask;
    }

    [TestMethod]
    public async Task MerkleProofVerifier_WrongRoot_Fails()
    {
        var leaves = Enumerable.Range(0, 4).Select(MerkleProofVerifierTests_LeafHash).ToArray();
        var h01 = MerkleProofVerifierTests_HashPair(leaves[0], leaves[1]);
        var h23 = MerkleProofVerifierTests_HashPair(leaves[2], leaves[3]);

        var unrelatedRoot = SHA256.HashData(new byte[] { 0xAB });

        var ok = MerkleProofVerifier.Verify(
            piecesRoot: unrelatedRoot,
            index: 0,
            baseLayerHashes: new[] { leaves[0], leaves[1] },
            proofHashes: new[] { h23 });
        if (ok) throw new Exception("mismatched root must fail verification");
        await Task.CompletedTask;
    }

    [TestMethod]
    public async Task MerkleProofVerifier_IndexNotMultipleOfLength_Fails()
    {
        var leaves = Enumerable.Range(0, 4).Select(MerkleProofVerifierTests_LeafHash).ToArray();
        var root = MerkleProofVerifierTests_HashPair(
            MerkleProofVerifierTests_HashPair(leaves[0], leaves[1]),
            MerkleProofVerifierTests_HashPair(leaves[2], leaves[3]));

        var ok = MerkleProofVerifier.Verify(
            piecesRoot: root,
            index: 1,
            baseLayerHashes: new[] { leaves[0], leaves[1] },
            proofHashes: new[] { MerkleProofVerifierTests_HashPair(leaves[2], leaves[3]) });
        if (ok) throw new Exception("index not a multiple of length must fail (BEP 52 spec)");
        await Task.CompletedTask;
    }

    [TestMethod]
    public async Task MerkleProofVerifier_NonPowerOfTwoLength_Fails()
    {
        var leaves = Enumerable.Range(0, 3).Select(MerkleProofVerifierTests_LeafHash).ToArray();
        var root = SHA256.HashData(new byte[] { 0 });

        var ok = MerkleProofVerifier.Verify(
            piecesRoot: root,
            index: 0,
            baseLayerHashes: leaves,
            proofHashes: Array.Empty<byte[]>());
        if (ok) throw new Exception("length not a power of two must fail (BEP 52 spec)");
        await Task.CompletedTask;
    }

    [TestMethod]
    public async Task MerkleProofVerifier_SingleLeafRoot_ReturnsDirectMatch()
    {
        var onlyLeaf = SHA256.HashData(new byte[] { 42 });

        var ok = MerkleProofVerifier.Verify(
            piecesRoot: onlyLeaf,
            index: 0,
            baseLayerHashes: new[] { onlyLeaf },
            proofHashes: Array.Empty<byte[]>());
        if (!ok) throw new Exception("single-leaf tree verifies when the leaf equals the root");
        await Task.CompletedTask;
    }

    [TestMethod]
    public async Task MerkleProofVerifier_WrongSizedHash_Fails()
    {
        var root = SHA256.HashData(new byte[] { 0 });
        var shortHash = new byte[16];

        var ok = MerkleProofVerifier.Verify(
            piecesRoot: root,
            index: 0,
            baseLayerHashes: new[] { shortHash, shortHash },
            proofHashes: Array.Empty<byte[]>());
        if (ok) throw new Exception("non-32-byte hashes must fail verification");
        await Task.CompletedTask;
    }

    [TestMethod]
    public async Task MerkleProofVerifier_HashesMessageOverload_SplitsListCorrectly()
    {
        // End-to-end: pack a proof as a wire Hashes message and route through the overload
        // that splits the flat HashList into base-layer + proof-layers.
        var leaves = Enumerable.Range(0, 4).Select(MerkleProofVerifierTests_LeafHash).ToArray();
        var h01 = MerkleProofVerifierTests_HashPair(leaves[0], leaves[1]);
        var h23 = MerkleProofVerifierTests_HashPair(leaves[2], leaves[3]);
        var root = MerkleProofVerifierTests_HashPair(h01, h23);

        var msg = new Bep52WireMessages.Hashes(
            PiecesRoot: root,
            BaseLayer: 0,
            Index: 0,
            Length: 2,
            ProofLayers: 1,
            HashList: new[] { leaves[0], leaves[1], h23 });

        if (!MerkleProofVerifier.Verify(msg))
            throw new Exception("Hashes-message overload failed to verify a valid proof");
        await Task.CompletedTask;
    }

    // ---- helpers ----

    private static byte[] MerkleProofVerifierTests_LeafHash(int seed)
    {
        var content = new byte[100];
        new Random(seed).NextBytes(content);
        return SHA256.HashData(content);
    }

    private static byte[] MerkleProofVerifierTests_HashPair(byte[] left, byte[] right)
    {
        var buf = new byte[left.Length + right.Length];
        Buffer.BlockCopy(left, 0, buf, 0, left.Length);
        Buffer.BlockCopy(right, 0, buf, left.Length, right.Length);
        return SHA256.HashData(buf);
    }
}
