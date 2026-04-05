using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace SpawnDev.WebTorrent;

/// <summary>
/// Distributed Hash Table (DHT) peer discovery — Kademlia-based (BEP 5).
/// Desktop only — requires UDP sockets.
/// Adapted from the original SpawnDev.WebTorrent implementation for _Alt.
///
/// KRPC protocol over UDP with bencode encoding.
/// Queries: ping, find_node, get_peers, announce_peer.
/// Iterative lookup to find nodes close to target info hash.
/// </summary>
public class DhtDiscovery : IAsyncDisposable
{
    private readonly DhtOptions _options;
    public readonly byte[] NodeId;
    internal readonly KademliaRoutingTable _routingTable;
    private UdpClient? _udp;
    private CancellationTokenSource? _cts;
    private int _transactionCounter;

    /// <summary>Fires with "ip:port" string for each peer found via DHT.</summary>
    public event Action<string>? OnPeer;
    public event Action<string>? OnWarning;

    /// <summary>Fires when a BEP 44 mutable item GET response is received.</summary>
    public event Action<Dictionary<string, object>, IPEndPoint>? OnGetResponse;

    public int NodeCount => _routingTable.NodeCount;
    public bool IsReady { get; private set; }

    public DhtDiscovery(DhtOptions? options = null)
    {
        _options = options ?? new DhtOptions();
        NodeId = new byte[20];
        RandomNumberGenerator.Fill(NodeId);
        _routingTable = new KademliaRoutingTable(NodeId);
    }

    public async Task StartAsync(byte[] infoHash, int port = 6881, CancellationToken ct = default)
    {
        if (OperatingSystem.IsBrowser()) return;

        try
        {
            _udp = new UdpClient(_options.Port);
            _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

            _ = ReceiveLoopAsync(_cts.Token);
            await BootstrapAsync(ct);
            await GetPeersAsync(infoHash, ct);

            IsReady = true;
        }
        catch (Exception ex)
        {
            OnWarning?.Invoke($"DHT start failed: {ex.Message}");
        }
    }

    public async Task AnnouncePeerAsync(byte[] infoHash, int port, CancellationToken ct = default)
    {
        if (_udp == null || !IsReady) return;

        var closest = _routingTable.GetClosest(infoHash, 8);
        foreach (var node in closest)
        {
            if (node.Token == null) continue;
            try
            {
                var query = BuildAnnouncePeer(infoHash, port, node.Token);
                await SendKrpcAsync(node.EndPoint, query, ct);
            }
            catch { }
        }
    }

    /// <summary>Send a BEP 44 get request to closest nodes for a target hash.</summary>
    public async Task GetAsync(byte[] target, CancellationToken ct = default)
    {
        if (_udp == null) return;
        var closest = _routingTable.GetClosest(target, 8);
        foreach (var node in closest)
        {
            try
            {
                var query = BuildGet(target);
                await SendKrpcAsync(node.EndPoint, query, ct);
            }
            catch { }
        }
    }

    /// <summary>Send a BEP 44 put request to nodes that have tokens for the target.</summary>
    public async Task PutAsync(byte[] target, Dictionary<string, object> args, CancellationToken ct = default)
    {
        if (_udp == null) return;
        var closest = _routingTable.GetClosest(target, 8);
        foreach (var node in closest)
        {
            if (node.Token == null) continue;
            try
            {
                args["token"] = node.Token;
                var query = EncodeKrpc(NextTxId(), "put", args);
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
                await SendKrpcAsync(ep, BuildFindNode(NodeId), ct);
                await Task.Delay(500, ct);
            }
            catch { }
        }
    }

    private async Task GetPeersAsync(byte[] infoHash, CancellationToken ct)
    {
        var queried = new HashSet<string>();
        var toQuery = _routingTable.GetClosest(infoHash, 8);

        for (int round = 0; round < 3 && toQuery.Count > 0; round++)
        {
            var batch = toQuery.Where(n => queried.Add(n.Id)).Take(3).ToList();
            foreach (var node in batch)
            {
                try
                {
                    await SendKrpcAsync(node.EndPoint, BuildGetPeers(infoHash), ct);
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

            var msgType = msg.TryGetValue("y", out var y) && y is byte[] yb
                ? Encoding.ASCII.GetString(yb) : "";

            switch (msgType)
            {
                case "r": HandleResponse(msg, from); break;
                case "q": HandleQuery(msg, from); break;
            }
        }
        catch { }
    }

    private void HandleResponse(Dictionary<string, object> msg, IPEndPoint from)
    {
        if (!msg.TryGetValue("r", out var rObj) || rObj is not Dictionary<string, object> r) return;

        // Extract node ID and add to routing table
        if (r.TryGetValue("id", out var idObj) && idObj is byte[] nodeId && nodeId.Length == 20)
        {
            var node = new DhtNode { NodeId = nodeId, EndPoint = from, LastSeen = DateTime.UtcNow };
            if (r.TryGetValue("token", out var tokenObj) && tokenObj is byte[] token)
                node.Token = token;
            _routingTable.AddNode(node);
        }

        // Extract peers from get_peers response
        if (r.TryGetValue("values", out var valuesObj) && valuesObj is List<object> values)
        {
            foreach (var v in values)
            {
                if (v is byte[] peerBytes && peerBytes.Length == 6)
                {
                    var ip = $"{peerBytes[0]}.{peerBytes[1]}.{peerBytes[2]}.{peerBytes[3]}";
                    var port = (peerBytes[4] << 8) | peerBytes[5];
                    OnPeer?.Invoke($"{ip}:{port}");
                }
            }
        }

        // Extract compact node info (26 bytes each = 20 ID + 6 addr)
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

        // BEP 44: Forward mutable item GET responses to subscribers
        if (r.ContainsKey("v") || r.ContainsKey("k") || r.ContainsKey("sig"))
            OnGetResponse?.Invoke(r, from);
    }

    private void HandleQuery(Dictionary<string, object> msg, IPEndPoint from)
    {
        if (!msg.TryGetValue("q", out var qObj) || qObj is not byte[] qBytes) return;
        var method = Encoding.ASCII.GetString(qBytes);
        var txId = msg.TryGetValue("t", out var t) && t is byte[] tid ? tid : new byte[] { 0, 0 };

        switch (method)
        {
            case "ping":
                _ = SendKrpcAsync(from, BuildPingResponse(txId), CancellationToken.None);
                break;
            case "find_node":
            case "get_peers":
                _ = SendKrpcAsync(from, BuildNodesResponse(txId, NodeId), CancellationToken.None);
                break;
            case "announce_peer":
                _ = SendKrpcAsync(from, BuildPingResponse(txId), CancellationToken.None);
                break;
        }
    }

    // ── KRPC Message Builders ──

    private byte[] NextTxId()
    {
        var id = Interlocked.Increment(ref _transactionCounter);
        return new[] { (byte)(id >> 8), (byte)id };
    }

    private byte[] BuildFindNode(byte[] target) =>
        EncodeKrpc(NextTxId(), "find_node", new Dictionary<string, object>
        {
            ["id"] = NodeId, ["target"] = target,
        });

    private byte[] BuildGetPeers(byte[] infoHash) =>
        EncodeKrpc(NextTxId(), "get_peers", new Dictionary<string, object>
        {
            ["id"] = NodeId, ["info_hash"] = infoHash,
        });

    private byte[] BuildGet(byte[] target) =>
        EncodeKrpc(NextTxId(), "get", new Dictionary<string, object>
        {
            ["id"] = NodeId, ["target"] = target,
        });

    private byte[] BuildAnnouncePeer(byte[] infoHash, int port, byte[]? token)
    {
        var args = new Dictionary<string, object>
        {
            ["id"] = NodeId, ["implied_port"] = 0,
            ["info_hash"] = infoHash, ["port"] = port,
        };
        if (token != null) args["token"] = token;
        return EncodeKrpc(NextTxId(), "announce_peer", args);
    }

    private byte[] BuildPingResponse(byte[] txId)
    {
        var buf = new List<byte>();
        buf.AddRange(Encoding.ASCII.GetBytes("d1:rd2:id20:"));
        buf.AddRange(NodeId);
        buf.AddRange(Encoding.ASCII.GetBytes($"e1:t{txId.Length}:"));
        buf.AddRange(txId);
        buf.AddRange(Encoding.ASCII.GetBytes("1:y1:re"));
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

        var token = new byte[4];
        RandomNumberGenerator.Fill(token);

        var buf = new List<byte>();
        buf.AddRange(Encoding.ASCII.GetBytes("d1:rd2:id20:"));
        buf.AddRange(NodeId);
        buf.AddRange(Encoding.ASCII.GetBytes($"5:nodes{nodesBytes.Count}:"));
        buf.AddRange(nodesBytes);
        buf.AddRange(Encoding.ASCII.GetBytes($"5:token{token.Length}:"));
        buf.AddRange(token);
        buf.AddRange(Encoding.ASCII.GetBytes($"e1:t{txId.Length}:"));
        buf.AddRange(txId);
        buf.AddRange(Encoding.ASCII.GetBytes("1:y1:re"));
        return buf.ToArray();
    }

    public static byte[] EncodeKrpc(byte[] txId, string method, Dictionary<string, object> args)
    {
        var buf = new List<byte>();
        buf.AddRange(Encoding.ASCII.GetBytes("d1:ad"));
        foreach (var (k, v) in args.OrderBy(x => x.Key, StringComparer.Ordinal))
        {
            buf.AddRange(Encoding.ASCII.GetBytes($"{k.Length}:{k}"));
            if (v is byte[] b)
            {
                buf.AddRange(Encoding.ASCII.GetBytes($"{b.Length}:"));
                buf.AddRange(b);
            }
            else if (v is int i) buf.AddRange(Encoding.ASCII.GetBytes($"i{i}e"));
            else if (v is long l) buf.AddRange(Encoding.ASCII.GetBytes($"i{l}e"));
        }
        buf.AddRange(Encoding.ASCII.GetBytes($"e1:q{method.Length}:{method}1:t{txId.Length}:"));
        buf.AddRange(txId);
        buf.AddRange(Encoding.ASCII.GetBytes("1:y1:qe"));
        return buf.ToArray();
    }

    public async Task SendKrpcAsync(IPEndPoint ep, byte[] data, CancellationToken ct)
    {
        if (_udp == null) return;
        await _udp.SendAsync(data, data.Length, ep);
    }

    /// <summary>Create a BEP 46 mutable items handler with the given signer.</summary>
    public DhtMutableItems CreateMutableItems(IDhtSigner signer) => new DhtMutableItems(this, signer);

    public async Task StopAsync()
    {
        _cts?.Cancel();
        _udp?.Close();
        _udp?.Dispose();
        _udp = null;
        IsReady = false;
    }

    public async ValueTask DisposeAsync() => await StopAsync();
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
    private const int K = 8;
    private const int Bits = 160;

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

        if (b.Count < K) b.Add(node);
        else
        {
            var stale = b.FirstOrDefault(n => (DateTime.UtcNow - n.LastSeen).TotalMinutes > 15);
            if (stale != null) { b.Remove(stale); b.Add(node); }
        }
    }

    public List<DhtNode> GetClosest(byte[] target, int count)
    {
        return _buckets.SelectMany(b => b)
            .OrderBy(n => XorDistance(n.NodeId, target), ByteArrayComparer.Instance)
            .Take(count).ToList();
    }

    private class ByteArrayComparer : IComparer<byte[]>
    {
        public static readonly ByteArrayComparer Instance = new();
        public int Compare(byte[]? x, byte[]? y)
        {
            if (x == null || y == null) return 0;
            for (int i = 0; i < Math.Min(x.Length, y.Length); i++)
                if (x[i] != y[i]) return x[i].CompareTo(y[i]);
            return x.Length.CompareTo(y.Length);
        }
    }

    private int GetBucketIndex(byte[] nodeId)
    {
        var distance = new byte[20];
        for (int i = 0; i < 20; i++)
            distance[i] = (byte)(_localId[i] ^ nodeId[i]);

        for (int i = 0; i < 20; i++)
        {
            if (distance[i] == 0) continue;
            for (int bit = 7; bit >= 0; bit--)
                if ((distance[i] & (1 << bit)) != 0)
                    return 159 - (i * 8 + (7 - bit));
        }
        return 0;
    }

    public static byte[] XorDistance(byte[] a, byte[] b)
    {
        var result = new byte[20];
        for (int i = 0; i < 20; i++)
            result[i] = (byte)(a[i] ^ b[i]);
        return result;
    }
}
