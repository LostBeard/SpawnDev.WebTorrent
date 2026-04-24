using System.Reflection;
using SpawnDev.UnitTesting;
using SpawnDev.WebTorrent;
using SpawnDev.WebTorrent.Storage;

namespace SpawnDev.WebTorrent.Demo.Shared;

/// <summary>
/// V2HashRequestCoordinator integration with Torrent: allocation, seed path, and event
/// forwarding across multiple wires. Migrated from NUnit TorrentV2HashCoordinationTests.
/// </summary>
public abstract partial class WebTorrentTestBase
{
    [TestMethod]
    public async Task TorrentV2Coord_SetMetadata_AllocatesV2Coordinator_ForV2Torrent()
    {
        var torrent = TorrentV2CoordTests_MakeV2Torrent(fileSize: 128 * 1024, pieceSize: 65536);
        if (torrent.V2HashCoord is null) throw new Exception("v2 torrent must allocate V2HashCoord");
        if (torrent.MetaVersion != 2) throw new Exception($"MetaVersion={torrent.MetaVersion}, expected 2");
        if (torrent.FileRoots.Length != 1) throw new Exception($"FileRoots.Length={torrent.FileRoots.Length}");
        if (torrent.PieceLayers.Count != 1)
            throw new Exception($"multi-piece file must populate piece layers, got count {torrent.PieceLayers.Count}");
        await Task.CompletedTask;
    }

    [TestMethod]
    public async Task TorrentV2Coord_SetMetadata_NoCoordinator_ForV1Torrent()
    {
        var torrent = TorrentV2CoordTests_MakeV1Torrent(pieceSize: 16384, pieceCount: 4);
        if (torrent.V2HashCoord is not null)
            throw new Exception("v1 torrent must NOT allocate V2HashCoord");
        await Task.CompletedTask;
    }

    [TestMethod]
    public async Task TorrentV2Coord_SeedPath_RespondsToPeerHashRequest_WithVerifiableHashes()
    {
        var torrent = TorrentV2CoordTests_MakeV2Torrent(fileSize: 8 * 65536, pieceSize: 65536);
        var fileRoot = torrent.FileRoots[0];
        int pieceLayerLevel = 2;

        var wire = TorrentV2CoordTests_AttachHandshakedWire(torrent, out var sentFrames);

        var req = new Bep52WireMessages.HashRequest(fileRoot, (uint)pieceLayerLevel, 0, 4, 1);
        wire.DataReceived(TorrentV2CoordTests_MakeMessage(Bep52WireMessages.MessageIdHashRequest, Bep52WireMessages.Encode(req)));

        var hashesFrame = sentFrames.FirstOrDefault(f => f.Length > 4 && f[4] == Bep52WireMessages.MessageIdHashes);
        if (hashesFrame is null) throw new Exception("torrent must answer valid hash_request with a hashes message");

        var payload = new byte[hashesFrame.Length - 5];
        Buffer.BlockCopy(hashesFrame, 5, payload, 0, payload.Length);
        var decoded = Bep52WireMessages.DecodeHashes(payload);
        if (!decoded.PiecesRoot.SequenceEqual(fileRoot)) throw new Exception("decoded PiecesRoot mismatch");
        if (decoded.HashList.Length != 4 + 1) throw new Exception($"HashList.Length={decoded.HashList.Length}, expected 5");
        if (!MerkleProofVerifier.Verify(decoded))
            throw new Exception("the hashes we sent back must round-trip through MerkleProofVerifier");
        await Task.CompletedTask;
    }

    [TestMethod]
    public async Task TorrentV2Coord_SeedPath_Rejects_UnknownRoot()
    {
        var torrent = TorrentV2CoordTests_MakeV2Torrent(fileSize: 8 * 65536, pieceSize: 65536);
        var wire = TorrentV2CoordTests_AttachHandshakedWire(torrent, out var sentFrames);

        var req = new Bep52WireMessages.HashRequest(new byte[32], 2, 0, 4, 1);
        wire.DataReceived(TorrentV2CoordTests_MakeMessage(Bep52WireMessages.MessageIdHashRequest, Bep52WireMessages.Encode(req)));

        var rejectFrame = sentFrames.FirstOrDefault(f => f.Length > 4 && f[4] == Bep52WireMessages.MessageIdHashReject);
        if (rejectFrame is null) throw new Exception("unknown root must produce hash_reject");
        await Task.CompletedTask;
    }

    [TestMethod]
    public async Task TorrentV2Coord_SeedPath_Rejects_LeafLevelRequest_WhenPiecesAreMissing()
    {
        // base_layer=0 leaf-level requests are refused SYNCHRONOUSLY on the wire thread
        // when no pieces are stored - no race against a fire-and-forget async reply.
        var torrent = TorrentV2CoordTests_MakeV2Torrent(fileSize: 8 * 65536, pieceSize: 65536);
        var fileRoot = torrent.FileRoots[0];
        var wire = TorrentV2CoordTests_AttachHandshakedWire(torrent, out var sentFrames);

        var req = new Bep52WireMessages.HashRequest(fileRoot, 0, 0, 2, 4);
        wire.DataReceived(TorrentV2CoordTests_MakeMessage(Bep52WireMessages.MessageIdHashRequest, Bep52WireMessages.Encode(req)));

        var rejectFrame = sentFrames.FirstOrDefault(f => f.Length > 4 && f[4] == Bep52WireMessages.MessageIdHashReject);
        if (rejectFrame is null)
            throw new Exception("leaf-level request without stored pieces must be refused synchronously");
        await Task.CompletedTask;
    }

    [TestMethod]
    public async Task TorrentV2Coord_ClientPath_IncomingHashesResolvesRequestAsync()
    {
        var torrent = TorrentV2CoordTests_MakeV2Torrent(fileSize: 8 * 65536, pieceSize: 65536);
        var fileRoot = torrent.FileRoots[0];
        int pieceLayerLevel = 2;

        var wireA = TorrentV2CoordTests_AttachHandshakedWire(torrent, out _);
        var wireB = TorrentV2CoordTests_AttachHandshakedWire(torrent, out _);

        var pieceLayerConcat = torrent.PieceLayers[fileRoot];
        var pieceLayer = new byte[pieceLayerConcat.Length / 32][];
        for (int i = 0; i < pieceLayer.Length; i++)
        {
            pieceLayer[i] = new byte[32];
            Buffer.BlockCopy(pieceLayerConcat, i * 32, pieceLayer[i], 0, 32);
        }
        var built = MerkleProofBuilder.Build(
            pieceLayer, pieceLayerLevel, index: 0, length: 4, proofLayers: 1, expectedRoot: fileRoot)!.Value;
        var hashList = new byte[built.baseLayer.Length + built.proof.Length][];
        Array.Copy(built.baseLayer, 0, hashList, 0, built.baseLayer.Length);
        Array.Copy(built.proof, 0, hashList, built.baseLayer.Length, built.proof.Length);

        var req = new Bep52WireMessages.HashRequest(fileRoot, (uint)pieceLayerLevel, 0, 4, 1);
        var reqTask = torrent.V2HashCoord!.RequestAsync(req, send: _ => Task.CompletedTask);

        var hashesMsg = new Bep52WireMessages.Hashes(fileRoot, (uint)pieceLayerLevel, 0, 4, 1, hashList);
        wireB.DataReceived(TorrentV2CoordTests_MakeMessage(Bep52WireMessages.MessageIdHashes, Bep52WireMessages.Encode(hashesMsg)));

        var result = await reqTask.WaitAsync(TimeSpan.FromSeconds(2));
        if (result.Length != 5) throw new Exception($"per-torrent coordinator must correlate across peers: result.Length={result.Length}");
        if (torrent.V2HashCoord.OutstandingCount != 0)
            throw new Exception($"OutstandingCount={torrent.V2HashCoord.OutstandingCount}, expected 0");
    }

    [TestMethod]
    public async Task TorrentV2Coord_ClientPath_IncomingHashRejectFailsRequestAsync()
    {
        var torrent = TorrentV2CoordTests_MakeV2Torrent(fileSize: 8 * 65536, pieceSize: 65536);
        var fileRoot = torrent.FileRoots[0];
        var wire = TorrentV2CoordTests_AttachHandshakedWire(torrent, out _);

        var req = new Bep52WireMessages.HashRequest(fileRoot, 2, 0, 4, 1);
        var reqTask = torrent.V2HashCoord!.RequestAsync(req, send: _ => Task.CompletedTask);

        var reject = new Bep52WireMessages.HashReject(fileRoot, 2, 0, 4, 1);
        wire.DataReceived(TorrentV2CoordTests_MakeMessage(Bep52WireMessages.MessageIdHashReject, Bep52WireMessages.Encode(reject)));

        try { await reqTask; }
        catch (HashRejectedException) { await Task.CompletedTask; return; }
        throw new Exception("expected HashRejectedException");
    }

    [TestMethod]
    public async Task TorrentV2Coord_RequestV2HashesAsync_ThrowsForV1Torrent()
    {
        var torrent = TorrentV2CoordTests_MakeV1Torrent(pieceSize: 16384, pieceCount: 4);
        var req = new Bep52WireMessages.HashRequest(new byte[32], 0, 0, 2, 1);
        try { await torrent.RequestV2HashesAsync(req); }
        catch (InvalidOperationException) { await Task.CompletedTask; return; }
        throw new Exception("expected InvalidOperationException on v1 torrent");
    }

    [TestMethod]
    public async Task TorrentV2Coord_RequestV2HashesAsync_ThrowsWhenNoWireAvailable()
    {
        var torrent = TorrentV2CoordTests_MakeV2Torrent(fileSize: 8 * 65536, pieceSize: 65536);
        var req = new Bep52WireMessages.HashRequest(torrent.FileRoots[0], 2, 0, 4, 1);
        try { await torrent.RequestV2HashesAsync(req); }
        catch (InvalidOperationException) { await Task.CompletedTask; return; }
        throw new Exception("expected InvalidOperationException when no wire available");
    }

    [TestMethod]
    public async Task TorrentV2Coord_TryBuildV2HashesPayload_RespectsPieceLayerLevel()
    {
        var torrent = TorrentV2CoordTests_MakeV2Torrent(fileSize: 4 * 16384, pieceSize: 16384);
        var fileRoot = torrent.FileRoots[0];

        var req = new Bep52WireMessages.HashRequest(fileRoot, 0, 0, 4, 0);
        var payload = torrent.TryBuildV2HashesPayload(req);
        if (payload is null) throw new Exception("16KB-piece torrent has piece layer at level 0; base_layer=0 must serve");
        await Task.CompletedTask;
    }

    [TestMethod]
    public async Task TorrentV2Coord_TryBuildV2HashesPayloadAsync_ServesLeafLevelFromStore()
    {
        int pieceSize = 65536;
        int pieceCount = 4;
        var data = new byte[pieceSize * pieceCount];
        new Random(91).NextBytes(data);
        var opts = new TorrentCreatorOptions { MetaVersion = 2, PieceLength = pieceSize };
        var (bytes, _) = TorrentCreator.CreateFromBytes("leaf-probe.bin", data, opts);
        var parsed = TorrentParser.Parse(bytes);

        var t = new Torrent();
        t.SetMetadata(parsed);

        var storeField = typeof(Torrent).GetField("_store",
            BindingFlags.NonPublic | BindingFlags.Instance);
        var store = (IChunkStore)storeField!.GetValue(t)!;
        for (int i = 0; i < pieceCount; i++)
        {
            var pieceData = new byte[pieceSize];
            Array.Copy(data, i * pieceSize, pieceData, 0, pieceSize);
            await store.PutAsync(i, pieceData);
            t.Bitfield[i] = true;
        }

        var fileRoot = t.FileRoots[0];
        var req = new Bep52WireMessages.HashRequest(fileRoot, BaseLayer: 0, Index: 0, Length: 4, ProofLayers: 2);

        var payload = await t.TryBuildV2HashesPayloadAsync(req);
        if (payload is null) throw new Exception("leaf-level request must be served when all pieces are stored");
        var (baseLayer, proof) = payload.Value;
        if (baseLayer.Length != 4) throw new Exception($"baseLayer.Length={baseLayer.Length}, expected 4");
        if (proof.Length != 2) throw new Exception($"proof.Length={proof.Length}, expected 2");

        if (!MerkleProofVerifier.Verify(fileRoot, 0, baseLayer, proof))
            throw new Exception("leaf-level seed output must pass MerkleProofVerifier round-trip");
    }

    [TestMethod]
    public async Task TorrentV2Coord_TryBuildV2HashesPayloadAsync_ReturnsNull_WhenPiecesMissing()
    {
        int pieceSize = 65536;
        var data = new byte[pieceSize * 4];
        new Random(92).NextBytes(data);
        var opts = new TorrentCreatorOptions { MetaVersion = 2, PieceLength = pieceSize };
        var (bytes, _) = TorrentCreator.CreateFromBytes("incomplete.bin", data, opts);

        var t = new Torrent();
        t.SetMetadata(TorrentParser.Parse(bytes));

        var storeField = typeof(Torrent).GetField("_store",
            BindingFlags.NonPublic | BindingFlags.Instance);
        var store = (IChunkStore)storeField!.GetValue(t)!;
        for (int i = 0; i < 2; i++)
        {
            var pieceData = new byte[pieceSize];
            Array.Copy(data, i * pieceSize, pieceData, 0, pieceSize);
            await store.PutAsync(i, pieceData);
            t.Bitfield[i] = true;
        }

        var fileRoot = t.FileRoots[0];
        var req = new Bep52WireMessages.HashRequest(fileRoot, BaseLayer: 0, Index: 0, Length: 4, ProofLayers: 2);
        var payload = await t.TryBuildV2HashesPayloadAsync(req);
        if (payload is not null)
            throw new Exception("leaf-level must refuse when pieces are missing - can't re-hash what we don't have");
    }

    // ---- helpers ----

    private static Torrent TorrentV2CoordTests_MakeV2Torrent(int fileSize, int pieceSize)
    {
        var data = new byte[fileSize];
        new Random(fileSize ^ pieceSize).NextBytes(data);
        var opts = new TorrentCreatorOptions { MetaVersion = 2, PieceLength = pieceSize };
        var (bytes, _) = TorrentCreator.CreateFromBytes("t.bin", data, opts);
        var parsed = TorrentParser.Parse(bytes);

        var t = new Torrent();
        t.SetMetadata(parsed);
        return t;
    }

    private static Torrent TorrentV2CoordTests_MakeV1Torrent(int pieceSize, int pieceCount)
    {
        var data = new byte[pieceSize * pieceCount];
        new Random(1).NextBytes(data);
        var opts = new TorrentCreatorOptions { MetaVersion = 0, PieceLength = pieceSize, HashAlgorithm = "SHA-1" };
        var (bytes, _) = TorrentCreator.CreateFromBytes("t.bin", data, opts);
        var parsed = TorrentParser.Parse(bytes);

        var t = new Torrent();
        t.SetMetadata(parsed);
        return t;
    }

    private static Wire TorrentV2CoordTests_AttachHandshakedWire(Torrent torrent, out List<byte[]> capturedSent)
    {
        var sent = new List<byte[]>();
        capturedSent = sent;

        var wire = new Wire();
        wire.SendRaw = data => { sent.Add(data); return Task.CompletedTask; };
        wire.DataReceived(TorrentV2CoordTests_MakeHandshake());

        torrent.Wires.Add(wire);
        torrent.OnWireWithMetadata(wire);
        return wire;
    }

    private static byte[] TorrentV2CoordTests_MakeHandshake()
    {
        var msg = new byte[68];
        msg[0] = 19;
        "BitTorrent protocol"u8.CopyTo(msg.AsSpan(1));
        var reserved = new byte[8];
        reserved[5] |= 0x10;
        reserved.CopyTo(msg, 20);
        return msg;
    }

    private static byte[] TorrentV2CoordTests_MakeMessage(byte id, byte[] payload)
    {
        int len = 1 + payload.Length;
        var msg = new byte[4 + len];
        msg[0] = (byte)((len >> 24) & 0xFF);
        msg[1] = (byte)((len >> 16) & 0xFF);
        msg[2] = (byte)((len >> 8) & 0xFF);
        msg[3] = (byte)(len & 0xFF);
        msg[4] = id;
        if (payload.Length > 0) payload.CopyTo(msg, 5);
        return msg;
    }
}
