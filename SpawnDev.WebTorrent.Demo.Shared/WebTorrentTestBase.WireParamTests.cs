using SpawnDev.UnitTesting;
using SpawnDev.WebTorrent;
using System.Text;

namespace SpawnDev.WebTorrent.Demo.Shared;

/// <summary>
/// Tests for wire protocol message parameter correctness.
/// Verifies that incoming messages are parsed with correct index, offset, length, and data.
/// Migrated from NUnit WireTests.
/// </summary>
public abstract partial class WebTorrentTestBase
{
    private static byte[] Int32BE(int value) => new[] {
        (byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)(value & 0xFF)
    };

    private static byte[] MakeWireMessage(byte id, params byte[] payload)
    {
        var len = 1 + payload.Length;
        var buf = new byte[4 + len];
        Int32BE(len).CopyTo(buf, 0);
        buf[4] = id;
        payload.CopyTo(buf, 5);
        return buf;
    }

    private static Wire CreateWireAfterHandshake()
    {
        var wire = new Wire();
        wire.SendRaw = async (data) => { };
        wire.Extensions.Fast = true;
        wire.Extensions.Extended = true;
        var hs = new byte[68];
        hs[0] = 19;
        Encoding.ASCII.GetBytes("BitTorrent protocol").CopyTo(hs, 1);
        hs[25] = 0x10; hs[27] = 0x04;
        wire.DataReceived(hs);
        return wire;
    }

    [TestMethod]
    public async Task Wire_Request_ParsesParams()
    {
        var wire = CreateWireAfterHandshake();
        int? rIdx = null, rOff = null, rLen = null;
        wire.OnRequest += (idx, off, len, respond) => { rIdx = idx; rOff = off; rLen = len; };
        wire.AmChoking = false;

        var payload = new byte[12];
        Int32BE(5).CopyTo(payload, 0);
        Int32BE(16384).CopyTo(payload, 4);
        Int32BE(16384).CopyTo(payload, 8);
        wire.DataReceived(MakeWireMessage(6, payload));

        if (rIdx != 5) throw new Exception($"Request index: expected 5, got {rIdx}");
        if (rOff != 16384) throw new Exception($"Request offset: expected 16384, got {rOff}");
        if (rLen != 16384) throw new Exception($"Request length: expected 16384, got {rLen}");
    }

    [TestMethod]
    public async Task Wire_Piece_ParsesData()
    {
        var wire = CreateWireAfterHandshake();
        int? pIdx = null, pOff = null;
        byte[]? pData = null;
        wire.OnPiece += (idx, off, data) => { pIdx = idx; pOff = off; pData = data; };

        var blockData = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF, 0xCA, 0xFE };
        var payload = new byte[8 + blockData.Length];
        Int32BE(3).CopyTo(payload, 0);
        Int32BE(0).CopyTo(payload, 4);
        blockData.CopyTo(payload, 8);
        wire.DataReceived(MakeWireMessage(7, payload));

        if (pIdx != 3) throw new Exception($"Piece index: expected 3, got {pIdx}");
        if (pOff != 0) throw new Exception($"Piece offset: expected 0, got {pOff}");
        if (!pData!.SequenceEqual(blockData)) throw new Exception("Piece data mismatch");
        if (wire.Downloaded != blockData.Length) throw new Exception($"Downloaded should be {blockData.Length}, got {wire.Downloaded}");
    }

    [TestMethod]
    public async Task Wire_Cancel_ParsesParams()
    {
        var wire = CreateWireAfterHandshake();
        int? cIdx = null, cOff = null, cLen = null;
        wire.OnCancel += (idx, off, len) => { cIdx = idx; cOff = off; cLen = len; };

        var payload = new byte[12];
        Int32BE(7).CopyTo(payload, 0);
        Int32BE(0).CopyTo(payload, 4);
        Int32BE(16384).CopyTo(payload, 8);
        wire.DataReceived(MakeWireMessage(8, payload));

        if (cIdx != 7) throw new Exception($"Cancel index: expected 7, got {cIdx}");
        if (cOff != 0) throw new Exception($"Cancel offset: expected 0, got {cOff}");
        if (cLen != 16384) throw new Exception($"Cancel length: expected 16384, got {cLen}");
    }

    [TestMethod]
    public async Task Wire_FastHandshake_SetsHasFast()
    {
        var wire = new Wire();
        wire.SendRaw = async (data) => { };
        wire.Extensions.Fast = true;
        wire.Extensions.Extended = true;

        string? parsedHash = null;
        wire.OnHandshake += (ih, pid, ext) => parsedHash = ih;

        // Handshake with Fast AND Extended bits
        var hs = new byte[68];
        hs[0] = 19;
        Encoding.ASCII.GetBytes("BitTorrent protocol").CopyTo(hs, 1);
        hs[25] = 0x10; // Extended
        hs[27] = 0x04; // Fast
        wire.DataReceived(hs);

        if (parsedHash == null) throw new Exception("Handshake not parsed");
        if (!wire.HasFast) throw new Exception("HasFast should be true when both sides support Fast");
        if (!wire.PeerExtensions.Fast) throw new Exception("PeerExtensions.Fast should be true");
        if (!wire.PeerExtensions.Extended) throw new Exception("PeerExtensions.Extended should be true");
    }

    [TestMethod]
    public async Task Wire_NoFastHandshake_HasFastFalse()
    {
        var wire = new Wire();
        wire.SendRaw = async (data) => { };
        wire.Extensions.Fast = true; // WE support Fast

        // Handshake WITHOUT Fast bit
        var hs = new byte[68];
        hs[0] = 19;
        Encoding.ASCII.GetBytes("BitTorrent protocol").CopyTo(hs, 1);
        hs[25] = 0x10; // Extended only, no Fast
        wire.DataReceived(hs);

        if (wire.HasFast) throw new Exception("HasFast should be false when peer doesn't support Fast");
    }
}
