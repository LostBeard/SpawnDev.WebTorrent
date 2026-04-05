using NUnit.Framework;
using System.Security.Cryptography;
using SpawnDev.WebTorrent;

namespace SpawnDev.WebTorrent.Tests;

/// <summary>
/// Tests Piece block assembly with real data and real SHA1 verification.
/// Every test creates real data, splits it into blocks, reassembles via Piece,
/// and verifies the SHA1 hash matches. If the production code is broken,
/// these tests fail.
/// </summary>
[TestFixture]
public class PieceTests
{
    [Test]
    public void SingleBlock_AssemblesAndVerifies()
    {
        // Real data: 1000 bytes of known content
        var data = new byte[1000];
        new Random(42).NextBytes(data);
        var expectedHash = SHA1.HashData(data);

        var piece = new Piece(1000);

        // Reserve and set the single block (1000 < 16384 = one block)
        int blockIdx = piece.Reserve();
        Assert.That(blockIdx, Is.EqualTo(0));

        bool complete = piece.Set(blockIdx, data, "peer1");
        Assert.That(complete, Is.True, "Piece should be complete after setting the only block");

        // Flush and verify hash
        var assembled = piece.Flush();
        Assert.That(assembled, Is.Not.Null);
        Assert.That(assembled!.Length, Is.EqualTo(1000));
        Assert.That(SHA1.HashData(assembled), Is.EqualTo(expectedHash),
            "Assembled piece SHA1 must match original data");
    }

    [Test]
    public void MultipleBlocks_AssembleInOrder()
    {
        // 40000 bytes = 3 blocks (16384 + 16384 + 7232)
        var data = new byte[40000];
        new Random(123).NextBytes(data);
        var expectedHash = SHA1.HashData(data);

        var piece = new Piece(40000);
        Assert.That(piece.ChunkLength(0), Is.EqualTo(Piece.BlockLength));  // 16384
        Assert.That(piece.ChunkLength(1), Is.EqualTo(Piece.BlockLength));  // 16384
        Assert.That(piece.ChunkLength(2), Is.EqualTo(7232));               // remainder

        // Reserve and set each block in order
        for (int i = 0; i < 3; i++)
        {
            int blockIdx = piece.Reserve();
            Assert.That(blockIdx, Is.EqualTo(i));

            int offset = piece.ChunkOffset(blockIdx);
            int len = piece.ChunkLength(blockIdx);
            var blockData = data[offset..(offset + len)];

            bool complete = piece.Set(blockIdx, blockData, $"peer{i}");
            Assert.That(complete, Is.EqualTo(i == 2), $"Complete should be true only on last block (i={i})");
        }

        var assembled = piece.Flush();
        Assert.That(assembled, Is.Not.Null);
        Assert.That(SHA1.HashData(assembled!), Is.EqualTo(expectedHash));
    }

    [Test]
    public void MultipleBlocks_AssembleOutOfOrder()
    {
        // 50000 bytes = 4 blocks (16384 + 16384 + 16384 + 848)
        var data = new byte[50000];
        new Random(456).NextBytes(data);
        var expectedHash = SHA1.HashData(data);

        var piece = new Piece(50000);

        // Reserve all blocks
        int b0 = piece.Reserve(); // 0
        int b1 = piece.Reserve(); // 1
        int b2 = piece.Reserve(); // 2
        int b3 = piece.Reserve(); // 3
        Assert.That(piece.Reserve(), Is.EqualTo(-1), "No more blocks to reserve");

        // Set blocks out of order: 2, 0, 3, 1
        piece.Set(b2, data[piece.ChunkOffset(b2)..(piece.ChunkOffset(b2) + piece.ChunkLength(b2))], "peerA");
        piece.Set(b0, data[piece.ChunkOffset(b0)..(piece.ChunkOffset(b0) + piece.ChunkLength(b0))], "peerB");
        piece.Set(b3, data[piece.ChunkOffset(b3)..(piece.ChunkOffset(b3) + piece.ChunkLength(b3))], "peerC");
        bool complete = piece.Set(b1, data[piece.ChunkOffset(b1)..(piece.ChunkOffset(b1) + piece.ChunkLength(b1))], "peerD");

        Assert.That(complete, Is.True);

        var assembled = piece.Flush();
        Assert.That(assembled, Is.Not.Null);
        Assert.That(SHA1.HashData(assembled!), Is.EqualTo(expectedHash),
            "Out-of-order assembly must produce identical data");
    }

    [Test]
    public void CancelAndReReserve_ProducesCorrectData()
    {
        var data = new byte[32768]; // 2 blocks
        new Random(789).NextBytes(data);
        var expectedHash = SHA1.HashData(data);

        var piece = new Piece(32768);

        // Reserve block 0, then cancel it
        int b0 = piece.Reserve();
        Assert.That(b0, Is.EqualTo(0));
        piece.Cancel(b0);

        // Reserve block 1
        int b1 = piece.Reserve();
        // After cancel, re-reserve gives back the cancelled block (LIFO stack)
        // b1 should be 0 (the cancelled one), then next reserve gives 1
        Assert.That(b1, Is.EqualTo(0), "Cancelled block re-reserved first (LIFO)");

        int b2 = piece.Reserve();
        Assert.That(b2, Is.EqualTo(1));

        // Set both blocks
        piece.Set(b1, data[piece.ChunkOffset(b1)..(piece.ChunkOffset(b1) + piece.ChunkLength(b1))], "peer1");
        bool complete = piece.Set(b2, data[piece.ChunkOffset(b2)..(piece.ChunkOffset(b2) + piece.ChunkLength(b2))], "peer2");

        Assert.That(complete, Is.True);
        var assembled = piece.Flush();
        Assert.That(SHA1.HashData(assembled!), Is.EqualTo(expectedHash));
    }

    [Test]
    public void DuplicateBlock_IgnoredSilently()
    {
        var data = new byte[16384]; // 1 block
        new Random(321).NextBytes(data);

        var piece = new Piece(16384);
        piece.Reserve();

        // Set the same block twice — second should be silently ignored
        bool complete1 = piece.Set(0, data, "peer1");
        Assert.That(complete1, Is.True);

        bool complete2 = piece.Set(0, data, "peer2");
        Assert.That(complete2, Is.True, "Already complete");

        var assembled = piece.Flush();
        Assert.That(assembled, Is.Not.Null);
        Assert.That(assembled, Is.EqualTo(data));

        // Only peer1 should be in sources (peer2's duplicate was ignored)
        // Sources is null after flush, but we can verify data integrity
    }

    [Test]
    public void FlushTwice_ReturnsNullSecondTime()
    {
        var data = new byte[100];
        new Random(111).NextBytes(data);

        var piece = new Piece(100);
        piece.Reserve();
        piece.Set(0, data, "peer1");

        var first = piece.Flush();
        Assert.That(first, Is.Not.Null);

        var second = piece.Flush();
        Assert.That(second, Is.Null, "Second flush should return null — buffers already cleared");
    }

    [Test]
    public void ReserveRemaining_WebSeedPattern()
    {
        // Web seeds use reserveRemaining() to claim all blocks at once
        var data = new byte[40000]; // 3 blocks
        new Random(555).NextBytes(data);
        var expectedHash = SHA1.HashData(data);

        var piece = new Piece(40000);

        // Reserve one block normally (simulating a peer took it)
        int b0 = piece.Reserve();
        Assert.That(b0, Is.EqualTo(0));

        // Web seed claims ALL remaining — returns the lowest unreserved index
        int wsStart = piece.ReserveRemaining();
        Assert.That(wsStart, Is.EqualTo(1), "Should start from block 1 (0 already reserved)");

        // Set all blocks
        for (int i = 0; i < 3; i++)
        {
            int offset = piece.ChunkOffset(i);
            int len = piece.ChunkLength(i);
            piece.Set(i, data[offset..(offset + len)], i == 0 ? "peer" : "webseed");
        }

        var assembled = piece.Flush();
        Assert.That(SHA1.HashData(assembled!), Is.EqualTo(expectedHash));
    }

    [Test]
    public void ExactBlockSize_NoRemainder()
    {
        // 32768 bytes = exactly 2 blocks, no remainder
        var data = new byte[32768];
        new Random(777).NextBytes(data);
        var expectedHash = SHA1.HashData(data);

        var piece = new Piece(32768);
        Assert.That(piece.ChunkLength(0), Is.EqualTo(Piece.BlockLength));
        Assert.That(piece.ChunkLength(1), Is.EqualTo(Piece.BlockLength)); // no remainder — full block

        piece.Reserve();
        piece.Reserve();
        piece.Set(0, data[..Piece.BlockLength], "p1");
        piece.Set(1, data[Piece.BlockLength..], "p2");

        var assembled = piece.Flush();
        Assert.That(SHA1.HashData(assembled!), Is.EqualTo(expectedHash));
    }
}
