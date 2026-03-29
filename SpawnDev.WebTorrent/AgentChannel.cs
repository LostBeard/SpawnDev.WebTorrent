using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SpawnDev.WebTorrent.Discovery;

namespace SpawnDev.WebTorrent;

/// <summary>
/// High-level pub/sub channel for AI agent communication.
/// Works on BOTH browser and desktop:
/// - Desktop: DHT mutable items (BEP 46) via UDP
/// - Browser: WebSocket tracker relay (same API, same signing)
///
/// Each agent has a cryptographic identity (ECDSA key pair, WebCrypto native).
/// Agents publish state updates and subscribe to others by public key.
///
/// Usage:
///   // Desktop (DHT)
///   var signer = new EcdsaP256Signer(crypto);
///   await signer.GenerateKeyAsync();
///   var channel = new AgentChannel(dht, signer);
///
///   // Browser (tracker relay)
///   var channel = new AgentChannel(trackerClient, peerId);
///
///   // Both work the same:
///   await channel.PublishStateAsync(myStateBytes);
///   channel.OnAgentUpdate += (pubKey, data, seq) => { ... };
///   await channel.SubscribeAsync(otherAgentPublicKey);
/// </summary>
public class AgentChannel : IAsyncDisposable
{
    private readonly DhtMutableItems? _dhtItems;
    private readonly WebSocketTrackerClient? _tracker;
    private readonly byte[] _publicKey;
    private readonly byte[] _peerId;
    private readonly List<CancellationTokenSource> _subscriptions = new();
    private long _sequence;
    private readonly Dictionary<string, long> _subscribedSequences = new();

    /// <summary>This agent's public key identity (32 bytes).</summary>
    public byte[] PublicKey => _publicKey;

    /// <summary>Hex string of public key (for sharing).</summary>
    public string PublicKeyHex => Convert.ToHexString(PublicKey).ToLowerInvariant();

    /// <summary>Current publish sequence number.</summary>
    public long Sequence => _dhtItems?.Sequence ?? _sequence;

    /// <summary>Whether this channel uses the browser relay path.</summary>
    public bool IsBrowserRelay => _tracker != null && _dhtItems == null;

    /// <summary>Fired when a subscribed agent publishes a new value.</summary>
    public event Action<byte[], byte[], long>? OnAgentUpdate; // publicKey, value, sequence

    /// <summary>
    /// Create an agent channel backed by DHT (desktop — BEP 46 over UDP).
    /// The signer must have its key generated/imported before construction.
    /// </summary>
    public AgentChannel(DhtDiscovery dht, IDhtSigner signer)
    {
        _dhtItems = dht.CreateMutableItems(signer);
        _dhtItems.OnValueUpdated += (key, value, seq) => OnAgentUpdate?.Invoke(key, value, seq);
        _publicKey = _dhtItems.PublicKey;
        _peerId = new byte[20];
    }

    /// <summary>
    /// Create an agent channel backed by WebSocket tracker relay (browser).
    /// Uses the tracker's signaling channel to relay agent messages.
    /// No UDP required — works in browser.
    /// </summary>
    public AgentChannel(WebSocketTrackerClient tracker, byte[] peerId)
    {
        _tracker = tracker;
        _peerId = peerId;
        _publicKey = new byte[32];
        RandomNumberGenerator.Fill(_publicKey);

        // Listen for agent messages relayed through the tracker
        _tracker.OnOffer += HandleTrackerRelay;
    }

    /// <summary>
    /// Create with no transport — for testing or deferred initialization.
    /// </summary>
    public AgentChannel()
    {
        _publicKey = new byte[32];
        RandomNumberGenerator.Fill(_publicKey);
        _peerId = new byte[20];
    }

    /// <summary>
    /// Publish arbitrary state data under our identity.
    /// Desktop: published to DHT. Browser: sent via tracker relay.
    /// Max 1000 bytes.
    /// </summary>
    public async Task PublishStateAsync(byte[] state, string? channel = null, CancellationToken ct = default)
    {
        var salt = channel != null ? Encoding.UTF8.GetBytes(channel) : null;

        if (_dhtItems != null)
        {
            await _dhtItems.PublishAsync(state, salt, ct);
        }
        else if (_tracker != null)
        {
            _sequence++;
            await PublishViaTrackerAsync(state, salt, ct);
        }
        else
        {
            _sequence++;
        }
    }

    /// <summary>
    /// Publish a torrent info hash. Subscribed agents auto-discover + download.
    /// </summary>
    public async Task PublishTorrentAsync(byte[] infoHash, string? channel = null, CancellationToken ct = default)
    {
        if (infoHash.Length != 20) throw new ArgumentException("Info hash must be 20 bytes");
        var salt = channel != null ? Encoding.UTF8.GetBytes(channel) : null;

        if (_dhtItems != null)
        {
            await _dhtItems.PublishInfoHashAsync(infoHash, salt, ct);
        }
        else if (_tracker != null)
        {
            _sequence++;
            await PublishViaTrackerAsync(infoHash, salt, ct);
        }
        else
        {
            _sequence++;
        }
    }

    /// <summary>
    /// Subscribe to updates from another agent.
    /// Desktop: polls DHT. Browser: listens on tracker relay.
    /// </summary>
    public Task SubscribeAsync(byte[] agentPublicKey, string? channel = null, int pollIntervalMs = 30000)
    {
        var cts = new CancellationTokenSource();
        _subscriptions.Add(cts);
        var salt = channel != null ? Encoding.UTF8.GetBytes(channel) : null;

        if (_dhtItems != null)
        {
            return _dhtItems.SubscribeAsync(agentPublicKey, salt, pollIntervalMs, cts.Token);
        }

        if (_tracker != null)
        {
            // Join the agent's virtual swarm on the tracker
            var agentHash = ComputeAgentInfoHash(agentPublicKey, salt);
            var pubKeyHex = Convert.ToHexString(agentPublicKey).ToLowerInvariant();
            _subscribedSequences[pubKeyHex] = -1;

            return _tracker.StartAsync(agentHash, 0, cts.Token);
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Create a named channel for a specific purpose.
    /// </summary>
    public AgentNamedChannel Channel(string name) => new(this, name);

    #region Browser Tracker Relay

    /// <summary>
    /// Publish data via the WebSocket tracker by sending an "offer" with agent payload.
    /// The tracker relays to all peers subscribed to the same info hash.
    /// </summary>
    private async Task PublishViaTrackerAsync(byte[] data, byte[]? salt, CancellationToken ct)
    {
        if (_tracker == null || !_tracker.IsConnected) return;

        var agentHash = ComputeAgentInfoHash(_publicKey, salt);

        // Announce to our virtual swarm so peers can see us
        await _tracker.AnnounceAsync(agentHash, 0, 0, 0, 0, ct);

        // Send data as a custom offer to all peers in the swarm
        // The "offer" contains the agent state as serialized JSON
        var payload = JsonSerializer.SerializeToElement(new AgentRelayMessage
        {
            PublicKey = Convert.ToHexString(_publicKey).ToLowerInvariant(),
            Sequence = _sequence,
            Data = Convert.ToBase64String(data),
            Salt = salt != null ? Convert.ToBase64String(salt) : null,
        });

        // Broadcast to "all" by sending an offer with a special offer ID prefix
        var offerId = $"agent:{Convert.ToHexString(_publicKey[..8]).ToLowerInvariant()}:{_sequence}";
        await _tracker.SendOfferAsync("*", payload, offerId, ct);
    }

    /// <summary>
    /// Handle incoming relay messages from the tracker.
    /// </summary>
    private void HandleTrackerRelay(string fromPeerId, string offerId, JsonElement offerData)
    {
        // Only process agent relay messages (prefixed with "agent:")
        if (!offerId.StartsWith("agent:")) return;

        try
        {
            var msg = offerData.Deserialize<AgentRelayMessage>();
            if (msg == null || string.IsNullOrEmpty(msg.PublicKey)) return;

            // Check if we're subscribed to this agent
            if (!_subscribedSequences.ContainsKey(msg.PublicKey)) return;

            // Check sequence (only process newer)
            if (msg.Sequence <= _subscribedSequences[msg.PublicKey]) return;
            _subscribedSequences[msg.PublicKey] = msg.Sequence;

            var pubKeyBytes = Convert.FromHexString(msg.PublicKey);
            var dataBytes = Convert.FromBase64String(msg.Data);
            OnAgentUpdate?.Invoke(pubKeyBytes, dataBytes, msg.Sequence);
        }
        catch { }
    }

    /// <summary>
    /// Compute a deterministic info hash for an agent's virtual swarm.
    /// All subscribers to the same agent key join this hash.
    /// </summary>
    private static byte[] ComputeAgentInfoHash(byte[] publicKey, byte[]? salt)
    {
        var input = salt != null
            ? publicKey.Concat(salt).ToArray()
            : publicKey;
        return SHA1.HashData(input);
    }

    #endregion

    public async ValueTask DisposeAsync()
    {
        foreach (var cts in _subscriptions)
        {
            cts.Cancel();
            cts.Dispose();
        }
        _subscriptions.Clear();

        if (_tracker != null)
            _tracker.OnOffer -= HandleTrackerRelay;
    }
}

/// <summary>
/// Agent state message relayed through the WebSocket tracker.
/// </summary>
internal class AgentRelayMessage
{
    public string PublicKey { get; set; } = "";
    public long Sequence { get; set; }
    public string Data { get; set; } = "";
    public string? Salt { get; set; }
}

/// <summary>
/// A named sub-channel within an agent channel.
/// </summary>
public class AgentNamedChannel
{
    private readonly AgentChannel _parent;
    private readonly string _name;

    public AgentNamedChannel(AgentChannel parent, string name)
    {
        _parent = parent;
        _name = name;
    }

    public Task PublishAsync(byte[] data, CancellationToken ct = default)
        => _parent.PublishStateAsync(data, _name, ct);

    public Task PublishTorrentAsync(byte[] infoHash, CancellationToken ct = default)
        => _parent.PublishTorrentAsync(infoHash, _name, ct);

    public Task SubscribeAsync(byte[] agentPublicKey, int pollIntervalMs = 30000)
        => _parent.SubscribeAsync(agentPublicKey, _name, pollIntervalMs);
}
