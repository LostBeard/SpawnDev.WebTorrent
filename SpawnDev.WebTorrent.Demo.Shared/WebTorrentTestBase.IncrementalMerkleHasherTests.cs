using SpawnDev.UnitTesting;
using SpawnDev.WebTorrent;

namespace SpawnDev.WebTorrent.Demo.Shared;

/// <summary>
/// Equivalence tests between <see cref="IncrementalMerkleHasher"/> (streaming API)
/// and <see cref="MerkleHasher.ComputeFileRoot"/> / <see cref="MerkleHasher.ComputePieceLayer"/>.
/// Streaming hasher must produce byte-identical output to the one-shot hasher — if they
/// diverge, streaming v2 torrents silently disagree with in-memory v2 torrents, breaking
/// interop. Migrated from NUnit IncrementalMerkleHasherTests.cs.
/// </summary>
public abstract partial class WebTorrentTestBase
{
    // ---- one-leaf-per-piece cases (pieceSize == 16 KiB) ----

    [TestMethod] public async Task IncrementalMerkle_OneLeafPerPiece_FileSize0()
        => await IncrementalMerkleTests_AssertEquivalent(fileSize: 0, pieceSize: MerkleHasher.LeafSize);
    [TestMethod] public async Task IncrementalMerkle_OneLeafPerPiece_FileSize1()
        => await IncrementalMerkleTests_AssertEquivalent(fileSize: 1, pieceSize: MerkleHasher.LeafSize);
    [TestMethod] public async Task IncrementalMerkle_OneLeafPerPiece_FileSize16383()
        => await IncrementalMerkleTests_AssertEquivalent(fileSize: 16383, pieceSize: MerkleHasher.LeafSize);
    [TestMethod] public async Task IncrementalMerkle_OneLeafPerPiece_FileSize16384()
        => await IncrementalMerkleTests_AssertEquivalent(fileSize: 16384, pieceSize: MerkleHasher.LeafSize);
    [TestMethod] public async Task IncrementalMerkle_OneLeafPerPiece_FileSize16385()
        => await IncrementalMerkleTests_AssertEquivalent(fileSize: 16385, pieceSize: MerkleHasher.LeafSize);
    [TestMethod] public async Task IncrementalMerkle_OneLeafPerPiece_FileSize32768()
        => await IncrementalMerkleTests_AssertEquivalent(fileSize: 32768, pieceSize: MerkleHasher.LeafSize);
    [TestMethod] public async Task IncrementalMerkle_OneLeafPerPiece_FileSize32769()
        => await IncrementalMerkleTests_AssertEquivalent(fileSize: 32769, pieceSize: MerkleHasher.LeafSize);
    [TestMethod] public async Task IncrementalMerkle_OneLeafPerPiece_FileSize200000()
        => await IncrementalMerkleTests_AssertEquivalent(fileSize: 200000, pieceSize: MerkleHasher.LeafSize);

    // ---- multi-leaf-per-piece cases ----

    [TestMethod] public async Task IncrementalMerkle_MultiLeaf_Empty()
        => await IncrementalMerkleTests_AssertEquivalent(fileSize: 0, pieceSize: 32768);
    [TestMethod] public async Task IncrementalMerkle_MultiLeaf_500Bytes()
        => await IncrementalMerkleTests_AssertEquivalent(fileSize: 500, pieceSize: 32768);
    [TestMethod] public async Task IncrementalMerkle_MultiLeaf_OneLeaf()
        => await IncrementalMerkleTests_AssertEquivalent(fileSize: 16384, pieceSize: 32768);
    [TestMethod] public async Task IncrementalMerkle_MultiLeaf_ExactOnePiece()
        => await IncrementalMerkleTests_AssertEquivalent(fileSize: 32768, pieceSize: 32768);
    [TestMethod] public async Task IncrementalMerkle_MultiLeaf_OnePiecePlus1()
        => await IncrementalMerkleTests_AssertEquivalent(fileSize: 32769, pieceSize: 32768);
    [TestMethod] public async Task IncrementalMerkle_MultiLeaf_MultiPiece100k()
        => await IncrementalMerkleTests_AssertEquivalent(fileSize: 100000, pieceSize: 32768);
    [TestMethod] public async Task IncrementalMerkle_MultiLeaf_500k_4LeavesPerPiece()
        => await IncrementalMerkleTests_AssertEquivalent(fileSize: 500000, pieceSize: 65536);
    [TestMethod] public async Task IncrementalMerkle_MultiLeaf_1MiB_8LeavesPerPiece()
        => await IncrementalMerkleTests_AssertEquivalent(fileSize: 1048576, pieceSize: 131072);

    // ---- chunking invariance ----

    [TestMethod] public async Task IncrementalMerkle_ChunkingIsIrrelevant_Chunk1()
        => await IncrementalMerkleTests_AssertChunkingInvariant(chunkSize: 1);
    [TestMethod] public async Task IncrementalMerkle_ChunkingIsIrrelevant_Chunk7()
        => await IncrementalMerkleTests_AssertChunkingInvariant(chunkSize: 7);
    [TestMethod] public async Task IncrementalMerkle_ChunkingIsIrrelevant_Chunk16383()
        => await IncrementalMerkleTests_AssertChunkingInvariant(chunkSize: 16383);
    [TestMethod] public async Task IncrementalMerkle_ChunkingIsIrrelevant_Chunk16384()
        => await IncrementalMerkleTests_AssertChunkingInvariant(chunkSize: 16384);
    [TestMethod] public async Task IncrementalMerkle_ChunkingIsIrrelevant_Chunk16385()
        => await IncrementalMerkleTests_AssertChunkingInvariant(chunkSize: 16385);
    [TestMethod] public async Task IncrementalMerkle_ChunkingIsIrrelevant_Chunk100000()
        => await IncrementalMerkleTests_AssertChunkingInvariant(chunkSize: 100000);

    // ---- stateful behavior ----

    [TestMethod]
    public async Task IncrementalMerkle_ByteByByte_MatchesOneShot()
    {
        // Feed one byte at a time. Tests the partial-leaf accumulation path every iteration.
        var data = IncrementalMerkleTests_NewRandom(40000, seed: 801);
        int pieceSize = 32768;

        var oneShotRoot = MerkleHasher.ComputeFileRoot(data, pieceSize);

        var hasher = MerkleHasher.CreateIncremental(pieceSize);
        for (int i = 0; i < data.Length; i++) hasher.Update(data.AsSpan(i, 1));
        var (root, _) = hasher.Finish();

        if (!root.SequenceEqual(oneShotRoot))
            throw new Exception("byte-by-byte incremental hash diverges from one-shot");
        await Task.CompletedTask;
    }

    [TestMethod]
    public async Task IncrementalMerkle_EmptyFile_ReturnsLevelZeroPad()
    {
        var hasher = MerkleHasher.CreateIncremental(MerkleHasher.LeafSize);
        var (root, layer) = hasher.Finish();
        if (!root.SequenceEqual(MerkleHasher.PadHashAtLevel(0)))
            throw new Exception("empty-file root should equal level-0 pad");
        if (layer.Length != 0) throw new Exception($"empty-file piece layer should be empty, got {layer.Length}");
        if (hasher.TotalBytesHashed != 0) throw new Exception($"TotalBytesHashed should be 0 on empty, got {hasher.TotalBytesHashed}");
        await Task.CompletedTask;
    }

    [TestMethod]
    public async Task IncrementalMerkle_TotalBytes_TrackedAcrossUpdates()
    {
        var hasher = MerkleHasher.CreateIncremental(65536);
        hasher.Update(new byte[1000]);
        hasher.Update(new byte[500]);
        hasher.Update(new byte[17000]);
        if (hasher.TotalBytesHashed != 18500)
            throw new Exception($"TotalBytesHashed = {hasher.TotalBytesHashed}, expected 18500");
        _ = hasher.Finish();
        await Task.CompletedTask;
    }

    [TestMethod]
    public async Task IncrementalMerkle_CompletedPieceCount_GrowsAsPiecesFill()
    {
        int pieceSize = 32768;
        var hasher = MerkleHasher.CreateIncremental(pieceSize);
        if (hasher.CompletedPieceCount != 0) throw new Exception($"initial CompletedPieceCount={hasher.CompletedPieceCount}, expected 0");

        hasher.Update(new byte[pieceSize]);
        if (hasher.CompletedPieceCount != 1) throw new Exception($"after 1 piece: CompletedPieceCount={hasher.CompletedPieceCount}, expected 1");

        hasher.Update(new byte[pieceSize / 2]);
        if (hasher.CompletedPieceCount != 1) throw new Exception($"after half-fill: CompletedPieceCount={hasher.CompletedPieceCount}, expected 1 (partial does not roll up)");

        hasher.Update(new byte[pieceSize / 2]);
        if (hasher.CompletedPieceCount != 2) throw new Exception($"after fill complete: CompletedPieceCount={hasher.CompletedPieceCount}, expected 2");

        _ = hasher.Finish();
        await Task.CompletedTask;
    }

    [TestMethod]
    public async Task IncrementalMerkle_FinishTwice_Throws()
    {
        var hasher = MerkleHasher.CreateIncremental(MerkleHasher.LeafSize);
        hasher.Update(new byte[5]);
        _ = hasher.Finish();
        try { _ = hasher.Finish(); }
        catch (InvalidOperationException) { await Task.CompletedTask; return; }
        throw new Exception("expected InvalidOperationException on second Finish");
    }

    [TestMethod]
    public async Task IncrementalMerkle_UpdateAfterFinish_Throws()
    {
        var hasher = MerkleHasher.CreateIncremental(MerkleHasher.LeafSize);
        _ = hasher.Finish();
        try { hasher.Update(new byte[1]); }
        catch (InvalidOperationException) { await Task.CompletedTask; return; }
        throw new Exception("expected InvalidOperationException on Update after Finish");
    }

    [TestMethod] public async Task IncrementalMerkle_InvalidPieceSize_15000_Throws()
        => await IncrementalMerkleTests_AssertInvalidPieceSizeThrows(15000);
    [TestMethod] public async Task IncrementalMerkle_InvalidPieceSize_0_Throws()
        => await IncrementalMerkleTests_AssertInvalidPieceSizeThrows(0);
    [TestMethod] public async Task IncrementalMerkle_InvalidPieceSize_Negative_Throws()
        => await IncrementalMerkleTests_AssertInvalidPieceSizeThrows(-16384);
    [TestMethod] public async Task IncrementalMerkle_InvalidPieceSize_49152_Throws()
        => await IncrementalMerkleTests_AssertInvalidPieceSizeThrows(49152);

    // ---- helpers ----

    private static async Task IncrementalMerkleTests_AssertEquivalent(int fileSize, int pieceSize)
    {
        var data = IncrementalMerkleTests_NewRandom(fileSize, seed: 800 + (fileSize % 100));

        var expectedRoot = MerkleHasher.ComputeFileRoot(data, pieceSize);
        var expectedLayer = fileSize > pieceSize
            ? MerkleHasher.ComputePieceLayer(data, pieceSize)
            : Array.Empty<byte[]>();

        var hasher = MerkleHasher.CreateIncremental(pieceSize);
        hasher.Update(data);
        var (actualRoot, actualLayer) = hasher.Finish();

        if (!actualRoot.SequenceEqual(expectedRoot))
            throw new Exception($"fileRoot mismatch for size={fileSize} piece={pieceSize}");

        if (actualLayer.Length != expectedLayer.Length)
            throw new Exception($"pieceLayer count mismatch for size={fileSize} piece={pieceSize}: got {actualLayer.Length}, expected {expectedLayer.Length}");
        for (int i = 0; i < expectedLayer.Length; i++)
        {
            if (!actualLayer[i].SequenceEqual(expectedLayer[i]))
                throw new Exception($"pieceLayer[{i}] mismatch for size={fileSize} piece={pieceSize}");
        }
        await Task.CompletedTask;
    }

    private static async Task IncrementalMerkleTests_AssertChunkingInvariant(int chunkSize)
    {
        var data = IncrementalMerkleTests_NewRandom(500000, seed: 820);
        int pieceSize = 65536;

        var oneShotRoot = MerkleHasher.ComputeFileRoot(data, pieceSize);

        var hasher = MerkleHasher.CreateIncremental(pieceSize);
        for (int pos = 0; pos < data.Length; pos += chunkSize)
        {
            int take = Math.Min(chunkSize, data.Length - pos);
            hasher.Update(data.AsSpan(pos, take));
        }
        var (root, _) = hasher.Finish();

        if (!root.SequenceEqual(oneShotRoot))
            throw new Exception($"chunkSize={chunkSize} produced different hash than one-shot");
        await Task.CompletedTask;
    }

    private static async Task IncrementalMerkleTests_AssertInvalidPieceSizeThrows(int pieceSize)
    {
        try { MerkleHasher.CreateIncremental(pieceSize); }
        catch (ArgumentOutOfRangeException) { await Task.CompletedTask; return; }
        throw new Exception($"expected ArgumentOutOfRangeException for CreateIncremental({pieceSize})");
    }

    private static byte[] IncrementalMerkleTests_NewRandom(int size, int seed)
    {
        if (size == 0) return Array.Empty<byte>();
        var data = new byte[size];
        new Random(seed).NextBytes(data);
        return data;
    }
}
