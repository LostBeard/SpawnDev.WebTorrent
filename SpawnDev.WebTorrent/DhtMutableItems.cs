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
public class DhtMutableItems : IDisposable
{
    private readonly DhtDiscovery _dht;
    private readonly IDhtSigner _signer;
    private bool _disposed;
    private long _sequence;
    private readonly ConcurrentDictionary<string, byte[]> _tokenCache = new();
    // Latest verified value per ITEM. Key: hex(target) where target = SHA-1(k + salt), so one key can publish any number of salted items (BEP 46 uses the salt to tell
    // a publisher's torrents apart) without them overwriting each other.
    private readonly ConcurrentDictionary<string, (byte[] value, long seq)> _valueCache = new();
    // The signature that goes with the value held in _valueCache for an item (the same key), kept so this node can hand the item on to others (RepublishAsync) without being its creator.
    private readonly ConcurrentDictionary<string, (long seq, byte[] signature)> _signatureCache = new();

    // A BEP 44 get response carries v, k, seq and sig but NOT the salt (the salt is part of what was signed, and the requester is the one who knows it). To verify a response we
    // therefore try the salts this instance has been asked about for that key (plus no salt). Key: hex(k) -> (hex(salt) or "" -> salt).
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, byte[]?>> _knownSalts = new();

    private void RegisterSalt(byte[] publicKey, byte[]? salt)
    {
        var salts = _knownSalts.GetOrAdd(Convert.ToHexString(publicKey), _ => new ConcurrentDictionary<string, byte[]?>());
        salts[salt == null || salt.Length == 0 ? "" : Convert.ToHexString(salt)] = salt == null || salt.Length == 0 ? null : salt;
    }

    /// <summary>The salts a response from <paramref name="publicKey"/> might have been signed with: none, then each one asked about.</summary>
    private List<byte[]?> SaltCandidates(byte[] publicKey, byte[]? echoedSalt)
    {
        var candidates = new List<byte[]?> { null };
        if (echoedSalt is { Length: > 0 }) candidates.Add(echoedSalt); // not standard, but a peer that echoes it is easy to support
        if (_knownSalts.TryGetValue(Convert.ToHexString(publicKey), out var known))
            foreach (var salt in known.Values)
                if (salt != null && !candidates.Any(c => c != null && c.AsSpan().SequenceEqual(salt))) candidates.Add(salt);
        return candidates;
    }

    // Server-side storage for BEP 44 put queries we've received + are now serving to
    // other peers on subsequent get queries. Key: hex(target) where target = SHA-1(k + salt).
    // Value: full stored item payload so we can reconstruct the get response.
    private readonly ConcurrentDictionary<string, StoredMutableItem> _storedItems = new();

    private sealed class StoredMutableItem
    {
        public byte[] Value = Array.Empty<byte>();
        public byte[] PublicKey = Array.Empty<byte>();
        public byte[] Signature = Array.Empty<byte>();
        public long Seq;
        public byte[]? Salt;
    }

    /// <summary>Async file system for persisting sequence numbers across restarts.</summary>
    public SpawnDev.AsyncFileSystem.IAsyncFS? AsyncFileSystem { get; set; }

    public byte[] PublicKey => _signer.PublicKey;
    public long Sequence => _sequence;
    public string Algorithm => _signer.Algorithm;
    public int CachedTokenCount => _tokenCache.Count;

    /// <summary>How many BEP 44 put messages this instance has sent (one per node written to): 0 after a publish means no node had given it a token.</summary>
    public long PutsSent => Interlocked.Read(ref _putsSent);
    private long _putsSent;

    /// <summary>Event fired when a subscribed key has a new value.</summary>
    public event Action<byte[], byte[], long>? OnValueUpdated; // publicKey, value, sequence

    /// <summary>
    /// Like <see cref="OnValueUpdated"/> but says WHICH item changed: (publicKey, salt, value, sequence), with a null salt for an unsalted item. Use this when a key publishes
    /// several salted items (one key, many torrents): <see cref="OnValueUpdated"/> cannot tell them apart.
    /// </summary>
    public event Action<byte[], byte[]?, byte[], long>? OnItemUpdated;

    public DhtMutableItems(DhtDiscovery dht, IDhtSigner signer)
    {
        _dht = dht;
        _signer = signer;

        // Wire GET response handling - this is the key fix from the original
        _dht.OnGetResponse += HandleGetResponse;

        // Serve inbound BEP 44 get/put queries so we participate as a DHT server,
        // not just a client. Required for 2-node loopback (nothing else on the wire
        // to store/relay on our behalf) and for real-network cases where this node
        // is one of the closest to a target.
        _dht.OnGetQuery += HandleIncomingGet;
        _dht.OnPutQuery += HandleIncomingPut;
    }

    // BEP 44 target = SHA1(k || salt). Static so tests can verify without an instance.
    private static string TargetHex(byte[] publicKey, byte[]? salt)
    {
        var input = salt != null && salt.Length > 0
            ? publicKey.Concat(salt).ToArray()
            : publicKey;
        return Convert.ToHexString(SHA1.HashData(input));
    }

    private byte[]? HandleIncomingGet(Dictionary<string, object> msg, IPEndPoint from)
    {
        try
        {
            if (!msg.TryGetValue("t", out var tObj) || tObj is not byte[] txId) return null;
            if (!msg.TryGetValue("a", out var aObj) || aObj is not Dictionary<string, object> args) return null;
            if (!args.TryGetValue("target", out var targetObj) || targetObj is not byte[] target) return null;

            var targetKey = Convert.ToHexString(target);
            if (_storedItems.TryGetValue(targetKey, out var item))
            {
                return _dht.BuildGetResponse(txId, _dht.NodeId,
                    value: item.Value, seq: item.Seq, signature: item.Signature, publicKey: item.PublicKey);
            }
            // No stored value for this target - let DhtDiscovery emit the default
            // empty-nodes + fresh-token response.
            return null;
        }
        catch { return null; }
    }

    private byte[]? HandleIncomingPut(Dictionary<string, object> msg, IPEndPoint from)
    {
        try
        {
            if (!msg.TryGetValue("a", out var aObj) || aObj is not Dictionary<string, object> args) return null;
            if (!args.TryGetValue("k", out var kObj) || kObj is not byte[] publicKey || publicKey.Length != 32) return null;
            if (!args.TryGetValue("v", out var vObj) || vObj is not byte[] value) return null;
            if (!args.TryGetValue("sig", out var sigObj) || sigObj is not byte[] signature || signature.Length != 64) return null;
            if (!args.TryGetValue("seq", out var seqObj)) return null;
            long seq = seqObj is long l ? l : seqObj is int i ? i : -1;
            if (seq < 0) return null;
            byte[]? salt = args.TryGetValue("salt", out var saltObj) && saltObj is byte[] s ? s : null;

            // Verify Ed25519 signature per BEP 44: sign over "3:seqi{seq}e1:v{len}:{value}"
            // (salt prefix if present). Reject forged writes rather than storing.
            var signData = BuildSignData(value, salt, seq);
            var verifyTask = _signer.VerifyAsync(publicKey, signData, signature);
            if (!verifyTask.GetAwaiter().GetResult()) return null;

            var target = TargetHex(publicKey, salt);

            // Monotonic sequence per BEP 44 section "updating": only accept if seq > stored.
            if (_storedItems.TryGetValue(target, out var existing) && existing.Seq >= seq) return null;

            _storedItems[target] = new StoredMutableItem
            {
                Value = value,
                PublicKey = publicKey,
                Signature = signature,
                Seq = seq,
                Salt = salt,
            };
            // Default ack from DhtDiscovery is fine.
            return null;
        }
        catch { return null; }
    }

    /// <summary>
    /// Raises the sequence number to at least <paramref name="minimum"/> (never lowers it): the next <see cref="PublishAsync"/> uses a value above it. A publisher with no persistent
    /// storage (so <see cref="RestoreSequenceAsync"/> has nothing to restore) starts every process at 1, and the DHT ignores a put whose sequence is not higher than the one it holds,
    /// so its republishes silently vanish. Passing the current Unix time in MILLISECONDS (seconds would collide for two publishes in one second) makes the sequence rise across restarts without storing anything (BEP 44 only asks that it increase).
    /// </summary>
    public void EnsureSequenceAtLeast(long minimum)
    {
        long current;
        while ((current = Interlocked.Read(ref _sequence)) < minimum)
            if (Interlocked.CompareExchange(ref _sequence, minimum, current) == current) break;
    }

    /// <summary>Restore the last published sequence number from persistent storage.</summary>
    public async Task RestoreSequenceAsync()
    {
        if (AsyncFileSystem == null) return;
        var dir = "webtorrent/_dht_seq";
        var path = $"{dir}/{Convert.ToHexString(PublicKey).ToLowerInvariant()}";
        try
        {
            if (await AsyncFileSystem.FileExists(path))
            {
                var data = await AsyncFileSystem.ReadBytes(path);
                if (data != null && data.Length == 8)
                    Interlocked.Exchange(ref _sequence, BitConverter.ToInt64(data));
            }
        }
        catch { }
    }

    private async Task PersistSequenceAsync()
    {
        if (AsyncFileSystem == null) return;
        var dir = "webtorrent/_dht_seq";
        var path = $"{dir}/{Convert.ToHexString(PublicKey).ToLowerInvariant()}";
        try
        {
            if (!await AsyncFileSystem.DirectoryExists(dir))
                await AsyncFileSystem.CreateDirectory(dir);
            await AsyncFileSystem.Write(path, BitConverter.GetBytes(_sequence));
        }
        catch { }
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

        byte[]? echoedSalt = response.TryGetValue("salt", out var saltObj) && saltObj is byte[] s ? s : null;
        var candidates = SaltCandidates(pubKey, echoedSalt);

        // Check if this is newer than what we have for every item it could be (fast path - no async needed)
        if (candidates.All(salt => _valueCache.TryGetValue(CacheKey(pubKey, salt), out var cached) && cached.seq >= seq))
            return; // Old or duplicate

        // Verify Ed25519 signature off the UDP receive loop to avoid blocking
        if (response.TryGetValue("sig", out var sigObj) && sigObj is byte[] signature)
        {
            // Queue verification work - never block the receive loop
            _ = Task.Run(async () =>
            {
                // the signature covers the salt, so the one that verifies is the item this is
                foreach (var salt in candidates)
                {
                    if (!await _signer.VerifyAsync(pubKey, BuildSignData(value, salt, seq), signature)) continue;
                    Accept(pubKey, salt, value, seq, signature);
                    return;
                }
                // verified under no salt we know of: forged, or an item nobody here asked about. Reject.
            });
        }
        else
        {
            // No signature - accept only if we have no signed version (bootstrapping)
            Accept(pubKey, null, value, seq);
        }
    }

    private static string CacheKey(byte[] publicKey, byte[]? salt) => TargetHex(publicKey, salt);

    /// <summary>Stores a verified value if it is newer than the one held for that item and tells the subscribers.</summary>
    private void Accept(byte[] pubKey, byte[]? salt, byte[] value, long seq, byte[]? signature = null)
    {
        var cacheKey = CacheKey(pubKey, salt);
        // Re-check freshness (another response may have arrived while we verified)
        if (_valueCache.TryGetValue(cacheKey, out var latest) && latest.seq >= seq) return;
        _valueCache[cacheKey] = (value, seq);
        if (signature != null) _signatureCache[cacheKey] = (seq, signature);
        OnItemUpdated?.Invoke(pubKey, salt, value, seq);
        OnValueUpdated?.Invoke(pubKey, value, seq);
    }

    /// <summary>Publish a mutable item to the DHT.</summary>
    public async Task PublishAsync(byte[] value, byte[]? salt = null, CancellationToken ct = default)
    {
        if (value.Length > 1000) throw new ArgumentException("Value too large (max 1000 bytes per BEP 44)");
        if (salt != null && salt.Length > 200) throw new ArgumentException("Salt too large (max 200 bytes per BEP 44)");
        if (_signer.PublicKey.Length != 32)
            throw new InvalidOperationException($"BEP 44 requires 32-byte Ed25519 public key, got {_signer.PublicKey.Length} bytes");

        RegisterSalt(_signer.PublicKey, salt);
        Interlocked.Increment(ref _sequence);
        await PersistSequenceAsync();

        // First walk to the target with a GET: that finds the nodes nearest it that are alive, and each of them hands back the write token a put needs
        var target = ComputeTarget(_signer.PublicKey, salt);
        var nearest = await _dht.LookupAsync(target, ct);

        // Wait for GET responses with tokens - retry up to 5 times with increasing delay
        var signData = BuildSignData(value, salt, _sequence);
        var signature = await _signer.SignAsync(signData);

        // Self-store: the publisher is one of the K nodes closest to the target
        // (it's close to anything not by definition, but in small swarms this is
        // load-bearing: a subscriber querying the publisher directly needs the
        // value served locally). Mirrors what coturn/libtorrent do for their own
        // puts. Safe in large swarms too - just means one extra node has the
        // value, which is the redundancy model anyway.
        var targetHex = Convert.ToHexString(target);
        _storedItems[targetHex] = new StoredMutableItem
        {
            Value = value,
            PublicKey = _signer.PublicKey,
            Signature = signature,
            Seq = _sequence,
            Salt = salt,
        };
        // Also surface via the normal received-value path so local consumers
        // (e.g. local OnValueUpdated subscribers on the same process) see it.
        _valueCache[CacheKey(_signer.PublicKey, salt)] = (value, _sequence);

        _signatureCache[CacheKey(_signer.PublicKey, salt)] = (_sequence, signature);
        await PutToNearestAsync(nearest, value, _signer.PublicKey, signature, _sequence, salt, ct);
    }

    /// <summary>
    /// Writes an already signed item to each of the nodes that answered the lookup, as their write tokens arrive (a token can lag the answer a moment); gives up on a node after a few
    /// attempts. Returns how many nodes it wrote to.
    /// </summary>
    private async Task<int> PutToNearestAsync(IReadOnlyCollection<IPEndPoint> nearest, byte[] value, byte[] publicKey, byte[] signature, long seq, byte[]? salt, CancellationToken ct)
    {
        var written = new HashSet<string>();
        int putCount = 0;
        for (int attempt = 0; attempt < 5 && !ct.IsCancellationRequested && written.Count < nearest.Count; attempt++)
        {
            await Task.Delay(attempt == 0 ? 250 : 500, ct);
            foreach (var endpoint in nearest)
            {
                var nodeKey = endpoint.ToString();
                if (written.Contains(nodeKey) || !_tokenCache.TryGetValue(nodeKey, out var nodeToken)) continue;
                try
                {
                    var putMsg = BuildPutMessage(value, publicKey, signature, seq, salt, nodeToken);
                    await _dht.SendKrpcAsync(endpoint, putMsg, ct);
                    written.Add(nodeKey);
                    putCount++; Interlocked.Increment(ref _putsSent);
                }
                catch { }
            }
        }

        if (putCount == 0 && WebTorrentClient.VerboseLogging)
            Console.WriteLine($"[DhtMutableItems] WARNING: sent 0 PUTs (no tokens acquired)");
        return putCount;
    }

    /// <summary>
    /// Hands on an item this node has SEEN (a verified get response) without being its creator: a mutable item carries the creator's signature, and any node may write a signed item to the
    /// nodes near its target (BEP 44), as long as it is not older than what they hold. A DHT forgets an item a couple of hours after its last write, so a world whose creator is offline
    /// stays findable only while somebody who has seen its pointer keeps writing it. This node also stores the item itself and answers get queries for it. Looks the item up again first
    /// (when <paramref name="refresh"/>, so a newer version is the one handed on); <paramref name="shouldHandOn"/> sees the value found and can decline it (a sharer that holds an older
    /// version of what the item points to should not vouch for the newer one). Returns false when the item is not known, or was not seen with its signature (the item is this node's own:
    /// publish it instead), or was declined, or no node accepted a write.
    /// </summary>
    public async Task<bool> RepublishAsync(byte[] publicKey, byte[]? salt = null, bool refresh = true, Func<byte[], bool>? shouldHandOn = null, CancellationToken ct = default)
    {
        RegisterSalt(publicKey, salt);
        var cacheKey = CacheKey(publicKey, salt);
        var target = ComputeTarget(publicKey, salt);
        long before = _valueCache.TryGetValue(cacheKey, out var known) ? known.seq : -1;
        if (refresh || before < 0)
        {
            await _dht.GetAsync(target, ct);
            for (int i = 0; i < 30 && !ct.IsCancellationRequested; i++)
            {
                await Task.Delay(100, ct);
                if (_valueCache.TryGetValue(cacheKey, out var now) && now.seq > before) break;
            }
        }
        if (!_valueCache.TryGetValue(cacheKey, out var item) || !_signatureCache.TryGetValue(cacheKey, out var sig) || sig.seq != item.seq) return false;

        if (shouldHandOn != null && !shouldHandOn(item.value)) return false;
        var targetHex = Convert.ToHexString(target);
        if (!_storedItems.TryGetValue(targetHex, out var stored) || stored.Seq < item.seq)
            _storedItems[targetHex] = new StoredMutableItem { Value = item.value, PublicKey = publicKey, Signature = sig.signature, Seq = item.seq, Salt = salt };
        var nearest = await _dht.LookupAsync(target, ct);
        return await PutToNearestAsync(nearest, item.value, publicKey, sig.signature, item.seq, salt, ct) > 0;
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
        RegisterSalt(publicKey, salt);
        var cacheKey = CacheKey(publicKey, salt);

        // Check cache first - may already have from prior subscription
        if (_valueCache.TryGetValue(cacheKey, out var existing))
            return (existing.value, existing.seq);

        var target = ComputeTarget(publicKey, salt);
        await _dht.GetAsync(target, ct);

        // Poll for response arrival - exit early when value appears (up to 3 seconds)
        for (int i = 0; i < 30 && !ct.IsCancellationRequested; i++)
        {
            await Task.Delay(100, ct);
            if (_valueCache.TryGetValue(cacheKey, out var cached))
                return (cached.value, cached.seq);
        }

        return null;
    }

    /// <summary>Subscribe to updates. Polls the DHT, fires OnValueUpdated on new sequence.</summary>
    public async Task SubscribeAsync(byte[] publicKey, byte[]? salt = null,
        int pollIntervalMs = 30000, CancellationToken ct = default)
    {
        RegisterSalt(publicKey, salt);
        while (!ct.IsCancellationRequested)
        {
            // Always force a live DHT lookup rather than the cache-first GetAsync path,
            // otherwise once the cache is warm we stop polling for newer sequences and
            // miss republishes. BEP 46's value proposition IS mutation, so polling must
            // keep reaching out.
            try
            {
                var target = ComputeTarget(publicKey, salt);
                await _dht.GetAsync(target, ct);
            }
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
    public bool IsNewerSequence(byte[] publicKey, long seq, byte[]? salt = null)
    {
        var key = CacheKey(publicKey, salt);
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

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _dht.OnGetResponse -= HandleGetResponse;
        _dht.OnGetQuery -= HandleIncomingGet;
        _dht.OnPutQuery -= HandleIncomingPut;
    }
}
