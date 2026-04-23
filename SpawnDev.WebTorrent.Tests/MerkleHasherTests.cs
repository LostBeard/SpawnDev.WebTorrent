using System.Security.Cryptography;
using NUnit.Framework;
using SpawnDev.WebTorrent;

namespace SpawnDev.WebTorrent.Tests;

/// <summary>
/// Tests for <see cref="MerkleHasher"/> - the BEP 52 Merkle-tree primitive.
///
/// Coverage:
/// - Pad-hash formula (leaf level and propagated up).
/// - Partial-leaf zero-padding.
/// - Merkle root for 1, 2, 3, and 4 inputs (verifies both no-padding and padding paths).
/// - Piece-layer construction across piece sizes.
/// - Piece-size invariance of the file root (spec-mandated - a file's v2 root must not depend
///   on the piece size chosen, because the Merkle tree underneath is the same).
/// - Input validation.
/// </summary>
[TestFixture]
public class MerkleHasherTests
{
    [Test]
    public void PadHashAtLevel_Zero_IsSha256OfZero16KiB()
    {
        var expected = SHA256.HashData(new byte[16384]);
        var actual = MerkleHasher.PadHashAtLevel(0);
        Assert.That(actual, Is.EqualTo(expected));
    }

    [Test]
    public void PadHashAtLevel_One_IsSha256OfPairedLevelZero()
    {
        var below = MerkleHasher.PadHashAtLevel(0);
        var concat = new byte[64];
        Buffer.BlockCopy(below, 0, concat, 0, 32);
        Buffer.BlockCopy(below, 0, concat, 32, 32);
        var expected = SHA256.HashData(concat);

        var actual = MerkleHasher.PadHashAtLevel(1);
        Assert.That(actual, Is.EqualTo(expected));
    }

    [Test]
    public void PadHashAtLevel_IsCached()
    {
        // Second call must return the same byte[] instance - proves memoization.
        var first = MerkleHasher.PadHashAtLevel(5);
        var second = MerkleHasher.PadHashAtLevel(5);
        Assert.That(second, Is.SameAs(first));
    }

    [Test]
    public void PadHashAtLevel_NegativeLevel_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => MerkleHasher.PadHashAtLevel(-1));
    }

    [Test]
    public void HashLeaf_FullZeros_EqualsLevelZeroPadHash()
    {
        var actual = MerkleHasher.HashLeaf(new byte[16384]);
        var expected = MerkleHasher.PadHashAtLevel(0);
        Assert.That(actual, Is.EqualTo(expected));
    }

    [Test]
    public void HashLeaf_PartialContent_ZeroPadsToLeafSize()
    {
        var partial = new byte[] { 1, 2, 3 };
        var explicitlyPadded = new byte[16384];
        Array.Copy(partial, explicitlyPadded, 3);
        var expected = SHA256.HashData(explicitlyPadded);

        var actual = MerkleHasher.HashLeaf(partial);
        Assert.That(actual, Is.EqualTo(expected));
    }

    [Test]
    public void HashLeaf_OverLeafSize_Throws()
    {
        Assert.Throws<ArgumentException>(() => MerkleHasher.HashLeaf(new byte[16385]));
    }

    [Test]
    public void ComputeRoot_SingleHash_ReturnsThatHash()
    {
        var h = SHA256.HashData(new byte[] { 0xAB });
        var root = MerkleHasher.ComputeRoot(new[] { h }, level: 0);
        Assert.That(root, Is.EqualTo(h));
    }

    [Test]
    public void ComputeRoot_TwoHashes_IsSha256OfConcatenation()
    {
        var h0 = SHA256.HashData(new byte[] { 1 });
        var h1 = SHA256.HashData(new byte[] { 2 });
        var concat = new byte[64];
        Buffer.BlockCopy(h0, 0, concat, 0, 32);
        Buffer.BlockCopy(h1, 0, concat, 32, 32);
        var expected = SHA256.HashData(concat);

        var actual = MerkleHasher.ComputeRoot(new[] { h0, h1 }, level: 0);
        Assert.That(actual, Is.EqualTo(expected));
    }

    [Test]
    public void ComputeRoot_ThreeHashes_PadsWithLevelZeroPad()
    {
        var h0 = SHA256.HashData(new byte[] { 1 });
        var h1 = SHA256.HashData(new byte[] { 2 });
        var h2 = SHA256.HashData(new byte[] { 3 });
        var pad0 = MerkleHasher.PadHashAtLevel(0);

        // Manually: SHA256(SHA256(h0||h1) || SHA256(h2||pad0))
        var left = HashConcat(h0, h1);
        var right = HashConcat(h2, pad0);
        var expected = HashConcat(left, right);

        var actual = MerkleHasher.ComputeRoot(new[] { h0, h1, h2 }, level: 0);
        Assert.That(actual, Is.EqualTo(expected));
    }

    [Test]
    public void ComputeRoot_UsesLevelSpecificPadHash()
    {
        // When feeding 3 piece-layer hashes at level 2, the pad hash must be PadHashAtLevel(2).
        var h0 = SHA256.HashData(new byte[] { 1 });
        var h1 = SHA256.HashData(new byte[] { 2 });
        var h2 = SHA256.HashData(new byte[] { 3 });
        var pad2 = MerkleHasher.PadHashAtLevel(2);

        var left = HashConcat(h0, h1);
        var right = HashConcat(h2, pad2);
        var expected = HashConcat(left, right);

        var actual = MerkleHasher.ComputeRoot(new[] { h0, h1, h2 }, level: 2);
        Assert.That(actual, Is.EqualTo(expected));
    }

    [Test]
    public void ComputeRoot_EmptyList_Throws()
    {
        Assert.Throws<ArgumentException>(() => MerkleHasher.ComputeRoot(Array.Empty<byte[]>(), level: 0));
    }

    [Test]
    public void ComputeRoot_WrongSizeHash_Throws()
    {
        Assert.Throws<ArgumentException>(() => MerkleHasher.ComputeRoot(new[] { new byte[16] }, level: 0));
    }

    [Test]
    public void ComputePieceLayer_SingleLeafPiece_MatchesLeafHash()
    {
        // Piece size == leaf size. 1-leaf file. Piece root = leaf hash.
        var data = new byte[] { 42, 7, 3 };
        var expected = MerkleHasher.HashLeaf(data);

        var layer = MerkleHasher.ComputePieceLayer(data, pieceSize: 16384);
        Assert.That(layer.Length, Is.EqualTo(1));
        Assert.That(layer[0], Is.EqualTo(expected));
    }

    [Test]
    public void ComputePieceLayer_TwoLeafPiece_IsMerkleOfLeaves()
    {
        // Piece size == 32 KiB = 2 leaves. File is 32 KiB random.
        var data = new byte[32768];
        Random.Shared.NextBytes(data);

        var leaf0 = MerkleHasher.HashLeaf(data.AsSpan(0, 16384));
        var leaf1 = MerkleHasher.HashLeaf(data.AsSpan(16384, 16384));
        var expectedPieceRoot = HashConcat(leaf0, leaf1);

        var layer = MerkleHasher.ComputePieceLayer(data, pieceSize: 32768);
        Assert.That(layer.Length, Is.EqualTo(1));
        Assert.That(layer[0], Is.EqualTo(expectedPieceRoot));
    }

    [Test]
    public void ComputePieceLayer_EmptyFile_ReturnsEmptyArray()
    {
        var layer = MerkleHasher.ComputePieceLayer(ReadOnlySpan<byte>.Empty, pieceSize: 16384);
        Assert.That(layer, Is.Empty);
    }

    [Test]
    public void ComputeFileRoot_SingleLeafFile_EqualsLeafHash()
    {
        var data = new byte[] { 1, 2, 3 };
        var expected = MerkleHasher.HashLeaf(data);

        var root = MerkleHasher.ComputeFileRoot(data, pieceSize: 16384);
        Assert.That(root, Is.EqualTo(expected));
    }

    [Test]
    public void ComputeFileRoot_EmptyFile_ReturnsLevelZeroPad()
    {
        var root = MerkleHasher.ComputeFileRoot(ReadOnlySpan<byte>.Empty, pieceSize: 16384);
        Assert.That(root, Is.EqualTo(MerkleHasher.PadHashAtLevel(0)));
    }

    [TestCase(16384)]   // piece == leaf
    [TestCase(32768)]   // piece == 2 leaves
    [TestCase(65536)]   // piece == 4 leaves
    [TestCase(131072)]  // piece == 8 leaves
    public void ComputeFileRoot_InvariantUnderPieceSize(int pieceSize)
    {
        // BEP 52 spec requires the file root to be independent of piece size because the
        // Merkle tree underneath is rooted in 16 KiB leaves regardless of how pieces are cut.
        var data = new byte[200000]; // ~12.2 leaves of content, awkward boundary to exercise padding
        Random.Shared.NextBytes(data);

        var baseline = MerkleHasher.ComputeFileRoot(data, pieceSize: 16384);
        var withPieceSize = MerkleHasher.ComputeFileRoot(data, pieceSize: pieceSize);

        Assert.That(withPieceSize, Is.EqualTo(baseline),
            $"File root must be identical across piece sizes. Baseline (piece=16 KiB) vs piece={pieceSize} differ.");
    }

    [Test]
    public void ComputeFileRoot_MatchesManualComputationForTwoPieceFile()
    {
        // Explicit manual reference for a 32 KiB file with piece size 16 KiB.
        // Piece layer = [hash(leaf0), hash(leaf1)]. File root = SHA256(hash(leaf0) || hash(leaf1)).
        var data = new byte[32768];
        Random.Shared.NextBytes(data);

        var leaf0 = MerkleHasher.HashLeaf(data.AsSpan(0, 16384));
        var leaf1 = MerkleHasher.HashLeaf(data.AsSpan(16384, 16384));
        var expected = HashConcat(leaf0, leaf1);

        var root = MerkleHasher.ComputeFileRoot(data, pieceSize: 16384);
        Assert.That(root, Is.EqualTo(expected));
    }

    [Test]
    public void ComputeFileRoot_ThreePieceFile_PadsPieceLayerCorrectly()
    {
        // 3 pieces × 16 KiB = 48 KiB file. Piece size 16 KiB (= 1 leaf per piece).
        // piece layer = [h0, h1, h2], padded to 4 with level-0 pad.
        // root = SHA256(SHA256(h0||h1) || SHA256(h2||pad0))
        var data = new byte[48 * 1024];
        Random.Shared.NextBytes(data);

        var h0 = MerkleHasher.HashLeaf(data.AsSpan(0, 16384));
        var h1 = MerkleHasher.HashLeaf(data.AsSpan(16384, 16384));
        var h2 = MerkleHasher.HashLeaf(data.AsSpan(32768, 16384));
        var pad0 = MerkleHasher.PadHashAtLevel(0);

        var left = HashConcat(h0, h1);
        var right = HashConcat(h2, pad0);
        var expected = HashConcat(left, right);

        var root = MerkleHasher.ComputeFileRoot(data, pieceSize: 16384);
        Assert.That(root, Is.EqualTo(expected));
    }

    [TestCase(15000)]  // less than leaf size, not multiple
    [TestCase(24576)]  // 24 KiB, 1.5x leaf size (not integer multiple)
    [TestCase(49152)]  // 48 KiB = 3 leaves (multiple of leaf, NOT power of 2)
    [TestCase(0)]
    [TestCase(-16384)]
    public void ComputeFileRoot_InvalidPieceSize_Throws(int pieceSize)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            MerkleHasher.ComputeFileRoot(new byte[] { 1, 2, 3 }, pieceSize));
    }

    private static byte[] HashConcat(byte[] left, byte[] right)
    {
        var buf = new byte[left.Length + right.Length];
        Buffer.BlockCopy(left, 0, buf, 0, left.Length);
        Buffer.BlockCopy(right, 0, buf, left.Length, right.Length);
        return SHA256.HashData(buf);
    }
}
