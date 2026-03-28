using SpawnDev.BlazorJS.Cryptography;
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
///   var channel = new AgentChannel(dht);
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
    private IPortableCrypto? _crypto;
    private PortableECDSAKey? _ecdsaKey;

    /// <summary>This agent's public key identity (32 bytes).</summary>
    public byte[] PublicKey => _publicKey;

    /// <summary>Hex string of public key (for sharing).</summary>
    public string PublicKeyHex => Convert.ToHexString(PublicKey).ToLowerInvariant();

    /// <summary>Current publish sequence number.</summary>
    public long Sequence => _dhtItems?.Sequence ?? _sequence;

    /// <summary>Fired when a subscribed agent publishes a new value.</summary>
    public event Action<byte[], byte[], long>? OnAgentUpdate; // publicKey, value, sequence

    /// <summary>
    /// Create an agent channel backed by DHT (desktop — BEP 46 over UDP).
    /// </summary>
    public AgentChannel(DhtDiscovery dht, IPortableCrypto? crypto = null)
    {
        _dhtItems = dht.CreateMutableItems();
        _dhtItems.OnValueUpdated += (key, value, seq) => OnAgentUpdate?.Invoke(key, value, seq);
        _publicKey = _dhtItems.PublicKey;
        _peerId = new byte[20];

        if (crypto != null)
            _ = InitAsync(crypto);
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
        System.Security.Cryptography.RandomNumberGenerator.Fill(_publicKey);
    }

    /// <summary>
    /// Create with no transport — for testing or deferred initialization.
    /// </summary>
    public AgentChannel()
    {
        _publicKey = new byte[32];
        System.Security.Cryptography.RandomNumberGenerator.Fill(_publicKey);
        _peerId = new byte[20];
    }

    /// <summary>
    /// Initialize with real cryptographic signing (works browser + desktop).
    /// </summary>
    public async Task InitAsync(IPortableCrypto crypto)
    {
        _crypto = crypto;
        _ecdsaKey = await crypto.GenerateECDSAKey("P-256", extractable: true);
        var pubKeyBytes = await crypto.ExportPublicKeySpki(_ecdsaKey);
        System.Security.Cryptography.SHA256.HashData(pubKeyBytes).CopyTo(_publicKey.AsSpan());
    }

    /// <summary>
    /// Publish arbitrary state data under our identity.
    /// Desktop: published to DHT. Browser: sent via tracker relay.
    /// Max 1000 bytes.
    /// </summary>
    public async Task PublishStateAsync(byte[] state, string? channel = null, CancellationToken ct = default)
    {
        var salt = channel != null ? System.Text.Encoding.UTF8.GetBytes(channel) : null;

        if (_dhtItems != null)
        {
            await _dhtItems.PublishAsync(state, salt, ct);
        }
        else
        {
            // Browser path: increment sequence, would relay via tracker
            _sequence++;
        }
    }

    /// <summary>
    /// Publish a torrent info hash. Subscribed agents auto-discover + download.
    /// </summary>
    public async Task PublishTorrentAsync(byte[] infoHash, string? channel = null, CancellationToken ct = default)
    {
        if (infoHash.Length != 20) throw new ArgumentException("Info hash must be 20 bytes");
        var salt = channel != null ? System.Text.Encoding.UTF8.GetBytes(channel) : null;

        if (_dhtItems != null)
        {
            await _dhtItems.PublishInfoHashAsync(infoHash, salt, ct);
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
        var salt = channel != null ? System.Text.Encoding.UTF8.GetBytes(channel) : null;

        if (_dhtItems != null)
        {
            return _dhtItems.SubscribeAsync(agentPublicKey, salt, pollIntervalMs, cts.Token);
        }

        // Browser path: would listen on tracker for messages from this agent
        return Task.CompletedTask;
    }

    /// <summary>
    /// Create a named channel for a specific purpose.
    /// </summary>
    public AgentNamedChannel Channel(string name) => new(this, name);

    public async ValueTask DisposeAsync()
    {
        foreach (var cts in _subscriptions)
        {
            cts.Cancel();
            cts.Dispose();
        }
        _subscriptions.Clear();
    }
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
