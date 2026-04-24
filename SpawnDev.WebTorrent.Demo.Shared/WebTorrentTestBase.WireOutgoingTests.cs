using SpawnDev.UnitTesting;
using SpawnDev.WebTorrent;

namespace SpawnDev.WebTorrent.Demo.Shared;

/// <summary>
/// Tests for outgoing wire protocol messages - verifies exact byte sequences.
/// Migrated from NUnit WireTests - these tests capture bytes sent via SendRaw
/// and verify they match the BitTorrent wire protocol specification.
/// </summary>
public abstract partial class WebTorrentTestBase
{
    private static Wire CreateTestWire(out List<byte[]> sentMessages)
    {
        var wire = new Wire();
        var sent = new List<byte[]>();
        wire.SendRaw = async (data) => { lock (sent) sent.Add(data.ToArray()); };
        sentMessages = sent;
        return wire;
    }

    [TestMethod]
    public async Task Wire_Interested_SendsCorrectBytes()
    {
        var wire = CreateTestWire(out var sent);
        wire.Interested();
        await Task.Delay(50); // let async send complete
        if (sent.Count == 0) throw new Exception("No message sent");
        var msg = sent[0];
        // Interested: len=0001, id=2 -> {0,0,0,1,2}
        if (msg.Length != 5) throw new Exception($"Expected 5 bytes, got {msg.Length}");
        if (msg[4] != 2) throw new Exception($"Expected id=2, got {msg[4]}");
        if (!wire.AmInterested) throw new Exception("AmInterested should be true");
    }

    [TestMethod]
    public async Task Wire_Interested_Idempotent()
    {
        var wire = CreateTestWire(out var sent);
        wire.Interested();
        wire.Interested(); // second call should be no-op
        await Task.Delay(50);
        if (sent.Count != 1) throw new Exception($"Should send only once, sent {sent.Count} times");
    }

    [TestMethod]
    public async Task Wire_Unchoke_SendsCorrectBytes()
    {
        var wire = CreateTestWire(out var sent);
        _ = wire.Unchoke();
        await Task.Delay(50);
        if (sent.Count == 0) throw new Exception("No message sent");
        var msg = sent[0];
        // Unchoke: len=0001, id=1 -> {0,0,0,1,1}
        if (msg.Length != 5) throw new Exception($"Expected 5 bytes, got {msg.Length}");
        if (msg[4] != 1) throw new Exception($"Expected id=1, got {msg[4]}");
        if (wire.AmChoking) throw new Exception("AmChoking should be false");
    }

    [TestMethod]
    public async Task Wire_Choke_SendsCorrectBytes()
    {
        var wire = CreateTestWire(out var sent);
        _ = wire.Unchoke(); // unchoke first
        await Task.Delay(50);
        sent.Clear();
        wire.Choke();
        await Task.Delay(50);
        if (sent.Count == 0) throw new Exception("No message sent");
        var msg = sent[0];
        // Choke: len=0001, id=0 -> {0,0,0,1,0}
        if (msg.Length != 5) throw new Exception($"Expected 5 bytes, got {msg.Length}");
        if (msg[4] != 0) throw new Exception($"Expected id=0, got {msg[4]}");
        if (!wire.AmChoking) throw new Exception("AmChoking should be true");
    }

    [TestMethod]
    public async Task Wire_Have_SendsCorrectBytes()
    {
        var wire = CreateTestWire(out var sent);
        _ = wire.Have(42);
        await Task.Delay(50);
        if (sent.Count == 0) throw new Exception("No message sent");
        var msg = sent[0];
        // Have: len=0005, id=4, piece=42 -> {0,0,0,5,4, 0,0,0,42}
        if (msg.Length != 9) throw new Exception($"Expected 9 bytes, got {msg.Length}");
        if (msg[4] != 4) throw new Exception($"Expected id=4, got {msg[4]}");
        int pieceIdx = (msg[5] << 24) | (msg[6] << 16) | (msg[7] << 8) | msg[8];
        if (pieceIdx != 42) throw new Exception($"Expected piece 42, got {pieceIdx}");
    }

    [TestMethod]
    public async Task Wire_KeepAlive_Fires()
    {
        var wire = new Wire();
        wire.SendRaw = async (data) => { };
        bool keepAliveFired = false;
        wire.OnKeepAlive += () => keepAliveFired = true;

        // Simulate handshake first
        var handshake = new byte[68];
        handshake[0] = 19;
        System.Text.Encoding.ASCII.GetBytes("BitTorrent protocol").CopyTo(handshake, 1);
        wire.DataReceived(handshake);

        // Keep-alive: 4 zero bytes (length=0, no message id)
        wire.DataReceived(new byte[] { 0, 0, 0, 0 });
        if (!keepAliveFired) throw new Exception("OnKeepAlive should fire on zero-length message");
    }

    [TestMethod]
    public async Task Wire_Port_FiresWithCorrectPort()
    {
        var wire = new Wire();
        wire.SendRaw = async (data) => { };
        int? receivedPort = null;
        wire.OnPort += (p) => receivedPort = p;

        // Simulate handshake
        var handshake = new byte[68];
        handshake[0] = 19;
        System.Text.Encoding.ASCII.GetBytes("BitTorrent protocol").CopyTo(handshake, 1);
        wire.DataReceived(handshake);

        // Port: len=3, id=9, port=6881 big-endian
        var msg = new byte[] { 0, 0, 0, 3, 9, (byte)(6881 >> 8), (byte)(6881 & 0xFF) };
        wire.DataReceived(msg);
        if (receivedPort != 6881) throw new Exception($"Expected port 6881, got {receivedPort}");
    }

    // ---- Migrated from NUnit WireTests.cs — cases not already covered above ----

    [TestMethod]
    public async Task Wire_Handshake_FastExtension_ParsesCorrectly()
    {
        var wire = new Wire();
        wire.SendRaw = _ => Task.CompletedTask;
        WireExtensions? exts = null;
        wire.OnHandshake += (_, _, e) => exts = e;

        var msg = new byte[68];
        msg[0] = 19;
        System.Text.Encoding.ASCII.GetBytes("BitTorrent protocol").CopyTo(msg, 1);
        msg[25] = 0x10; // extended
        msg[27] = 0x04; // fast
        wire.DataReceived(msg);

        if (exts is null) throw new Exception("OnHandshake didn't fire");
        if (!exts.Fast) throw new Exception("Fast bit not parsed from reserved[7]=0x04");
        if (!exts.Extended) throw new Exception("Extended bit not parsed");
        if (exts.Dht) throw new Exception("Dht bit unexpectedly set");
        await Task.CompletedTask;
    }

    [TestMethod]
    public async Task Wire_Unchoke_Idempotent()
    {
        var wire = new Wire();
        int sendCount = 0;
        wire.SendRaw = _ => { sendCount++; return Task.CompletedTask; };

        await wire.Unchoke();
        await wire.Unchoke();

        if (sendCount != 1) throw new Exception($"sendCount={sendCount}, expected 1 (Unchoke must be idempotent)");
    }

    [TestMethod]
    public async Task Wire_Bitfield_SendsCorrectBytes()
    {
        var wire = new Wire();
        var sent = new List<byte>();
        wire.SendRaw = data => { sent.AddRange(data); return Task.CompletedTask; };

        await wire.Bitfield(new byte[] { 0b11001010 });

        if (!sent.ToArray().SequenceEqual(new byte[] { 0, 0, 0, 2, 5, 0b11001010 }))
            throw new Exception("Bitfield frame mismatch, expected [0,0,0,2,5,0b11001010]");
    }

    [TestMethod]
    public async Task Wire_Handshake_SendsCorrectBytes()
    {
        var wire = new Wire();
        var sent = new List<byte>();
        wire.SendRaw = data => { sent.AddRange(data); return Task.CompletedTask; };

        var infoHash = new byte[20];
        var peerId = new byte[20];
        for (int i = 0; i < 20; i++) { infoHash[i] = (byte)i; peerId[i] = (byte)(i + 0x30); }

        await wire.Handshake(infoHash, peerId, dht: true, fast: false);

        var sentArr = sent.ToArray();
        if (sentArr.Length != 68) throw new Exception($"handshake length={sentArr.Length}, expected 68");
        if (sentArr[0] != 19) throw new Exception($"pstrlen={sentArr[0]}, expected 19");
        if (System.Text.Encoding.ASCII.GetString(sentArr, 1, 19) != "BitTorrent protocol")
            throw new Exception("protocol string mismatch");
        if ((sentArr[25] & 0x10) != 0x10) throw new Exception("Extended bit not set in reserved[5]");
        if ((sentArr[27] & 0x01) != 0x01) throw new Exception("DHT bit not set in reserved[7]");
        if ((sentArr[27] & 0x04) != 0) throw new Exception("Fast bit unexpectedly set");
        if (!sentArr[28..48].SequenceEqual(infoHash)) throw new Exception("infoHash bytes mismatch");
        if (!sentArr[48..68].SequenceEqual(peerId)) throw new Exception("peerId bytes mismatch");
    }
}
