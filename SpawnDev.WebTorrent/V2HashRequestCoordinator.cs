namespace SpawnDev.WebTorrent;

/// <summary>
/// State machine for BEP 52 v2 hash-request / hashes / hash-reject correlation. Tracks
/// outstanding requests by their natural key (pieces_root + base_layer + index + length),
/// times them out after a configurable deadline, and resolves the returned
/// <see cref="Task{TResult}"/> either with the verified hash list (on success) or with an
/// exception (on reject, timeout, or verification failure).
///
/// Design scope (Phase 2c step 2.3b foundation):
/// - Pure correlation + verification logic. No Wire/Torrent awareness. Callers invoke
///   <see cref="RequestAsync"/> with a send callback and feed received messages via
///   <see cref="HandleHashes"/> / <see cref="HandleReject"/>. Integrating with Wire.cs
///   events is one-line glue in the consumer.
/// - Verification uses <see cref="MerkleProofVerifier"/>. A peer that returns bytes not
///   cryptographically consistent with the requested root is rejected; the request can be
///   retried against another peer at the caller's discretion.
/// - No retry or peer-ranking policy baked in. That's a consumer concern (which peer to
///   ask first, how many retries, backoff). This class only guarantees correlation +
///   timeout + verification on the response path.
///
/// Thread-safety: internal dictionary + task-completion-source manipulations are lock-
/// guarded so callers can safely invoke from different tasks (Wire event handlers and
/// piece-arrival paths can race).
/// </summary>
public sealed class V2HashRequestCoordinator
{
    private readonly object _lock = new();
    private readonly Dictionary<RequestKey, PendingRequest> _pending = new();

    /// <summary>Default timeout for an outstanding hash_request before it fails with TimeoutException.</summary>
    public TimeSpan DefaultTimeout { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Issue a hash_request and wait for the peer to reply with a matching hashes message.
    /// The send callback is invoked synchronously before the method returns; the returned
    /// Task completes when the response arrives (or the request is rejected / times out).
    /// </summary>
    /// <param name="req">The hash_request to issue and correlate against.</param>
    /// <param name="send">Callback to actually emit the request on the wire. Typically
    /// wraps <c>Wire.SendHashRequest</c>.</param>
    /// <param name="ct">Cancellation token. Cancelling disposes the correlation entry; a
    /// late-arriving response is dropped.</param>
    /// <param name="timeout">Optional override for <see cref="DefaultTimeout"/>.</param>
    /// <returns>The verified hash list (length + proof_layers entries) exactly as the peer
    /// delivered them.</returns>
    public async Task<byte[][]> RequestAsync(
        Bep52WireMessages.HashRequest req,
        Func<Bep52WireMessages.HashRequest, Task> send,
        CancellationToken ct = default,
        TimeSpan? timeout = null)
    {
        if (send == null) throw new ArgumentNullException(nameof(send));

        var key = RequestKey.From(req);
        var pending = new PendingRequest();

        lock (_lock)
        {
            if (_pending.ContainsKey(key))
                throw new InvalidOperationException("A hash_request with the same key is already outstanding; coalesce at the caller.");
            _pending[key] = pending;
        }

        // Wire up cancellation + timeout before issuing the send so we don't lose a
        // very-fast response that arrives during the send-callback await.
        var deadline = timeout ?? DefaultTimeout;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(deadline);
        using var _ = cts.Token.Register(() =>
        {
            lock (_lock)
            {
                if (_pending.Remove(key, out var taken))
                {
                    if (ct.IsCancellationRequested)
                        taken.Tcs.TrySetCanceled(ct);
                    else
                        taken.Tcs.TrySetException(new TimeoutException(
                            $"BEP 52 hash_request timed out after {deadline.TotalSeconds:F1}s (root={HexShort(req.PiecesRoot)}, base_layer={req.BaseLayer}, index={req.Index}, length={req.Length})."));
                }
            }
        });

        try
        {
            await send(req);
        }
        catch
        {
            lock (_lock) { _pending.Remove(key); }
            throw;
        }

        return await pending.Tcs.Task;
    }

    /// <summary>
    /// Feed a received <see cref="Bep52WireMessages.Hashes"/> into the coordinator. Runs
    /// <see cref="MerkleProofVerifier.Verify(Bep52WireMessages.Hashes)"/> to confirm the
    /// peer's response is cryptographically consistent with its claimed pieces_root, then
    /// resolves the matching pending request. If no request matches, the message is
    /// dropped silently (peer sent unsolicited hashes).
    /// </summary>
    public void HandleHashes(Bep52WireMessages.Hashes msg)
    {
        var key = RequestKey.From(msg.PiecesRoot, msg.BaseLayer, msg.Index, msg.Length);
        PendingRequest? pending;
        lock (_lock)
        {
            if (!_pending.Remove(key, out pending)) return; // unsolicited, drop
        }

        if (!MerkleProofVerifier.Verify(msg))
        {
            pending.Tcs.TrySetException(new InvalidOperationException(
                "BEP 52 hashes response failed Merkle verification (peer returned cryptographically inconsistent data)."));
            return;
        }

        pending.Tcs.TrySetResult(msg.HashList);
    }

    /// <summary>
    /// Feed a received <see cref="Bep52WireMessages.HashReject"/> into the coordinator.
    /// Matches by key; resolves the corresponding pending request with a
    /// <see cref="OperationCanceledException"/>-wrapping-HashRejectedException so the
    /// caller can distinguish peer-rejection from network timeout.
    /// </summary>
    public void HandleReject(Bep52WireMessages.HashReject msg)
    {
        var key = RequestKey.From(msg.PiecesRoot, msg.BaseLayer, msg.Index, msg.Length);
        PendingRequest? pending;
        lock (_lock)
        {
            if (!_pending.Remove(key, out pending)) return;
        }
        pending.Tcs.TrySetException(new HashRejectedException(
            $"Peer rejected hash_request (root={HexShort(msg.PiecesRoot)}, base_layer={msg.BaseLayer}, index={msg.Index}, length={msg.Length})."));
    }

    /// <summary>Number of requests currently in flight. Useful for tests + diagnostics.</summary>
    public int OutstandingCount
    {
        get { lock (_lock) return _pending.Count; }
    }

    private sealed class PendingRequest
    {
        public readonly TaskCompletionSource<byte[][]> Tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private readonly struct RequestKey : IEquatable<RequestKey>
    {
        private readonly byte[] _root;
        private readonly uint _baseLayer;
        private readonly uint _index;
        private readonly uint _length;

        private RequestKey(byte[] root, uint baseLayer, uint index, uint length)
        {
            _root = root;
            _baseLayer = baseLayer;
            _index = index;
            _length = length;
        }

        public static RequestKey From(Bep52WireMessages.HashRequest r) => new(r.PiecesRoot, r.BaseLayer, r.Index, r.Length);
        public static RequestKey From(byte[] root, uint baseLayer, uint index, uint length) => new(root, baseLayer, index, length);

        public bool Equals(RequestKey other)
        {
            if (_baseLayer != other._baseLayer || _index != other._index || _length != other._length) return false;
            return _root.AsSpan().SequenceEqual(other._root);
        }

        public override bool Equals(object? obj) => obj is RequestKey k && Equals(k);

        public override int GetHashCode()
        {
            // FNV-1a over (root || baseLayer || index || length).
            unchecked
            {
                uint h = 2166136261u;
                foreach (var b in _root) h = (h ^ b) * 16777619u;
                h = (h ^ _baseLayer) * 16777619u;
                h = (h ^ _index) * 16777619u;
                h = (h ^ _length) * 16777619u;
                return (int)h;
            }
        }
    }

    private static string HexShort(byte[] b) =>
        b.Length >= 8 ? Convert.ToHexString(b, 0, 8).ToLowerInvariant() + "..." : Convert.ToHexString(b).ToLowerInvariant();
}

/// <summary>Thrown when a peer refuses a BEP 52 hash_request with a hash_reject message.</summary>
public sealed class HashRejectedException : Exception
{
    public HashRejectedException(string message) : base(message) { }
}
