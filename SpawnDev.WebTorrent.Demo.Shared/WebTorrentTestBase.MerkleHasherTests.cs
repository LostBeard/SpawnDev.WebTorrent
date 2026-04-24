using System.Security.Cryptography;
using SpawnDev.UnitTesting;
using SpawnDev.WebTorrent;

namespace SpawnDev.WebTorrent.Demo.Shared;

/// <summary>
/// BEP 52 Merkle-tree primitive tests.
/// Migrated from NUnit SpawnDev.WebTorrent.Tests/MerkleHasherTests.cs so these run under
/// PlaywrightMultiTest (browser + desktop) rather than desktop-only NUnit.
///
/// Covers:
/// - Pad-hash formula (leaf level and propagated up).
/// - Partial-leaf zero-padding.
/// - Merkle root for 1, 2, 3, 4 inputs (both no-pad and pad paths).
/// - Piece-layer construction across piece sizes.
/// - Piece-size invariance of the file root (spec-mandated).
/// - Input validation.
/// </summary>
public abstract partial class WebTorrentTestBase
{
    [TestMethod]
    public async Task MerkleHasher_PadHashAtLevel_Zero_IsSha256OfZero16KiB()
    {
        var expected = SHA256.HashData(new byte[16384]);
        var actual = MerkleHasher.PadHashAtLevel(0);
        if (!actual.SequenceEqual(expected)) throw new Exception("level-0 pad hash mismatch");
        await Task.CompletedTask;
    }

    [TestMethod]
    public async Task MerkleHasher_PadHashAtLevel_One_IsSha256OfPairedLevelZero()
    {
        var below = MerkleHasher.PadHashAtLevel(0);
        var concat = new byte[64];
        Buffer.BlockCopy(below, 0, concat, 0, 32);
        Buffer.BlockCopy(below, 0, concat, 32, 32);
        var expected = SHA256.HashData(concat);

        var actual = MerkleHasher.PadHashAtLevel(1);
        if (!actual.SequenceEqual(expected)) throw new Exception("level-1 pad hash mismatch");
        await Task.CompletedTask;
    }

    [TestMethod]
    public async Task MerkleHasher_PadHashAtLevel_IsCached()
    {
        // Second call must return the same byte[] instance - proves memoization.
        var first = MerkleHasher.PadHashAtLevel(5);
        var second = MerkleHasher.PadHashAtLevel(5);
        if (!ReferenceEquals(second, first))
            throw new Exception("PadHashAtLevel must return cached instance");
        await Task.CompletedTask;
    }

    [TestMethod]
    public async Task MerkleHasher_PadHashAtLevel_NegativeLevel_Throws()
    {
        try { MerkleHasher.PadHashAtLevel(-1); }
        catch (ArgumentOutOfRangeException) { await Task.CompletedTask; return; }
        throw new Exception("expected ArgumentOutOfRangeException for negative level");
    }

    [TestMethod]
    public async Task MerkleHasher_HashLeaf_FullZeros_EqualsLevelZeroPadHash()
    {
        var actual = MerkleHasher.HashLeaf(new byte[16384]);
        var expected = MerkleHasher.PadHashAtLevel(0);
        if (!actual.SequenceEqual(expected)) throw new Exception("full-zero leaf should equal level-0 pad");
        await Task.CompletedTask;
    }

    [TestMethod]
    public async Task MerkleHasher_HashLeaf_PartialContent_ZeroPadsToLeafSize()
    {
        var partial = new byte[] { 1, 2, 3 };
        var explicitlyPadded = new byte[16384];
        Array.Copy(partial, explicitlyPadded, 3);
        var expected = SHA256.HashData(explicitlyPadded);

        var actual = MerkleHasher.HashLeaf(partial);
        if (!actual.SequenceEqual(expected)) throw new Exception("partial leaf should zero-pad to 16 KiB");
        await Task.CompletedTask;
    }

    [TestMethod]
    public async Task MerkleHasher_HashLeaf_OverLeafSize_Throws()
    {
        try { MerkleHasher.HashLeaf(new byte[16385]); }
        catch (ArgumentException) { await Task.CompletedTask; return; }
        throw new Exception("expected ArgumentException for over-16 KiB leaf");
    }

    [TestMethod]
    public async Task MerkleHasher_ComputeRoot_SingleHash_ReturnsThatHash()
    {
        var h = SHA256.HashData(new byte[] { 0xAB });
        var root = MerkleHasher.ComputeRoot(new[] { h }, level: 0);
        if (!root.SequenceEqual(h)) throw new Exception("single-hash root should be the hash itself");
        await Task.CompletedTask;
    }

    [TestMethod]
    public async Task MerkleHasher_ComputeRoot_TwoHashes_IsSha256OfConcatenation()
    {
        var h0 = SHA256.HashData(new byte[] { 1 });
        var h1 = SHA256.HashData(new byte[] { 2 });
        var concat = new byte[64];
        Buffer.BlockCopy(h0, 0, concat, 0, 32);
        Buffer.BlockCopy(h1, 0, concat, 32, 32);
        var expected = SHA256.HashData(concat);

        var actual = MerkleHasher.ComputeRoot(new[] { h0, h1 }, level: 0);
        if (!actual.SequenceEqual(expected)) throw new Exception("two-hash root mismatch");
        await Task.CompletedTask;
    }

    [TestMethod]
    public async Task MerkleHasher_ComputeRoot_ThreeHashes_PadsWithLevelZeroPad()
    {
        var h0 = SHA256.HashData(new byte[] { 1 });
        var h1 = SHA256.HashData(new byte[] { 2 });
        var h2 = SHA256.HashData(new byte[] { 3 });
        var pad0 = MerkleHasher.PadHashAtLevel(0);

        var left = MerkleHasherTests_HashConcat(h0, h1);
        var right = MerkleHasherTests_HashConcat(h2, pad0);
        var expected = MerkleHasherTests_HashConcat(left, right);

        var actual = MerkleHasher.ComputeRoot(new[] { h0, h1, h2 }, level: 0);
        if (!actual.SequenceEqual(expected)) throw new Exception("three-hash root should pad with level-0 pad");
        await Task.CompletedTask;
    }

    [TestMethod]
    public async Task MerkleHasher_ComputeRoot_UsesLevelSpecificPadHash()
    {
        // When feeding 3 piece-layer hashes at level 2, the pad hash must be PadHashAtLevel(2).
        var h0 = SHA256.HashData(new byte[] { 1 });
        var h1 = SHA256.HashData(new byte[] { 2 });
        var h2 = SHA256.HashData(new byte[] { 3 });
        var pad2 = MerkleHasher.PadHashAtLevel(2);

        var left = MerkleHasherTests_HashConcat(h0, h1);
        var right = MerkleHasherTests_HashConcat(h2, pad2);
        var expected = MerkleHasherTests_HashConcat(left, right);

        var actual = MerkleHasher.ComputeRoot(new[] { h0, h1, h2 }, level: 2);
        if (!actual.SequenceEqual(expected)) throw new Exception("level-specific pad not used");
        await Task.CompletedTask;
    }

    [TestMethod]
    public async Task MerkleHasher_ComputeRoot_EmptyList_Throws()
    {
        try { MerkleHasher.ComputeRoot(Array.Empty<byte[]>(), level: 0); }
        catch (ArgumentException) { await Task.CompletedTask; return; }
        throw new Exception("expected ArgumentException for empty hash list");
    }

    [TestMethod]
    public async Task MerkleHasher_ComputeRoot_WrongSizeHash_Throws()
    {
        try { MerkleHasher.ComputeRoot(new[] { new byte[16] }, level: 0); }
        catch (ArgumentException) { await Task.CompletedTask; return; }
        throw new Exception("expected ArgumentException for non-32-byte hash");
    }

    [TestMethod]
    public async Task MerkleHasher_ComputePieceLayer_SingleLeafPiece_MatchesLeafHash()
    {
        var data = new byte[] { 42, 7, 3 };
        var expected = MerkleHasher.HashLeaf(data);

        var layer = MerkleHasher.ComputePieceLayer(data, pieceSize: 16384);
        if (layer.Length != 1) throw new Exception($"expected 1 entry in piece layer, got {layer.Length}");
        if (!layer[0].SequenceEqual(expected)) throw new Exception("single-leaf piece layer hash mismatch");
        await Task.CompletedTask;
    }

    [TestMethod]
    public async Task MerkleHasher_ComputePieceLayer_TwoLeafPiece_IsMerkleOfLeaves()
    {
        var data = new byte[32768];
        new Random(201).NextBytes(data);

        var leaf0 = MerkleHasher.HashLeaf(data.AsSpan(0, 16384));
        var leaf1 = MerkleHasher.HashLeaf(data.AsSpan(16384, 16384));
        var expectedPieceRoot = MerkleHasherTests_HashConcat(leaf0, leaf1);

        var layer = MerkleHasher.ComputePieceLayer(data, pieceSize: 32768);
        if (layer.Length != 1) throw new Exception($"expected 1 entry in piece layer, got {layer.Length}");
        if (!layer[0].SequenceEqual(expectedPieceRoot))
            throw new Exception("2-leaf piece root should be SHA256(leaf0 || leaf1)");
        await Task.CompletedTask;
    }

    [TestMethod]
    public async Task MerkleHasher_ComputePieceLayer_EmptyFile_ReturnsEmptyArray()
    {
        var layer = MerkleHasher.ComputePieceLayer(ReadOnlySpan<byte>.Empty, pieceSize: 16384);
        if (layer.Length != 0) throw new Exception($"empty file should give empty piece layer, got {layer.Length} entries");
        await Task.CompletedTask;
    }

    [TestMethod]
    public async Task MerkleHasher_ComputeFileRoot_SingleLeafFile_EqualsLeafHash()
    {
        var data = new byte[] { 1, 2, 3 };
        var expected = MerkleHasher.HashLeaf(data);

        var root = MerkleHasher.ComputeFileRoot(data, pieceSize: 16384);
        if (!root.SequenceEqual(expected)) throw new Exception("1-leaf file root should be leaf hash");
        await Task.CompletedTask;
    }

    [TestMethod]
    public async Task MerkleHasher_ComputeFileRoot_EmptyFile_ReturnsLevelZeroPad()
    {
        var root = MerkleHasher.ComputeFileRoot(ReadOnlySpan<byte>.Empty, pieceSize: 16384);
        if (!root.SequenceEqual(MerkleHasher.PadHashAtLevel(0)))
            throw new Exception("empty file root should equal level-0 pad hash");
        await Task.CompletedTask;
    }

    [TestMethod]
    public async Task MerkleHasher_ComputeFileRoot_InvariantUnderPieceSize_16k()
        => await MerkleHasherTests_AssertFileRootInvariant(16384);

    [TestMethod]
    public async Task MerkleHasher_ComputeFileRoot_InvariantUnderPieceSize_32k()
        => await MerkleHasherTests_AssertFileRootInvariant(32768);

    [TestMethod]
    public async Task MerkleHasher_ComputeFileRoot_InvariantUnderPieceSize_64k()
        => await MerkleHasherTests_AssertFileRootInvariant(65536);

    [TestMethod]
    public async Task MerkleHasher_ComputeFileRoot_InvariantUnderPieceSize_128k()
        => await MerkleHasherTests_AssertFileRootInvariant(131072);

    [TestMethod]
    public async Task MerkleHasher_ComputeFileRoot_MatchesManualComputationForTwoPieceFile()
    {
        var data = new byte[32768];
        new Random(202).NextBytes(data);

        var leaf0 = MerkleHasher.HashLeaf(data.AsSpan(0, 16384));
        var leaf1 = MerkleHasher.HashLeaf(data.AsSpan(16384, 16384));
        var expected = MerkleHasherTests_HashConcat(leaf0, leaf1);

        var root = MerkleHasher.ComputeFileRoot(data, pieceSize: 16384);
        if (!root.SequenceEqual(expected))
            throw new Exception("2-piece (16 KiB piece) file root should be SHA256(leaf0 || leaf1)");
        await Task.CompletedTask;
    }

    [TestMethod]
    public async Task MerkleHasher_ComputeFileRoot_ThreePieceFile_PadsPieceLayerCorrectly()
    {
        var data = new byte[48 * 1024];
        new Random(203).NextBytes(data);

        var h0 = MerkleHasher.HashLeaf(data.AsSpan(0, 16384));
        var h1 = MerkleHasher.HashLeaf(data.AsSpan(16384, 16384));
        var h2 = MerkleHasher.HashLeaf(data.AsSpan(32768, 16384));
        var pad0 = MerkleHasher.PadHashAtLevel(0);

        var left = MerkleHasherTests_HashConcat(h0, h1);
        var right = MerkleHasherTests_HashConcat(h2, pad0);
        var expected = MerkleHasherTests_HashConcat(left, right);

        var root = MerkleHasher.ComputeFileRoot(data, pieceSize: 16384);
        if (!root.SequenceEqual(expected))
            throw new Exception("3-piece file root should pad piece layer with level-0 pad");
        await Task.CompletedTask;
    }

    [TestMethod]
    public async Task MerkleHasher_ComputeFileRoot_InvalidPieceSize_15000_Throws()
        => await MerkleHasherTests_AssertInvalidPieceSizeThrows(15000);

    [TestMethod]
    public async Task MerkleHasher_ComputeFileRoot_InvalidPieceSize_24576_Throws()
        => await MerkleHasherTests_AssertInvalidPieceSizeThrows(24576);

    [TestMethod]
    public async Task MerkleHasher_ComputeFileRoot_InvalidPieceSize_49152_Throws()
        => await MerkleHasherTests_AssertInvalidPieceSizeThrows(49152);

    [TestMethod]
    public async Task MerkleHasher_ComputeFileRoot_InvalidPieceSize_0_Throws()
        => await MerkleHasherTests_AssertInvalidPieceSizeThrows(0);

    [TestMethod]
    public async Task MerkleHasher_ComputeFileRoot_InvalidPieceSize_Negative_Throws()
        => await MerkleHasherTests_AssertInvalidPieceSizeThrows(-16384);

    // ---- helpers ----

    private static async Task MerkleHasherTests_AssertFileRootInvariant(int pieceSize)
    {
        // BEP 52: file root is independent of piece size because the Merkle tree underneath
        // is rooted in 16 KiB leaves regardless of how pieces are cut.
        var data = new byte[200000]; // ~12.2 leaves, awkward boundary to exercise padding
        new Random(204).NextBytes(data);

        var baseline = MerkleHasher.ComputeFileRoot(data, pieceSize: 16384);
        var withPieceSize = MerkleHasher.ComputeFileRoot(data, pieceSize: pieceSize);

        if (!withPieceSize.SequenceEqual(baseline))
            throw new Exception(
                $"File root must be identical across piece sizes. " +
                $"Baseline (piece=16 KiB) vs piece={pieceSize} differ.");
        await Task.CompletedTask;
    }

    private static async Task MerkleHasherTests_AssertInvalidPieceSizeThrows(int pieceSize)
    {
        try { MerkleHasher.ComputeFileRoot(new byte[] { 1, 2, 3 }, pieceSize); }
        catch (ArgumentOutOfRangeException) { await Task.CompletedTask; return; }
        throw new Exception($"expected ArgumentOutOfRangeException for pieceSize={pieceSize}");
    }

    private static byte[] MerkleHasherTests_HashConcat(byte[] left, byte[] right)
    {
        var buf = new byte[left.Length + right.Length];
        Buffer.BlockCopy(left, 0, buf, 0, left.Length);
        Buffer.BlockCopy(right, 0, buf, left.Length, right.Length);
        return SHA256.HashData(buf);
    }
}
