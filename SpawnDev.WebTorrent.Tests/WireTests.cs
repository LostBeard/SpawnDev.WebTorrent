using NUnit.Framework;
using SpawnDev.WebTorrent;

namespace SpawnDev.WebTorrent.Tests;

/// <summary>
/// Unit tests for Wire.cs — verify every message type parses correctly
/// by feeding raw BitTorrent protocol bytes and checking events fire.
/// Tests are against the actual wire format, not mocks.
/// </summary>
[TestFixture]
public class WireTests
{
    // Helper: build a length-prefixed BT message
    private static byte[] MakeMessage(byte id, params byte[] payload)
    {
        int len = 1 + payload.Length;
        var msg = new byte[4 + len];
        msg[0] = (byte)((len >> 24) & 0xFF);
        msg[1] = (byte)((len >> 16) & 0xFF);
        msg[2] = (byte)((len >> 8) & 0xFF);
        msg[3] = (byte)(len & 0xFF);
        msg[4] = id;
        if (payload.Length > 0)
            payload.CopyTo(msg, 5);
        return msg;
    }

    // Helper: build a 4-byte big-endian int
    private static byte[] Int32BE(int value) => new byte[]
    {
        (byte)((value >> 24) & 0xFF),
        (byte)((value >> 16) & 0xFF),
        (byte)((value >> 8) & 0xFF),
        (byte)(value & 0xFF),
    };

    // Helper: build the full BT handshake (68 bytes)
    private static byte[] MakeHandshake(byte[] infoHash, byte[] peerId, bool extended = true, bool dht = false, bool fast = false)
    {
        var msg = new byte[68];
        msg[0] = 19; // pstrlen
        "BitTorrent protocol"u8.CopyTo(msg.AsSpan(1));
        var reserved = new byte[8];
        if (extended) reserved[5] |= 0x10;
        if (dht) reserved[7] |= 0x01;
        if (fast) reserved[7] |= 0x04;
        reserved.CopyTo(msg, 20);
        infoHash.CopyTo(msg, 28);
        peerId.CopyTo(msg, 48);
        return msg;
    }

    // ========================
    // HANDSHAKE TESTS
    // ========================

    [Test]
    public void Handshake_ParsesCorrectly()
    {
        var wire = new Wire();
        var sentBytes = new List<byte>();
        wire.SendRaw = (data) => { sentBytes.AddRange(data); return Task.CompletedTask; };

        string? receivedInfoHash = null;
        string? receivedPeerId = null;
        WireExtensions? receivedExts = null;

        wire.OnHandshake += (ih, pid, exts) =>
        {
            receivedInfoHash = ih;
            receivedPeerId = pid;
            receivedExts = exts;
        };

        var infoHash = new byte[20];
        var peerId = new byte[20];
        for (int i = 0; i < 20; i++) { infoHash[i] = (byte)(i + 1); peerId[i] = (byte)(i + 0x41); }

        wire.DataReceived(MakeHandshake(infoHash, peerId, extended: true, dht: true, fast: false));

        Assert.That(receivedInfoHash, Is.Not.Null, "OnHandshake should have fired");
        Assert.That(receivedInfoHash, Is.EqualTo(Convert.ToHexString(infoHash).ToLowerInvariant()));
        Assert.That(receivedPeerId, Is.EqualTo(Convert.ToHexString(peerId).ToLowerInvariant()));
        Assert.That(receivedExts!.Extended, Is.True);
        Assert.That(receivedExts!.Dht, Is.True);
        Assert.That(receivedExts!.Fast, Is.False);
    }

    [Test]
    public void Handshake_FastExtension_ParsesCorrectly()
    {
        var wire = new Wire();
        wire.SendRaw = (data) => Task.CompletedTask;
        WireExtensions? exts = null;
        wire.OnHandshake += (_, _, e) => exts = e;

        var ih = new byte[20]; var pid = new byte[20];
        wire.DataReceived(MakeHandshake(ih, pid, extended: true, dht: false, fast: true));

        Assert.That(exts!.Fast, Is.True);
        Assert.That(exts!.Extended, Is.True);
        Assert.That(exts!.Dht, Is.False);
    }

    // ========================
    // MESSAGE PARSING TESTS — after handshake
    // ========================

    private Wire CreateWireAfterHandshake()
    {
        var wire = new Wire();
        wire.SendRaw = (data) => Task.CompletedTask;
        var ih = new byte[20]; var pid = new byte[20];
        wire.DataReceived(MakeHandshake(ih, pid));
        return wire;
    }

    [Test]
    public void KeepAlive_Fires()
    {
        var wire = CreateWireAfterHandshake();
        bool fired = false;
        wire.OnKeepAlive += () => fired = true;

        // Keep-alive: 4 zero bytes (length = 0)
        wire.DataReceived(new byte[] { 0, 0, 0, 0 });

        Assert.That(fired, Is.True, "OnKeepAlive should fire on 4 zero bytes");
    }

    [Test]
    public void Choke_SetsState()
    {
        var wire = CreateWireAfterHandshake();
        bool fired = false;
        wire.OnChoke += () => fired = true;

        // Choke: len=1, id=0
        wire.DataReceived(MakeMessage(0));

        Assert.That(fired, Is.True);
        Assert.That(wire.PeerChoking, Is.True);
    }

    [Test]
    public void Unchoke_SetsState()
    {
        var wire = CreateWireAfterHandshake();
        bool fired = false;
        wire.OnUnchoke += () => fired = true;

        // Unchoke: len=1, id=1
        wire.DataReceived(MakeMessage(1));

        Assert.That(fired, Is.True);
        Assert.That(wire.PeerChoking, Is.False);
    }

    [Test]
    public void Interested_SetsState()
    {
        var wire = CreateWireAfterHandshake();
        bool fired = false;
        wire.OnInterested += () => fired = true;

        // Interested: len=1, id=2
        wire.DataReceived(MakeMessage(2));

        Assert.That(fired, Is.True);
        Assert.That(wire.PeerInterested, Is.True);
    }

    [Test]
    public void Uninterested_SetsState()
    {
        var wire = CreateWireAfterHandshake();
        wire.PeerInterested = true; // set first
        bool fired = false;
        wire.OnUninterested += () => fired = true;

        // Uninterested: len=1, id=3
        wire.DataReceived(MakeMessage(3));

        Assert.That(fired, Is.True);
        Assert.That(wire.PeerInterested, Is.False);
    }

    [Test]
    public void Have_FiresWithCorrectIndex()
    {
        var wire = CreateWireAfterHandshake();
        int? receivedIndex = null;
        wire.OnHave += (idx) => receivedIndex = idx;

        // Have: len=5, id=4, piece_index=42
        wire.DataReceived(MakeMessage(4, Int32BE(42)));

        Assert.That(receivedIndex, Is.EqualTo(42));
        Assert.That(wire.PeerHasPiece(42), Is.True);
        Assert.That(wire.PeerHasPiece(41), Is.False);
    }

    [Test]
    public void Bitfield_ParsesCorrectly()
    {
        var wire = CreateWireAfterHandshake();
        byte[]? receivedBitfield = null;
        wire.OnBitfield += (bf) => receivedBitfield = bf;

        // Bitfield: len=1+N, id=5, bitfield bytes
        // 0b11001010 = pieces 0,1,4,6 set (out of first 8)
        wire.DataReceived(MakeMessage(5, 0b11001010));

        Assert.That(receivedBitfield, Is.Not.Null);
        Assert.That(receivedBitfield!.Length, Is.EqualTo(1));
        Assert.That(wire.PeerHasPiece(0), Is.True);
        Assert.That(wire.PeerHasPiece(1), Is.True);
        Assert.That(wire.PeerHasPiece(2), Is.False);
        Assert.That(wire.PeerHasPiece(3), Is.False);
        Assert.That(wire.PeerHasPiece(4), Is.True);
        Assert.That(wire.PeerHasPiece(5), Is.False);
        Assert.That(wire.PeerHasPiece(6), Is.True);
        Assert.That(wire.PeerHasPiece(7), Is.False);
    }

    [Test]
    public void Request_FiresWithCorrectParams()
    {
        var wire = CreateWireAfterHandshake();
        int? rIdx = null, rOff = null, rLen = null;
        wire.OnRequest += (idx, off, len, respond) => { rIdx = idx; rOff = off; rLen = len; };

        // Need to unchoke first (we're choking by default, request would be rejected)
        wire.AmChoking = false;

        // Request: len=13, id=6, index=5, begin=16384, length=16384
        var payload = new byte[12];
        Int32BE(5).CopyTo(payload, 0);
        Int32BE(16384).CopyTo(payload, 4);
        Int32BE(16384).CopyTo(payload, 8);
        wire.DataReceived(MakeMessage(6, payload));

        Assert.That(rIdx, Is.EqualTo(5));
        Assert.That(rOff, Is.EqualTo(16384));
        Assert.That(rLen, Is.EqualTo(16384));
    }

    [Test]
    public void Piece_FiresWithCorrectData()
    {
        var wire = CreateWireAfterHandshake();
        int? pIdx = null, pOff = null;
        byte[]? pData = null;
        wire.OnPiece += (idx, off, data) => { pIdx = idx; pOff = off; pData = data; };

        // Piece: len=9+block_len, id=7, index=3, begin=0, block=bytes
        var blockData = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF, 0xCA, 0xFE };
        var payload = new byte[8 + blockData.Length];
        Int32BE(3).CopyTo(payload, 0);
        Int32BE(0).CopyTo(payload, 4);
        blockData.CopyTo(payload, 8);
        wire.DataReceived(MakeMessage(7, payload));

        Assert.That(pIdx, Is.EqualTo(3));
        Assert.That(pOff, Is.EqualTo(0));
        Assert.That(pData, Is.EqualTo(blockData));
        Assert.That(wire.Downloaded, Is.EqualTo(blockData.Length));
    }

    [Test]
    public void Cancel_FiresWithCorrectParams()
    {
        var wire = CreateWireAfterHandshake();
        int? cIdx = null, cOff = null, cLen = null;
        wire.OnCancel += (idx, off, len) => { cIdx = idx; cOff = off; cLen = len; };

        // Cancel: len=13, id=8, index=7, begin=0, length=16384
        var payload = new byte[12];
        Int32BE(7).CopyTo(payload, 0);
        Int32BE(0).CopyTo(payload, 4);
        Int32BE(16384).CopyTo(payload, 8);
        wire.DataReceived(MakeMessage(8, payload));

        Assert.That(cIdx, Is.EqualTo(7));
        Assert.That(cOff, Is.EqualTo(0));
        Assert.That(cLen, Is.EqualTo(16384));
    }

    [Test]
    public void Port_FiresWithCorrectPort()
    {
        var wire = CreateWireAfterHandshake();
        int? receivedPort = null;
        wire.OnPort += (p) => receivedPort = p;

        // Port: len=3, id=9, port=6881 (big-endian)
        wire.DataReceived(MakeMessage(9, (byte)(6881 >> 8), (byte)(6881 & 0xFF)));

        Assert.That(receivedPort, Is.EqualTo(6881));
    }

    [Test]
    public void HaveAll_BEP6_Fires()
    {
        var wire = CreateWireAfterHandshake();
        // Enable fast extension on both sides
        wire.Extensions = new WireExtensions { Fast = true, Extended = true };
        wire.HasFast = true;

        bool fired = false;
        wire.OnHaveAll += () => fired = true;

        // HaveAll: len=1, id=0x0E
        wire.DataReceived(MakeMessage(0x0E));

        Assert.That(fired, Is.True);
        Assert.That(wire.PeerHasAll, Is.True);
        Assert.That(wire.PeerHasPiece(0), Is.True);
        Assert.That(wire.PeerHasPiece(999999), Is.True);
    }

    [Test]
    public void HaveNone_BEP6_Fires()
    {
        var wire = CreateWireAfterHandshake();
        wire.Extensions = new WireExtensions { Fast = true, Extended = true };
        wire.HasFast = true;

        bool fired = false;
        wire.OnHaveNone += () => fired = true;

        // HaveNone: len=1, id=0x0F
        wire.DataReceived(MakeMessage(0x0F));

        Assert.That(fired, Is.True);
    }

    [Test]
    public void Extended_BEP10_FiresHandshake()
    {
        var wire = CreateWireAfterHandshake();
        string? extName = null;
        byte[]? extPayload = null;
        wire.OnExtended += (name, payload) => { extName = name; extPayload = payload; };

        // Extended: len=variable, id=20, ext_id=0 (handshake), bencode payload
        // Minimal bencode dict: d1:md11:ut_metadatai1eee ({"m":{"ut_metadata":1}})
        var bencodePayload = System.Text.Encoding.ASCII.GetBytes("d1:md11:ut_metadatai1eee");
        var payload = new byte[1 + bencodePayload.Length];
        payload[0] = 0; // ext_id 0 = handshake
        bencodePayload.CopyTo(payload, 1);
        wire.DataReceived(MakeMessage(20, payload));

        Assert.That(extName, Is.EqualTo("handshake"));
        Assert.That(wire.PeerExtendedMapping.ContainsKey("ut_metadata"), Is.True);
        Assert.That(wire.PeerExtendedMapping["ut_metadata"], Is.EqualTo(1));
    }

    // ========================
    // OUTGOING MESSAGE TESTS — verify what we send
    // ========================

    [Test]
    public async Task Interested_SendsCorrectBytes()
    {
        var wire = new Wire();
        var sent = new List<byte>();
        wire.SendRaw = (data) => { sent.AddRange(data); return Task.CompletedTask; };

        await wire.Interested();

        // Interested: 00 00 00 01 02
        Assert.That(sent.ToArray(), Is.EqualTo(new byte[] { 0, 0, 0, 1, 2 }));
        Assert.That(wire.AmInterested, Is.True);
    }

    [Test]
    public async Task Interested_Idempotent()
    {
        var wire = new Wire();
        int sendCount = 0;
        wire.SendRaw = (data) => { sendCount++; return Task.CompletedTask; };

        await wire.Interested();
        await wire.Interested(); // second call should be no-op

        Assert.That(sendCount, Is.EqualTo(1), "Interested should only send once (idempotent)");
    }

    [Test]
    public async Task Unchoke_SendsCorrectBytes()
    {
        var wire = new Wire();
        var sent = new List<byte>();
        wire.SendRaw = (data) => { sent.AddRange(data); return Task.CompletedTask; };

        await wire.Unchoke();

        // Unchoke: 00 00 00 01 01
        Assert.That(sent.ToArray(), Is.EqualTo(new byte[] { 0, 0, 0, 1, 1 }));
        Assert.That(wire.AmChoking, Is.False);
    }

    [Test]
    public async Task Unchoke_Idempotent()
    {
        var wire = new Wire();
        int sendCount = 0;
        wire.SendRaw = (data) => { sendCount++; return Task.CompletedTask; };

        await wire.Unchoke();
        await wire.Unchoke();

        Assert.That(sendCount, Is.EqualTo(1), "Unchoke should only send once (idempotent)");
    }

    [Test]
    public async Task Choke_SendsCorrectBytes()
    {
        var wire = new Wire();
        var sent = new List<byte>();
        wire.SendRaw = (data) => { sent.AddRange(data); return Task.CompletedTask; };

        // Must unchoke first so choke has an effect
        await wire.Unchoke();
        sent.Clear();
        await wire.Choke();

        Assert.That(sent.ToArray(), Is.EqualTo(new byte[] { 0, 0, 0, 1, 0 }));
        Assert.That(wire.AmChoking, Is.True);
    }

    [Test]
    public async Task Have_SendsCorrectBytes()
    {
        var wire = new Wire();
        var sent = new List<byte>();
        wire.SendRaw = (data) => { sent.AddRange(data); return Task.CompletedTask; };

        await wire.Have(42);

        // Have: len=5, id=4, index=42 (big-endian)
        Assert.That(sent.ToArray(), Is.EqualTo(new byte[] { 0, 0, 0, 5, 4, 0, 0, 0, 42 }));
    }

    [Test]
    public async Task Bitfield_SendsCorrectBytes()
    {
        var wire = new Wire();
        var sent = new List<byte>();
        wire.SendRaw = (data) => { sent.AddRange(data); return Task.CompletedTask; };

        await wire.Bitfield(new byte[] { 0b11001010 });

        // Bitfield: len=2, id=5, bitfield
        Assert.That(sent.ToArray(), Is.EqualTo(new byte[] { 0, 0, 0, 2, 5, 0b11001010 }));
    }

    [Test]
    public async Task Handshake_SendsCorrectBytes()
    {
        var wire = new Wire();
        var sent = new List<byte>();
        wire.SendRaw = (data) => { sent.AddRange(data); return Task.CompletedTask; };

        var infoHash = new byte[20];
        var peerId = new byte[20];
        for (int i = 0; i < 20; i++) { infoHash[i] = (byte)i; peerId[i] = (byte)(i + 0x30); }

        await wire.Handshake(infoHash, peerId, dht: true, fast: false);

        var sentArr = sent.ToArray();
        Assert.That(sentArr.Length, Is.EqualTo(68), "Handshake is 68 bytes");
        Assert.That(sentArr[0], Is.EqualTo(19), "pstrlen = 19");
        Assert.That(System.Text.Encoding.ASCII.GetString(sentArr, 1, 19), Is.EqualTo("BitTorrent protocol"));
        // reserved[5] should have 0x10 (extended)
        Assert.That(sentArr[25] & 0x10, Is.EqualTo(0x10), "Extended bit set");
        // reserved[7] should have 0x01 (DHT)
        Assert.That(sentArr[27] & 0x01, Is.EqualTo(0x01), "DHT bit set");
        // reserved[7] should NOT have 0x04 (fast)
        Assert.That(sentArr[27] & 0x04, Is.EqualTo(0), "Fast bit not set");
        // infoHash at offset 28
        Assert.That(sentArr[28..48], Is.EqualTo(infoHash));
        // peerId at offset 48
        Assert.That(sentArr[48..68], Is.EqualTo(peerId));
    }

    // ========================
    // MULTI-MESSAGE / FRAMING TESTS
    // ========================

    [Test]
    public void MultipleMessages_ParsedCorrectly()
    {
        var wire = CreateWireAfterHandshake();
        var events = new List<string>();
        wire.OnChoke += () => events.Add("choke");
        wire.OnUnchoke += () => events.Add("unchoke");
        wire.OnInterested += () => events.Add("interested");

        // Send choke + unchoke + interested in one buffer
        var combined = new byte[15]; // 5 + 5 + 5
        MakeMessage(0).CopyTo(combined, 0);    // choke
        MakeMessage(1).CopyTo(combined, 5);    // unchoke
        MakeMessage(2).CopyTo(combined, 10);   // interested

        wire.DataReceived(combined);

        Assert.That(events, Is.EqualTo(new[] { "choke", "unchoke", "interested" }));
    }

    [Test]
    public void FragmentedMessage_ParsedCorrectly()
    {
        var wire = CreateWireAfterHandshake();
        int? receivedIndex = null;
        wire.OnHave += (idx) => receivedIndex = idx;

        // Have message (9 bytes total) split into 3 fragments
        var full = MakeMessage(4, Int32BE(100));
        wire.DataReceived(full[..3]);   // first 3 bytes
        Assert.That(receivedIndex, Is.Null, "Should not fire yet");

        wire.DataReceived(full[3..6]);  // next 3 bytes
        Assert.That(receivedIndex, Is.Null, "Should not fire yet");

        wire.DataReceived(full[6..]);   // remaining bytes
        Assert.That(receivedIndex, Is.EqualTo(100), "Should fire after all bytes received");
    }

    // ========================
    // EDGE CASES
    // ========================

    [Test]
    public void Destroy_StopsProcessing()
    {
        var wire = CreateWireAfterHandshake();
        bool fired = false;
        wire.OnChoke += () => fired = true;

        wire.Destroy();
        wire.DataReceived(MakeMessage(0)); // choke after destroy

        Assert.That(fired, Is.False, "Events should not fire after destroy");
        Assert.That(wire.Destroyed, Is.True);
    }

    [Test]
    public void Request_RejectedWhenWeAreChoking()
    {
        var wire = CreateWireAfterHandshake();
        // Wire defaults to AmChoking = true
        Assert.That(wire.AmChoking, Is.True);

        bool requestFired = false;
        wire.OnRequest += (_, _, _, _) => requestFired = true;

        var payload = new byte[12];
        Int32BE(0).CopyTo(payload, 0);
        Int32BE(0).CopyTo(payload, 4);
        Int32BE(16384).CopyTo(payload, 8);
        wire.DataReceived(MakeMessage(6, payload));

        Assert.That(requestFired, Is.False, "Request should be rejected when we are choking");
    }
}
