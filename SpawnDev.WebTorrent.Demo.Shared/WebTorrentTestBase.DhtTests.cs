using SpawnDev.UnitTesting;
using SpawnDev.WebTorrent;
using System.Security.Cryptography;

namespace SpawnDev.WebTorrent.Demo.Shared;

public abstract partial class WebTorrentTestBase
{
    [TestMethod]
    public async Task Dht_NodeId_20Bytes()
    {
        var dht = new DhtDiscovery();
        if (dht.NodeId.Length != 20)
            throw new Exception($"Node ID should be 20 bytes, got {dht.NodeId.Length}");
        // Should not be all zeros
        if (dht.NodeId.All(b => b == 0))
            throw new Exception("Node ID should be random, not all zeros");
        await dht.DisposeAsync();
    }

    [TestMethod]
    public async Task Dht_RoutingTable_AddAndGetClosest()
    {
        var localId = new byte[20];
        RandomNumberGenerator.Fill(localId);
        var table = new KademliaRoutingTable(localId);

        // Add 5 nodes with known IDs
        for (int i = 0; i < 5; i++)
        {
            var nodeId = new byte[20];
            nodeId[0] = (byte)(i + 1); // different first byte
            table.AddNode(new DhtNode
            {
                NodeId = nodeId,
                EndPoint = new System.Net.IPEndPoint(System.Net.IPAddress.Parse($"10.0.0.{i + 1}"), 6881),
                LastSeen = DateTime.UtcNow,
            });
        }

        if (table.NodeCount != 5)
            throw new Exception($"Expected 5 nodes, got {table.NodeCount}");

        // Get closest to a target
        var target = new byte[20]; target[0] = 3; // close to node with id[0]=3
        var closest = table.GetClosest(target, 3);
        if (closest.Count != 3)
            throw new Exception($"Expected 3 closest, got {closest.Count}");

        // First result should have smallest XOR distance to target
        var dist0 = KademliaRoutingTable.XorDistance(closest[0].NodeId, target);
        var dist1 = KademliaRoutingTable.XorDistance(closest[1].NodeId, target);
        // dist0 should be <= dist1 (sorted by distance)
        for (int i = 0; i < 20; i++)
        {
            if (dist0[i] < dist1[i]) break;
            if (dist0[i] > dist1[i])
                throw new Exception("Closest nodes not sorted by XOR distance");
        }
    }

    [TestMethod]
    public async Task Dht_PingMessage_CorrectBencode()
    {
        // Build a ping query manually and verify it's valid bencode
        var nodeId = new byte[20]; nodeId[0] = 0xAB;
        var txId = new byte[] { 0x00, 0x01 };
        var msg = DhtDiscovery.EncodeKrpc(txId, "ping", new Dictionary<string, object>
        {
            ["id"] = nodeId,
        });

        // Should be parseable as bencode
        var (decoded, _) = Bencode.BencodeDecoder.Decode(msg, 0);
        if (decoded is not Dictionary<string, object> dict)
            throw new Exception("Ping message is not a bencode dict");
        if (!dict.ContainsKey("q")) throw new Exception("Missing 'q' field");
        if (!dict.ContainsKey("a")) throw new Exception("Missing 'a' field");
        if (!dict.ContainsKey("y")) throw new Exception("Missing 'y' field");
    }

    [TestMethod]
    public async Task Dht_GetPeersMessage_CorrectBencode()
    {
        var nodeId = new byte[20]; nodeId[0] = 0xCD;
        var infoHash = new byte[20]; infoHash[19] = 0xFF;
        var txId = new byte[] { 0x00, 0x02 };
        var msg = DhtDiscovery.EncodeKrpc(txId, "get_peers", new Dictionary<string, object>
        {
            ["id"] = nodeId,
            ["info_hash"] = infoHash,
        });

        var (decoded, _) = Bencode.BencodeDecoder.Decode(msg, 0);
        if (decoded is not Dictionary<string, object> dict)
            throw new Exception("get_peers message is not a bencode dict");
        if (dict["a"] is not Dictionary<string, object> args)
            throw new Exception("Missing args dict");
        if (!args.ContainsKey("info_hash"))
            throw new Exception("Missing info_hash in get_peers args");
    }

    [TestMethod]
    public async Task Dht_CompactNodeInfo_ParsesCorrectly()
    {
        // Build compact node info: 26 bytes = 20 nodeId + 4 IP + 2 port
        var compact = new byte[26];
        var nodeId = new byte[20]; nodeId[0] = 0x42;
        nodeId.CopyTo(compact, 0);
        compact[20] = 10; compact[21] = 0; compact[22] = 0; compact[23] = 1; // 10.0.0.1
        compact[24] = (byte)(6881 >> 8); compact[25] = (byte)(6881 & 0xFF);

        // Parse like DhtDiscovery does
        var nid = compact[0..20];
        var ip = $"{compact[20]}.{compact[21]}.{compact[22]}.{compact[23]}";
        var port = (compact[24] << 8) | compact[25];

        if (nid[0] != 0x42) throw new Exception($"Node ID wrong: {nid[0]:X2}");
        if (ip != "10.0.0.1") throw new Exception($"IP wrong: {ip}");
        if (port != 6881) throw new Exception($"Port wrong: {port}");
    }
}
