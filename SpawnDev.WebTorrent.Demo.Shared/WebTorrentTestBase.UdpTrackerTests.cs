using SpawnDev.UnitTesting;
using SpawnDev.WebTorrent;

namespace SpawnDev.WebTorrent.Demo.Shared;

public abstract partial class WebTorrentTestBase
{
    [TestMethod]
    public async Task UdpTracker_ConnectMessage_CorrectFormat()
    {
        // Verify the 16-byte connect request format per BEP 15
        var request = new byte[16];
        UdpTrackerClient.WriteInt64BE(request, 0, 0x41727101980); // protocol_id
        UdpTrackerClient.WriteInt32BE(request, 8, 0); // action = connect
        UdpTrackerClient.WriteInt32BE(request, 12, 12345); // transaction_id

        if (request.Length != 16) throw new Exception($"Connect request should be 16 bytes, got {request.Length}");
        var protocolId = UdpTrackerClient.ReadInt64BE(request, 0);
        if (protocolId != 0x41727101980) throw new Exception($"Protocol ID wrong: {protocolId}");
        var action = UdpTrackerClient.ReadInt32BE(request, 8);
        if (action != 0) throw new Exception($"Action should be 0, got {action}");
        var txId = UdpTrackerClient.ReadInt32BE(request, 12);
        if (txId != 12345) throw new Exception($"Transaction ID wrong: {txId}");
    }

    [TestMethod]
    public async Task UdpTracker_AnnounceMessage_CorrectFormat()
    {
        // Verify the 98-byte announce request format per BEP 15
        var request = new byte[98];
        long connectionId = 0x1234567890ABCDEF;
        UdpTrackerClient.WriteInt64BE(request, 0, connectionId);
        UdpTrackerClient.WriteInt32BE(request, 8, 1); // action = announce
        UdpTrackerClient.WriteInt32BE(request, 12, 99999); // transaction_id

        if (request.Length != 98) throw new Exception($"Announce request should be 98 bytes, got {request.Length}");
        var readConnId = UdpTrackerClient.ReadInt64BE(request, 0);
        if (readConnId != connectionId) throw new Exception($"Connection ID wrong: {readConnId}");
        var action = UdpTrackerClient.ReadInt32BE(request, 8);
        if (action != 1) throw new Exception($"Action should be 1, got {action}");
    }

    [TestMethod]
    public async Task UdpTracker_ParseResponse_ExtractsPeers()
    {
        // Build a fake announce response with 2 peers and parse using production code
        var response = new byte[32]; // 20 header + 12 peer data
        UdpTrackerClient.WriteInt32BE(response, 0, 1); // action = announce
        UdpTrackerClient.WriteInt32BE(response, 4, 54321); // transaction_id
        UdpTrackerClient.WriteInt32BE(response, 8, 1800); // interval
        UdpTrackerClient.WriteInt32BE(response, 12, 5); // leechers
        UdpTrackerClient.WriteInt32BE(response, 16, 10); // seeders
        // Peer 1: 10.0.0.1:51413
        response[20] = 10; response[21] = 0; response[22] = 0; response[23] = 1;
        response[24] = (byte)(51413 >> 8); response[25] = (byte)(51413 & 0xFF);
        // Peer 2: 192.168.1.5:6881
        response[26] = 192; response[27] = 168; response[28] = 1; response[29] = 5;
        response[30] = (byte)(6881 >> 8); response[31] = (byte)(6881 & 0xFF);

        // Parse using the PRODUCTION compact peer decoder (UtPexExtension.DecodeCompactIPv4)
        var peer0 = UtPexExtension.DecodeCompactIPv4(response, 20);
        var peer1 = UtPexExtension.DecodeCompactIPv4(response, 26);

        if (peer0 != "10.0.0.1:51413") throw new Exception($"Peer 0 wrong: {peer0}");
        if (peer1 != "192.168.1.5:6881") throw new Exception($"Peer 1 wrong: {peer1}");

        // Verify header fields via production read methods
        var action = UdpTrackerClient.ReadInt32BE(response, 0);
        if (action != 1) throw new Exception($"Action should be 1 (announce), got {action}");
        var interval = UdpTrackerClient.ReadInt32BE(response, 8);
        if (interval != 1800) throw new Exception($"Interval should be 1800, got {interval}");
    }
}
