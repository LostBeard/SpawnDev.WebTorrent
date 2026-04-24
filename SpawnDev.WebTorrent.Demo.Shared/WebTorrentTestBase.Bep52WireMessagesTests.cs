using SpawnDev.UnitTesting;
using SpawnDev.WebTorrent;

namespace SpawnDev.WebTorrent.Demo.Shared;

/// <summary>
/// BEP 52 v2 peer-wire message codec tests (hash_request 21 / hashes 22 / hash_reject 23).
/// Known-byte-sequence pinning + round-trip symmetry + malformed-input rejection.
/// Migrated from NUnit Bep52WireMessagesTests.cs so they run under PlaywrightMultiTest.
/// </summary>
public abstract partial class WebTorrentTestBase
{
    [TestMethod]
    public async Task Bep52Wire_HashRequest_RoundTrip_PreservesAllFields()
    {
        var root = Bep52WireMessagesTests_MakeRoot(0x42);
        var original = new Bep52WireMessages.HashRequest(root, BaseLayer: 2, Index: 0x10203040, Length: 5, ProofLayers: 3);
        var wire = Bep52WireMessages.Encode(original);
        if (wire.Length != Bep52WireMessages.HeaderSize)
            throw new Exception($"wire.Length={wire.Length}, expected {Bep52WireMessages.HeaderSize}");
        var decoded = Bep52WireMessages.DecodeHashRequest(wire);
        if (!decoded.PiecesRoot.SequenceEqual(root)) throw new Exception("PiecesRoot round-trip mismatch");
        if (decoded.BaseLayer != 2u) throw new Exception($"BaseLayer round-trip: got {decoded.BaseLayer}");
        if (decoded.Index != 0x10203040u) throw new Exception($"Index round-trip: got {decoded.Index:x}");
        if (decoded.Length != 5u) throw new Exception($"Length round-trip: got {decoded.Length}");
        if (decoded.ProofLayers != 3u) throw new Exception($"ProofLayers round-trip: got {decoded.ProofLayers}");
        await Task.CompletedTask;
    }

    [TestMethod]
    public async Task Bep52Wire_HashRequest_BigEndianWireFormat()
    {
        // Pinned known-answer for big-endian u32 serialization.
        var msg = new Bep52WireMessages.HashRequest(new byte[32], 1u, 0x01020304u, 0x00AABBCCu, 0xFF00FF00u);
        var wire = Bep52WireMessages.Encode(msg);

        var expected = new byte[48];
        expected[35] = 0x01;                                                     // BaseLayer
        expected[36] = 0x01; expected[37] = 0x02; expected[38] = 0x03; expected[39] = 0x04; // Index
        expected[40] = 0x00; expected[41] = 0xAA; expected[42] = 0xBB; expected[43] = 0xCC; // Length
        expected[44] = 0xFF; expected[45] = 0x00; expected[46] = 0xFF; expected[47] = 0x00; // ProofLayers

        if (!wire.SequenceEqual(expected))
            throw new Exception("wire bytes must match big-endian u32 pinned layout");
        await Task.CompletedTask;
    }

    [TestMethod]
    public async Task Bep52Wire_Hashes_RoundTrip_PreservesAllHashes()
    {
        var root = Bep52WireMessagesTests_MakeRoot(0x01);
        var hashes = new byte[][]
        {
            Bep52WireMessagesTests_MakeRoot(0x10), Bep52WireMessagesTests_MakeRoot(0x11), Bep52WireMessagesTests_MakeRoot(0x12),
            Bep52WireMessagesTests_MakeRoot(0x20), Bep52WireMessagesTests_MakeRoot(0x21),
        };
        var original = new Bep52WireMessages.Hashes(root, BaseLayer: 0, Index: 0, Length: 3, ProofLayers: 2, HashList: hashes);
        var wire = Bep52WireMessages.Encode(original);
        if (wire.Length != Bep52WireMessages.HeaderSize + 5 * 32)
            throw new Exception($"wire.Length={wire.Length}, expected {Bep52WireMessages.HeaderSize + 5 * 32}");

        var decoded = Bep52WireMessages.DecodeHashes(wire);
        if (decoded.HashList.Length != 5) throw new Exception($"HashList.Length={decoded.HashList.Length}, expected 5");
        for (int i = 0; i < 5; i++)
        {
            if (!decoded.HashList[i].SequenceEqual(hashes[i]))
                throw new Exception($"hash[{i}] round-trip mismatch");
        }
        await Task.CompletedTask;
    }

    [TestMethod]
    public async Task Bep52Wire_Hashes_MismatchedHashCount_EncodeThrows()
    {
        var msg = new Bep52WireMessages.Hashes(
            Bep52WireMessagesTests_MakeRoot(0),
            BaseLayer: 0, Index: 0, Length: 2, ProofLayers: 2,
            HashList: new[] { Bep52WireMessagesTests_MakeRoot(1), Bep52WireMessagesTests_MakeRoot(2), Bep52WireMessagesTests_MakeRoot(3) });
        try { Bep52WireMessages.Encode(msg); }
        catch (ArgumentException) { await Task.CompletedTask; return; }
        throw new Exception("Encode must reject HashList.Count != Length + ProofLayers");
    }

    [TestMethod]
    public async Task Bep52Wire_Hashes_MismatchedPayloadLength_DecodeThrows()
    {
        // Payload claims Length=2 + ProofLayers=1 but only carries 2 hashes.
        var wire = new byte[Bep52WireMessages.HeaderSize + 2 * 32];
        wire[43] = 0x02; // Length=2 at offset 40 (last byte)
        wire[47] = 0x01; // ProofLayers=1 at offset 44 (last byte)
        try { Bep52WireMessages.DecodeHashes(wire); }
        catch (ArgumentException) { await Task.CompletedTask; return; }
        throw new Exception("DecodeHashes must reject payload with count mismatching header");
    }

    [TestMethod]
    public async Task Bep52Wire_HashReject_RoundTrip_SameShapeAsHashRequest()
    {
        var root = Bep52WireMessagesTests_MakeRoot(0xAA);
        var original = new Bep52WireMessages.HashReject(root, BaseLayer: 7, Index: 123, Length: 4, ProofLayers: 2);
        var wire = Bep52WireMessages.Encode(original);
        if (wire.Length != Bep52WireMessages.HeaderSize)
            throw new Exception($"wire.Length={wire.Length}, expected {Bep52WireMessages.HeaderSize}");

        var decoded = Bep52WireMessages.DecodeHashReject(wire);
        if (!decoded.PiecesRoot.SequenceEqual(root)) throw new Exception("PiecesRoot round-trip mismatch");
        if (decoded.BaseLayer != 7u) throw new Exception($"BaseLayer round-trip: got {decoded.BaseLayer}");
        if (decoded.Index != 123u) throw new Exception($"Index round-trip: got {decoded.Index}");
        if (decoded.Length != 4u) throw new Exception($"Length round-trip: got {decoded.Length}");
        if (decoded.ProofLayers != 2u) throw new Exception($"ProofLayers round-trip: got {decoded.ProofLayers}");
        await Task.CompletedTask;
    }

    [TestMethod]
    public async Task Bep52Wire_DecodeHashRequest_WrongLength_Throws()
    {
        try { Bep52WireMessages.DecodeHashRequest(new byte[47]); goto fail47; } catch (ArgumentException) { }
        try { Bep52WireMessages.DecodeHashRequest(new byte[49]); goto fail49; } catch (ArgumentException) { }
        await Task.CompletedTask;
        return;
        fail47: throw new Exception("DecodeHashRequest must reject 47-byte payload");
        fail49: throw new Exception("DecodeHashRequest must reject 49-byte payload");
    }

    [TestMethod]
    public async Task Bep52Wire_DecodeHashReject_WrongLength_Throws()
    {
        try { Bep52WireMessages.DecodeHashReject(new byte[47]); goto fail47; } catch (ArgumentException) { }
        try { Bep52WireMessages.DecodeHashReject(new byte[49]); goto fail49; } catch (ArgumentException) { }
        await Task.CompletedTask;
        return;
        fail47: throw new Exception("DecodeHashReject must reject 47-byte payload");
        fail49: throw new Exception("DecodeHashReject must reject 49-byte payload");
    }

    [TestMethod]
    public async Task Bep52Wire_Encode_InvalidPiecesRoot_Throws()
    {
        try { Bep52WireMessages.Encode(new Bep52WireMessages.HashRequest(new byte[31], 0, 0, 0, 0)); goto failReq; }
        catch (ArgumentException) { }
        try { Bep52WireMessages.Encode(new Bep52WireMessages.HashReject(new byte[33], 0, 0, 0, 0)); goto failRej; }
        catch (ArgumentException) { }
        await Task.CompletedTask;
        return;
        failReq: throw new Exception("Encode must reject non-32-byte PiecesRoot on HashRequest");
        failRej: throw new Exception("Encode must reject non-32-byte PiecesRoot on HashReject");
    }

    [TestMethod]
    public async Task Bep52Wire_MessageIdConstants_MatchBep52Spec()
    {
        // BEP 52 reserves 21 / 22 / 23. Changing these silently breaks interop.
        if (Bep52WireMessages.MessageIdHashRequest != 21) throw new Exception("MessageIdHashRequest must be 21");
        if (Bep52WireMessages.MessageIdHashes != 22) throw new Exception("MessageIdHashes must be 22");
        if (Bep52WireMessages.MessageIdHashReject != 23) throw new Exception("MessageIdHashReject must be 23");
        await Task.CompletedTask;
    }

    // ---- helpers ----

    private static byte[] Bep52WireMessagesTests_MakeRoot(byte fill)
    {
        var r = new byte[32];
        Array.Fill(r, fill);
        return r;
    }
}
