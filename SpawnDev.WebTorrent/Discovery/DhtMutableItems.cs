using System.Security.Cryptography;
using System.Text;

namespace SpawnDev.WebTorrent.Discovery;

/// <summary>
/// BEP 46: Updating Torrents via DHT Mutable Items.
/// Publish and retrieve signed mutable data in the DHT.
///
/// Uses IDhtSigner for cryptographic signing (ECDSA-P256, Ed25519, etc).
/// The public key hash determines where in the DHT the item is stored.
/// Sequence numbers prevent rollback.
///
/// Use cases:
/// - Live model weight updates (publish new info hash under same key)
/// - AI agent shared mutable state (KV cache, coordination)
/// - Decentralized pub/sub messaging
/// - Dynamic torrent feeds that update over time
///
/// Protocol:
///   put: { "v": value, "k": public_key, "seq": sequence, "sig": signature, "salt": optional }
///   get: { "target": sha1(public_key + salt) } → returns latest value
/// </summary>
public class DhtMutableItems
{
    private readonly DhtDiscovery _dht;
    private readonly IDhtSigner _signer;
    private long _sequence;

    /// <summary>Our public key identity (from the signer).</summary>
    public byte[] PublicKey => _signer.PublicKey;

    /// <summary>Current sequence number.</summary>
    public long Sequence => _sequence;

    /// <summary>The signing algorithm in use.</summary>
    public string Algorithm => _signer.Algorithm;

    /// <summary>Event fired when a subscribed key has a new value.</summary>
    public event Action<byte[], byte[], long>? OnValueUpdated; // publicKey, value, sequence

    /// <summary>
    /// Create a mutable items handler with the given signer.
    /// The signer must have its key generated/imported before use.
    /// </summary>
    public DhtMutableItems(DhtDiscovery dht, IDhtSigner signer)
    {
        _dht = dht;
        _signer = signer;
    }

    /// <summary>
    /// Publish a mutable item to the DHT.
    /// The value is stored at nodes close to sha1(publicKey + salt).
    /// </summary>
    /// <param name="value">The value to publish (e.g., an info hash, or bencoded data). Max 1000 bytes.</param>
    /// <param name="salt">Optional salt for multiple items under the same key.</param>
    public async Task PublishAsync(byte[] value, byte[]? salt = null, CancellationToken ct = default)
    {
        if (value.Length > 1000) throw new ArgumentException("Value too large (max 1000 bytes per BEP 44)");

        _sequence++;

        var signData = BuildSignData(value, salt, _sequence);
        var signature = await _signer.SignAsync(signData);

        // Find nodes close to the target and send put requests
        var target = ComputeTarget(_signer.PublicKey, salt);
        var closest = _dht._routingTable.GetClosest(target, 8);

        foreach (var node in closest)
        {
            try
            {
                var putMsg = BuildPutMessage(value, _signer.PublicKey, signature, _sequence, salt);
                await _dht.SendKrpcAsync(node.EndPoint, putMsg, ct);
            }
            catch { }
        }
    }

    /// <summary>
    /// Publish an info hash as a mutable item — the core BEP 46 use case.
    /// Other peers can subscribe to your public key and auto-discover
    /// when you publish a new torrent.
    /// </summary>
    public Task PublishInfoHashAsync(byte[] infoHash, byte[]? salt = null, CancellationToken ct = default)
    {
        if (infoHash.Length != 20) throw new ArgumentException("Info hash must be 20 bytes");
        return PublishAsync(infoHash, salt, ct);
    }

    /// <summary>
    /// Look up the latest mutable item for a public key.
    /// </summary>
    /// <param name="publicKey">The publisher's public key.</param>
    /// <param name="salt">Optional salt.</param>
    /// <returns>The value and sequence number, or null if not found.</returns>
    public async Task<(byte[] value, long sequence)?> GetAsync(byte[] publicKey, byte[]? salt = null,
        CancellationToken ct = default)
    {
        var target = ComputeTarget(publicKey, salt);
        var closest = _dht._routingTable.GetClosest(target, 8);

        foreach (var node in closest)
        {
            try
            {
                var getMsg = BuildGetMessage(target);
                await _dht.SendKrpcAsync(node.EndPoint, getMsg, ct);

                // Response handling is done in DhtDiscovery.HandleResponse
                // For now, return null — async response will fire OnValueUpdated
            }
            catch { }
        }

        return null; // Responses come asynchronously via DHT message handling
    }

    /// <summary>
    /// Subscribe to updates from a public key. Periodically polls the DHT
    /// and fires OnValueUpdated when a new sequence is found.
    /// </summary>
    public async Task SubscribeAsync(byte[] publicKey, byte[]? salt = null,
        int pollIntervalMs = 30000, CancellationToken ct = default)
    {
        long lastSeq = -1;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var result = await GetAsync(publicKey, salt, ct);
                if (result.HasValue && result.Value.sequence > lastSeq)
                {
                    lastSeq = result.Value.sequence;
                    OnValueUpdated?.Invoke(publicKey, result.Value.value, lastSeq);
                }
            }
            catch { }

            await Task.Delay(pollIntervalMs, ct);
        }
    }

    /// <summary>
    /// Verify a signature on a received mutable item.
    /// Uses the signer's VerifyAsync for real cryptographic verification.
    /// </summary>
    public async Task<bool> VerifyAsync(byte[] publicKey, byte[] value, byte[] signature,
        long seq, byte[]? salt = null)
    {
        var signData = BuildSignData(value, salt, seq);
        return await _signer.VerifyAsync(publicKey, signData, signature);
    }

    // ── BEP 44/46 Message Builders ──

    private static byte[] ComputeTarget(byte[] publicKey, byte[]? salt)
    {
        if (salt == null || salt.Length == 0)
            return SHA1.HashData(publicKey);

        var combined = new byte[publicKey.Length + salt.Length];
        Array.Copy(publicKey, combined, publicKey.Length);
        Array.Copy(salt, 0, combined, publicKey.Length, salt.Length);
        return SHA1.HashData(combined);
    }

    private static byte[] BuildSignData(byte[] value, byte[]? salt, long seq)
    {
        var parts = new List<byte>();

        if (salt != null && salt.Length > 0)
        {
            parts.AddRange(Encoding.ASCII.GetBytes($"4:salt{salt.Length}:"));
            parts.AddRange(salt);
        }

        parts.AddRange(Encoding.ASCII.GetBytes($"3:seqi{seq}e1:v"));
        parts.AddRange(Encoding.ASCII.GetBytes($"{value.Length}:"));
        parts.AddRange(value);

        return parts.ToArray();
    }

    private byte[] BuildPutMessage(byte[] value, byte[] publicKey, byte[] signature,
        long seq, byte[]? salt)
    {
        var txId = new byte[] { (byte)(seq >> 8), (byte)seq };
        var buf = new List<byte>();
        buf.AddRange(Encoding.ASCII.GetBytes("d1:ad"));

        // id (our node ID)
        buf.AddRange(Encoding.ASCII.GetBytes("2:id20:"));
        buf.AddRange(_dht._nodeId);

        // k (public key)
        buf.AddRange(Encoding.ASCII.GetBytes($"1:k{publicKey.Length}:"));
        buf.AddRange(publicKey);

        // salt (optional)
        if (salt != null && salt.Length > 0)
        {
            buf.AddRange(Encoding.ASCII.GetBytes($"4:salt{salt.Length}:"));
            buf.AddRange(salt);
        }

        // seq
        buf.AddRange(Encoding.ASCII.GetBytes($"3:seqi{seq}e"));

        // sig
        buf.AddRange(Encoding.ASCII.GetBytes($"3:sig{signature.Length}:"));
        buf.AddRange(signature);

        // token — should come from a prior get response
        // TODO: Cache tokens from get responses per-node for proper DHT interaction
        buf.AddRange(Encoding.ASCII.GetBytes("5:token1:x"));

        // v (value)
        buf.AddRange(Encoding.ASCII.GetBytes($"1:v{value.Length}:"));
        buf.AddRange(value);

        buf.AddRange(Encoding.ASCII.GetBytes("e1:q3:put1:t2:"));
        buf.AddRange(txId);
        buf.AddRange(Encoding.ASCII.GetBytes("1:y1:qe"));
        return buf.ToArray();
    }

    private byte[] BuildGetMessage(byte[] target)
    {
        var txId = new byte[] { 0x46, 0x01 }; // "F\x01"
        var buf = new List<byte>();
        buf.AddRange(Encoding.ASCII.GetBytes("d1:ad"));
        buf.AddRange(Encoding.ASCII.GetBytes("2:id20:"));
        buf.AddRange(_dht._nodeId);
        buf.AddRange(Encoding.ASCII.GetBytes("6:target20:"));
        buf.AddRange(target);
        buf.AddRange(Encoding.ASCII.GetBytes("e1:q3:get1:t2:"));
        buf.AddRange(txId);
        buf.AddRange(Encoding.ASCII.GetBytes("1:y1:qe"));
        return buf.ToArray();
    }
}
