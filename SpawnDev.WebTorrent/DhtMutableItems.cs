using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace SpawnDev.WebTorrent;

/// <summary>
/// BEP 44/46: Mutable DHT Items — publish and retrieve signed data in the DHT.
/// Adapted from the original SpawnDev.WebTorrent implementation for _Alt.
///
/// Key fix from original: GET responses are now wired back from DhtDiscovery.OnGetResponse
/// into this class, so SubscribeAsync actually receives updates.
///
/// Protocol:
///   put: { v, k, seq, sig, salt?, token }
///   get: { target: sha1(k + salt) } → returns latest { v, k, seq, sig }
/// </summary>
public class DhtMutableItems
{
    private readonly DhtDiscovery _dht;
    private readonly IDhtSigner _signer;
    private long _sequence;
    private readonly ConcurrentDictionary<string, byte[]> _tokenCache = new();
    private readonly ConcurrentDictionary<string, (byte[] value, long seq)> _valueCache = new();

    public byte[] PublicKey => _signer.PublicKey;
    public long Sequence => _sequence;
    public string Algorithm => _signer.Algorithm;
    public int CachedTokenCount => _tokenCache.Count;

    /// <summary>Event fired when a subscribed key has a new value.</summary>
    public event Action<byte[], byte[], long>? OnValueUpdated; // publicKey, value, sequence

    public DhtMutableItems(DhtDiscovery dht, IDhtSigner signer)
    {
        _dht = dht;
        _signer = signer;

        // Wire GET response handling — this is the key fix from the original
        _dht.OnGetResponse += HandleGetResponse;
    }

    private void HandleGetResponse(Dictionary<string, object> response, IPEndPoint from)
    {
        // Cache the token for subsequent PUT requests
        if (response.TryGetValue("token", out var tokenObj) && tokenObj is byte[] token)
            _tokenCache[from.ToString()] = token;

        // Extract mutable item fields
        if (!response.TryGetValue("v", out var vObj) || vObj is not byte[] value) return;
        if (!response.TryGetValue("k", out var kObj) || kObj is not byte[] pubKey) return;
        if (!response.TryGetValue("seq", out var seqObj)) return;
        long seq = seqObj is long l ? l : seqObj is int i ? i : 0;

        // Verify Ed25519 signature (BEP 44 security: reject unsigned/forged values)
        if (response.TryGetValue("sig", out var sigObj) && sigObj is byte[] signature)
        {
            byte[]? salt = response.TryGetValue("salt", out var saltObj) && saltObj is byte[] s ? s : null;
            var signData = BuildSignData(value, salt, seq);
            var verified = _signer.VerifyAsync(pubKey, signData, signature).GetAwaiter().GetResult();
            if (!verified) return; // Reject forged value
        }

        // Check if this is newer than what we have
        var cacheKey = Convert.ToHexString(pubKey);
        if (_valueCache.TryGetValue(cacheKey, out var cached) && cached.seq >= seq)
            return; // Old or duplicate

        _valueCache[cacheKey] = (value, seq);
        OnValueUpdated?.Invoke(pubKey, value, seq);
    }

    /// <summary>Publish a mutable item to the DHT.</summary>
    public async Task PublishAsync(byte[] value, byte[]? salt = null, CancellationToken ct = default)
    {
        if (value.Length > 1000) throw new ArgumentException("Value too large (max 1000 bytes per BEP 44)");
        if (salt != null && salt.Length > 200) throw new ArgumentException("Salt too large (max 200 bytes per BEP 44)");
        if (_signer.PublicKey.Length != 32)
            throw new InvalidOperationException($"BEP 44 requires 32-byte Ed25519 public key, got {_signer.PublicKey.Length} bytes");

        Interlocked.Increment(ref _sequence);

        // First do a GET to acquire tokens from nearby nodes
        var target = ComputeTarget(_signer.PublicKey, salt);
        await _dht.GetAsync(target, ct);
        await Task.Delay(500, ct); // Allow time for GET responses with tokens

        var signData = BuildSignData(value, salt, _sequence);
        var signature = await _signer.SignAsync(signData);

        var closest = _dht._routingTable.GetClosest(target, 8);
        foreach (var node in closest)
        {
            var nodeKey = node.EndPoint.ToString();
            if (!_tokenCache.TryGetValue(nodeKey, out var nodeToken)) continue;
            try
            {
                var putMsg = BuildPutMessage(value, _signer.PublicKey, signature, _sequence, salt, nodeToken);
                await _dht.SendKrpcAsync(node.EndPoint, putMsg, ct);
            }
            catch { }
        }
    }

    /// <summary>Publish an info hash as a mutable item (core BEP 46 use case).</summary>
    public Task PublishInfoHashAsync(byte[] infoHash, byte[]? salt = null, CancellationToken ct = default)
    {
        if (infoHash.Length != 20) throw new ArgumentException("Info hash must be 20 bytes");
        return PublishAsync(infoHash, salt, ct);
    }

    /// <summary>Look up the latest mutable item for a public key.</summary>
    public async Task<(byte[] value, long sequence)?> GetAsync(byte[] publicKey, byte[]? salt = null,
        CancellationToken ct = default)
    {
        var target = ComputeTarget(publicKey, salt);
        await _dht.GetAsync(target, ct);

        // Check cache for any response that arrived
        await Task.Delay(1000, ct); // Allow time for responses
        var cacheKey = Convert.ToHexString(publicKey);
        if (_valueCache.TryGetValue(cacheKey, out var cached))
            return (cached.value, cached.seq);

        return null;
    }

    /// <summary>Subscribe to updates. Polls the DHT, fires OnValueUpdated on new sequence.</summary>
    public async Task SubscribeAsync(byte[] publicKey, byte[]? salt = null,
        int pollIntervalMs = 30000, CancellationToken ct = default)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await GetAsync(publicKey, salt, ct); }
            catch { }
            await Task.Delay(pollIntervalMs, ct);
        }
    }

    /// <summary>Verify a signature on a received mutable item.</summary>
    public async Task<bool> VerifyAsync(byte[] publicKey, byte[] value, byte[] signature,
        long seq, byte[]? salt = null)
    {
        var signData = BuildSignData(value, salt, seq);
        return await _signer.VerifyAsync(publicKey, signData, signature);
    }

    // ── BEP 44 Helpers ──

    public static byte[] ComputeTarget(byte[] publicKey, byte[]? salt)
    {
        if (salt == null || salt.Length == 0)
            return SHA1.HashData(publicKey);
        var combined = new byte[publicKey.Length + salt.Length];
        Array.Copy(publicKey, combined, publicKey.Length);
        Array.Copy(salt, 0, combined, publicKey.Length, salt.Length);
        return SHA1.HashData(combined);
    }

    public static byte[] BuildSignData(byte[] value, byte[]? salt, long seq)
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

    /// <summary>
    /// Rejects old sequence numbers. Returns true if seq is newer than cached.
    /// </summary>
    public bool IsNewerSequence(byte[] publicKey, long seq)
    {
        var key = Convert.ToHexString(publicKey);
        if (_valueCache.TryGetValue(key, out var cached))
            return seq > cached.seq;
        return true; // No cached value — anything is newer
    }

    private byte[] BuildPutMessage(byte[] value, byte[] publicKey, byte[] signature,
        long seq, byte[]? salt, byte[] token)
    {
        var txId = new byte[] { (byte)(seq >> 8), (byte)seq };
        var buf = new List<byte>();
        buf.AddRange(Encoding.ASCII.GetBytes("d1:ad"));

        buf.AddRange(Encoding.ASCII.GetBytes("2:id20:"));
        buf.AddRange(_dht.NodeId);

        buf.AddRange(Encoding.ASCII.GetBytes($"1:k{publicKey.Length}:"));
        buf.AddRange(publicKey);

        if (salt != null && salt.Length > 0)
        {
            buf.AddRange(Encoding.ASCII.GetBytes($"4:salt{salt.Length}:"));
            buf.AddRange(salt);
        }

        buf.AddRange(Encoding.ASCII.GetBytes($"3:seqi{seq}e"));
        buf.AddRange(Encoding.ASCII.GetBytes($"3:sig{signature.Length}:"));
        buf.AddRange(signature);
        buf.AddRange(Encoding.ASCII.GetBytes($"5:token{token.Length}:"));
        buf.AddRange(token);
        buf.AddRange(Encoding.ASCII.GetBytes($"1:v{value.Length}:"));
        buf.AddRange(value);

        buf.AddRange(Encoding.ASCII.GetBytes("e1:q3:put1:t2:"));
        buf.AddRange(txId);
        buf.AddRange(Encoding.ASCII.GetBytes("1:y1:qe"));
        return buf.ToArray();
    }
}
