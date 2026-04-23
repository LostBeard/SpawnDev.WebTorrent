using System.Security.Cryptography;
using NUnit.Framework;
using SpawnDev.WebTorrent;

namespace SpawnDev.WebTorrent.Tests;

/// <summary>
/// Tests for <see cref="MerkleProofVerifier"/> - BEP 52 Merkle-proof verification.
/// Build synthetic Merkle trees locally, extract known-good proofs, verify they pass,
/// then tamper with each element and verify they fail. Complements the wire-message
/// codecs in <see cref="Bep52WireMessagesTests"/> which cover serialization only.
/// </summary>
[TestFixture]
public class MerkleProofVerifierTests
{
    [Test]
    public void Verify_TwoLeafTree_WithEmptyProof_MatchesRoot()
    {
        // Smallest non-trivial tree: two leaves, their combined hash IS the root.
        // A "hashes" response covering both leaves needs no proof hashes - the receiver
        // internally combines them to get the root directly.
        var l0 = LeafHash(0);
        var l1 = LeafHash(1);
        var root = HashPair(l0, l1);

        var ok = MerkleProofVerifier.Verify(
            piecesRoot: root,
            index: 0,
            baseLayerHashes: new[] { l0, l1 },
            proofHashes: Array.Empty<byte[]>());
        Assert.That(ok, Is.True);
    }

    [Test]
    public void Verify_FourLeafTree_LeftHalf_NeedsOneProofHash()
    {
        // Tree shape:
        //           root = H(h01 || h23)
        //          /          \
        //      h01              h23
        //     /    \           /    \
        //   l0     l1        l2      l3
        //
        // Requesting base=0 (leaves), index=0, length=2: receiver sends [l0, l1].
        // Reduce to h01 internally. Climb to root needs sibling h23 = 1 proof hash.
        var leaves = Enumerable.Range(0, 4).Select(LeafHash).ToArray();
        var h01 = HashPair(leaves[0], leaves[1]);
        var h23 = HashPair(leaves[2], leaves[3]);
        var root = HashPair(h01, h23);

        var ok = MerkleProofVerifier.Verify(
            piecesRoot: root,
            index: 0,
            baseLayerHashes: new[] { leaves[0], leaves[1] },
            proofHashes: new[] { h23 });
        Assert.That(ok, Is.True);
    }

    [Test]
    public void Verify_FourLeafTree_RightHalf_SiblingPlacedOnLeft()
    {
        // Same tree. Requesting index=2, length=2: receiver sends [l2, l3].
        // Reduce to h23. Climb: sibling is h01, MUST be placed on the left of the current
        // climbing hash (the "right half" case). If the verifier always places siblings on
        // the right the result will differ.
        var leaves = Enumerable.Range(0, 4).Select(LeafHash).ToArray();
        var h01 = HashPair(leaves[0], leaves[1]);
        var h23 = HashPair(leaves[2], leaves[3]);
        var root = HashPair(h01, h23);

        var ok = MerkleProofVerifier.Verify(
            piecesRoot: root,
            index: 2,
            baseLayerHashes: new[] { leaves[2], leaves[3] },
            proofHashes: new[] { h01 });
        Assert.That(ok, Is.True);
    }

    [Test]
    public void Verify_FourLeafTree_AllLeaves_NoProofNeeded()
    {
        // Requesting the entire base layer: receiver sends all 4 leaves, internally
        // reduces to root, no proof layers needed. "A proof layer is omitted if the
        // requested hashes include the entire child layer."
        var leaves = Enumerable.Range(0, 4).Select(LeafHash).ToArray();
        var h01 = HashPair(leaves[0], leaves[1]);
        var h23 = HashPair(leaves[2], leaves[3]);
        var root = HashPair(h01, h23);

        var ok = MerkleProofVerifier.Verify(
            piecesRoot: root,
            index: 0,
            baseLayerHashes: leaves,
            proofHashes: Array.Empty<byte[]>());
        Assert.That(ok, Is.True);
    }

    [Test]
    public void Verify_EightLeafTree_MiddleRange_UsesCorrectSiblingOrder()
    {
        // 8 leaves. Request index=4, length=2 (leaves 4 + 5).
        // Reduce l4||l5 -> h45. Climb needs:
        //   - sibling h67 (right of h45) -> h4567
        //   - sibling h0123 (left of h4567) -> root
        // Proof order per spec: leaf-side first, root-side last -> [h67, h0123].
        var leaves = Enumerable.Range(0, 8).Select(LeafHash).ToArray();
        var h01 = HashPair(leaves[0], leaves[1]);
        var h23 = HashPair(leaves[2], leaves[3]);
        var h45 = HashPair(leaves[4], leaves[5]);
        var h67 = HashPair(leaves[6], leaves[7]);
        var h0123 = HashPair(h01, h23);
        var h4567 = HashPair(h45, h67);
        var root = HashPair(h0123, h4567);

        var ok = MerkleProofVerifier.Verify(
            piecesRoot: root,
            index: 4,
            baseLayerHashes: new[] { leaves[4], leaves[5] },
            proofHashes: new[] { h67, h0123 });
        Assert.That(ok, Is.True);
    }

    [Test]
    public void Verify_TamperedLeaf_Fails()
    {
        var leaves = Enumerable.Range(0, 4).Select(LeafHash).ToArray();
        var h01 = HashPair(leaves[0], leaves[1]);
        var h23 = HashPair(leaves[2], leaves[3]);
        var root = HashPair(h01, h23);

        // Flip a byte in l0 so the combined h01 won't match the real one.
        var tampered = (byte[])leaves[0].Clone();
        tampered[0] ^= 0x01;

        var ok = MerkleProofVerifier.Verify(
            piecesRoot: root,
            index: 0,
            baseLayerHashes: new[] { tampered, leaves[1] },
            proofHashes: new[] { h23 });
        Assert.That(ok, Is.False);
    }

    [Test]
    public void Verify_TamperedProofHash_Fails()
    {
        var leaves = Enumerable.Range(0, 4).Select(LeafHash).ToArray();
        var h01 = HashPair(leaves[0], leaves[1]);
        var h23 = HashPair(leaves[2], leaves[3]);
        var root = HashPair(h01, h23);

        var tamperedSibling = (byte[])h23.Clone();
        tamperedSibling[^1] ^= 0x80;

        var ok = MerkleProofVerifier.Verify(
            piecesRoot: root,
            index: 0,
            baseLayerHashes: new[] { leaves[0], leaves[1] },
            proofHashes: new[] { tamperedSibling });
        Assert.That(ok, Is.False);
    }

    [Test]
    public void Verify_WrongRoot_Fails()
    {
        var leaves = Enumerable.Range(0, 4).Select(LeafHash).ToArray();
        var h01 = HashPair(leaves[0], leaves[1]);
        var h23 = HashPair(leaves[2], leaves[3]);

        var unrelatedRoot = SHA256.HashData(new byte[] { 0xAB });

        var ok = MerkleProofVerifier.Verify(
            piecesRoot: unrelatedRoot,
            index: 0,
            baseLayerHashes: new[] { leaves[0], leaves[1] },
            proofHashes: new[] { h23 });
        Assert.That(ok, Is.False);
    }

    [Test]
    public void Verify_IndexNotMultipleOfLength_Fails()
    {
        // Spec: "Index MUST be a multiple of length."
        var leaves = Enumerable.Range(0, 4).Select(LeafHash).ToArray();
        var root = HashPair(HashPair(leaves[0], leaves[1]), HashPair(leaves[2], leaves[3]));

        var ok = MerkleProofVerifier.Verify(
            piecesRoot: root,
            index: 1, // not a multiple of length=2
            baseLayerHashes: new[] { leaves[0], leaves[1] },
            proofHashes: new[] { HashPair(leaves[2], leaves[3]) });
        Assert.That(ok, Is.False);
    }

    [Test]
    public void Verify_NonPowerOfTwoLength_Fails()
    {
        // Spec: "Length MUST be equal-to-or-greater-than two and a power of two."
        // Length = 3 is invalid. Verifier rejects without attempting to reduce.
        var leaves = Enumerable.Range(0, 3).Select(LeafHash).ToArray();
        var root = SHA256.HashData(new byte[] { 0 }); // placeholder

        var ok = MerkleProofVerifier.Verify(
            piecesRoot: root,
            index: 0,
            baseLayerHashes: leaves,
            proofHashes: Array.Empty<byte[]>());
        Assert.That(ok, Is.False);
    }

    [Test]
    public void Verify_SingleLeafRoot_ReturnsDirectMatch()
    {
        // Length=1 is the trivial edge: a single-piece file's root IS the single piece's
        // root hash; no reduction, no proof hashes. Verifier accepts if the single hash
        // matches pieces_root.
        var onlyLeaf = SHA256.HashData(new byte[] { 42 });

        var ok = MerkleProofVerifier.Verify(
            piecesRoot: onlyLeaf,
            index: 0,
            baseLayerHashes: new[] { onlyLeaf },
            proofHashes: Array.Empty<byte[]>());
        Assert.That(ok, Is.True);
    }

    [Test]
    public void Verify_WrongSizedHash_Fails()
    {
        // Internal hash with wrong length - verifier rejects without computing.
        var root = SHA256.HashData(new byte[] { 0 });
        var shortHash = new byte[16];

        var ok = MerkleProofVerifier.Verify(
            piecesRoot: root,
            index: 0,
            baseLayerHashes: new[] { shortHash, shortHash },
            proofHashes: Array.Empty<byte[]>());
        Assert.That(ok, Is.False);
    }

    [Test]
    public void Verify_HashesMessageOverload_SplitsListCorrectly()
    {
        // End-to-end: build a real proof, pack it into a Bep52WireMessages.Hashes
        // message as it would arrive on the wire, send it through the Verify(Hashes)
        // overload. Exercises the split-the-flat-list logic.
        var leaves = Enumerable.Range(0, 4).Select(LeafHash).ToArray();
        var h01 = HashPair(leaves[0], leaves[1]);
        var h23 = HashPair(leaves[2], leaves[3]);
        var root = HashPair(h01, h23);

        // Request: base_layer=0, index=0, length=2, proof_layers=1.
        // HashList (wire order): [l0, l1, h23].
        var msg = new Bep52WireMessages.Hashes(
            PiecesRoot: root,
            BaseLayer: 0,
            Index: 0,
            Length: 2,
            ProofLayers: 1,
            HashList: new[] { leaves[0], leaves[1], h23 });

        Assert.That(MerkleProofVerifier.Verify(msg), Is.True);
    }

    private static byte[] LeafHash(int seed)
    {
        var content = new byte[100];
        new Random(seed).NextBytes(content);
        return SHA256.HashData(content);
    }

    private static byte[] HashPair(byte[] left, byte[] right)
    {
        var buf = new byte[left.Length + right.Length];
        Buffer.BlockCopy(left, 0, buf, 0, left.Length);
        Buffer.BlockCopy(right, 0, buf, left.Length, right.Length);
        return SHA256.HashData(buf);
    }
}
