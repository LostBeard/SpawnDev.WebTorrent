using SpawnDev.UnitTesting;
using SpawnDev.WebTorrent;
using System.Text;

namespace SpawnDev.WebTorrent.Demo.Shared;

/// <summary>
/// Tests for wire protocol state transitions - choke/unchoke/interested/uninterested
/// set correct state on the wire. Migrated from NUnit WireTests.
/// </summary>
public abstract partial class WebTorrentTestBase
{
    [TestMethod]
    public async Task Wire_Choke_SetsState()
    {
        var (wire, _) = CreateTestWire();
        wire.DataReceived(BuildHandshake(new byte[20], new byte[20]));
        wire.OnChoke += () => { };
        wire.DataReceived(BuildMessage(0, Array.Empty<byte>())); // choke
        if (!wire.PeerChoking) throw new Exception("PeerChoking should be true after choke message");
    }

    [TestMethod]
    public async Task Wire_Unchoke_SetsState()
    {
        var (wire, _) = CreateTestWire();
        wire.DataReceived(BuildHandshake(new byte[20], new byte[20]));
        wire.DataReceived(BuildMessage(1, Array.Empty<byte>())); // unchoke
        if (wire.PeerChoking) throw new Exception("PeerChoking should be false after unchoke message");
    }

    [TestMethod]
    public async Task Wire_Interested_SetsState()
    {
        var (wire, _) = CreateTestWire();
        wire.DataReceived(BuildHandshake(new byte[20], new byte[20]));
        wire.DataReceived(BuildMessage(2, Array.Empty<byte>())); // interested
        if (!wire.PeerInterested) throw new Exception("PeerInterested should be true");
    }

    [TestMethod]
    public async Task Wire_Uninterested_SetsState()
    {
        var (wire, _) = CreateTestWire();
        wire.DataReceived(BuildHandshake(new byte[20], new byte[20]));
        wire.DataReceived(BuildMessage(2, Array.Empty<byte>())); // interested first
        if (!wire.PeerInterested) throw new Exception("Should be interested first");
        wire.DataReceived(BuildMessage(3, Array.Empty<byte>())); // uninterested
        if (wire.PeerInterested) throw new Exception("PeerInterested should be false after uninterested");
    }

    [TestMethod]
    public async Task Wire_Have_SetsCorrectPiece()
    {
        var (wire, _) = CreateTestWire();
        wire.DataReceived(BuildHandshake(new byte[20], new byte[20]));
        int? haveIdx = null;
        wire.OnHave += (idx) => haveIdx = idx;

        var payload = new byte[4];
        payload[0] = 0; payload[1] = 0; payload[2] = 0; payload[3] = 42;
        wire.DataReceived(BuildMessage(4, payload));

        if (haveIdx != 42) throw new Exception($"Have index: expected 42, got {haveIdx}");
        if (!wire.PeerHasPiece(42)) throw new Exception("PeerHasPiece(42) should be true");
        if (wire.PeerHasPiece(41)) throw new Exception("PeerHasPiece(41) should be false");
    }

    [TestMethod]
    public async Task Wire_Bitfield_ParsesIndividualBits()
    {
        var (wire, _) = CreateTestWire();
        wire.DataReceived(BuildHandshake(new byte[20], new byte[20]));

        // 0b11001010 = pieces 0,1,4,6 set; pieces 2,3,5,7 not set
        wire.DataReceived(BuildMessage(5, new byte[] { 0b11001010 }));

        if (!wire.PeerHasPiece(0)) throw new Exception("Piece 0 should be set");
        if (!wire.PeerHasPiece(1)) throw new Exception("Piece 1 should be set");
        if (wire.PeerHasPiece(2)) throw new Exception("Piece 2 should NOT be set");
        if (wire.PeerHasPiece(3)) throw new Exception("Piece 3 should NOT be set");
        if (!wire.PeerHasPiece(4)) throw new Exception("Piece 4 should be set");
        if (wire.PeerHasPiece(5)) throw new Exception("Piece 5 should NOT be set");
        if (!wire.PeerHasPiece(6)) throw new Exception("Piece 6 should be set");
        if (wire.PeerHasPiece(7)) throw new Exception("Piece 7 should NOT be set");
    }
}
