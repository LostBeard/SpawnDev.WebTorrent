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

    /// <summary>
    /// BEP 44 incoming "get" query. Handlers return the response bytes to send back
    /// (built via <see cref="BuildGetResponse(byte[], byte[], byte[], long, byte[], byte[])"/>)
    /// or null to fall through to the default empty-nodes+token response.
    /// </summary>
    public event Func<Dictionary<string, object>, IPEndPoint, byte[]?>? OnGetQuery;

    /// <summary>
    /// BEP 44 incoming "put" query. Handlers return the response bytes to send back
    /// (ping-shaped ack for success; error per RFC). Return null to fall through to
    /// the default ping-shaped ack (stateless relay).
    /// </summary>
    public event Func<Dictionary<string, object>, IPEndPoint, byte[]?>? OnPutQuery;

    public int NodeCount => _routingTable.NodeCount;

    // Health counters: how many answers this node has had, how many were KRPC errors, and the last error, so a DHT that is "not finding anything" can be told apart from one that is
    // unreachable (no responses), reachable but talking to nodes that do not implement BEP 44 (errors 204), or not given tokens.
    private long _responsesReceived, _errorsReceived, _tokensReceived;
    public long ResponsesReceived => Interlocked.Read(ref _responsesReceived);
    public long ErrorsReceived => Interlocked.Read(ref _errorsReceived);
    /// <summary>Responses that carried a write token (what a BEP 44 put needs from each node it writes to).</summary>
    public long TokensReceived => Interlocked.Read(ref _tokensReceived);
    public string? LastError { get; private set; }
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
            // Windows: a datagram sent to a port nobody listens on comes back as an ICMP "port unreachable", which the NEXT receive on this socket reports as a connection reset. On the
            // real DHT many nodes are gone, so without this a node goes deaf after a few sends (the receive loop died on the first such error).
            if (OperatingSystem.IsWindows())
            {
                try { _udp.Client.IOControl(unchecked((int)0x9800000C), new byte[] { 0 }, null); } // SIO_UDP_CONNRESET off
                catch (Exception) { /* the receive loop below survives these errors anyway */ }
            }
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

    /// <summary>Nodes asked in parallel per round of <see cref="GetAsync"/>, how many of the closest must have answered before it stops, and the most rounds it runs.</summary>
    private const int LookupAlpha = 8, LookupClosest = 8;
    private static readonly TimeSpan LookupMaxTime = TimeSpan.FromSeconds(15), LookupNodeTimeout = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Walks toward <paramref name="target"/> the way Kademlia does, asking each node it meets a BEP 44 <c>get</c> (so values and write tokens come back through the normal
    /// response path), and returns the nearest nodes that ANSWERED, nearest first: where a put for that target belongs and where a get will find it.
    /// The search keeps its own pool of candidates built from the answers it receives. It cannot rely on the routing table for that: the table is organised around THIS node's id,
    /// its buckets hold 8 nodes each and drop newcomers, so the nodes near an arbitrary target (far from our id) are exactly the ones it refuses to keep. Candidates also have a state
    /// (asked / answered / given up on after a timeout), because on the real DHT many nodes named in answers are gone; it ends when the nearest candidates not given up on have all
    /// answered, or after <see cref="LookupMaxTime"/>. (It used to ask 3 nodes for 3 rounds, which finds an item on a 2-node loopback network but not on the real one.)
    /// </summary>
    public async Task<IReadOnlyList<IPEndPoint>> LookupAsync(byte[] target, CancellationToken ct = default)
    {
        if (_udp == null) return Array.Empty<IPEndPoint>();

        var pool = new System.Collections.Concurrent.ConcurrentDictionary<string, DhtNode>();
        void Learn(byte[] id, IPEndPoint endpoint) => pool.TryAdd(endpoint.ToString(), new DhtNode { NodeId = id, EndPoint = endpoint });
        foreach (var node in _routingTable.GetClosest(target, 96)) Learn(node.NodeId, node.EndPoint);
        NodeLearned += Learn;
        try
        {
            var askedAt = new Dictionary<string, DateTime>();
            var started = DateTime.UtcNow;
            // A node counts as having answered only if it answered THIS get (with a token or a value) after we asked it: one that merely answered the bootstrap find_node, or that
            // answers get with an error (BEP 44 is optional: some nodes reply 204 "unknown method", the bootstrap routers reply 203), is no use for a put and is given up on.
            bool Answered(DhtNode n) => askedAt.TryGetValue(n.EndPoint.ToString(), out var at) && _getAnswers.TryGetValue(n.EndPoint.ToString(), out var answered) && answered >= at;
            while (!ct.IsCancellationRequested && DateTime.UtcNow - started < LookupMaxTime)
            {
                var now = DateTime.UtcNow;
                bool Dead(DhtNode n) => askedAt.TryGetValue(n.EndPoint.ToString(), out var at) && !Answered(n) && ((_errorsFrom.TryGetValue(n.EndPoint.ToString(), out var failed) && failed >= at) || now - at > LookupNodeTimeout);
                var candidates = pool.Values.Where(n => !Dead(n)).OrderBy(n => XorDistanceTo(n.NodeId, target), XorOrder.Instance).ToList();
                var nearest = candidates.Take(LookupClosest).ToList();
                if (WebTorrentClient.VerboseLogging) Console.WriteLine($"[Dht.Lookup] t={(DateTime.UtcNow - started).TotalSeconds:0.0}s pool={pool.Count} live={candidates.Count} asked={askedAt.Count} answered={candidates.Count(Answered)} nearest8answered={nearest.Count(Answered)}/{nearest.Count}");
                if (nearest.Count > 0 && nearest.All(Answered)) break; // the nearest live nodes all answered: nothing closer turned up
                var batch = candidates.Take(LookupClosest * 3).Where(n => !askedAt.ContainsKey(n.EndPoint.ToString())).Take(LookupAlpha).ToList();
                if (batch.Count == 0 && nearest.Count > 0 && nearest.All(n => askedAt.ContainsKey(n.EndPoint.ToString())))
                {
                    await Task.Delay(150, ct); // everything near is asked and we are waiting on answers (or their timeout)
                    continue;
                }
                if (batch.Count == 0) break;
                foreach (var node in batch)
                {
                    askedAt[node.EndPoint.ToString()] = now;
                    var endpoint = node.EndPoint;
                    _ = Task.Run(async () =>
                    {
                        try { await SendKrpcAsync(endpoint, BuildGet(target), ct); }
                        catch { }
                    }, ct);
                }
                await Task.Delay(250, ct); // answers arrive on the receive loop and add closer nodes to the pool
            }
            return pool.Values.Where(Answered).OrderBy(n => XorDistanceTo(n.NodeId, target), XorOrder.Instance).Take(LookupClosest).Select(n => n.EndPoint).ToList();
        }
        finally { NodeLearned -= Learn; }
    }

    /// <summary>A BEP 44 get lookup; see <see cref="LookupAsync"/>.</summary>
    public Task GetAsync(byte[] target, CancellationToken ct = default) => LookupAsync(target, ct);

    /// <summary>Raised for every node named in an answer (and every node that answers), so a lookup can build its own candidate pool.</summary>
    private event Action<byte[], IPEndPoint>? NodeLearned;

    private static byte[] XorDistanceTo(byte[] id, byte[] target) => KademliaRoutingTable.XorDistance(id, target);

    private sealed class XorOrder : IComparer<byte[]>
    {
        public static readonly XorOrder Instance = new();
        public int Compare(byte[]? x, byte[]? y) => x == null || y == null ? 0 : x.AsSpan().SequenceCompareTo(y);
    }

    // Endpoints that have answered one of our queries, with when: a node that has answered is alive and (for a get) holds a write token for us.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTime> _responders = new();

    // When each endpoint last answered with a write token or a value (a get answer), and when it last sent a KRPC error.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTime> _getAnswers = new(), _errorsFrom = new();

    /// <summary>True if the node at <paramref name="endpoint"/> has answered one of our queries (as opposed to merely being named in someone else's answer).</summary>
    public bool HasResponded(IPEndPoint endpoint) => _responders.ContainsKey(endpoint.ToString());

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
                // every IPv4 address of the router, not just the first: a bootstrap host often has several and any one may be down
                foreach (var address in addresses.Where(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork))
                {
                    try { await SendKrpcAsync(new IPEndPoint(address, port), BuildFindNode(NodeId), ct); } catch { }
                }
                await Task.Delay(500, ct);
            }
            catch { }
        }

        // Fill the routing table by walking outward: ask the nodes we have learned about for the nodes nearest our own id, until there are enough to search with.
        // (One find_node to the routers used to leave 2-3 nodes, which is too few to find where anything is stored.)
        var asked = new HashSet<string>();
        var deadline = DateTime.UtcNow.AddSeconds(8);
        while (!ct.IsCancellationRequested && DateTime.UtcNow < deadline && _routingTable.NodeCount < BootstrapTargetNodes)
        {
            var batch = _routingTable.GetClosest(NodeId, 256).Where(n => asked.Add(n.EndPoint.ToString())).Take(8).ToList();
            if (batch.Count == 0) { await Task.Delay(300, ct); if (_routingTable.NodeCount == 0) break; continue; }
            foreach (var node in batch)
            {
                var endpoint = node.EndPoint;
                _ = Task.Run(async () => { try { await SendKrpcAsync(endpoint, BuildFindNode(NodeId), ct); } catch { } }, ct);
            }
            await Task.Delay(400, ct);
        }
    }

    private const int BootstrapTargetNodes = 40;

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
                try
                {
                    var result = await _udp.ReceiveAsync(ct);
                    ProcessMessage(result.Buffer, result.RemoteEndPoint);
                }
                catch (SocketException e) when (e.SocketErrorCode is SocketError.ConnectionReset or SocketError.ConnectionRefused or SocketError.HostUnreachable or SocketError.NetworkUnreachable or SocketError.MessageSize)
                {
                    // an earlier send went to a dead node (ICMP came back): not a reason to stop listening for everyone else
                }
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
                case "r": Interlocked.Increment(ref _responsesReceived); _responders[from.ToString()] = DateTime.UtcNow; HandleResponse(msg, from); break;
                case "q": HandleQuery(msg, from); break;
                case "e":
                    // KRPC error: [code, message]. A node that does not implement a method (BEP 44 get/put are optional) answers 204 "method unknown".
                    Interlocked.Increment(ref _errorsReceived);
                    _errorsFrom[from.ToString()] = DateTime.UtcNow;
                    if (msg.TryGetValue("e", out var eObj) && eObj is List<object> { Count: >= 2 } e && e[1] is byte[] text)
                        LastError = $"{e[0]}: {Encoding.UTF8.GetString(text)}";
                    break;
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
            NodeLearned?.Invoke(nodeId, from);
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
                    var learned = new IPEndPoint(IPAddress.Parse(ip), port);
                    _routingTable.AddNode(new DhtNode
                    {
                        NodeId = nid,
                        EndPoint = learned,
                        LastSeen = DateTime.UtcNow,
                    });
                    NodeLearned?.Invoke(nid, learned);
                }
            }
        }

        // BEP 44: Forward mutable item GET responses to subscribers. Fire on ANY
        // response that carries a token (which identifies it as a get reply, even
        // when the node has no stored value to return) OR that carries mutable
        // fields (v/k/sig). Token-only replies matter because the subsequent PUT
        // needs that token - without firing here, token caching never happens.
        if (r.ContainsKey("token")) Interlocked.Increment(ref _tokensReceived);
        if (r.ContainsKey("token") || r.ContainsKey("v")) _getAnswers[from.ToString()] = DateTime.UtcNow;
        if (r.ContainsKey("token") || r.ContainsKey("v") || r.ContainsKey("k") || r.ContainsKey("sig"))
            OnGetResponse?.Invoke(r, from);
    }

    private void HandleQuery(Dictionary<string, object> msg, IPEndPoint from)
    {
        if (!msg.TryGetValue("q", out var qObj) || qObj is not byte[] qBytes) return;
        var method = Encoding.ASCII.GetString(qBytes);
        var txId = msg.TryGetValue("t", out var t) && t is byte[] tid ? tid : new byte[] { 0, 0 };

        // BEP 5: on receiving any KRPC query, learn the sender's NodeId from the
        // "a" args dict and add the sender to our routing table. Mirrors the
        // response-path logic in HandleResponse - without this, we never learn
        // about peers who initiate contact with us (only peers we query first),
        // which breaks small-swarm / bootstrap scenarios where one side has no
        // prior knowledge of the other.
        if (msg.TryGetValue("a", out var aObj) && aObj is Dictionary<string, object> args
            && args.TryGetValue("id", out var senderIdObj) && senderIdObj is byte[] senderId
            && senderId.Length == 20)
        {
            _routingTable.AddNode(new DhtNode
            {
                NodeId = senderId,
                EndPoint = from,
                LastSeen = DateTime.UtcNow,
            });
        }

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
            case "get":
            {
                // BEP 44 get. Defer to a handler (typically DhtMutableItems) if one is
                // subscribed; otherwise respond with empty-nodes + fresh token so the
                // follow-up put can proceed.
                byte[]? custom = null;
                if (OnGetQuery != null)
                {
                    foreach (Func<Dictionary<string, object>, IPEndPoint, byte[]?> h in OnGetQuery.GetInvocationList())
                    {
                        custom = h(msg, from);
                        if (custom != null) break;
                    }
                }
                var response = custom ?? BuildGetResponse(txId, NodeId, value: null, seq: -1, signature: null, publicKey: null);
                _ = SendKrpcAsync(from, response, CancellationToken.None);
                break;
            }
            case "put":
            {
                byte[]? custom = null;
                if (OnPutQuery != null)
                {
                    foreach (Func<Dictionary<string, object>, IPEndPoint, byte[]?> h in OnPutQuery.GetInvocationList())
                    {
                        custom = h(msg, from);
                        if (custom != null) break;
                    }
                }
                // Default: ping-shaped ack (stateless accept).
                _ = SendKrpcAsync(from, custom ?? BuildPingResponse(txId), CancellationToken.None);
                break;
            }
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

    /// <summary>
    /// Send a KRPC ping to a specific endpoint. The response will populate our
    /// routing table with that node, making it a known peer for future queries.
    /// Useful for manually introducing peers (e.g. LAN discovery, unit tests,
    /// cases where the user has an out-of-band endpoint but no bootstrap node).
    /// </summary>
    public Task PingAsync(IPEndPoint ep, CancellationToken ct = default) =>
        SendKrpcAsync(ep, BuildPing(), ct);

    /// <summary>
    /// Send a KRPC find_node query to a specific endpoint, targeting this node's
    /// own ID. Triggers a response that populates our routing table plus primes
    /// the remote end's routing table with us (via <see cref="HandleQuery"/>'s
    /// BEP 5 learn-from-query logic).
    /// </summary>
    public Task FindNodeAsync(IPEndPoint ep, CancellationToken ct = default) =>
        SendKrpcAsync(ep, BuildFindNode(NodeId), ct);

    private byte[] BuildPing() =>
        EncodeKrpc(NextTxId(), "ping", new Dictionary<string, object> { ["id"] = NodeId });

    /// <summary>
    /// Build a BEP 44 "get" response. When a value is known for the queried target,
    /// include <paramref name="value"/> + <paramref name="publicKey"/> +
    /// <paramref name="seq"/> + <paramref name="signature"/> so the querier can verify
    /// and cache it. When there's nothing stored, pass <c>null</c>/<c>-1</c> for those
    /// and the response carries only the token (+ closest-node hints for Kademlia
    /// iteration). The token is freshly generated for each response - callers that
    /// care about replay prevention must add nonce tracking themselves.
    /// </summary>
    public byte[] BuildGetResponse(byte[] txId, byte[] responderNodeId, byte[]? value,
        long seq, byte[]? signature, byte[]? publicKey)
    {
        var closest = _routingTable.GetClosest(responderNodeId, 8);
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

        // Bencode dict: keys must be sorted. Sorted order inside "r": id, k?, nodes,
        // seq?, sig?, token, v? (alphabetical).
        var buf = new List<byte>();
        buf.AddRange(Encoding.ASCII.GetBytes("d1:rd"));
        buf.AddRange(Encoding.ASCII.GetBytes("2:id20:"));
        buf.AddRange(responderNodeId);
        if (publicKey != null && publicKey.Length > 0)
        {
            buf.AddRange(Encoding.ASCII.GetBytes($"1:k{publicKey.Length}:"));
            buf.AddRange(publicKey);
        }
        buf.AddRange(Encoding.ASCII.GetBytes($"5:nodes{nodesBytes.Count}:"));
        buf.AddRange(nodesBytes);
        if (seq >= 0)
        {
            buf.AddRange(Encoding.ASCII.GetBytes($"3:seqi{seq}e"));
        }
        if (signature != null && signature.Length > 0)
        {
            buf.AddRange(Encoding.ASCII.GetBytes($"3:sig{signature.Length}:"));
            buf.AddRange(signature);
        }
        buf.AddRange(Encoding.ASCII.GetBytes($"5:token{token.Length}:"));
        buf.AddRange(token);
        if (value != null && value.Length > 0)
        {
            buf.AddRange(Encoding.ASCII.GetBytes($"1:v{value.Length}:"));
            buf.AddRange(value);
        }
        buf.AddRange(Encoding.ASCII.GetBytes($"e1:t{txId.Length}:"));
        buf.AddRange(txId);
        buf.AddRange(Encoding.ASCII.GetBytes("1:y1:re"));
        return buf.ToArray();
    }

    /// <summary>Build a generic response with just the responder id + txId (used for put ack).</summary>
    public byte[] BuildPingResponsePublic(byte[] txId) => BuildPingResponse(txId);

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
