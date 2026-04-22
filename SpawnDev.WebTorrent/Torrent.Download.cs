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

            // Piece complete — flush and verify hash
            var buf = piece.Flush();
            if (buf == null) { UpdateWires(); return; }

            // BEP 52: detect algorithm from stored hash length rather than hashing twice.
            // 32-byte = SHA-256, 20-byte = SHA-1. Avoids computing the wrong algorithm
            // on every piece of a SHA-256 torrent.
            bool hashMatch = false;
            if (index < _hashes.Length)
            {
                var expected = _hashes[index];
                var actual = expected.Length == 32
                    ? System.Security.Cryptography.SHA256.HashData(buf)
                    : System.Security.Cryptography.SHA1.HashData(buf);
                hashMatch = actual.SequenceEqual(expected);
            }

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
}
