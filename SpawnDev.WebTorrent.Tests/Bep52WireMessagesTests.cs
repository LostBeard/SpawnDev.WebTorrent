using NUnit.Framework;
using SpawnDev.WebTorrent;

namespace SpawnDev.WebTorrent.Tests;

/// <summary>
/// Encode / decode tests for <see cref="Bep52WireMessages"/>, the BEP 52 v2 peer-wire
/// Merkle-proof message codecs (types 21 hash_request / 22 hashes / 23 hash_reject).
/// Known-byte-sequence tests ensure the big-endian u32 serialization on the wire matches
/// the spec; round-trip tests verify symmetry; malformed-input tests verify the decoder
/// rejects corrupt payloads loudly.
/// </summary>
[TestFixture]
public class Bep52WireMessagesTests
{
    [Test]
    public void HashRequest_RoundTrip_PreservesAllFields()
    {
        var root = MakeRoot(0x42);
        var original = new Bep52WireMessages.HashRequest(root, BaseLayer: 2, Index: 0x10203040, Length: 5, ProofLayers: 3);
        var wire = Bep52WireMessages.Encode(original);
        Assert.That(wire.Length, Is.EqualTo(Bep52WireMessages.HeaderSize));
        var decoded = Bep52WireMessages.DecodeHashRequest(wire);
        Assert.That(decoded.PiecesRoot, Is.EqualTo(root));
        Assert.That(decoded.BaseLayer, Is.EqualTo(2u));
        Assert.That(decoded.Index, Is.EqualTo(0x10203040u));
        Assert.That(decoded.Length, Is.EqualTo(5u));
        Assert.That(decoded.ProofLayers, Is.EqualTo(3u));
    }

    [Test]
    public void HashRequest_BigEndianWireFormat()
    {
        // Construct a known-answer byte sequence so the wire format is independently pinned.
        // PiecesRoot = 32 zero bytes; BaseLayer=1; Index=0x01020304; Length=0x00AABBCC;
        // ProofLayers=0xFF00FF00. Every u32 must be big-endian.
        var msg = new Bep52WireMessages.HashRequest(new byte[32], 1u, 0x01020304u, 0x00AABBCCu, 0xFF00FF00u);
        var wire = Bep52WireMessages.Encode(msg);

        var expected = new byte[48];
        // PiecesRoot bytes 0..32 stay zero.
        // BaseLayer u32 at offset 32: 00 00 00 01
        expected[35] = 0x01;
        // Index u32 at offset 36: 01 02 03 04
        expected[36] = 0x01; expected[37] = 0x02; expected[38] = 0x03; expected[39] = 0x04;
        // Length u32 at offset 40: 00 AA BB CC
        expected[40] = 0x00; expected[41] = 0xAA; expected[42] = 0xBB; expected[43] = 0xCC;
        // ProofLayers u32 at offset 44: FF 00 FF 00
        expected[44] = 0xFF; expected[45] = 0x00; expected[46] = 0xFF; expected[47] = 0x00;

        Assert.That(wire, Is.EqualTo(expected));
    }

    [Test]
    public void Hashes_RoundTrip_PreservesAllHashes()
    {
        var root = MakeRoot(0x01);
        var hashes = new byte[][]
        {
            MakeRoot(0x10), MakeRoot(0x11), MakeRoot(0x12), // 3 "length" requested hashes
            MakeRoot(0x20), MakeRoot(0x21),                 // 2 "proof" hashes up toward root
        };
        var original = new Bep52WireMessages.Hashes(root, BaseLayer: 0, Index: 0, Length: 3, ProofLayers: 2, HashList: hashes);
        var wire = Bep52WireMessages.Encode(original);
        Assert.That(wire.Length, Is.EqualTo(Bep52WireMessages.HeaderSize + 5 * 32));

        var decoded = Bep52WireMessages.DecodeHashes(wire);
        Assert.That(decoded.HashList.Length, Is.EqualTo(5));
        for (int i = 0; i < 5; i++)
        {
            Assert.That(decoded.HashList[i], Is.EqualTo(hashes[i]), $"Hash at index {i} must round-trip identically");
        }
    }

    [Test]
    public void Hashes_MismatchedHashCount_EncodeThrows()
    {
        // HashList count must equal Length + ProofLayers. Encoder must reject malformed.
        var msg = new Bep52WireMessages.Hashes(
            MakeRoot(0),
            BaseLayer: 0,
            Index: 0,
            Length: 2,
            ProofLayers: 2,
            HashList: new[] { MakeRoot(1), MakeRoot(2), MakeRoot(3) }); // 3 hashes but 4 expected
        Assert.Throws<ArgumentException>(() => Bep52WireMessages.Encode(msg));
    }

    [Test]
    public void Hashes_MismatchedPayloadLength_DecodeThrows()
    {
        // Build a hashes payload that claims Length=2 + ProofLayers=1 but only carries 2 hashes.
        var wire = new byte[Bep52WireMessages.HeaderSize + 2 * 32];
        // BaseLayer=0, Index=0 already zero. Length=2 at offset 40, ProofLayers=1 at offset 44.
        wire[43] = 0x02;
        wire[47] = 0x01;
        Assert.Throws<ArgumentException>(() => Bep52WireMessages.DecodeHashes(wire));
    }

    [Test]
    public void HashReject_RoundTrip_SameShapeAsHashRequest()
    {
        var root = MakeRoot(0xAA);
        var original = new Bep52WireMessages.HashReject(root, BaseLayer: 7, Index: 123, Length: 4, ProofLayers: 2);
        var wire = Bep52WireMessages.Encode(original);
        Assert.That(wire.Length, Is.EqualTo(Bep52WireMessages.HeaderSize));

        var decoded = Bep52WireMessages.DecodeHashReject(wire);
        Assert.That(decoded.PiecesRoot, Is.EqualTo(root));
        Assert.That(decoded.BaseLayer, Is.EqualTo(7u));
        Assert.That(decoded.Index, Is.EqualTo(123u));
        Assert.That(decoded.Length, Is.EqualTo(4u));
        Assert.That(decoded.ProofLayers, Is.EqualTo(2u));
    }

    [Test]
    public void DecodeHashRequest_WrongLength_Throws()
    {
        Assert.Throws<ArgumentException>(() => Bep52WireMessages.DecodeHashRequest(new byte[47]));
        Assert.Throws<ArgumentException>(() => Bep52WireMessages.DecodeHashRequest(new byte[49]));
    }

    [Test]
    public void DecodeHashReject_WrongLength_Throws()
    {
        Assert.Throws<ArgumentException>(() => Bep52WireMessages.DecodeHashReject(new byte[47]));
        Assert.Throws<ArgumentException>(() => Bep52WireMessages.DecodeHashReject(new byte[49]));
    }

    [Test]
    public void Encode_InvalidPiecesRoot_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            Bep52WireMessages.Encode(new Bep52WireMessages.HashRequest(new byte[31], 0, 0, 0, 0)));
        Assert.Throws<ArgumentException>(() =>
            Bep52WireMessages.Encode(new Bep52WireMessages.HashReject(new byte[33], 0, 0, 0, 0)));
    }

    [Test]
    public void MessageIdConstants_MatchBep52Spec()
    {
        // BEP 52 reserves ids 21 / 22 / 23 for the v2 peer-wire extension messages.
        // If anyone ever changes these constants, everything downstream silently breaks
        // interop with other v2 clients. Pin them explicitly.
        Assert.That(Bep52WireMessages.MessageIdHashRequest, Is.EqualTo((byte)21));
        Assert.That(Bep52WireMessages.MessageIdHashes, Is.EqualTo((byte)22));
        Assert.That(Bep52WireMessages.MessageIdHashReject, Is.EqualTo((byte)23));
    }

    private static byte[] MakeRoot(byte fill)
    {
        var r = new byte[32];
        Array.Fill(r, fill);
        return r;
    }
}
