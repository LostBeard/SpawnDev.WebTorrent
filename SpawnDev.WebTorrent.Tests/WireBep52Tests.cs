using NUnit.Framework;
using SpawnDev.WebTorrent;

namespace SpawnDev.WebTorrent.Tests;

/// <summary>
/// Wire.cs integration tests for BEP 52 v2 peer-protocol messages (types 21 / 22 / 23).
/// Feeds raw bytes into Wire.DataReceived (same path a real TCP/SCTP peer uses) and
/// asserts the typed <c>OnHashRequest</c> / <c>OnHashes</c> / <c>OnHashReject</c> events
/// fire with correctly decoded payloads. Also verifies the outgoing Send* methods emit
/// spec-correct wire frames (4-byte length prefix + 1-byte msg id + payload).
/// </summary>
[TestFixture]
public class WireBep52Tests
{
    // Helpers from WireTests.cs pattern.
    private static Wire CreateWireAfterHandshake()
    {
        var wire = new Wire();
        wire.SendRaw = (_) => Task.CompletedTask;
        wire.DataReceived(MakeHandshake());
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

    private static byte[] MakeMessage(byte id, params byte[] payload)
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

    private static byte[] MakeRoot(byte fill)
    {
        var r = new byte[32];
        Array.Fill(r, fill);
        return r;
    }

    // ── Incoming message parsing ──

    [Test]
    public void Wire_DecodesIncomingHashRequest()
    {
        var wire = CreateWireAfterHandshake();
        Bep52WireMessages.HashRequest? captured = null;
        wire.OnHashRequest += r => captured = r;

        var payload = Bep52WireMessages.Encode(
            new Bep52WireMessages.HashRequest(MakeRoot(0xAA), BaseLayer: 1, Index: 4, Length: 2, ProofLayers: 3));
        wire.DataReceived(MakeMessage(Bep52WireMessages.MessageIdHashRequest, payload));

        Assert.That(captured, Is.Not.Null);
        Assert.That(captured!.Value.PiecesRoot, Is.EqualTo(MakeRoot(0xAA)));
        Assert.That(captured!.Value.BaseLayer, Is.EqualTo(1u));
        Assert.That(captured!.Value.Index, Is.EqualTo(4u));
        Assert.That(captured!.Value.Length, Is.EqualTo(2u));
        Assert.That(captured!.Value.ProofLayers, Is.EqualTo(3u));
    }

    [Test]
    public void Wire_DecodesIncomingHashes_IncludingHashList()
    {
        var wire = CreateWireAfterHandshake();
        Bep52WireMessages.Hashes? captured = null;
        wire.OnHashes += h => captured = h;

        var hashList = new[] { MakeRoot(0x10), MakeRoot(0x11), MakeRoot(0x20) };
        var payload = Bep52WireMessages.Encode(
            new Bep52WireMessages.Hashes(
                MakeRoot(0x01), BaseLayer: 0, Index: 0, Length: 2, ProofLayers: 1, HashList: hashList));
        wire.DataReceived(MakeMessage(Bep52WireMessages.MessageIdHashes, payload));

        Assert.That(captured, Is.Not.Null);
        Assert.That(captured!.Value.HashList.Length, Is.EqualTo(3));
        for (int i = 0; i < 3; i++)
        {
            Assert.That(captured!.Value.HashList[i], Is.EqualTo(hashList[i]));
        }
    }

    [Test]
    public void Wire_DecodesIncomingHashReject()
    {
        var wire = CreateWireAfterHandshake();
        Bep52WireMessages.HashReject? captured = null;
        wire.OnHashReject += r => captured = r;

        var payload = Bep52WireMessages.Encode(
            new Bep52WireMessages.HashReject(MakeRoot(0x55), BaseLayer: 2, Index: 8, Length: 4, ProofLayers: 1));
        wire.DataReceived(MakeMessage(Bep52WireMessages.MessageIdHashReject, payload));

        Assert.That(captured, Is.Not.Null);
        Assert.That(captured!.Value.PiecesRoot, Is.EqualTo(MakeRoot(0x55)));
        Assert.That(captured!.Value.BaseLayer, Is.EqualTo(2u));
    }

    [Test]
    public void Wire_MalformedHashRequest_FiresUnknownMessageNotEvent()
    {
        // Corrupt payload (wrong length) should not fire OnHashRequest - the decoder
        // rejects it and the handler falls back to OnUnknownMessage.
        var wire = CreateWireAfterHandshake();
        bool hashRequestFired = false;
        byte[]? unknownFired = null;
        wire.OnHashRequest += _ => hashRequestFired = true;
        wire.OnUnknownMessage += bytes => unknownFired = bytes;

        // 47-byte payload instead of 48 - decoder will throw ArgumentException.
        wire.DataReceived(MakeMessage(Bep52WireMessages.MessageIdHashRequest, new byte[47]));

        Assert.That(hashRequestFired, Is.False);
        Assert.That(unknownFired, Is.Not.Null);
    }

    // ── Outgoing message framing ──

    [Test]
    public async Task Wire_SendHashRequest_EmitsCorrectFrame()
    {
        var wire = new Wire();
        var captured = new List<byte>();
        wire.SendRaw = data => { captured.AddRange(data); return Task.CompletedTask; };

        var msg = new Bep52WireMessages.HashRequest(MakeRoot(0xAA), 1, 4, 2, 3);
        await wire.SendHashRequest(msg);

        // Expected: [4B length = 49] [msg id 21] [48B payload]
        Assert.That(captured.Count, Is.EqualTo(4 + 1 + 48));
        Assert.That(captured[0], Is.EqualTo(0));
        Assert.That(captured[1], Is.EqualTo(0));
        Assert.That(captured[2], Is.EqualTo(0));
        Assert.That(captured[3], Is.EqualTo(49));
        Assert.That(captured[4], Is.EqualTo(Bep52WireMessages.MessageIdHashRequest));

        // Payload bytes must match the direct Bep52WireMessages.Encode output.
        var expectedPayload = Bep52WireMessages.Encode(msg);
        Assert.That(captured.Skip(5).ToArray(), Is.EqualTo(expectedPayload));
    }

    [Test]
    public async Task Wire_SendHashes_EmitsCorrectFrameWithHashList()
    {
        var wire = new Wire();
        var captured = new List<byte>();
        wire.SendRaw = data => { captured.AddRange(data); return Task.CompletedTask; };

        var hashList = new[] { MakeRoot(0x10), MakeRoot(0x11), MakeRoot(0x20) };
        var msg = new Bep52WireMessages.Hashes(MakeRoot(0x01), 0, 0, 2, 1, hashList);
        await wire.SendHashes(msg);

        // Expected: [4B length = 1 + 48 + 96] [msg id 22] [48B header] [3 x 32B hashes]
        int expectedFrameLen = 4 + 1 + 48 + 3 * 32;
        Assert.That(captured.Count, Is.EqualTo(expectedFrameLen));
        int bodyLen = 1 + 48 + 3 * 32;
        Assert.That(captured[0], Is.EqualTo((byte)((bodyLen >> 24) & 0xFF)));
        Assert.That(captured[1], Is.EqualTo((byte)((bodyLen >> 16) & 0xFF)));
        Assert.That(captured[2], Is.EqualTo((byte)((bodyLen >> 8) & 0xFF)));
        Assert.That(captured[3], Is.EqualTo((byte)(bodyLen & 0xFF)));
        Assert.That(captured[4], Is.EqualTo(Bep52WireMessages.MessageIdHashes));
    }

    [Test]
    public async Task Wire_SendHashReject_RoundTripsThroughAnotherWire()
    {
        // The cleanest interop test: one Wire's SendHashReject output is fed into
        // another Wire's DataReceived. The receiving wire's OnHashReject must fire with
        // the same payload.
        var sender = new Wire();
        var receiver = new Wire();
        receiver.SendRaw = _ => Task.CompletedTask;
        receiver.DataReceived(MakeHandshake());

        Bep52WireMessages.HashReject? captured = null;
        receiver.OnHashReject += r => captured = r;

        sender.SendRaw = data => { receiver.DataReceived(data); return Task.CompletedTask; };
        var msg = new Bep52WireMessages.HashReject(MakeRoot(0x42), 1, 2, 4, 1);
        await sender.SendHashReject(msg);

        Assert.That(captured, Is.Not.Null);
        Assert.That(captured!.Value.PiecesRoot, Is.EqualTo(MakeRoot(0x42)));
        Assert.That(captured!.Value.BaseLayer, Is.EqualTo(1u));
        Assert.That(captured!.Value.Index, Is.EqualTo(2u));
        Assert.That(captured!.Value.Length, Is.EqualTo(4u));
        Assert.That(captured!.Value.ProofLayers, Is.EqualTo(1u));
    }

    [Test]
    public async Task Wire_SendHashRequest_ReceiverCanVerifyWithProofVerifier()
    {
        // End-to-end: peer A has a Merkle tree, peer B requests a proof, peer A responds
        // with hashes, peer B verifies. The whole Phase 2c step 2 loop without the state
        // machine in between (step 2.3) - just the wire + codec + verifier.
        //
        // Use a 4-leaf tree, request left half (leaves 0..1), expect one proof hash (h23).
        var leaves = new byte[][]
        {
            System.Security.Cryptography.SHA256.HashData(new byte[] { 1 }),
            System.Security.Cryptography.SHA256.HashData(new byte[] { 2 }),
            System.Security.Cryptography.SHA256.HashData(new byte[] { 3 }),
            System.Security.Cryptography.SHA256.HashData(new byte[] { 4 }),
        };
        byte[] HashPair(byte[] l, byte[] r)
        {
            var b = new byte[64];
            l.CopyTo(b, 0);
            r.CopyTo(b, 32);
            return System.Security.Cryptography.SHA256.HashData(b);
        }
        var h01 = HashPair(leaves[0], leaves[1]);
        var h23 = HashPair(leaves[2], leaves[3]);
        var root = HashPair(h01, h23);

        // Peer A (server) receives hash_request and responds with hashes.
        var peerA = new Wire();
        var peerB = new Wire();
        peerA.SendRaw = data => { peerB.DataReceived(data); return Task.CompletedTask; };
        peerB.SendRaw = data => { peerA.DataReceived(data); return Task.CompletedTask; };
        peerA.DataReceived(MakeHandshake());
        peerB.DataReceived(MakeHandshake());

        peerA.OnHashRequest += async req =>
        {
            // Send leaves 0..1 + proof h23.
            await peerA.SendHashes(new Bep52WireMessages.Hashes(
                req.PiecesRoot, req.BaseLayer, req.Index, req.Length, req.ProofLayers,
                new[] { leaves[0], leaves[1], h23 }));
        };

        bool verified = false;
        peerB.OnHashes += h =>
        {
            verified = MerkleProofVerifier.Verify(h);
        };

        await peerB.SendHashRequest(new Bep52WireMessages.HashRequest(
            root, BaseLayer: 0, Index: 0, Length: 2, ProofLayers: 1));

        // Let any pending event handlers complete.
        await Task.Yield();
        Assert.That(verified, Is.True, "Full request -> response -> verify loop must validate");
    }
}
