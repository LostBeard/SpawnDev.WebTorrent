using SpawnDev.BlazorJS.Cryptography;
using SpawnDev.WebTorrent.Discovery;

namespace SpawnDev.WebTorrent;

/// <summary>
/// High-level pub/sub channel for AI agent communication over the BitTorrent DHT.
/// Built on BEP 46 (DHT Mutable Items) with WebCrypto-native ECDSA signing.
///
/// Each agent has a cryptographic identity (ECDSA key pair). Agents publish
/// state updates (model weights, KV cache, coordination messages) to the DHT.
/// Other agents subscribe by public key and receive updates automatically.
///
/// Usage:
///   var channel = new AgentChannel(dht, crypto);
///   await channel.InitAsync();
///
///   // Publish state
///   await channel.PublishStateAsync(myStateBytes);
///
///   // Subscribe to another agent's updates
///   channel.OnAgentUpdate += (pubKey, data, seq) => { ... };
///   await channel.SubscribeAsync(otherAgentPublicKey);
///
///   // Publish a torrent info hash (other agents auto-discover + download)
///   await channel.PublishTorrentAsync(modelInfoHash);
/// </summary>
public class AgentChannel : IAsyncDisposable
{
    private readonly DhtMutableItems _items;
    private readonly List<CancellationTokenSource> _subscriptions = new();

    /// <summary>This agent's public key identity (32 bytes).</summary>
    public byte[] PublicKey => _items.PublicKey;

    /// <summary>Hex string of public key (for sharing).</summary>
    public string PublicKeyHex => Convert.ToHexString(PublicKey).ToLowerInvariant();

    /// <summary>Current publish sequence number.</summary>
    public long Sequence => _items.Sequence;

    /// <summary>Fired when a subscribed agent publishes a new value.</summary>
    public event Action<byte[], byte[], long>? OnAgentUpdate; // publicKey, value, sequence

    /// <summary>
    /// Create an agent channel. Call InitAsync() before using.
    /// </summary>
    public AgentChannel(DhtDiscovery dht, IPortableCrypto? crypto = null)
    {
        _items = dht.CreateMutableItems();
        _items.OnValueUpdated += (key, value, seq) => OnAgentUpdate?.Invoke(key, value, seq);

        if (crypto != null)
            _ = _items.InitCryptoAsync(crypto);
    }

    /// <summary>
    /// Initialize with real cryptographic signing.
    /// </summary>
    public async Task InitAsync(IPortableCrypto crypto)
    {
        await _items.InitCryptoAsync(crypto);
    }

    /// <summary>
    /// Publish arbitrary state data to the DHT under our identity.
    /// Other agents subscribed to our public key will receive this.
    /// Max 1000 bytes per BEP 44.
    /// </summary>
    public Task PublishStateAsync(byte[] state, string? channel = null, CancellationToken ct = default)
    {
        var salt = channel != null ? System.Text.Encoding.UTF8.GetBytes(channel) : null;
        return _items.PublishAsync(state, salt, ct);
    }

    /// <summary>
    /// Publish a torrent info hash. Subscribed agents can auto-discover
    /// and download the torrent (e.g., updated model weights).
    /// </summary>
    public Task PublishTorrentAsync(byte[] infoHash, string? channel = null, CancellationToken ct = default)
    {
        var salt = channel != null ? System.Text.Encoding.UTF8.GetBytes(channel) : null;
        return _items.PublishInfoHashAsync(infoHash, salt, ct);
    }

    /// <summary>
    /// Subscribe to updates from another agent.
    /// Polls the DHT periodically and fires OnAgentUpdate.
    /// </summary>
    /// <param name="agentPublicKey">The agent's public key (32 bytes).</param>
    /// <param name="channel">Optional channel name for scoped subscriptions.</param>
    /// <param name="pollIntervalMs">How often to check for updates (default 30s).</param>
    public Task SubscribeAsync(byte[] agentPublicKey, string? channel = null, int pollIntervalMs = 30000)
    {
        var cts = new CancellationTokenSource();
        _subscriptions.Add(cts);
        var salt = channel != null ? System.Text.Encoding.UTF8.GetBytes(channel) : null;
        return _items.SubscribeAsync(agentPublicKey, salt, pollIntervalMs, cts.Token);
    }

    /// <summary>
    /// Create a named channel for a specific purpose (e.g., "kv-cache", "gradients", "coordination").
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
/// Scopes publish/subscribe to a specific topic (e.g., "weights", "cache", "control").
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
