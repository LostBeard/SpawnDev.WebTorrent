using NUnit.Framework;
using SpawnDev.WebTorrent;

namespace SpawnDev.WebTorrent.Tests;

/// <summary>
/// Equivalence tests between <see cref="IncrementalMerkleHasher"/> (streaming API)
/// and the one-shot <see cref="MerkleHasher.ComputeFileRoot"/> /
/// <see cref="MerkleHasher.ComputePieceLayer"/>. The whole point of the incremental
/// hasher is to produce the same hashes as the one-shot hasher without buffering the
/// full file - if these diverge, streaming v2 torrents will silently disagree with
/// in-memory v2 torrents and break interop.
/// </summary>
[TestFixture]
public class IncrementalMerkleHasherTests
{
    [TestCase(0)]
    [TestCase(1)]
    [TestCase(16383)]
    [TestCase(16384)]        // exactly one leaf
    [TestCase(16385)]        // one leaf + 1 byte
    [TestCase(32768)]        // two leaves
    [TestCase(32769)]        // two leaves + 1 byte
    [TestCase(200000)]       // awkward multi-leaf
    public void Incremental_EquivalentToOneShot_OneLeafPerPiece(int fileSize)
    {
        var data = NewRandom(fileSize);
        int pieceSize = MerkleHasher.LeafSize; // 1 leaf per piece - each leaf is its own piece root

        var expectedRoot = MerkleHasher.ComputeFileRoot(data, pieceSize);
        var expectedLayer = fileSize > pieceSize
            ? MerkleHasher.ComputePieceLayer(data, pieceSize)
            : Array.Empty<byte[]>();

        var hasher = MerkleHasher.CreateIncremental(pieceSize);
        hasher.Update(data);
        var (actualRoot, actualLayer) = hasher.Finish();

        Assert.That(actualRoot, Is.EqualTo(expectedRoot), $"fileRoot mismatch for size {fileSize}");
        AssertHashArraysEqual(actualLayer, expectedLayer, $"pieceLayer mismatch for size {fileSize}");
    }

    [TestCase(0, 32768)]
    [TestCase(500, 32768)]
    [TestCase(16384, 32768)]
    [TestCase(32768, 32768)]   // exactly one piece (2 leaves)
    [TestCase(32769, 32768)]   // one piece + 1 byte
    [TestCase(100000, 32768)]  // multi-piece awkward
    [TestCase(500000, 65536)]  // 4 leaves per piece
    [TestCase(1048576, 131072)] // 8 leaves per piece, 1 MiB file
    public void Incremental_EquivalentToOneShot_MultiLeafPerPiece(int fileSize, int pieceSize)
    {
        var data = NewRandom(fileSize);

        var expectedRoot = MerkleHasher.ComputeFileRoot(data, pieceSize);
        var expectedLayer = fileSize > pieceSize
            ? MerkleHasher.ComputePieceLayer(data, pieceSize)
            : Array.Empty<byte[]>();

        var hasher = MerkleHasher.CreateIncremental(pieceSize);
        hasher.Update(data);
        var (actualRoot, actualLayer) = hasher.Finish();

        Assert.That(actualRoot, Is.EqualTo(expectedRoot), $"fileRoot mismatch for size={fileSize} piece={pieceSize}");
        AssertHashArraysEqual(actualLayer, expectedLayer, $"pieceLayer mismatch for size={fileSize} piece={pieceSize}");
    }

    [TestCase(1)]
    [TestCase(7)]
    [TestCase(16383)]
    [TestCase(16384)]
    [TestCase(16385)]
    [TestCase(100000)]
    public void Incremental_ChunkingIsIrrelevant(int chunkSize)
    {
        // Same data, fed in chunks of varying sizes, must produce the same hash.
        var data = NewRandom(500000);
        int pieceSize = 65536;

        var oneShotRoot = MerkleHasher.ComputeFileRoot(data, pieceSize);

        var hasher = MerkleHasher.CreateIncremental(pieceSize);
        for (int pos = 0; pos < data.Length; pos += chunkSize)
        {
            int take = Math.Min(chunkSize, data.Length - pos);
            hasher.Update(data.AsSpan(pos, take));
        }
        var (root, _) = hasher.Finish();

        Assert.That(root, Is.EqualTo(oneShotRoot), $"chunkSize {chunkSize} produced different hash than one-shot");
    }

    [Test]
    public void Incremental_ByteByByte_MatchesOneShot()
    {
        // Extreme case: feed one byte at a time. Tests the partial-leaf accumulation path
        // every single iteration. Easy-to-trigger off-by-one bug surface.
        var data = NewRandom(40000);
        int pieceSize = 32768;

        var oneShotRoot = MerkleHasher.ComputeFileRoot(data, pieceSize);

        var hasher = MerkleHasher.CreateIncremental(pieceSize);
        for (int i = 0; i < data.Length; i++) hasher.Update(data.AsSpan(i, 1));
        var (root, _) = hasher.Finish();

        Assert.That(root, Is.EqualTo(oneShotRoot));
    }

    [Test]
    public void Incremental_EmptyFile_ReturnsLevelZeroPad()
    {
        var hasher = MerkleHasher.CreateIncremental(MerkleHasher.LeafSize);
        var (root, layer) = hasher.Finish();
        Assert.That(root, Is.EqualTo(MerkleHasher.PadHashAtLevel(0)));
        Assert.That(layer, Is.Empty);
        Assert.That(hasher.TotalBytesHashed, Is.EqualTo(0));
    }

    [Test]
    public void Incremental_TotalBytes_TrackedAcrossUpdates()
    {
        var hasher = MerkleHasher.CreateIncremental(65536);
        hasher.Update(new byte[1000]);
        hasher.Update(new byte[500]);
        hasher.Update(new byte[17000]);
        Assert.That(hasher.TotalBytesHashed, Is.EqualTo(18500));
        _ = hasher.Finish();
    }

    [Test]
    public void Incremental_CompletedPieceCount_GrowsAsPiecesFill()
    {
        int pieceSize = 32768;
        var hasher = MerkleHasher.CreateIncremental(pieceSize);
        Assert.That(hasher.CompletedPieceCount, Is.EqualTo(0));

        hasher.Update(new byte[pieceSize]);
        Assert.That(hasher.CompletedPieceCount, Is.EqualTo(1), "One full piece should be rolled up");

        hasher.Update(new byte[pieceSize / 2]);
        Assert.That(hasher.CompletedPieceCount, Is.EqualTo(1), "Partial second piece does not roll up until filled");

        hasher.Update(new byte[pieceSize / 2]);
        Assert.That(hasher.CompletedPieceCount, Is.EqualTo(2), "Second piece fills, rolls up");

        _ = hasher.Finish();
    }

    [Test]
    public void Incremental_FinishTwice_Throws()
    {
        var hasher = MerkleHasher.CreateIncremental(MerkleHasher.LeafSize);
        hasher.Update(new byte[5]);
        _ = hasher.Finish();
        Assert.Throws<InvalidOperationException>(() => hasher.Finish());
    }

    [Test]
    public void Incremental_UpdateAfterFinish_Throws()
    {
        var hasher = MerkleHasher.CreateIncremental(MerkleHasher.LeafSize);
        _ = hasher.Finish();
        Assert.Throws<InvalidOperationException>(() => hasher.Update(new byte[1]));
    }

    [TestCase(15000)]
    [TestCase(0)]
    [TestCase(-16384)]
    [TestCase(49152)]   // multiple of 16 KiB but not power-of-two multiple
    public void Incremental_InvalidPieceSize_Throws(int pieceSize)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => MerkleHasher.CreateIncremental(pieceSize));
    }

    private static byte[] NewRandom(int size)
    {
        if (size == 0) return Array.Empty<byte>();
        var data = new byte[size];
        Random.Shared.NextBytes(data);
        return data;
    }

    private static void AssertHashArraysEqual(byte[][] actual, byte[][] expected, string context)
    {
        Assert.That(actual.Length, Is.EqualTo(expected.Length), $"{context}: count mismatch");
        for (int i = 0; i < expected.Length; i++)
        {
            Assert.That(actual[i], Is.EqualTo(expected[i]), $"{context}: hash [{i}] mismatch");
        }
    }
}
