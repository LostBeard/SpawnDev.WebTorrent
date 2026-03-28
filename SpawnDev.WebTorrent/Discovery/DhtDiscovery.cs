using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace SpawnDev.WebTorrent.Discovery;

/// <summary>
/// Distributed Hash Table (DHT) peer discovery — Kademlia-based (BEP 5).
/// Desktop only — requires UDP sockets.
///
/// KRPC protocol over UDP with bencode encoding.
/// Queries: ping, find_node, get_peers, announce_peer.
/// Iterative lookup to find nodes close to target info hash.
/// </summary>
public class DhtDiscovery : IDiscovery
{
    private readonly DhtOptions _options;
    internal readonly byte[] _nodeId;
    internal readonly KademliaRoutingTable _routingTable;
    private UdpClient? _udp;
    private CancellationTokenSource? _cts;
    private int _transactionCounter;

    public string Type => "dht";
    public event Action<PeerInfo>? OnPeer;
    public event Action<string>? OnError;

    public int NodeCount => _routingTable.NodeCount;
    public bool IsReady { get; private set; }

    public DhtDiscovery(DhtOptions? options = null)
    {
        _options = options ?? new DhtOptions();
        _nodeId = new byte[20];
        RandomNumberGenerator.Fill(_nodeId);
        _routingTable = new KademliaRoutingTable(_nodeId);
    }

    public async Task StartAsync(byte[] infoHash, int port, CancellationToken ct = default)
    {
        if (!OperatingSystem.IsBrowser())
        {
            try
            {
                _udp = new UdpClient(_options.Port);
                _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

                // Start receive loop
                _ = ReceiveLoopAsync(_cts.Token);

                // Bootstrap by contacting known nodes
                await BootstrapAsync(ct);

                // Lookup peers for the info hash
                await GetPeersAsync(infoHash, ct);

                IsReady = true;
            }
            catch (Exception ex)
            {
                OnError?.Invoke($"DHT start failed: {ex.Message}");
            }
        }
    }

    public async Task AnnounceAsync(byte[] infoHash, int port,
        long uploaded, long downloaded, long left, CancellationToken ct = default)
    {
        if (_udp == null || !IsReady) return;

        // Find closest nodes to info hash, then announce to them
        var closest = _routingTable.GetClosest(infoHash, 8);
        foreach (var node in closest)
        {
            try
            {
                var query = BuildAnnouncePeer(infoHash, port, node.Token);
                await SendKrpcAsync(node.EndPoint, query, ct);
            }
            catch { }
        }
    }

    private async Task BootstrapAsync(CancellationToken ct)
    {
        foreach (var bootstrap in _options.BootstrapNodes)
        {
            try
            {
                var parts = bootstrap.Split(':');
                var host = parts[0];
                var port = parts.Length > 1 ? int.Parse(parts[1]) : 6881;

                var addresses = await Dns.GetHostAddressesAsync(host, ct);
                if (addresses.Length == 0) continue;

                var ep = new IPEndPoint(addresses[0], port);
                var findNode = BuildFindNode(_nodeId);
                await SendKrpcAsync(ep, findNode, ct);

                // Give bootstrap time to respond
                await Task.Delay(500, ct);
            }
            catch { }
        }
    }

    private async Task GetPeersAsync(byte[] infoHash, CancellationToken ct)
    {
        // Iterative lookup: query closest nodes for peers
        var queried = new HashSet<string>();
        var toQuery = _routingTable.GetClosest(infoHash, 8);

        for (int round = 0; round < 3 && toQuery.Count > 0; round++)
        {
            var batch = toQuery.Where(n => queried.Add(n.Id)).Take(3).ToList();
            foreach (var node in batch)
            {
                try
                {
                    var query = BuildGetPeers(infoHash);
                    await SendKrpcAsync(node.EndPoint, query, ct);
                }
                catch { }
            }
            await Task.Delay(300, ct);
            toQuery = _routingTable.GetClosest(infoHash, 8);
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested && _udp != null)
            {
                var result = await _udp.ReceiveAsync(ct);
                ProcessMessage(result.Buffer, result.RemoteEndPoint);
            }
        }
        catch (OperationCanceledException) { }
        catch { }
    }

    private void ProcessMessage(byte[] data, IPEndPoint from)
    {
        try
        {
            var (decoded, _) = Bencode.BencodeDecoder.Decode(data, 0);
            if (decoded is not Dictionary<string, object> msg) return;

            var msgType = msg.TryGetValue("y", out var y) && y is byte[] yb ? Encoding.ASCII.GetString(yb) : "";

            switch (msgType)
            {
                case "r": // Response
                    HandleResponse(msg, from);
                    break;
                case "q": // Query from another node
                    HandleQuery(msg, from);
                    break;
                case "e": // Error
                    break;
            }
        }
        catch { }
    }

    private void HandleResponse(Dictionary<string, object> msg, IPEndPoint from)
    {
        if (!msg.TryGetValue("r", out var rObj) || rObj is not Dictionary<string, object> r) return;

        // Extract node ID
        if (r.TryGetValue("id", out var idObj) && idObj is byte[] nodeId && nodeId.Length == 20)
        {
            var node = new DhtNode { NodeId = nodeId, EndPoint = from, LastSeen = DateTime.UtcNow };

            // Extract token (for later announce_peer)
            if (r.TryGetValue("token", out var tokenObj) && tokenObj is byte[] token)
                node.Token = token;

            _routingTable.AddNode(node);
        }

        // Extract peers (from get_peers response)
        if (r.TryGetValue("values", out var valuesObj) && valuesObj is List<object> values)
        {
            foreach (var v in values)
            {
                if (v is byte[] peerBytes && peerBytes.Length == 6)
                {
                    var ip = $"{peerBytes[0]}.{peerBytes[1]}.{peerBytes[2]}.{peerBytes[3]}";
                    var port = (peerBytes[4] << 8) | peerBytes[5];
                    OnPeer?.Invoke(new PeerInfo { Address = $"{ip}:{port}", Source = "dht" });
                }
            }
        }

        // Extract nodes (compact node info: 26 bytes each = 20 ID + 6 addr)
        if (r.TryGetValue("nodes", out var nodesObj) && nodesObj is byte[] nodesBytes)
        {
            for (int i = 0; i + 26 <= nodesBytes.Length; i += 26)
            {
                var nid = nodesBytes[i..(i + 20)];
                var ip = $"{nodesBytes[i + 20]}.{nodesBytes[i + 21]}.{nodesBytes[i + 22]}.{nodesBytes[i + 23]}";
                var port = (nodesBytes[i + 24] << 8) | nodesBytes[i + 25];

                if (port > 0)
                {
                    _routingTable.AddNode(new DhtNode
                    {
                        NodeId = nid,
                        EndPoint = new IPEndPoint(IPAddress.Parse(ip), port),
                        LastSeen = DateTime.UtcNow,
                    });
                }
            }
        }
    }

    private void HandleQuery(Dictionary<string, object> msg, IPEndPoint from)
    {
        if (!msg.TryGetValue("q", out var qObj) || qObj is not byte[] qBytes) return;
        var method = Encoding.ASCII.GetString(qBytes);
        var txId = msg.TryGetValue("t", out var t) && t is byte[] tid ? tid : new byte[] { 0, 0 };

        switch (method)
        {
            case "ping":
                // Respond with our node ID
                var pong = BuildPingResponse(txId);
                _ = SendKrpcAsync(from, pong, CancellationToken.None);
                break;
            case "find_node":
            case "get_peers":
                // Respond with closest nodes
                var resp = BuildNodesResponse(txId, _nodeId);
                _ = SendKrpcAsync(from, resp, CancellationToken.None);
                break;
        }
    }

    // ── KRPC Message Builders ──

    private byte[] NextTxId()
    {
        var id = Interlocked.Increment(ref _transactionCounter);
        return new[] { (byte)(id >> 8), (byte)id };
    }

    private byte[] BuildFindNode(byte[] target)
    {
        var txId = NextTxId();
        var msg = $"d1:ad2:id20:{Enc(_nodeId)}6:target20:{Enc(target)}e1:q9:find_node1:t2:{Enc(txId)}1:y1:qe";
        return EncodeKrpc(txId, "find_node", new Dictionary<string, byte[]>
        {
            ["id"] = _nodeId,
            ["target"] = target,
        });
    }

    private byte[] BuildGetPeers(byte[] infoHash)
    {
        return EncodeKrpc(NextTxId(), "get_peers", new Dictionary<string, byte[]>
        {
            ["id"] = _nodeId,
            ["info_hash"] = infoHash,
        });
    }

    private byte[] BuildAnnouncePeer(byte[] infoHash, int port, byte[]? token)
    {
        var args = new Dictionary<string, byte[]>
        {
            ["id"] = _nodeId,
            ["info_hash"] = infoHash,
            ["port"] = BitConverter.GetBytes((ushort)port).Reverse().ToArray(),
        };
        if (token != null) args["token"] = token;
        return EncodeKrpc(NextTxId(), "announce_peer", args);
    }

    private byte[] BuildPingResponse(byte[] txId)
    {
        var dict = "d1:rd2:id20:";
        var suffix = "e1:t2:";
        var end = "1:y1:re";
        var buf = new List<byte>();
        buf.AddRange(Encoding.ASCII.GetBytes(dict));
        buf.AddRange(_nodeId);
        buf.AddRange(Encoding.ASCII.GetBytes(suffix));
        buf.AddRange(txId);
        buf.AddRange(Encoding.ASCII.GetBytes(end));
        return buf.ToArray();
    }

    private byte[] BuildNodesResponse(byte[] txId, byte[] target)
    {
        var closest = _routingTable.GetClosest(target, 8);
        var nodesBytes = new List<byte>();
        foreach (var n in closest)
        {
            nodesBytes.AddRange(n.NodeId);
            var addr = n.EndPoint.Address.GetAddressBytes();
            nodesBytes.AddRange(addr.Length == 4 ? addr : new byte[4]);
            nodesBytes.Add((byte)(n.EndPoint.Port >> 8));
            nodesBytes.Add((byte)(n.EndPoint.Port & 0xFF));
        }

        var buf = new List<byte>();
        buf.AddRange(Encoding.ASCII.GetBytes("d1:rd2:id20:"));
        buf.AddRange(_nodeId);
        buf.AddRange(Encoding.ASCII.GetBytes($"5:nodes{nodesBytes.Count}:"));
        buf.AddRange(nodesBytes);
        buf.AddRange(Encoding.ASCII.GetBytes("e1:t2:"));
        buf.AddRange(txId);
        buf.AddRange(Encoding.ASCII.GetBytes("1:y1:re"));
        return buf.ToArray();
    }

    private static byte[] EncodeKrpc(byte[] txId, string method, Dictionary<string, byte[]> args)
    {
        var buf = new List<byte>();
        buf.AddRange(Encoding.ASCII.GetBytes("d1:ad"));
        foreach (var (k, v) in args.OrderBy(x => x.Key, StringComparer.Ordinal))
        {
            buf.AddRange(Encoding.ASCII.GetBytes($"{k.Length}:{k}{v.Length}:"));
            buf.AddRange(v);
        }
        buf.AddRange(Encoding.ASCII.GetBytes($"e1:q{method.Length}:{method}1:t2:"));
        buf.AddRange(txId);
        buf.AddRange(Encoding.ASCII.GetBytes("1:y1:qe"));
        return buf.ToArray();
    }

    private static string Enc(byte[] b) => Encoding.Latin1.GetString(b);

    internal async Task SendKrpcAsync(IPEndPoint ep, byte[] data, CancellationToken ct)
    {
        if (_udp == null) return;
        await _udp.SendAsync(data, data.Length, ep);
    }

    /// <summary>
    /// Create a BEP 46 mutable items handler with a new identity.
    /// Enables publishing and subscribing to mutable data in the DHT.
    /// </summary>
    public DhtMutableItems CreateMutableItems() => new(this);

    /// <summary>
    /// Create a BEP 46 mutable items handler with an existing key pair.
    /// </summary>
    public DhtMutableItems CreateMutableItems(byte[] privateKey, byte[] publicKey) => new(this, privateKey, publicKey);

    public async Task StopAsync()
    {
        _cts?.Cancel();
        _udp?.Close();
        _udp?.Dispose();
        _udp = null;
        IsReady = false;
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
    }
}

/// <summary>DHT configuration.</summary>
public class DhtOptions
{
    public string[] BootstrapNodes { get; set; } = new[]
    {
        "router.bittorrent.com:6881",
        "dht.transmissionbt.com:6881",
        "router.utorrent.com:6881",
    };
    public int Port { get; set; } = 6881;
    public int MaxNodes { get; set; } = 1600;
}

/// <summary>A node in the DHT routing table.</summary>
public class DhtNode
{
    public byte[] NodeId { get; set; } = Array.Empty<byte>();
    public IPEndPoint EndPoint { get; set; } = new(IPAddress.Any, 0);
    public DateTime LastSeen { get; set; }
    public byte[]? Token { get; set; }

    public string Id => Convert.ToHexString(NodeId).ToLowerInvariant();
}

/// <summary>
/// Kademlia routing table — 160 k-buckets based on XOR distance.
/// Each bucket holds up to K=8 nodes.
/// </summary>
public class KademliaRoutingTable
{
    private readonly byte[] _localId;
    private readonly List<DhtNode>[] _buckets;
    private const int K = 8; // nodes per bucket
    private const int Bits = 160; // SHA-1 hash size

    public int NodeCount => _buckets.Sum(b => b.Count);

    public KademliaRoutingTable(byte[] localId)
    {
        _localId = localId;
        _buckets = new List<DhtNode>[Bits];
        for (int i = 0; i < Bits; i++)
            _buckets[i] = new List<DhtNode>();
    }

    public void AddNode(DhtNode node)
    {
        if (node.NodeId.SequenceEqual(_localId)) return;

        int bucket = GetBucketIndex(node.NodeId);
        if (bucket < 0 || bucket >= Bits) return;

        var b = _buckets[bucket];
        var existing = b.FirstOrDefault(n => n.NodeId.SequenceEqual(node.NodeId));
        if (existing != null)
        {
            existing.LastSeen = node.LastSeen;
            existing.Token = node.Token ?? existing.Token;
            return;
        }

        if (b.Count < K)
            b.Add(node);
        else
        {
            // Evict oldest if stale (>15 min)
            var stale = b.FirstOrDefault(n => (DateTime.UtcNow - n.LastSeen).TotalMinutes > 15);
            if (stale != null)
            {
                b.Remove(stale);
                b.Add(node);
            }
        }
    }

    public List<DhtNode> GetClosest(byte[] target, int count)
    {
        return _buckets.SelectMany(b => b)
            .OrderBy(n => XorDistance(n.NodeId, target), ByteArrayComparer.Instance)
            .Take(count)
            .ToList();
    }

    private class ByteArrayComparer : IComparer<byte[]>
    {
        public static readonly ByteArrayComparer Instance = new();
        public int Compare(byte[]? x, byte[]? y)
        {
            if (x == null || y == null) return 0;
            for (int i = 0; i < Math.Min(x.Length, y.Length); i++)
            {
                if (x[i] != y[i]) return x[i].CompareTo(y[i]);
            }
            return x.Length.CompareTo(y.Length);
        }
    }

    private int GetBucketIndex(byte[] nodeId)
    {
        var distance = new byte[20];
        for (int i = 0; i < 20; i++)
            distance[i] = (byte)(_localId[i] ^ nodeId[i]);

        // Find the first set bit (log2 of distance)
        for (int i = 0; i < 20; i++)
        {
            if (distance[i] == 0) continue;
            for (int bit = 7; bit >= 0; bit--)
            {
                if ((distance[i] & (1 << bit)) != 0)
                    return 159 - (i * 8 + (7 - bit));
            }
        }
        return 0;
    }

    private static byte[] XorDistance(byte[] a, byte[] b)
    {
        var result = new byte[20];
        for (int i = 0; i < 20; i++)
            result[i] = (byte)(a[i] ^ b[i]);
        return result;
    }
}
