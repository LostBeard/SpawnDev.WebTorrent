using SpawnDev.UnitTesting;
using SpawnDev.WebTorrent;
using System.Security.Cryptography;

namespace SpawnDev.WebTorrent.Demo.Shared;

/// <summary>
/// Tests Piece block assembly with real data and SHA1 verification.
/// Migrated from NUnit PieceTests - every test creates real data, splits into blocks,
/// reassembles via Piece, and verifies SHA1 hash matches.
/// </summary>
public abstract partial class WebTorrentTestBase
{
    [TestMethod]
    public async Task Piece_SingleBlock_AssemblesAndVerifies()
    {
        var data = new byte[1000];
        new Random(42).NextBytes(data);
        var expectedHash = SHA1.HashData(data);

        var piece = new Piece(1000);
        int blockIdx = piece.Reserve();
        if (blockIdx != 0) throw new Exception($"Expected block 0, got {blockIdx}");

        bool complete = piece.Set(blockIdx, data, "peer1");
        if (!complete) throw new Exception("Piece should be complete after setting the only block");

        var assembled = piece.Flush();
        if (assembled == null) throw new Exception("Flush returned null");
        if (assembled.Length != 1000) throw new Exception($"Expected 1000 bytes, got {assembled.Length}");
        if (!SHA1.HashData(assembled).SequenceEqual(expectedHash))
            throw new Exception("Assembled piece SHA1 mismatch");
    }

    [TestMethod]
    public async Task Piece_MultiBlock_InOrder()
    {
        var data = new byte[40000];
        new Random(123).NextBytes(data);
        var expectedHash = SHA1.HashData(data);

        var piece = new Piece(40000);
        for (int i = 0; i < 3; i++)
        {
            int blockIdx = piece.Reserve();
            if (blockIdx != i) throw new Exception($"Expected block {i}, got {blockIdx}");
            int offset = piece.ChunkOffset(blockIdx);
            int len = piece.ChunkLength(blockIdx);
            piece.Set(blockIdx, data[offset..(offset + len)], $"peer{i}");
        }

        var assembled = piece.Flush();
        if (!SHA1.HashData(assembled!).SequenceEqual(expectedHash))
            throw new Exception("Multi-block in-order assembly SHA1 mismatch");
    }

    [TestMethod]
    public async Task Piece_MultiBlock_OutOfOrder()
    {
        var data = new byte[50000];
        new Random(456).NextBytes(data);
        var expectedHash = SHA1.HashData(data);

        var piece = new Piece(50000);
        int b0 = piece.Reserve(), b1 = piece.Reserve(), b2 = piece.Reserve(), b3 = piece.Reserve();
        if (piece.Reserve() != -1) throw new Exception("Should have no more blocks");

        // Set out of order: 2, 0, 3, 1
        piece.Set(b2, data[piece.ChunkOffset(b2)..(piece.ChunkOffset(b2) + piece.ChunkLength(b2))], "A");
        piece.Set(b0, data[piece.ChunkOffset(b0)..(piece.ChunkOffset(b0) + piece.ChunkLength(b0))], "B");
        piece.Set(b3, data[piece.ChunkOffset(b3)..(piece.ChunkOffset(b3) + piece.ChunkLength(b3))], "C");
        bool complete = piece.Set(b1, data[piece.ChunkOffset(b1)..(piece.ChunkOffset(b1) + piece.ChunkLength(b1))], "D");

        if (!complete) throw new Exception("Should be complete");
        if (!SHA1.HashData(piece.Flush()!).SequenceEqual(expectedHash))
            throw new Exception("Out-of-order assembly SHA1 mismatch");
    }

    [TestMethod]
    public async Task Piece_CancelAndReReserve()
    {
        var data = new byte[32768];
        new Random(789).NextBytes(data);
        var expectedHash = SHA1.HashData(data);

        var piece = new Piece(32768);
        int b0 = piece.Reserve();
        piece.Cancel(b0);

        int b1 = piece.Reserve(); // should get cancelled block back (LIFO)
        if (b1 != 0) throw new Exception($"Cancelled block should be re-reserved first, got {b1}");
        int b2 = piece.Reserve();
        if (b2 != 1) throw new Exception($"Expected block 1, got {b2}");

        piece.Set(b1, data[piece.ChunkOffset(b1)..(piece.ChunkOffset(b1) + piece.ChunkLength(b1))], "p1");
        piece.Set(b2, data[piece.ChunkOffset(b2)..(piece.ChunkOffset(b2) + piece.ChunkLength(b2))], "p2");

        if (!SHA1.HashData(piece.Flush()!).SequenceEqual(expectedHash))
            throw new Exception("Cancel/re-reserve assembly SHA1 mismatch");
    }

    [TestMethod]
    public async Task Piece_DuplicateBlock_Ignored()
    {
        var data = new byte[16384];
        new Random(321).NextBytes(data);

        var piece = new Piece(16384);
        piece.Reserve();
        piece.Set(0, data, "peer1");
        piece.Set(0, data, "peer2"); // duplicate - should be ignored

        var assembled = piece.Flush();
        if (!assembled!.SequenceEqual(data)) throw new Exception("Duplicate block corrupted data");
    }

    [TestMethod]
    public async Task Piece_FlushTwice_NullSecondTime()
    {
        var data = new byte[100];
        new Random(111).NextBytes(data);

        var piece = new Piece(100);
        piece.Reserve();
        piece.Set(0, data, "peer1");

        var first = piece.Flush();
        if (first == null) throw new Exception("First flush should return data");
        var second = piece.Flush();
        if (second != null) throw new Exception("Second flush should return null");
    }

    [TestMethod]
    public async Task Piece_ReserveRemaining_WebSeedPattern()
    {
        var data = new byte[40000];
        new Random(555).NextBytes(data);
        var expectedHash = SHA1.HashData(data);

        var piece = new Piece(40000);
        piece.Reserve(); // block 0 taken by peer
        int wsStart = piece.ReserveRemaining(); // web seed claims rest
        if (wsStart != 1) throw new Exception($"Web seed should start at block 1, got {wsStart}");

        for (int i = 0; i < 3; i++)
        {
            int offset = piece.ChunkOffset(i);
            int len = piece.ChunkLength(i);
            piece.Set(i, data[offset..(offset + len)], i == 0 ? "peer" : "webseed");
        }

        if (!SHA1.HashData(piece.Flush()!).SequenceEqual(expectedHash))
            throw new Exception("ReserveRemaining assembly SHA1 mismatch");
    }

    [TestMethod]
    public async Task Piece_ExactBlockSize_NoRemainder()
    {
        var data = new byte[32768];
        new Random(777).NextBytes(data);
        var expectedHash = SHA1.HashData(data);

        var piece = new Piece(32768);
        if (piece.ChunkLength(0) != Piece.BlockLength) throw new Exception("Block 0 should be full size");
        if (piece.ChunkLength(1) != Piece.BlockLength) throw new Exception("Block 1 should be full size (no remainder)");

        piece.Reserve();
        piece.Reserve();
        piece.Set(0, data[..Piece.BlockLength], "p1");
        piece.Set(1, data[Piece.BlockLength..], "p2");

        if (!SHA1.HashData(piece.Flush()!).SequenceEqual(expectedHash))
            throw new Exception("Exact block size assembly SHA1 mismatch");
    }
}
