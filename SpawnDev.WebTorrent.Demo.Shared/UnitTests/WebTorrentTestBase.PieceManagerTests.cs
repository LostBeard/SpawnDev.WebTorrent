using SpawnDev.UnitTesting;
using SpawnDev.WebTorrent.Storage;
using SpawnDev.WebTorrent.Torrent;
using System.Security.Cryptography;

namespace SpawnDev.WebTorrent.Demo.Shared.UnitTests;

/// <summary>
/// PieceManager tests — block tracking, piece selection, verification, and state machine.
/// Pure logic with MemoryChunkStore — no network, no browser.
/// </summary>
public abstract partial class WebTorrentTestBase
{
    /// <summary>Helper: create metadata + PieceManager for testing with known data.</summary>
    private static (PieceManager pm, byte[] data, TorrentMetadata meta) CreateTestPieceManager(
        int dataSize = 65536, int pieceLength = 16384)
    {
        var data = new byte[dataSize];
        Random.Shared.NextBytes(data);
        var (_, meta) = TorrentCreator.CreateFromBytes("test.bin", data,
            new TorrentCreatorOptions { PieceLength = pieceLength });
        var store = new MemoryChunkStore(pieceLength);
        var pm = new PieceManager(meta, store);
        return (pm, data, meta);
    }

    // ═══════════════════════════════════════════════════════════
    //  PieceManager — Initialization
    // ═══════════════════════════════════════════════════════════

    [TestMethod]
    public async Task PM_Init_PieceCount()
    {
        var (pm, _, _) = CreateTestPieceManager(65536, 16384);
        if (pm.PieceCount != 4) throw new Exception($"PieceCount: {pm.PieceCount}");
    }

    [TestMethod]
    public async Task PM_Init_NotComplete()
    {
        var (pm, _, _) = CreateTestPieceManager();
        if (pm.IsComplete) throw new Exception("Should not be complete initially");
        if (pm.CompletedCount != 0) throw new Exception($"CompletedCount: {pm.CompletedCount}");
        if (pm.Progress != 0) throw new Exception($"Progress: {pm.Progress}");
    }

    [TestMethod]
    public async Task PM_Init_BitfieldEmpty()
    {
        var (pm, _, _) = CreateTestPieceManager();
        if (pm.Bitfield.Any(b => b))
            throw new Exception("Bitfield should be all false initially");
    }

    [TestMethod]
    public async Task PM_Init_LastPieceShorter()
    {
        // 50000 bytes, 16384 piece length → last piece is 50000 - 3*16384 = 848 bytes
        var (pm, _, _) = CreateTestPieceManager(50000, 16384);
        if (pm.PieceCount != 4) throw new Exception($"PieceCount: {pm.PieceCount}");
    }

    // ═══════════════════════════════════════════════════════════
    //  PieceManager — Block Tracking
    // ═══════════════════════════════════════════════════════════

    [TestMethod]
    public async Task PM_GetNextBlock_ReturnsFirstBlock()
    {
        var (pm, _, _) = CreateTestPieceManager();
        var (offset, length) = pm.GetNextBlock(0);
        if (offset != 0) throw new Exception($"First block offset: {offset}");
        if (length != PieceManager.BlockSize) throw new Exception($"Block length: {length}");
    }

    [TestMethod]
    public async Task PM_GetNextBlock_Sequential()
    {
        var (pm, _, _) = CreateTestPieceManager(65536, 16384);
        // Piece 0 = 16384 bytes = 1 block (16384/16384)
        var (off1, len1) = pm.GetNextBlock(0);
        if (off1 != 0) throw new Exception($"Block 0 offset: {off1}");
        // After requesting, next call should return -1 (all blocks requested)
        var (off2, _) = pm.GetNextBlock(0);
        if (off2 != -1) throw new Exception($"Should be -1 after all requested, got {off2}");
    }

    [TestMethod]
    public async Task PM_GetNextBlock_MultiBlock()
    {
        // 4 blocks per piece: pieceLength=65536, blockSize=16384
        var (pm, _, _) = CreateTestPieceManager(65536, 65536);
        var (off0, _) = pm.GetNextBlock(0);
        if (off0 != 0) throw new Exception($"Block 0: {off0}");
        var (off1, _) = pm.GetNextBlock(0);
        if (off1 != 16384) throw new Exception($"Block 1: {off1}");
        var (off2, _) = pm.GetNextBlock(0);
        if (off2 != 32768) throw new Exception($"Block 2: {off2}");
        var (off3, _) = pm.GetNextBlock(0);
        if (off3 != 49152) throw new Exception($"Block 3: {off3}");
        var (off4, _) = pm.GetNextBlock(0);
        if (off4 != -1) throw new Exception($"Should be -1: {off4}");
    }

    [TestMethod]
    public async Task PM_CancelBlock_AllowsReRequest()
    {
        var (pm, _, _) = CreateTestPieceManager();
        pm.GetNextBlock(0); // request block 0
        pm.CancelBlock(0, 0); // cancel it
        var (offset, _) = pm.GetNextBlock(0); // should be available again
        if (offset != 0) throw new Exception($"Cancelled block should be re-requestable: {offset}");
    }

    // ═══════════════════════════════════════════════════════════
    //  PieceManager — Piece Verification
    // ═══════════════════════════════════════════════════════════

    [TestMethod]
    public async Task PM_ReceiveBlock_CorrectData_Verifies()
    {
        var (pm, data, _) = CreateTestPieceManager(16384, 16384);
        // Single piece, single block
        var pieceData = data.AsSpan(0, 16384).ToArray();
        pm.GetNextBlock(0); // mark as requested
        var result = await pm.ReceiveBlockAsync(0, 0, pieceData);
        if (!result) throw new Exception("Correct data should verify");
        if (!pm.Bitfield[0]) throw new Exception("Bitfield should be set");
        if (pm.CompletedCount != 1) throw new Exception($"CompletedCount: {pm.CompletedCount}");
        if (!pm.IsComplete) throw new Exception("Should be complete (1 piece torrent)");
    }

    [TestMethod]
    public async Task PM_ReceiveBlock_CorruptData_Rejects()
    {
        var (pm, data, _) = CreateTestPieceManager(16384, 16384);
        var corrupt = new byte[16384];
        Array.Fill(corrupt, (byte)0xFF);
        pm.GetNextBlock(0);
        var result = await pm.ReceiveBlockAsync(0, 0, corrupt);
        if (result) throw new Exception("Corrupt data should NOT verify");
        if (pm.Bitfield[0]) throw new Exception("Bitfield should NOT be set for corrupt piece");
        if (pm.CompletedCount != 0) throw new Exception($"CompletedCount: {pm.CompletedCount}");
    }

    [TestMethod]
    public async Task PM_ReceiveCompletePiece_Verifies()
    {
        var (pm, data, _) = CreateTestPieceManager(32768, 16384);
        // Receive piece 0 as complete piece (web seed style)
        var piece0 = data.AsSpan(0, 16384).ToArray();
        var result = await pm.ReceiveCompletePieceAsync(0, piece0);
        if (!result) throw new Exception("Complete piece with correct data should verify");
        if (pm.CompletedCount != 1) throw new Exception($"CompletedCount: {pm.CompletedCount}");
        if (pm.Progress != 0.5) throw new Exception($"Progress should be 0.5: {pm.Progress}");
    }

    [TestMethod]
    public async Task PM_ReceiveCompletePiece_AllPieces()
    {
        var (pm, data, meta) = CreateTestPieceManager(65536, 16384);
        // Receive all 4 pieces
        for (int i = 0; i < 4; i++)
        {
            var offset = i * 16384;
            var piece = data.AsSpan(offset, 16384).ToArray();
            var result = await pm.ReceiveCompletePieceAsync(i, piece);
            if (!result) throw new Exception($"Piece {i} should verify");
        }
        if (!pm.IsComplete) throw new Exception("Should be complete after all pieces");
        if (pm.Progress != 1.0) throw new Exception($"Progress: {pm.Progress}");
    }

    [TestMethod]
    public async Task PM_ReceiveBlock_OutOfRange_Ignored()
    {
        var (pm, _, _) = CreateTestPieceManager();
        var result = await pm.ReceiveBlockAsync(-1, 0, new byte[16384]);
        if (result) throw new Exception("Negative index should return false");
        result = await pm.ReceiveBlockAsync(999, 0, new byte[16384]);
        if (result) throw new Exception("Out of range index should return false");
    }

    [TestMethod]
    public async Task PM_ReceiveBlock_AlreadyComplete_Ignored()
    {
        var (pm, data, _) = CreateTestPieceManager(16384, 16384);
        var piece = data.AsSpan(0, 16384).ToArray();
        await pm.ReceiveCompletePieceAsync(0, piece);
        // Try to receive again
        var result = await pm.ReceiveBlockAsync(0, 0, piece);
        if (result) throw new Exception("Already complete piece should return false for blocks");
        if (pm.CompletedCount != 1) throw new Exception("Count should still be 1");
    }

    // ═══════════════════════════════════════════════════════════
    //  PieceManager — Piece Selection
    // ═══════════════════════════════════════════════════════════

    [TestMethod]
    public async Task PM_SelectPiece_PeerHasAll()
    {
        var (pm, _, _) = CreateTestPieceManager();
        var peerBitfield = new bool[] { true, true, true, true };
        var idx = pm.SelectPiece(peerBitfield);
        if (idx < 0 || idx > 3) throw new Exception($"Should select a valid piece: {idx}");
    }

    [TestMethod]
    public async Task PM_SelectPiece_PeerHasNone()
    {
        var (pm, _, _) = CreateTestPieceManager();
        var peerBitfield = new bool[] { false, false, false, false };
        var idx = pm.SelectPiece(peerBitfield);
        if (idx != -1) throw new Exception($"Should return -1 when peer has nothing: {idx}");
    }

    [TestMethod]
    public async Task PM_SelectPiece_PeerHasOne()
    {
        var (pm, _, _) = CreateTestPieceManager();
        var peerBitfield = new bool[] { false, false, true, false };
        var idx = pm.SelectPiece(peerBitfield);
        if (idx != 2) throw new Exception($"Should select piece 2: {idx}");
    }

    [TestMethod]
    public async Task PM_SelectPiece_Sequential()
    {
        var (pm, _, _) = CreateTestPieceManager();
        var peerBitfield = new bool[] { true, true, true, true };
        var idx = pm.SelectPiece(peerBitfield, "sequential");
        if (idx != 0) throw new Exception($"Sequential should pick piece 0: {idx}");
    }

    [TestMethod]
    public async Task PM_SelectPiece_SkipsComplete()
    {
        var (pm, data, _) = CreateTestPieceManager();
        // Complete piece 0
        await pm.ReceiveCompletePieceAsync(0, data.AsSpan(0, 16384).ToArray());
        var peerBitfield = new bool[] { true, true, true, true };
        var idx = pm.SelectPiece(peerBitfield, "sequential");
        if (idx != 1) throw new Exception($"Should skip completed piece 0: {idx}");
    }

    // ═══════════════════════════════════════════════════════════
    //  PieceManager — MarkComplete
    // ═══════════════════════════════════════════════════════════

    [TestMethod]
    public async Task PM_MarkComplete_UpdatesBitfield()
    {
        var (pm, _, _) = CreateTestPieceManager();
        pm.MarkComplete(2);
        if (!pm.Bitfield[2]) throw new Exception("Bitfield[2] should be set");
        if (pm.CompletedCount != 1) throw new Exception($"CompletedCount: {pm.CompletedCount}");
    }

    // ═══════════════════════════════════════════════════════════
    //  PieceManager — Events
    // ═══════════════════════════════════════════════════════════

    [TestMethod]
    public async Task PM_Events_OnPieceComplete()
    {
        var (pm, data, _) = CreateTestPieceManager(16384, 16384);
        int? completedPiece = null;
        pm.OnPieceComplete += idx => completedPiece = idx;
        await pm.ReceiveCompletePieceAsync(0, data);
        if (completedPiece != 0) throw new Exception($"OnPieceComplete not fired or wrong index: {completedPiece}");
    }

    [TestMethod]
    public async Task PM_Events_OnBlockReceived()
    {
        var (pm, data, _) = CreateTestPieceManager(16384, 16384);
        int? receivedPiece = null;
        int? receivedOffset = null;
        pm.OnBlockReceived += (pi, off) => { receivedPiece = pi; receivedOffset = off; };
        pm.GetNextBlock(0);
        await pm.ReceiveBlockAsync(0, 0, data);
        if (receivedPiece != 0) throw new Exception($"OnBlockReceived piece: {receivedPiece}");
        if (receivedOffset != 0) throw new Exception($"OnBlockReceived offset: {receivedOffset}");
    }
}
