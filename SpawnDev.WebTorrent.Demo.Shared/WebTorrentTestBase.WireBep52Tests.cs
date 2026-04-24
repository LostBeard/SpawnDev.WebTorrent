using System.Security.Cryptography;
using SpawnDev.UnitTesting;
using SpawnDev.WebTorrent;

namespace SpawnDev.WebTorrent.Demo.Shared;

/// <summary>
/// Wire.cs integration tests for BEP 52 v2 peer-protocol messages (21 / 22 / 23).
/// Feeds raw bytes into Wire.DataReceived (same path real peers use) and asserts the
/// typed OnHashRequest / OnHashes / OnHashReject events fire with decoded payloads.
/// Verifies outgoing Send* methods emit spec-correct wire frames.
/// Migrated from NUnit WireBep52Tests.cs.
/// </summary>
public abstract partial class WebTorrentTestBase
{
    [TestMethod]
    public async Task WireBep52_DecodesIncomingHashRequest()
    {
        var wire = WireBep52Tests_CreateWireAfterHandshake();
        Bep52WireMessages.HashRequest? captured = null;
        wire.OnHashRequest += r => captured = r;

        var payload = Bep52WireMessages.Encode(
            new Bep52WireMessages.HashRequest(WireBep52Tests_MakeRoot(0xAA), BaseLayer: 1, Index: 4, Length: 2, ProofLayers: 3));
        wire.DataReceived(WireBep52Tests_MakeMessage(Bep52WireMessages.MessageIdHashRequest, payload));

        if (captured is null) throw new Exception("OnHashRequest should have fired");
        if (!captured.Value.PiecesRoot.SequenceEqual(WireBep52Tests_MakeRoot(0xAA))) throw new Exception("PiecesRoot mismatch");
        if (captured.Value.BaseLayer != 1u) throw new Exception($"BaseLayer={captured.Value.BaseLayer}");
        if (captured.Value.Index != 4u) throw new Exception($"Index={captured.Value.Index}");
        if (captured.Value.Length != 2u) throw new Exception($"Length={captured.Value.Length}");
        if (captured.Value.ProofLayers != 3u) throw new Exception($"ProofLayers={captured.Value.ProofLayers}");
        await Task.CompletedTask;
    }

    [TestMethod]
    public async Task WireBep52_DecodesIncomingHashes_IncludingHashList()
    {
        var wire = WireBep52Tests_CreateWireAfterHandshake();
        Bep52WireMessages.Hashes? captured = null;
        wire.OnHashes += h => captured = h;

        var hashList = new[] { WireBep52Tests_MakeRoot(0x10), WireBep52Tests_MakeRoot(0x11), WireBep52Tests_MakeRoot(0x20) };
        var payload = Bep52WireMessages.Encode(
            new Bep52WireMessages.Hashes(
                WireBep52Tests_MakeRoot(0x01), BaseLayer: 0, Index: 0, Length: 2, ProofLayers: 1, HashList: hashList));
        wire.DataReceived(WireBep52Tests_MakeMessage(Bep52WireMessages.MessageIdHashes, payload));

        if (captured is null) throw new Exception("OnHashes should have fired");
        if (captured.Value.HashList.Length != 3) throw new Exception($"HashList.Length={captured.Value.HashList.Length}");
        for (int i = 0; i < 3; i++)
        {
            if (!captured.Value.HashList[i].SequenceEqual(hashList[i]))
                throw new Exception($"hashList[{i}] mismatch");
        }
        await Task.CompletedTask;
    }

    [TestMethod]
    public async Task WireBep52_DecodesIncomingHashReject()
    {
        var wire = WireBep52Tests_CreateWireAfterHandshake();
        Bep52WireMessages.HashReject? captured = null;
        wire.OnHashReject += r => captured = r;

        var payload = Bep52WireMessages.Encode(
            new Bep52WireMessages.HashReject(WireBep52Tests_MakeRoot(0x55), BaseLayer: 2, Index: 8, Length: 4, ProofLayers: 1));
        wire.DataReceived(WireBep52Tests_MakeMessage(Bep52WireMessages.MessageIdHashReject, payload));

        if (captured is null) throw new Exception("OnHashReject should have fired");
        if (!captured.Value.PiecesRoot.SequenceEqual(WireBep52Tests_MakeRoot(0x55))) throw new Exception("PiecesRoot mismatch");
        if (captured.Value.BaseLayer != 2u) throw new Exception($"BaseLayer={captured.Value.BaseLayer}");
        await Task.CompletedTask;
    }

    [TestMethod]
    public async Task WireBep52_MalformedHashRequest_FiresUnknownMessageNotEvent()
    {
        var wire = WireBep52Tests_CreateWireAfterHandshake();
        bool hashRequestFired = false;
        byte[]? unknownFired = null;
        wire.OnHashRequest += _ => hashRequestFired = true;
        wire.OnUnknownMessage += bytes => unknownFired = bytes;

        // 47-byte payload instead of 48 - decoder throws, Wire falls back to OnUnknownMessage.
        wire.DataReceived(WireBep52Tests_MakeMessage(Bep52WireMessages.MessageIdHashRequest, new byte[47]));

        if (hashRequestFired) throw new Exception("malformed payload should NOT fire OnHashRequest");
        if (unknownFired is null) throw new Exception("malformed payload should fire OnUnknownMessage");
        await Task.CompletedTask;
    }

    [TestMethod]
    public async Task WireBep52_SendHashRequest_EmitsCorrectFrame()
    {
        var wire = new Wire();
        var captured = new List<byte>();
        wire.SendRaw = data => { captured.AddRange(data); return Task.CompletedTask; };

        var msg = new Bep52WireMessages.HashRequest(WireBep52Tests_MakeRoot(0xAA), 1, 4, 2, 3);
        await wire.SendHashRequest(msg);

        if (captured.Count != 4 + 1 + 48) throw new Exception($"frame length={captured.Count}, expected 53");
        if (captured[0] != 0 || captured[1] != 0 || captured[2] != 0 || captured[3] != 49)
            throw new Exception("body length prefix must be big-endian 49");
        if (captured[4] != Bep52WireMessages.MessageIdHashRequest)
            throw new Exception($"msg id={captured[4]}, expected {Bep52WireMessages.MessageIdHashRequest}");

        var expectedPayload = Bep52WireMessages.Encode(msg);
        if (!captured.Skip(5).ToArray().SequenceEqual(expectedPayload))
            throw new Exception("frame payload must match direct Encode output");
    }

    [TestMethod]
    public async Task WireBep52_SendHashes_EmitsCorrectFrameWithHashList()
    {
        var wire = new Wire();
        var captured = new List<byte>();
        wire.SendRaw = data => { captured.AddRange(data); return Task.CompletedTask; };

        var hashList = new[] { WireBep52Tests_MakeRoot(0x10), WireBep52Tests_MakeRoot(0x11), WireBep52Tests_MakeRoot(0x20) };
        var msg = new Bep52WireMessages.Hashes(WireBep52Tests_MakeRoot(0x01), 0, 0, 2, 1, hashList);
        await wire.SendHashes(msg);

        int expectedFrameLen = 4 + 1 + 48 + 3 * 32;
        if (captured.Count != expectedFrameLen) throw new Exception($"frame length={captured.Count}, expected {expectedFrameLen}");
        int bodyLen = 1 + 48 + 3 * 32;
        if (captured[0] != (byte)((bodyLen >> 24) & 0xFF)) throw new Exception("body length[0] wrong");
        if (captured[1] != (byte)((bodyLen >> 16) & 0xFF)) throw new Exception("body length[1] wrong");
        if (captured[2] != (byte)((bodyLen >> 8) & 0xFF)) throw new Exception("body length[2] wrong");
        if (captured[3] != (byte)(bodyLen & 0xFF)) throw new Exception("body length[3] wrong");
        if (captured[4] != Bep52WireMessages.MessageIdHashes)
            throw new Exception($"msg id={captured[4]}, expected {Bep52WireMessages.MessageIdHashes}");
    }

    [TestMethod]
    public async Task WireBep52_SendHashReject_RoundTripsThroughAnotherWire()
    {
        var sender = new Wire();
        var receiver = new Wire();
        receiver.SendRaw = _ => Task.CompletedTask;
        receiver.DataReceived(WireBep52Tests_MakeHandshake());

        Bep52WireMessages.HashReject? captured = null;
        receiver.OnHashReject += r => captured = r;

        sender.SendRaw = data => { receiver.DataReceived(data); return Task.CompletedTask; };
        var msg = new Bep52WireMessages.HashReject(WireBep52Tests_MakeRoot(0x42), 1, 2, 4, 1);
        await sender.SendHashReject(msg);

        if (captured is null) throw new Exception("receiver OnHashReject should have fired");
        if (!captured.Value.PiecesRoot.SequenceEqual(WireBep52Tests_MakeRoot(0x42))) throw new Exception("PiecesRoot mismatch");
        if (captured.Value.BaseLayer != 1u) throw new Exception($"BaseLayer={captured.Value.BaseLayer}");
        if (captured.Value.Index != 2u) throw new Exception($"Index={captured.Value.Index}");
        if (captured.Value.Length != 4u) throw new Exception($"Length={captured.Value.Length}");
        if (captured.Value.ProofLayers != 1u) throw new Exception($"ProofLayers={captured.Value.ProofLayers}");
    }

    [TestMethod]
    public async Task WireBep52_SendHashRequest_ReceiverCanVerifyWithProofVerifier()
    {
        // End-to-end: peer A has a Merkle tree, peer B requests a proof, peer A responds with
        // hashes, peer B verifies. Full Phase 2c step 2 loop minus the coordinator state
        // machine -- wire + codec + verifier.
        var leaves = new byte[][]
        {
            SHA256.HashData(new byte[] { 1 }),
            SHA256.HashData(new byte[] { 2 }),
            SHA256.HashData(new byte[] { 3 }),
            SHA256.HashData(new byte[] { 4 }),
        };
        var h01 = WireBep52Tests_HashPair(leaves[0], leaves[1]);
        var h23 = WireBep52Tests_HashPair(leaves[2], leaves[3]);
        var root = WireBep52Tests_HashPair(h01, h23);

        var peerA = new Wire();
        var peerB = new Wire();
        peerA.SendRaw = data => { peerB.DataReceived(data); return Task.CompletedTask; };
        peerB.SendRaw = data => { peerA.DataReceived(data); return Task.CompletedTask; };
        peerA.DataReceived(WireBep52Tests_MakeHandshake());
        peerB.DataReceived(WireBep52Tests_MakeHandshake());

        peerA.OnHashRequest += async req =>
        {
            await peerA.SendHashes(new Bep52WireMessages.Hashes(
                req.PiecesRoot, req.BaseLayer, req.Index, req.Length, req.ProofLayers,
                new[] { leaves[0], leaves[1], h23 }));
        };

        bool verified = false;
        peerB.OnHashes += h => { verified = MerkleProofVerifier.Verify(h); };

        await peerB.SendHashRequest(new Bep52WireMessages.HashRequest(
            root, BaseLayer: 0, Index: 0, Length: 2, ProofLayers: 1));

        await Task.Yield();
        if (!verified) throw new Exception("full request -> response -> verify loop must validate");
    }

    // ---- helpers ----

    private static Wire WireBep52Tests_CreateWireAfterHandshake()
    {
        var wire = new Wire();
        wire.SendRaw = _ => Task.CompletedTask;
        wire.DataReceived(WireBep52Tests_MakeHandshake());
        return wire;
    }

    private static byte[] WireBep52Tests_MakeHandshake()
    {
        var msg = new byte[68];
        msg[0] = 19;
        "BitTorrent protocol"u8.CopyTo(msg.AsSpan(1));
        var reserved = new byte[8];
        reserved[5] |= 0x10; // extended
        reserved.CopyTo(msg, 20);
        return msg;
    }

    private static byte[] WireBep52Tests_MakeMessage(byte id, params byte[] payload)
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

    private static byte[] WireBep52Tests_MakeRoot(byte fill)
    {
        var r = new byte[32];
        Array.Fill(r, fill);
        return r;
    }

    private static byte[] WireBep52Tests_HashPair(byte[] l, byte[] r)
    {
        var b = new byte[64];
        l.CopyTo(b, 0);
        r.CopyTo(b, 32);
        return SHA256.HashData(b);
    }
}
