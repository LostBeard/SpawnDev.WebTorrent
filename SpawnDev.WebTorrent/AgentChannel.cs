using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SpawnDev.WebTorrent;

/// <summary>
/// High-level pub/sub channel for AI agent communication.
/// Adapted from the original SpawnDev.WebTorrent implementation for _Alt.
///
/// Works on both browser and desktop:
/// - Desktop: DHT mutable items (BEP 46) via UDP
/// - Browser: WebSocket tracker relay (same API, same signing)
///
/// Each agent has a cryptographic identity (Ed25519 key pair).
/// Agents publish state updates and subscribe to others by public key.
/// </summary>
public class AgentChannel : IAsyncDisposable
{
    private readonly DhtMutableItems? _dhtItems;
    private readonly IDhtSigner? _signer;
    private readonly byte[] _publicKey;
    private readonly List<CancellationTokenSource> _subscriptions = new();
    private long _sequence;
    private readonly ConcurrentDictionary<string, long> _subscribedSequences = new();

    /// <summary>This agent's public key identity.</summary>
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
    public AgentChannel(DhtDiscovery dht, IDhtSigner signer)
    {
        _signer = signer;
        _dhtItems = dht.CreateMutableItems(signer);
        _dhtItems.OnValueUpdated += (key, value, seq) => OnAgentUpdate?.Invoke(key, value, seq);
        _publicKey = _dhtItems.PublicKey;
    }

    /// <summary>
    /// Create with no transport — for testing, deferred init, or browser relay (to be wired later).
    /// </summary>
    public AgentChannel(IDhtSigner? signer = null)
    {
        _signer = signer;
        _publicKey = signer?.PublicKey ?? new byte[32];
        if (_publicKey.Length == 0 || _publicKey.All(b => b == 0))
        {
            _publicKey = new byte[32];
            RandomNumberGenerator.Fill(_publicKey);
        }
    }

    /// <summary>Publish arbitrary state data under our identity.</summary>
    public async Task PublishStateAsync(byte[] state, string? channel = null, CancellationToken ct = default)
    {
        var salt = channel != null ? Encoding.UTF8.GetBytes(channel) : null;

        if (_dhtItems != null)
        {
            await _dhtItems.PublishAsync(state, salt, ct);
        }
        else
        {
            _sequence++;
        }
    }

    /// <summary>Publish a torrent info hash. Subscribed agents auto-discover + download.</summary>
    public async Task PublishTorrentAsync(byte[] infoHash, string? channel = null, CancellationToken ct = default)
    {
        if (infoHash.Length != 20) throw new ArgumentException("Info hash must be 20 bytes");
        var salt = channel != null ? Encoding.UTF8.GetBytes(channel) : null;

        if (_dhtItems != null)
        {
            await _dhtItems.PublishInfoHashAsync(infoHash, salt, ct);
        }
        else
        {
            _sequence++;
        }
    }

    /// <summary>Subscribe to updates from another agent.</summary>
    public Task SubscribeAsync(byte[] agentPublicKey, string? channel = null, int pollIntervalMs = 30000)
    {
        var cts = new CancellationTokenSource();
        _subscriptions.Add(cts);
        var salt = channel != null ? Encoding.UTF8.GetBytes(channel) : null;

        if (_dhtItems != null)
        {
            return _dhtItems.SubscribeAsync(agentPublicKey, salt, pollIntervalMs, cts.Token);
        }

        return Task.CompletedTask;
    }

    /// <summary>Create a named sub-channel for a specific purpose.</summary>
    public AgentNamedChannel Channel(string name) => new(this, name);

    /// <summary>
    /// Verify a received agent message signature.
    /// </summary>
    public async Task<bool> VerifyMessageAsync(byte[] publicKey, byte[] data, byte[] signature)
    {
        if (_signer == null) return false;
        return await _signer.VerifyAsync(publicKey, data, signature);
    }

    /// <summary>
    /// Compute a deterministic info hash for an agent's virtual swarm.
    /// </summary>
    public static byte[] ComputeAgentInfoHash(byte[] publicKey, byte[]? salt)
    {
        var input = salt != null ? publicKey.Concat(salt).ToArray() : publicKey;
        return SHA1.HashData(input);
    }

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

/// <summary>Agent state message format for relay/serialization.</summary>
public class AgentRelayMessage
{
    public string PublicKey { get; set; } = "";
    public long Sequence { get; set; }
    public string Data { get; set; } = "";
    public string? Salt { get; set; }
    public string? Signature { get; set; }
}

/// <summary>A named sub-channel within an agent channel.</summary>
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
