using System.Security.Cryptography;
using NUnit.Framework;
using SpawnDev.WebTorrent;

namespace SpawnDev.WebTorrent.Tests;

/// <summary>
/// Integration tests for <see cref="V2HashRequestCoordinator"/> wired into <see cref="Torrent"/>.
/// Covers the three edges of the integration:
/// <list type="bullet">
/// <item>Coordinator allocation: v2 torrents get a coordinator; v1 torrents don't.</item>
/// <item>Seed path: a peer's <c>hash_request</c> flowing in via Wire raw bytes is decoded
/// and answered with a <c>hashes</c> (or <c>hash_reject</c>) whose payload is what the
/// peer's <see cref="MerkleProofVerifier"/> would accept.</item>
/// <item>Event forwarding: a <c>hashes</c> message arriving on Wire A resolves a
/// <see cref="V2HashRequestCoordinator.RequestAsync"/> task even if the request was
/// initiated through Wire B (the coordinator is per-torrent, not per-wire).</item>
/// </list>
///
/// Tests feed real BEP 52 wire bytes into <see cref="Wire.DataReceived"/> (the same entry
/// point real TCP/SCTP peers use), so the assertions exercise the production decode +
/// dispatch path.
/// </summary>
[TestFixture]
public class TorrentV2HashCoordinationTests
{
    [Test]
    public void SetMetadata_AllocatesV2Coordinator_ForV2Torrent()
    {
        var torrent = MakeV2Torrent(fileSize: 128 * 1024, pieceSize: 65536);
        Assert.That(torrent.V2HashCoord, Is.Not.Null);
        Assert.That(torrent.MetaVersion, Is.EqualTo(2));
        Assert.That(torrent.FileRoots.Length, Is.EqualTo(1));
        Assert.That(torrent.PieceLayers.Count, Is.EqualTo(1), "Multi-piece file must populate piece layers dict");
    }

    [Test]
    public void SetMetadata_NoCoordinator_ForV1Torrent()
    {
        var torrent = MakeV1Torrent(pieceSize: 16384, pieceCount: 4);
        Assert.That(torrent.V2HashCoord, Is.Null,
            "v1 torrents MUST NOT allocate the v2 coordinator - wasted allocation + wrong semantics.");
    }

    [Test]
    public void SeedPath_RespondsToPeerHashRequest_WithVerifiableHashes()
    {
        // 8-piece file at piece=64 KiB. Piece layer sits at level 2 (log2(4 leaves/piece)).
        var torrent = MakeV2Torrent(fileSize: 8 * 65536, pieceSize: 65536);
        var fileRoot = torrent.FileRoots[0];
        int pieceLayerLevel = 2;

        var wire = AttachHandshakedWire(torrent, capturedSent: out var sentFrames);

        // Peer asks: piece-layer range [0..3], proof_layers=1 (tree height above piece
        // layer with 8 entries padded to 8 = 3, minus log2(length)=2 -> 1).
        var req = new Bep52WireMessages.HashRequest(fileRoot, (uint)pieceLayerLevel, 0, 4, 1);
        wire.DataReceived(MakeMessage(Bep52WireMessages.MessageIdHashRequest, Bep52WireMessages.Encode(req)));

        // Expect a hashes message back on the wire.
        var hashesFrame = sentFrames.FirstOrDefault(f => f.Length > 4 && f[4] == Bep52WireMessages.MessageIdHashes);
        Assert.That(hashesFrame, Is.Not.Null,
            "Torrent must answer a hash_request we can serve with a hashes message");

        // Parse the response and verify it re-climbs to the advertised pieces_root.
        var payload = new byte[hashesFrame!.Length - 5];
        Buffer.BlockCopy(hashesFrame, 5, payload, 0, payload.Length);
        var decoded = Bep52WireMessages.DecodeHashes(payload);
        Assert.That(decoded.PiecesRoot, Is.EqualTo(fileRoot));
        Assert.That(decoded.HashList.Length, Is.EqualTo(4 + 1), "4 base-layer + 1 proof hash");
        Assert.That(MerkleProofVerifier.Verify(decoded), Is.True,
            "The hashes we seeded must pass MerkleProofVerifier - real peers will use it to accept/reject us");
    }

    [Test]
    public void SeedPath_Rejects_UnknownRoot()
    {
        var torrent = MakeV2Torrent(fileSize: 8 * 65536, pieceSize: 65536);
        var wire = AttachHandshakedWire(torrent, capturedSent: out var sentFrames);

        // Unknown root (zeros) - we can't serve.
        var req = new Bep52WireMessages.HashRequest(new byte[32], 2, 0, 4, 1);
        wire.DataReceived(MakeMessage(Bep52WireMessages.MessageIdHashRequest, Bep52WireMessages.Encode(req)));

        var rejectFrame = sentFrames.FirstOrDefault(f => f.Length > 4 && f[4] == Bep52WireMessages.MessageIdHashReject);
        Assert.That(rejectFrame, Is.Not.Null, "Unknown root must produce a hash_reject");
    }

    [Test]
    public void SeedPath_Rejects_LeafLevelRequest_WhenPiecesAreMissing()
    {
        // base_layer = 0 leaf-level requests CAN be served (shipped 2026-04-23) when we
        // have every piece of the requested file in the chunk store. When we don't (as
        // here - fresh torrent with Bitfield still all false), CanPossiblyServeLeafLevel
        // rejects synchronously on the wire thread - no race against a fire-and-forget
        // async reply.
        var torrent = MakeV2Torrent(fileSize: 8 * 65536, pieceSize: 65536);
        var fileRoot = torrent.FileRoots[0];
        var wire = AttachHandshakedWire(torrent, capturedSent: out var sentFrames);

        var req = new Bep52WireMessages.HashRequest(fileRoot, 0, 0, 2, 4); // base_layer=0
        wire.DataReceived(MakeMessage(Bep52WireMessages.MessageIdHashRequest, Bep52WireMessages.Encode(req)));

        var rejectFrame = sentFrames.FirstOrDefault(f => f.Length > 4 && f[4] == Bep52WireMessages.MessageIdHashReject);
        Assert.That(rejectFrame, Is.Not.Null,
            "Leaf-level request without stored pieces must be refused synchronously (not race-pending behind a fire-and-forget Task).");
    }

    [Test]
    public async Task ClientPath_IncomingHashesResolvesRequestAsync()
    {
        // A request issued through the coordinator resolves when a peer sends a matching
        // hashes message on ANY wire subscribed to the torrent. Simulates the case where
        // we fanned out the request to multiple peers.
        var torrent = MakeV2Torrent(fileSize: 8 * 65536, pieceSize: 65536);
        var fileRoot = torrent.FileRoots[0];
        int pieceLayerLevel = 2;

        var wireA = AttachHandshakedWire(torrent, capturedSent: out _);
        var wireB = AttachHandshakedWire(torrent, capturedSent: out _);

        // Build the hashes payload a well-behaved peer would send.
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

        // Issue the request through wireA (we don't care whether this is actually
        // transmitted - the test substitutes the remote response below).
        var req = new Bep52WireMessages.HashRequest(fileRoot, (uint)pieceLayerLevel, 0, 4, 1);
        var reqTask = torrent.V2HashCoord!.RequestAsync(req, send: _ => Task.CompletedTask);

        // Now simulate wireB (not wireA!) delivering the hashes response. The coordinator
        // is per-torrent so this must still resolve reqTask.
        var hashesMsg = new Bep52WireMessages.Hashes(fileRoot, (uint)pieceLayerLevel, 0, 4, 1, hashList);
        wireB.DataReceived(MakeMessage(Bep52WireMessages.MessageIdHashes, Bep52WireMessages.Encode(hashesMsg)));

        // TCS is constructed with RunContinuationsAsynchronously so the awaiter is async.
        // Bound the wait rather than spinning forever if the coordinator is broken.
        var result = await reqTask.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.That(result.Length, Is.EqualTo(5),
            "Per-torrent coordinator must correlate responses across peers, not per-wire");
        Assert.That(torrent.V2HashCoord.OutstandingCount, Is.EqualTo(0));
    }

    [Test]
    public void ClientPath_IncomingHashRejectFailsRequestAsync()
    {
        var torrent = MakeV2Torrent(fileSize: 8 * 65536, pieceSize: 65536);
        var fileRoot = torrent.FileRoots[0];
        var wire = AttachHandshakedWire(torrent, out _);

        var req = new Bep52WireMessages.HashRequest(fileRoot, 2, 0, 4, 1);
        var reqTask = torrent.V2HashCoord!.RequestAsync(req, send: _ => Task.CompletedTask);

        var reject = new Bep52WireMessages.HashReject(fileRoot, 2, 0, 4, 1);
        wire.DataReceived(MakeMessage(Bep52WireMessages.MessageIdHashReject, Bep52WireMessages.Encode(reject)));

        Assert.ThrowsAsync<HashRejectedException>(async () => await reqTask);
    }

    [Test]
    public void RequestV2HashesAsync_ThrowsForV1Torrent()
    {
        var torrent = MakeV1Torrent(pieceSize: 16384, pieceCount: 4);
        var req = new Bep52WireMessages.HashRequest(new byte[32], 0, 0, 2, 1);
        Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await torrent.RequestV2HashesAsync(req));
    }

    [Test]
    public void RequestV2HashesAsync_ThrowsWhenNoWireAvailable()
    {
        var torrent = MakeV2Torrent(fileSize: 8 * 65536, pieceSize: 65536);
        var req = new Bep52WireMessages.HashRequest(torrent.FileRoots[0], 2, 0, 4, 1);
        Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await torrent.RequestV2HashesAsync(req),
            "Torrent with no connected peers has nobody to ask - must throw instead of hanging");
    }

    [Test]
    public void TryBuildV2HashesPayload_RespectsPieceLayerLevel()
    {
        // Internal unit for the piece-layer builder lookup. 16 KiB piece = 1 leaf/piece ->
        // piece layer is at level 0 (same as leaf layer). base_layer=0 should be served.
        var torrent = MakeV2Torrent(fileSize: 4 * 16384, pieceSize: 16384);
        var fileRoot = torrent.FileRoots[0];

        var req = new Bep52WireMessages.HashRequest(fileRoot, 0, 0, 4, 0);
        var payload = torrent.TryBuildV2HashesPayload(req);
        Assert.That(payload, Is.Not.Null);
    }

    [Test]
    public async Task TryBuildV2HashesPayloadAsync_ServesLeafLevelFromStore()
    {
        // Leaf-level seed path: a peer asks for base_layer=0 hashes (16 KiB leaf hashes).
        // Requires reading piece content from the chunk store and re-hashing. Previously
        // refused; shipped 2026-04-23 as the final Phase 2c completion item.
        // 64 KiB piece = 4 leaves per piece. 4-piece file = 16 leaves. Request 4 leaves
        // starting at index 0 with proof_layers that climb from leaf layer to root.
        int pieceSize = 65536;
        int pieceCount = 4;
        var data = new byte[pieceSize * pieceCount];
        new Random(91).NextBytes(data);
        var opts = new TorrentCreatorOptions { MetaVersion = 2, PieceLength = pieceSize };
        var (bytes, _) = TorrentCreator.CreateFromBytes("leaf-probe.bin", data, opts);
        var parsed = TorrentParser.Parse(bytes);

        var t = new Torrent();
        t.SetMetadata(parsed);

        // Populate the chunk store with every piece so the seed path can re-hash.
        var storeField = typeof(Torrent).GetField("_store",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var store = (SpawnDev.WebTorrent.Storage.IChunkStore)storeField!.GetValue(t)!;
        for (int i = 0; i < pieceCount; i++)
        {
            var pieceData = new byte[pieceSize];
            Array.Copy(data, i * pieceSize, pieceData, 0, pieceSize);
            await store.PutAsync(i, pieceData);
            t.Bitfield[i] = true;
        }

        var fileRoot = t.FileRoots[0];
        // 16 leaves in the file; ask for the first 4 at level 0. log2(16) = 4 internal
        // levels from full leaf layer to root; length=4 consumes 2 of those internally
        // (log2(4)=2), leaving 2 proof layers.
        var req = new Bep52WireMessages.HashRequest(fileRoot, BaseLayer: 0, Index: 0, Length: 4, ProofLayers: 2);

        var payload = await t.TryBuildV2HashesPayloadAsync(req);
        Assert.That(payload, Is.Not.Null, "Leaf-level request must be served when we hold all pieces of the file.");
        var (baseLayer, proof) = payload!.Value;
        Assert.That(baseLayer.Length, Is.EqualTo(4));
        Assert.That(proof.Length, Is.EqualTo(2));

        // Round-trip through the verifier - proves the leaves we emitted climb to the
        // advertised pieces_root exactly.
        Assert.That(MerkleProofVerifier.Verify(fileRoot, 0, baseLayer, proof), Is.True,
            "Leaf-level seed output must pass MerkleProofVerifier round-trip.");
    }

    [Test]
    public async Task TryBuildV2HashesPayloadAsync_ReturnsNull_WhenPiecesMissing()
    {
        // Same 4-piece 64 KiB layout, but this time we DON'T populate pieces 2 and 3.
        // Leaf-level can't be served without the raw piece content; expected to return null.
        int pieceSize = 65536;
        var data = new byte[pieceSize * 4];
        new Random(92).NextBytes(data);
        var opts = new TorrentCreatorOptions { MetaVersion = 2, PieceLength = pieceSize };
        var (bytes, _) = TorrentCreator.CreateFromBytes("incomplete.bin", data, opts);

        var t = new Torrent();
        t.SetMetadata(TorrentParser.Parse(bytes));

        // Populate only pieces 0-1; leave 2-3 missing (Bitfield stays false).
        var storeField = typeof(Torrent).GetField("_store",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var store = (SpawnDev.WebTorrent.Storage.IChunkStore)storeField!.GetValue(t)!;
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
        Assert.That(payload, Is.Null,
            "Leaf-level request must be refused when we don't have every piece of the file - can't re-hash what we don't have.");
    }

    // ── Helpers ──

    private static Torrent MakeV2Torrent(int fileSize, int pieceSize)
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

    private static Torrent MakeV1Torrent(int pieceSize, int pieceCount)
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

    private static Wire AttachHandshakedWire(Torrent torrent, out List<byte[]> capturedSent)
    {
        var sent = new List<byte[]>();
        capturedSent = sent;

        var wire = new Wire();
        wire.SendRaw = data => { sent.Add(data); return Task.CompletedTask; };
        wire.DataReceived(MakeHandshake());

        torrent.Wires.Add(wire);
        torrent.OnWireWithMetadata(wire);
        return wire;
    }

    private static byte[] MakeHandshake()
    {
        var msg = new byte[68];
        msg[0] = 19;
        "BitTorrent protocol"u8.CopyTo(msg.AsSpan(1));
        var reserved = new byte[8];
        reserved[5] |= 0x10; // extended
        reserved.CopyTo(msg, 20);
        return msg;
    }

    private static byte[] MakeMessage(byte id, byte[] payload)
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
