namespace SpawnDev.WebTorrent;

/// <summary>
/// Torrent download engine — piece selection, request pipeline, choke/unchoke, hotswap.
/// Direct 1:1 port of the download logic from webtorrent/lib/torrent.js.
/// This is a partial class — combined with Torrent.cs for the full implementation.
/// </summary>
public partial class Torrent
{
    // ========================
    // CONSTANTS (match JS exactly)
    // ========================
    public const int PieceTimeoutMs = 30_000;
    public const int ChokeTimeoutMs = 5_000;
    public const int SpeedThreshold = 3 * Piece.BlockLength;  // 49152 bytes/sec
    public const double PipelineMinDuration = 0.5;
    public const double PipelineMaxDuration = 1.0;
    public const int RechokeInterval = 10_000;
    public const int RechokeOptimisticDuration = 2;  // rechoke cycles
    /// <summary>Max simultaneous web seed connections. Configurable per torrent.</summary>
    public int MaxWebConns { get; set; } = 4;

    // ========================
    // DOWNLOAD STATE
    // ========================

    public string Strategy { get; set; } = "rarest";  // JS default is "sequential" but rarest is correct for interop
    public bool Done { get; set; }
    public bool Ready { get; set; }
    public bool[] Bitfield { get; set; } = Array.Empty<bool>();

    private RarityMap? _rarityMap;
    private Selections _selections = new();
    private System.Collections.Concurrent.ConcurrentDictionary<int, bool> _critical = new();
    private Dictionary<int, Wire?[]> _reservations = new();
    private int _rechokeNumSlots = 10;  // JS default: opts.uploads || 10
    private Wire? _rechokeOptimisticWire;
    private int _rechokeOptimisticTime;
    private Timer? _rechokeTimer;
    private byte[][] _hashes = Array.Empty<byte[]>();
    private static readonly Random _random = new();

    // ========================
    // ADAPTIVE PIPELINE (matches JS getBlockPipelineLength exactly)
    // ========================

    /// <summary>
    /// Calculate number of outstanding requests to maintain for a wire.
    /// Formula: 2 + ceil(duration * downloadSpeed / blockLength)
    /// Respects peer's reqq (max outstanding requests) from BEP 10 handshake.
    /// </summary>
    private static int GetBlockPipelineLength(Wire wire, double duration)
    {
        int length = 2 + (int)Math.Ceiling(duration * wire.DownloadSpeed() / Piece.BlockLength);

        // Honor reqq from BEP 10 extended handshake
        if (wire.PeerExtendedHandshake != null &&
            wire.PeerExtendedHandshake.TryGetValue("reqq", out var reqqObj))
        {
            if (reqqObj is long reqq && reqq > 0)
                length = Math.Min(length, (int)reqq);
            else if (reqqObj is int reqqInt && reqqInt > 0)
                length = Math.Min(length, reqqInt);
        }

        return length;
    }

    private static int GetPiecePipelineLength(Wire wire, double duration, int pieceLength)
    {
        return 1 + (int)Math.Ceiling(duration * wire.DownloadSpeed() / pieceLength);
    }

    // ========================
    // REQUEST A BLOCK (matches JS _request exactly)
    // ========================

    /// <summary>
    /// Attempt to request a block from the given wire for the given piece.
    /// Returns true if a request was made, false if no blocks available.
    /// </summary>
    private bool RequestBlock(Wire wire, int index, bool hotswap)
    {
        if (wire.Destroyed) return false;
        bool isWebSeed = wire.Type == "webSeed";

        if (index >= Bitfield.Length || Bitfield[index]) return false;

        int maxOutstanding = isWebSeed
            ? Math.Min(GetPiecePipelineLength(wire, PipelineMaxDuration, PieceLength), MaxWebConns)
            : GetBlockPipelineLength(wire, PipelineMaxDuration);

        if (wire.Requests.Count >= maxOutstanding) return false;

        var piece = Pieces[index];
        int reservation = isWebSeed ? piece.ReserveRemaining() : piece.Reserve();

        if (reservation == -1 && hotswap && Hotswap(wire, index))
            reservation = isWebSeed ? piece.ReserveRemaining() : piece.Reserve();

        if (reservation == -1) return false;

        // Track which wire reserved which block
        if (!_reservations.TryGetValue(index, out var r))
        {
            r = new Wire?[piece.Length / Piece.BlockLength + 1];
            _reservations[index] = r;
        }
        int slot = Array.IndexOf(r, null);
        if (slot == -1) { Array.Resize(ref r, r.Length + 1); slot = r.Length - 1; _reservations[index] = r; }
        r[slot] = wire;

        int chunkOffset = piece.ChunkOffset(reservation);
        int chunkLength = isWebSeed ? piece.ChunkLengthRemaining(reservation) : piece.ChunkLength(reservation);

        wire.Request(index, chunkOffset, chunkLength, async (err, chunk) =>
        {
            if (Done) return;
            if (slot < r.Length && r[slot] == wire) r[slot] = null;
            if (piece != Pieces[index]) { UpdateWires(); return; }

            if (err != null)
            {
                if (isWebSeed) piece.CancelRemaining(reservation);
                else piece.Cancel(reservation);
                UpdateWires();
                return;
            }

            if (chunk == null) { UpdateWires(); return; }

            // Set block data — returns true if piece is now complete
            if (!piece.Set(reservation, chunk, wire.PeerId ?? "unknown"))
            {
                UpdateWires();
                return;
            }

            // Piece complete — flush and verify hash (v1 SHA-1, Phase 1 flat SHA-256,
            // or BEP 52 v2 Merkle via MetaVersion-aware VerifyPieceHash).
            var buf = piece.Flush();
            if (buf == null) { UpdateWires(); return; }

            bool hashMatch = VerifyPieceHash(index, buf);

            if (hashMatch)
            {
                // Verified! Store the piece to chunk store for seeding
                if (_store != null)
                    await _store.PutAsync(index, buf);

                Pieces[index] = new Piece(0); // mark as done (length 0 = flushed)
                Bitfield[index] = true;

                // Announce to all peers
                foreach (var w in Wires.ToArray())
                    _ = w.Have(index);

                OnPieceVerified?.Invoke(index);
                CheckDone();
            }
            else
            {
                // Failed verification — reset piece
                Pieces[index] = new Piece(Pieces[index].Length > 0 ? Pieces[index].Length : PieceLength);
                OnWarning?.Invoke($"Piece {index} failed verification");
            }

            UpdateWires();
        });

        return true;
    }

    // ========================
    // HOTSWAP (matches JS _hotswap exactly)
    // ========================

    /// <summary>
    /// Attempt to steal a block from the slowest peer working on this piece.
    /// The thief must be at least 2x faster than the victim.
    /// Does NOT send Cancel — duplicate data is harmlessly ignored.
    /// </summary>
    private bool Hotswap(Wire wire, int index)
    {
        double speed = wire.DownloadSpeed();
        if (speed < Piece.BlockLength) return false;
        if (!_reservations.TryGetValue(index, out var r)) return false;

        double minSpeed = double.MaxValue;
        Wire? minWire = null;

        for (int i = 0; i < r.Length; i++)
        {
            var otherWire = r[i];
            if (otherWire == null || otherWire == wire) continue;
            double otherSpeed = otherWire.DownloadSpeed();
            if (otherSpeed >= SpeedThreshold) continue;
            if (2 * otherSpeed > speed || otherSpeed > minSpeed) continue;
            minWire = otherWire;
            minSpeed = otherSpeed;
        }

        if (minWire == null) return false;

        // Cancel blocks owned by the slow wire
        for (int i = 0; i < r.Length; i++)
            if (r[i] == minWire) r[i] = null;

        foreach (var req in minWire.Requests)
        {
            if (req.Piece != index) continue;
            Pieces[index].Cancel(req.Offset / Piece.BlockLength);
        }

        OnHotswap?.Invoke(minWire, wire, index);
        return true;
    }

    // ========================
    // UPDATE WIRES (matches JS _updateWire / _updateWireInterest)
    // ========================

    /// <summary>Trigger piece requests on all wires.</summary>
    private void UpdateWires()
    {
        foreach (var wire in Wires.ToArray())
            UpdateWire(wire);
    }

    /// <summary>Update interest and request pieces for a single wire.</summary>
    private void UpdateWire(Wire wire)
    {
        if (wire.Destroyed || Paused || Destroyed) return;

        int minOutstanding = GetBlockPipelineLength(wire, PipelineMinDuration);
        if (wire.Requests.Count >= minOutstanding) return;
        int maxOutstanding = GetBlockPipelineLength(wire, PipelineMaxDuration);

        if (wire.PeerChoking) return;

        // Try selections: first without hotswap, then with
        TrySelectWire(wire, maxOutstanding, false);
        TrySelectWire(wire, maxOutstanding, true);
    }

    private bool TrySelectWire(Wire wire, int maxOutstanding, bool hotswap)
    {
        if (wire.Requests.Count >= maxOutstanding) return true;

        for (int i = 0; i < _selections.Length; i++)
        {
            var next = _selections.Get(i);
            if (next == null) continue;

            if (Strategy == "rarest" && _rarityMap != null)
            {
                int start = next.From + next.Offset;
                int end = next.To;
                int len = end - start + 1;
                var tried = new HashSet<int>();
                int tries = 0;

                while (tries < len)
                {
                    int piece = _rarityMap.GetRarestPiece(idx =>
                        idx >= start && idx <= end && !tried.Contains(idx) && wire.PeerHasPiece(idx));
                    if (piece < 0) break;

                    while (RequestBlock(wire, piece, _critical.ContainsKey(piece) || hotswap) &&
                           wire.Requests.Count < maxOutstanding) { }

                    if (wire.Requests.Count >= maxOutstanding) return true;
                    tried.Add(piece);
                    tries++;
                }
            }
            else
            {
                // Sequential
                for (int piece = next.From + next.Offset; piece <= next.To; piece++)
                {
                    if (!wire.PeerHasPiece(piece)) continue;

                    while (RequestBlock(wire, piece, _critical.ContainsKey(piece) || hotswap) &&
                           wire.Requests.Count < maxOutstanding) { }

                    if (wire.Requests.Count >= maxOutstanding) return true;
                }
            }
        }

        return false;
    }

    /// <summary>Update interest state for a wire based on available pieces.</summary>
    private void UpdateWireInterest(Wire wire)
    {
        bool dominated = false;

        // Check if the wire has any piece we need
        for (int i = 0; i < Bitfield.Length; i++)
        {
            if (!Bitfield[i] && wire.PeerHasPiece(i))
            {
                dominated = true;
                break;
            }
        }

        if (dominated)
        {
            if (!wire.AmInterested) _ = wire.Interested();
        }
        else
        {
            if (wire.AmInterested) _ = wire.Uninterested();
        }
    }

    // ========================
    // RECHOKE (matches JS _rechoke exactly — multi-criteria sort, 10 slots, optimistic)
    // ========================

    /// <summary>
    /// Periodically update choke status of all peers.
    /// Sort by download speed, upload speed, current choke state, random tiebreaker.
    /// Unchoke top N-1, optimistic unchoke 1 more, choke the rest.
    /// </summary>
    private void Rechoke()
    {
        if (Destroyed) return;
        if (!Ready || Paused || Destroyed) return;

        // Sort wires: increasing quality (pop = best)
        var wireStack = Wires.ToArray()
            .Select(wire => (wire, random: _random.NextDouble()))
            .OrderBy(x =>
            {
                var w = x.wire;
                return (w.DownloadSpeed(), w.UploadSpeed(), w.AmChoking ? 0 : 1, x.random);
            })
            .Select(x => x.wire)
            .ToList();

        if (_rechokeOptimisticTime <= 0)
            _rechokeOptimisticWire = null;
        else
            _rechokeOptimisticTime--;

        int numInterestedUnchoked = 0;
        while (wireStack.Count > 0 && numInterestedUnchoked < _rechokeNumSlots - 1)
        {
            var wire = wireStack[^1];
            wireStack.RemoveAt(wireStack.Count - 1);

            if (wire == _rechokeOptimisticWire) continue;

            _ = wire.Unchoke();
            if (wire.PeerInterested) numInterestedUnchoked++;
        }

        // Fill optimistic unchoke slot
        if (_rechokeOptimisticWire == null && _rechokeNumSlots > 0)
        {
            var remaining = wireStack.Where(w => w.PeerInterested).ToList();
            if (remaining.Count > 0)
            {
                var newOptimistic = remaining[_random.Next(remaining.Count)];
                _ = newOptimistic.Unchoke();
                _rechokeOptimisticWire = newOptimistic;
                _rechokeOptimisticTime = RechokeOptimisticDuration;
            }
        }

        // Choke the rest (except optimistic)
        foreach (var wire in wireStack)
        {
            if (wire != _rechokeOptimisticWire)
                _ = wire.Choke();
        }
    }

    /// <summary>Start the rechoke timer.</summary>
    public void StartRechoke()
    {
        _rechokeTimer?.Dispose();
        _rechokeTimer = new Timer(_ => Rechoke(), null, RechokeInterval, RechokeInterval);
    }

    // ========================
    // WIRE SETUP (matches JS _onWireWithMetadata)
    // ========================

    /// <summary>
    /// Set up a newly connected wire for piece exchange.
    /// Called after metadata is available and handshake is complete.
    /// </summary>
    public void OnWireWithMetadata(Wire wire)
    {
        wire.SetTimeout(PieceTimeoutMs);

        // Send our bitfield (or HaveNone if empty)
        bool hasPieces = Bitfield.Any(b => b);
        if (hasPieces)
        {
            _ = wire.Bitfield(BoolBitfieldToBytes(Bitfield));
        }
        else if (wire.HasFast)
        {
            _ = wire.HaveNone_Send();
        }
        else
        {
            // Send empty bitfield (BEP 3 requires bitfield message)
            _ = wire.Bitfield(new byte[(int)Math.Ceiling(Bitfield.Length / 8.0)]);
        }

        // Express interest in the peer's pieces
        UpdateWireInterest(wire);

        // When peer bitfield/have changes, update interest and request
        wire.OnBitfield += (_) => { UpdateWireInterest(wire); UpdateWire(wire); };
        wire.OnHaveAll += () => { UpdateWireInterest(wire); UpdateWire(wire); };
        wire.OnHaveNone += () => { UpdateWireInterest(wire); };
        wire.OnHave += (_) => { UpdateWireInterest(wire); UpdateWire(wire); };

        // When peer unchokes us, start requesting
        wire.OnUnchoke += () => UpdateWire(wire);

        // BEP 52 v2 peer-wire extensions (msg ids 21/22/23). We forward the peer's hashes
        // and hash_reject messages into the per-torrent coordinator so the outstanding
        // RequestAsync task resolves correctly, regardless of which wire in the swarm we
        // originally sent the matching hash_request through. OnHashRequest is the inbound
        // seed path - handled in OnV2HashRequest.
        if (MetaVersion == 2 && V2HashCoord != null)
        {
            var coord = V2HashCoord;
            wire.OnHashes += msg => coord.HandleHashes(msg);
            wire.OnHashReject += msg => coord.HandleReject(msg);
            wire.OnHashRequest += msg => OnV2HashRequest(wire, msg);
        }

        // BEP 11: Wire PEX discovered peers into TCP connection pipeline
        var pex = wire.GetExtension<UtPexExtension>();
        if (pex != null)
        {
            pex.OnPeersReceived += (peers) =>
            {
                foreach (var p in peers)
                {
                    if (!OperatingSystem.IsBrowser())
                        ConnectTcpPeer(p.Address);
                }
            };
        }

        // Handle incoming piece requests (seeding) — serve from store
        wire.OnRequest += (index, offset, length, respond) =>
        {
            if (_store == null || index < 0 || index >= Bitfield.Length || !Bitfield[index])
            {
                respond(new Exception("piece not available"), null);
                return;
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    var data = await _store.GetAsync(index, offset, length);
                    if (data != null)
                    {
                        // Apply upload rate limiting if configured
                        if (_client?.UploadRateLimiter is { Rate: >= 0 } limiter)
                            await limiter.WaitAsync(data.Length);
                        UploadedTotal += data.Length;
                        respond(null, data);
                    }
                    else
                    {
                        respond(new Exception("piece data not found in store"), null);
                    }
                }
                catch (Exception ex)
                {
                    respond(ex, null);
                }
            });
        };
    }

    // ========================
    // CHECK DONE
    // ========================

    private void CheckDone()
    {
        if (Done) return;

        bool allDone;
        if (_selections.Length > 0)
        {
            // BEP 53 / partial selection: "done" means all selected pieces are downloaded
            allDone = true;
            foreach (var sel in _selections.ToArray())
            {
                for (int i = sel.From; i <= sel.To && i < Bitfield.Length; i++)
                {
                    if (!Bitfield[i]) { allDone = false; break; }
                }
                if (!allDone) break;
            }
        }
        else
        {
            allDone = Bitfield.All(b => b);
        }

        if (allDone)
        {
            Done = true;
            OnDone?.Invoke();
            OnIdle?.Invoke();
        }
    }

    // ========================
    // EVENTS
    // ========================

    public event Action<int>? OnPieceVerified;  // piece index
    public event Action? OnDone;
    public event Action<Wire, Wire, int>? OnHotswap;  // oldWire, newWire, pieceIndex
    public event Action<string>? OnWarning;

    // ========================
    // HELPERS
    // ========================

    /// <summary>Convert bool[] bitfield to byte[] for wire protocol.</summary>
    public static byte[] BoolBitfieldToBytes(bool[] bitfield)
    {
        var bytes = new byte[(int)Math.Ceiling(bitfield.Length / 8.0)];
        for (int i = 0; i < bitfield.Length; i++)
            if (bitfield[i])
                bytes[i / 8] |= (byte)(1 << (7 - (i % 8)));
        return bytes;
    }

    /// <summary>
    /// BEP 52 client path: issue a <c>hash_request</c> for a Merkle-hash range on behalf of
    /// this torrent and wait for the verified response. Picks a peer wire that supports the
    /// BEP 52 extension (plain BitTorrent peer-wire messages 21/22/23 - core protocol, not
    /// BEP 10 extended) to actually send through; the per-torrent
    /// <see cref="V2HashRequestCoordinator"/> correlates the reply, verifies it against the
    /// <paramref name="req"/>'s <c>pieces_root</c>, and resolves the returned task.
    ///
    /// Intended consumer: magnet-only v2 bootstrap (when we have the info dict but not the
    /// piece layers dict), and re-verification on a corrupted piece where the cached piece-
    /// layer hash may itself be suspect.
    /// </summary>
    /// <param name="req">The hash_request to issue. PiecesRoot, BaseLayer, Index, Length,
    /// and ProofLayers must all be consistent with BEP 52's indexing rules.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <param name="wire">Optional specific wire to send through. When null, picks the
    /// first connected wire that is neither destroyed nor a web seed.</param>
    /// <returns>The verified hash list (length + proof_layers entries) as delivered by the
    /// peer, already validated to re-climb to the claimed <c>pieces_root</c>.</returns>
    public Task<byte[][]> RequestV2HashesAsync(
        Bep52WireMessages.HashRequest req,
        CancellationToken ct = default,
        Wire? wire = null)
    {
        if (V2HashCoord == null)
            throw new InvalidOperationException(
                "V2HashRequestCoordinator is only allocated for v2 torrents. " +
                "Check MetaVersion == 2 before calling RequestV2HashesAsync.");

        Wire? target = wire ?? PickV2HashRequestWire();
        if (target == null)
            throw new InvalidOperationException(
                "No peer wire available to serve a BEP 52 hash_request. Connect a peer first.");

        return V2HashCoord.RequestAsync(req, send: target.SendHashRequest, ct: ct);
    }

    private Wire? PickV2HashRequestWire()
    {
        // Web seeds are plain HTTP range servers - they can't speak the BEP 52 peer wire.
        foreach (var w in Wires.ToArray())
        {
            if (w.Destroyed) continue;
            if (w.Type == "webSeed") continue;
            return w;
        }
        return null;
    }

    /// <summary>
    /// BEP 52 seed path: a peer asked us for a range of Merkle hashes for a file root we may
    /// know. If we hold piece-layer hashes for that root (from our parsed metadata), build the
    /// matching base-layer + proof payload via <see cref="MerkleProofBuilder"/> and reply with
    /// a <c>hashes</c> message. Otherwise reply with <c>hash_reject</c>.
    ///
    /// Only <c>base_layer == pieceLayerLevel</c> requests are served today: a peer asking for
    /// piece-layer hashes to bootstrap a v2-only magnet or to re-verify after a corrupt piece.
    /// Leaf-level (base_layer=0) requests would require re-hashing piece data from our chunk
    /// store, which is doable but deferred - we reject politely rather than stall.
    /// </summary>
    private void OnV2HashRequest(Wire wire, Bep52WireMessages.HashRequest req)
    {
        var payload = TryBuildV2HashesPayload(req);
        if (payload != null)
        {
            var (baseLayer, proof) = payload.Value;
            var hashList = new byte[baseLayer.Length + proof.Length][];
            Array.Copy(baseLayer, 0, hashList, 0, baseLayer.Length);
            Array.Copy(proof, 0, hashList, baseLayer.Length, proof.Length);
            _ = wire.SendHashes(new Bep52WireMessages.Hashes(
                req.PiecesRoot, req.BaseLayer, req.Index, req.Length, req.ProofLayers, hashList));
        }
        else
        {
            _ = wire.SendHashReject(new Bep52WireMessages.HashReject(
                req.PiecesRoot, req.BaseLayer, req.Index, req.Length, req.ProofLayers));
        }
    }

    /// <summary>
    /// Internal for tests: attempt to build the hashes-message payload for a hash_request.
    /// Returns <c>null</c> to signal "I can't serve this - send hash_reject."
    /// </summary>
    internal (byte[][] baseLayer, byte[][] proof)? TryBuildV2HashesPayload(Bep52WireMessages.HashRequest req)
    {
        if (PieceLength < MerkleHasher.LeafSize || PieceLength % MerkleHasher.LeafSize != 0) return null;
        int leavesPerPiece = PieceLength / MerkleHasher.LeafSize;
        if (leavesPerPiece < 1 || (leavesPerPiece & (leavesPerPiece - 1)) != 0) return null;
        int pieceLayerLevel = IntLog2(leavesPerPiece);

        // Only the piece-layer level is served today. Other levels require file content.
        if ((int)req.BaseLayer != pieceLayerLevel) return null;

        if (!PieceLayers.TryGetValue(req.PiecesRoot, out var concat)) return null;
        if (concat == null || concat.Length == 0 || concat.Length % MerkleHasher.HashSize != 0) return null;

        int totalPieces = concat.Length / MerkleHasher.HashSize;
        var layer = new byte[totalPieces][];
        for (int i = 0; i < totalPieces; i++)
        {
            layer[i] = new byte[MerkleHasher.HashSize];
            Buffer.BlockCopy(concat, i * MerkleHasher.HashSize, layer[i], 0, MerkleHasher.HashSize);
        }

        return MerkleProofBuilder.Build(
            layer,
            baseLayerLevel: pieceLayerLevel,
            index: req.Index,
            length: (int)req.Length,
            proofLayers: (int)req.ProofLayers,
            expectedRoot: req.PiecesRoot);
    }

    private static int IntLog2(int powerOfTwo)
    {
        int log = 0;
        while ((1 << log) < powerOfTwo) log++;
        return log;
    }

    /// <summary>
    /// Verify an assembled piece buffer against the expected hash at <paramref name="index"/>.
    /// Branch matrix:
    /// <list type="bullet">
    /// <item><c>MetaVersion = 2</c> (BEP 52 v2 / hybrid): the stored hash is the piece layer
    /// hash - the Merkle root over the piece's 16 KiB leaves, NOT a flat SHA-256 of the
    /// whole piece. Recompute via <see cref="MerkleHasher.ComputePieceLayer"/> and compare.
    /// Required for correctness when <see cref="PieceLength"/> &gt; 16 KiB: a flat SHA-256
    /// would always mismatch.</item>
    /// <item><c>MetaVersion = 0</c> (v1 / Phase 1): the stored hash is a flat 20-byte SHA-1
    /// (v1) or flat 32-byte SHA-256 (Phase 1 "BEP 52 Phase 1" torrents). Pick the algorithm
    /// from the stored hash length, matching the original Phase 1 behavior.</item>
    /// </list>
    /// Returns false on any out-of-range index, mismatched hash, or malformed piece data.
    /// Internal for test visibility; the production caller is the piece-arrival path in
    /// <c>_onPiece</c>.
    /// </summary>
    internal bool VerifyPieceHash(int index, byte[] buf)
    {
        if (index < 0 || index >= _hashes.Length) return false;
        var expected = _hashes[index];

        if (MetaVersion == 2)
        {
            // BEP 52 Merkle piece-layer hash.
            if (expected.Length != MerkleHasher.HashSize) return false;
            if (PieceLength < MerkleHasher.LeafSize || PieceLength % MerkleHasher.LeafSize != 0) return false;
            var pieceLayer = MerkleHasher.ComputePieceLayer(buf, PieceLength);
            if (pieceLayer.Length != 1) return false;
            return pieceLayer[0].AsSpan().SequenceEqual(expected);
        }

        // v1 / Phase 1: flat SHA-1 (20B) or SHA-256 (32B) per stored hash length.
        var actual = expected.Length == 32
            ? System.Security.Cryptography.SHA256.HashData(buf)
            : System.Security.Cryptography.SHA1.HashData(buf);
        return actual.SequenceEqual(expected);
    }
}
