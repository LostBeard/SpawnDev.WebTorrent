using SpawnDev.UnitTesting;
using SpawnDev.WebTorrent;
using System.Text;

namespace SpawnDev.WebTorrent.Demo.Shared;

public abstract partial class WebTorrentTestBase
{
    // Helper: create a wire with a data sink (Fast extension enabled for BEP 6 tests)
    private static (Wire wire, List<byte[]> sent) CreateTestWire(bool fast = true)
    {
        var sent = new List<byte[]>();
        var wire = new Wire();
        wire.SendRaw = async (data) => sent.Add(data);
        wire.Extensions.Fast = fast;    // Enable Fast on our side
        wire.Extensions.Extended = true; // Enable Extended on our side
        return (wire, sent);
    }

    // Helper: build a BT handshake with both Extended and Fast bits
    private static byte[] BuildHandshake(byte[] infoHash, byte[] peerId, bool extended = true, bool fast = true)
    {
        var buf = new byte[68];
        buf[0] = 19;
        Encoding.ASCII.GetBytes("BitTorrent protocol").CopyTo(buf, 1);
        if (extended) buf[25] = 0x10; // BEP 10 extension bit (reserved[5])
        if (fast) buf[27] = 0x04;     // BEP 6 Fast extension bit (reserved[7])
        infoHash.CopyTo(buf, 28);
        peerId.CopyTo(buf, 48);
        return buf;
    }

    // Helper: build a BT message (4-byte length prefix + id + payload)
    private static byte[] BuildMessage(byte id, byte[] payload)
    {
        var len = 1 + payload.Length;
        var buf = new byte[4 + len];
        buf[0] = (byte)(len >> 24); buf[1] = (byte)(len >> 16);
        buf[2] = (byte)(len >> 8); buf[3] = (byte)len;
        buf[4] = id;
        payload.CopyTo(buf, 5);
        return buf;
    }

    [TestMethod]
    public async Task Wire_Handshake_Parses()
    {
        var (wire, _) = CreateTestWire();
        var infoHash = new byte[20]; infoHash[0] = 0xAB;
        var peerId = new byte[20]; peerId[0] = 0xCD;
        string? parsedHash = null;
        wire.OnHandshake += (ih, pid, ext) => parsedHash = ih;

        wire.DataReceived(BuildHandshake(infoHash, peerId));
        await Task.Delay(10);

        if (parsedHash == null) throw new Exception("Handshake not parsed");
        if (!parsedHash.StartsWith("ab")) throw new Exception($"Wrong infoHash: {parsedHash}");
    }

    [TestMethod]
    public async Task Wire_Choke_Unchoke()
    {
        var (wire, _) = CreateTestWire();
        wire.DataReceived(BuildHandshake(new byte[20], new byte[20]));

        bool choked = false, unchoked = false;
        wire.OnChoke += () => choked = true;
        wire.OnUnchoke += () => unchoked = true;

        wire.DataReceived(BuildMessage(0, Array.Empty<byte>())); // choke
        wire.DataReceived(BuildMessage(1, Array.Empty<byte>())); // unchoke
        await Task.Delay(10);

        if (!choked) throw new Exception("Choke not received");
        if (!unchoked) throw new Exception("Unchoke not received");
    }

    [TestMethod]
    public async Task Wire_Interested_Uninterested()
    {
        var (wire, _) = CreateTestWire();
        wire.DataReceived(BuildHandshake(new byte[20], new byte[20]));

        bool interested = false, uninterested = false;
        wire.OnInterested += () => interested = true;
        wire.OnUninterested += () => uninterested = true;

        wire.DataReceived(BuildMessage(2, Array.Empty<byte>())); // interested
        wire.DataReceived(BuildMessage(3, Array.Empty<byte>())); // not interested
        await Task.Delay(10);

        if (!interested) throw new Exception("Interested not received");
        if (!uninterested) throw new Exception("Uninterested not received");
    }

    [TestMethod]
    public async Task Wire_Have_UpdatesBitfield()
    {
        var (wire, _) = CreateTestWire();
        wire.DataReceived(BuildHandshake(new byte[20], new byte[20]));

        int? haveIndex = null;
        wire.OnHave += (idx) => haveIndex = idx;

        var payload = new byte[4];
        payload[3] = 42; // index 42
        wire.DataReceived(BuildMessage(4, payload));
        await Task.Delay(10);

        if (haveIndex != 42) throw new Exception($"Expected have(42), got {haveIndex}");
    }

    [TestMethod]
    public async Task Wire_Bitfield_Parses()
    {
        var (wire, _) = CreateTestWire();
        wire.DataReceived(BuildHandshake(new byte[20], new byte[20]));

        byte[] rawBitfield = Array.Empty<byte>();
        wire.OnBitfield += (bf) => rawBitfield = bf;

        wire.DataReceived(BuildMessage(5, new byte[] { 0b10100000 })); // pieces 0 and 2
        await Task.Delay(10);

        if (rawBitfield.Length == 0) throw new Exception("Bitfield not received");
        // Verify bit 0 and bit 2 are set (0b10100000 = bits 7,5 in MSB order = pieces 0,2)
        if ((rawBitfield[0] & 0x80) == 0) throw new Exception("Piece 0 should be set");
        if ((rawBitfield[0] & 0x20) == 0) throw new Exception("Piece 2 should be set");
        if ((rawBitfield[0] & 0x40) != 0) throw new Exception("Piece 1 should not be set");
    }

    [TestMethod]
    public async Task Wire_Request_Fires()
    {
        var (wire, _) = CreateTestWire();
        wire.DataReceived(BuildHandshake(new byte[20], new byte[20]));

        // Unchoke the wire — OnRequest only fires when we are NOT choking the peer
        wire.AmChoking = false;

        (int idx, int off, int len)? req = null;
        wire.OnRequest += (i, o, l, respond) => req = (i, o, l);

        var payload = new byte[12];
        payload[3] = 5; // index 5
        payload[7] = 0; // offset 0
        payload[8] = 0; payload[9] = 0; payload[10] = 0x40; payload[11] = 0x00; // length 16384
        wire.DataReceived(BuildMessage(6, payload));
        await Task.Delay(10);

        if (req == null) throw new Exception("Request not fired");
        if (req.Value.idx != 5) throw new Exception($"Wrong index: {req.Value.idx}");
    }

    [TestMethod]
    public async Task Wire_Piece_Fires()
    {
        var (wire, _) = CreateTestWire();
        wire.DataReceived(BuildHandshake(new byte[20], new byte[20]));

        byte[]? pieceData = null;
        wire.OnPiece += (idx, off, data) => pieceData = data;

        var block = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF };
        var payload = new byte[8 + block.Length];
        payload[3] = 7; // index 7
        // offset 0 already
        block.CopyTo(payload, 8);
        wire.DataReceived(BuildMessage(7, payload));
        await Task.Delay(10);

        if (pieceData == null) throw new Exception("Piece not fired");
        if (pieceData.Length != 4) throw new Exception($"Wrong data length: {pieceData.Length}");
    }

    [TestMethod]
    public async Task Wire_Cancel_Fires()
    {
        var (wire, _) = CreateTestWire();
        wire.DataReceived(BuildHandshake(new byte[20], new byte[20]));

        (int idx, int off, int len)? cancel = null;
        wire.OnCancel += (i, o, l) => cancel = (i, o, l);

        var payload = new byte[12];
        payload[3] = 3; // index 3
        wire.DataReceived(BuildMessage(8, payload));
        await Task.Delay(10);

        if (cancel == null) throw new Exception("Cancel not fired");
        if (cancel.Value.idx != 3) throw new Exception($"Wrong cancel index: {cancel.Value.idx}");
    }

    [TestMethod]
    public async Task Wire_HaveAll_BEP6()
    {
        var (wire, _) = CreateTestWire();
        wire.DataReceived(BuildHandshake(new byte[20], new byte[20], extended: true));

        bool haveAll = false;
        wire.OnHaveAll += () => haveAll = true;

        wire.DataReceived(BuildMessage(0x0E, Array.Empty<byte>())); // have-all (BEP 6)
        await Task.Delay(10);

        if (!haveAll) throw new Exception("HaveAll not fired");
    }

    [TestMethod]
    public async Task Wire_HaveNone_BEP6()
    {
        var (wire, _) = CreateTestWire();
        wire.DataReceived(BuildHandshake(new byte[20], new byte[20], extended: true));

        bool haveNone = false;
        wire.OnHaveNone += () => haveNone = true;

        wire.DataReceived(BuildMessage(0x0F, Array.Empty<byte>())); // have-none (BEP 6)
        await Task.Delay(10);

        if (!haveNone) throw new Exception("HaveNone not fired");
    }

    [TestMethod]
    public async Task Wire_Extended_BEP10_Handshake()
    {
        var (wire, _) = CreateTestWire();
        wire.DataReceived(BuildHandshake(new byte[20], new byte[20], extended: true));

        Dictionary<string, object>? extHs = null;
        wire.OnExtended += (name, data) =>
        {
            if (name == "handshake")
            {
                var (decoded, _) = Bencode.BencodeDecoder.Decode(data, 0);
                extHs = decoded as Dictionary<string, object>;
            }
        };

        // Build a minimal extended handshake: d1:md11:ut_metadatai1eee
        var hsPayload = Encoding.ASCII.GetBytes("d1:md11:ut_metadatai1eee");
        var extMsg = new byte[1 + hsPayload.Length];
        extMsg[0] = 0; // extended handshake id
        hsPayload.CopyTo(extMsg, 1);
        wire.DataReceived(BuildMessage(20, extMsg)); // msg id 20 = extended
        await Task.Delay(10);

        if (extHs == null) throw new Exception("Extended handshake not parsed");
    }

    [TestMethod]
    public async Task Wire_FragmentedMessage()
    {
        var (wire, _) = CreateTestWire();
        wire.DataReceived(BuildHandshake(new byte[20], new byte[20]));

        bool gotChoke = false;
        wire.OnChoke += () => gotChoke = true;

        // Send choke message in two fragments
        var choke = BuildMessage(0, Array.Empty<byte>()); // [0,0,0,1, 0]
        wire.DataReceived(choke[..3]); // first 3 bytes
        wire.DataReceived(choke[3..]); // remaining bytes
        await Task.Delay(10);

        if (!gotChoke) throw new Exception("Fragmented choke not assembled");
    }

    [TestMethod]
    public async Task Wire_MultipleMessages_OneBuffer()
    {
        var (wire, _) = CreateTestWire();
        wire.DataReceived(BuildHandshake(new byte[20], new byte[20]));

        int eventCount = 0;
        wire.OnChoke += () => eventCount++;
        wire.OnUnchoke += () => eventCount++;

        var choke = BuildMessage(0, Array.Empty<byte>());
        var unchoke = BuildMessage(1, Array.Empty<byte>());
        var combined = new byte[choke.Length + unchoke.Length];
        choke.CopyTo(combined, 0);
        unchoke.CopyTo(combined, choke.Length);
        wire.DataReceived(combined);
        await Task.Delay(10);

        if (eventCount != 2) throw new Exception($"Expected 2 events, got {eventCount}");
    }

    [TestMethod]
    public async Task Wire_Destroy_StopsProcessing()
    {
        var (wire, _) = CreateTestWire();
        wire.DataReceived(BuildHandshake(new byte[20], new byte[20]));

        wire.Destroy();

        bool gotEvent = false;
        wire.OnChoke += () => gotEvent = true;
        wire.DataReceived(BuildMessage(0, Array.Empty<byte>()));
        await Task.Delay(10);

        if (gotEvent) throw new Exception("Event fired after Destroy");
    }

    [TestMethod]
    public async Task Wire_RequestRejectedWhenChoking()
    {
        var (wire, _) = CreateTestWire();
        wire.DataReceived(BuildHandshake(new byte[20], new byte[20]));

        // Wire starts choking by default (AmChoking = true)
        bool requestFired = false;
        wire.OnRequest += (i, o, l, r) => requestFired = true;

        var payload = new byte[12];
        wire.DataReceived(BuildMessage(6, payload)); // request
        await Task.Delay(10);

        // Should NOT fire because we are choking the peer
        if (requestFired) throw new Exception("Request should be rejected when choking");
    }
}
