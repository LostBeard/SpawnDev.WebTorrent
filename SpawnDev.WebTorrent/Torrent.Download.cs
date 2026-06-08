using SpawnDev.BlazorJS;
// Narrow aliases (not the whole JSObjects namespace) so JS `Array` doesn't shadow System.Array.
using Uint8Array = SpawnDev.BlazorJS.JSObjects.Uint8Array;
using SubtleCrypto = SpawnDev.BlazorJS.JSObjects.SubtleCrypto;
using ArrayBuffer = SpawnDev.BlazorJS.JSObjects.ArrayBuffer;

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
    public int MaxWebConns { get; set; } = 4; // parallel HTTP web-seed range requests. Kept <= the browser's ~6/origin HTTP/1.1 cap to avoid queueing; browser throughput is bounded by single-thread per-piece processing, not connection count.

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
    /// <summary>Piece indices with an in-flight ZERO-COPY web-seed fetch (browser path). Caps that path's
    /// concurrency at <see cref="MaxWebConns"/> (the zero-copy path doesn't use wire.Requests).</summary>
    private readonly HashSet<int> _zeroCopyInFlight = new();
    /// <summary>Count of pieces verified + stored via the ZERO-COPY browser web-seed path (data never
    /// entered the .NET heap). Diagnostic + lets tests assert the zero-copy path actually fired.</summary>
    public int ZeroCopyPiecesVerified { get; private set; }
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

        // ZERO-COPY browser web-seed path: keep the piece's bytes in JS end to end (fetch -> SubtleCrypto
        // leaf-hash -> OPFS) so they never cross into the .NET heap — the browser model-download bottleneck.
        // Only when the store is browser OPFS and the torrent is single-file (the contiguous-range case).
        if (isWebSeed && _store is Storage.AsyncFSChunkStore { SupportsUint8Array: true }
            && Files != null && (Files.Length == 1 || !PieceSpansMultipleFiles(index)))
        {
            var wc = _webConns.FirstOrDefault(c => c.WireInstance == wire);
            if (wc != null) return RequestPieceZeroCopy(wc, index);
        }

        // Web seed = HTTP range requests. Use a FIXED concurrency (MaxWebConns) to hide per-request latency
        // and saturate bandwidth. Do NOT gate it on the speed-based piece pipeline: GetPiecePipelineLength is
        // chicken-and-egg for a web seed — it starts at ~1, so the wire runs one range at a time, so the
        // measured DownloadSpeed stays low, so the pipeline stays at ~1. Observed as one 4MB piece in flight
        // → model loads from the hub crawled far below LAN bandwidth.
        int maxOutstanding = isWebSeed
            ? MaxWebConns
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
            // This runs inside a fire-and-forget wire.Request callback: an unhandled throw here (e.g. an OPFS
            // PutAsync failure) used to be silently swallowed, leaving the piece at missing=0 / Bitfield=false
            // FOREVER — the picker thinks it's still needed but it never re-downloads (observed stranding the
            // tail moov's last piece, so a non-faststart <video> never got loadedmetadata). Catch + reset so a
            // failed completion re-requests instead of stranding, and record why for triage.
            byte[]? buf = null;
            try
            {
                buf = piece.Flush();
                if (buf == null)
                {
                    // Set() said complete but Flush() saw _buffered!=_chunks — a concurrent completion (the
                    // same piece finished on another wire / the zero-copy web-seed path) already flushed it.
                    if (index == PieceCount - 1) LastCompletionNote = $"p{index} flush=null (already flushed elsewhere)";
                    UpdateWires();
                    return;
                }

                bool hashMatch = VerifyPieceHash(index, buf);

                if (hashMatch)
                {
                    // Verified! Store the piece to chunk store for seeding
                    if (_store != null)
                        await _store.PutAsync(index, buf);

                    Pieces[index] = new Piece(0); // mark as done (length 0 = flushed)
                    Bitfield[index] = true;
                    if (index == PieceCount - 1) LastCompletionNote = $"p{index} OK len={buf.Length}";

                    // Announce to all peers
                    foreach (var w in Wires.ToArray())
                        _ = w.Have(index);

                    OnPieceVerified?.Invoke(index);
                    CheckDone();
                }
                else
                {
                    // Failed verification — reset piece
                    if (index == PieceCount - 1) LastCompletionNote = $"p{index} verify-FAIL len={buf.Length}";
                    Pieces[index] = new Piece(Pieces[index].Length > 0 ? Pieces[index].Length : PieceLength);
                    OnWarning?.Invoke($"Piece {index} failed verification");
                }
            }
            catch (Exception cex)
            {
                // NEVER strand the piece on a completion error — reset it so the picker re-requests it.
                if (index == PieceCount - 1) LastCompletionNote = $"p{index} THREW (buf={(buf?.Length ?? -1)}): {cex.GetType().Name}: {cex.Message}";
                if (piece == Pieces[index])
                    Pieces[index] = new Piece(Pieces[index].Length > 0 ? Pieces[index].Length : PieceLength);
                OnWarning?.Invoke($"Piece {index} completion error: {cex.GetType().Name}: {cex.Message}");
            }

            UpdateWires();
        });

        return true;
    }

    /// <summary>
    /// ZERO-COPY web-seed piece download (browser): fetch the piece as a JS <see cref="Uint8Array"/>,
    /// verify it with SubtleCrypto (leaf hashes in JS, Merkle tree in .NET), and store it to OPFS — all
    /// without the piece bytes ever entering the .NET heap. Concurrency is capped at <see cref="MaxWebConns"/>
    /// via <see cref="_zeroCopyInFlight"/>. Returns true if a fetch was started for this piece.
    /// </summary>
    /// <summary>True if piece <paramref name="index"/>'s byte range straddles a file boundary in a multi-file
    /// torrent. Such pieces can't use the single-fetch zero-copy web-seed path (one file range per fetch); they
    /// fall back to the .NET block path, which already splits a piece across files. Interior pieces (the vast
    /// majority) return false and stay on the fast zero-copy path.</summary>
    private bool PieceSpansMultipleFiles(int index)
    {
        var files = Files;
        if (files == null || files.Length <= 1) return false;
        long s = (long)index * PieceLength;
        long e = s + Pieces[index].Length - 1;
        int hits = 0;
        foreach (var f in files)
        {
            if (f.Offset > e || f.Offset + f.Length - 1 < s) continue;
            if (++hits > 1) return true;
        }
        return false;
    }

    private bool RequestPieceZeroCopy(WebConn webConn, int index)
    {
        if (_zeroCopyInFlight.Contains(index)) return false;          // already fetching this piece
        if (_zeroCopyInFlight.Count >= MaxWebConns) return false;     // concurrency cap
        var piece = Pieces[index];
        int pieceLen = piece.Length;
        if (pieceLen <= 0) return false;

        _zeroCopyInFlight.Add(index);
        long rangeStart = (long)index * PieceLength;
        long rangeEnd = rangeStart + pieceLen - 1;
        _ = ZeroCopyPieceAsync(webConn, index, piece, rangeStart, rangeEnd, pieceLen);
        return true;
    }

    private async Task ZeroCopyPieceAsync(WebConn webConn, int index, Piece piece, long rangeStart, long rangeEnd, int pieceLen)
    {
        Uint8Array? ua = null;
        try
        {
            ua = await webConn.FetchPieceUint8ArrayAsync(rangeStart, rangeEnd);
            if (Done || piece != Pieces[index]) return;              // superseded/finished while fetching

            bool match = await VerifyPieceZeroCopyAsync(index, ua, pieceLen);
            if (piece != Pieces[index]) return;

            if (match)
            {
                if (_store is Storage.AsyncFSChunkStore afs)
                    await afs.PutUint8ArrayAsync(index, ua);          // JS Uint8Array -> OPFS, no .NET copy
                Pieces[index] = new Piece(0);                         // mark done (length 0 = flushed)
                Bitfield[index] = true;
                ZeroCopyPiecesVerified++;
                foreach (var w in Wires.ToArray()) _ = w.Have(index);
                OnPieceVerified?.Invoke(index);
                CheckDone();
            }
            else
            {
                Pieces[index] = new Piece(piece.Length > 0 ? piece.Length : PieceLength);
                OnWarning?.Invoke($"Piece {index} failed verification (zero-copy)");
            }
        }
        catch (Exception ex)
        {
            if (WebTorrentClient.VerboseLogging)
                Console.WriteLine($"[ZeroCopy] piece {index} failed: {ex.GetType().Name}: {ex.Message}");
            // leave the piece unflagged so the picker retries it
        }
        finally
        {
            ua?.Dispose();
            _zeroCopyInFlight.Remove(index);
            UpdateWires();
        }
    }

    /// <summary>
    /// Verifies a fetched piece (JS <see cref="Uint8Array"/>) against its expected hash WITHOUT copying the
    /// bytes into .NET. v2 (Merkle): hash each 16 KiB leaf with SubtleCrypto over zero-copy Uint8Array views
    /// (final partial leaf zero-padded to 16 KiB), then build the tree in .NET via
    /// <see cref="MerkleHasher.ComputePieceRootFromLeafHashes"/>. v1/flat: a single SubtleCrypto digest of
    /// the whole piece. Only the small (≤32-byte) hashes cross the boundary. Mirrors <see cref="VerifyPieceHash"/>.
    /// </summary>
    private async Task<bool> VerifyPieceZeroCopyAsync(int index, Uint8Array pieceData, int pieceLen)
    {
        if (index < 0 || index >= _hashes.Length) return false;
        var expected = _hashes[index];
        using var subtle = BlazorJSRuntime.JS.Get<SubtleCrypto>("crypto.subtle");

        if (MetaVersion == 2)
        {
            if (expected.Length != MerkleHasher.HashSize) return false;
            if (PieceLength < MerkleHasher.LeafSize || PieceLength % MerkleHasher.LeafSize != 0) return false;
            int leavesPerPiece = PieceLength / MerkleHasher.LeafSize;
            int actualLeaves = (pieceLen + MerkleHasher.LeafSize - 1) / MerkleHasher.LeafSize;
            // Hash all 16 KiB leaves CONCURRENTLY. The leaf digests are independent, so firing them all and
            // awaiting one Task.WhenAll lets the native SubtleCrypto overlap them. Awaiting each digest
            // sequentially (the prior version) was N interop round-trips per piece and cost more than the
            // byte[] copy it replaced -- the cause of the "identical, if not worse" measurement.
            var inputs = new Uint8Array[actualLeaves];               // leaf inputs, kept alive until WhenAll
            var digests = new Task<ArrayBuffer>[actualLeaves];
            for (int li = 0; li < actualLeaves; li++)
            {
                int leafStart = li * MerkleHasher.LeafSize;
                int leafLen = Math.Min(MerkleHasher.LeafSize, pieceLen - leafStart);
                Uint8Array input;
                if (leafLen == MerkleHasher.LeafSize)
                {
                    input = pieceData.SubArray(leafStart, leafStart + leafLen);
                }
                else
                {
                    input = new Uint8Array(MerkleHasher.LeafSize);   // zero-filled tail pad
                    using var tail = pieceData.SubArray(leafStart, leafStart + leafLen);
                    input.Set(tail, 0);
                }
                inputs[li] = input;
                digests[li] = subtle.Digest("SHA-256", input);
            }

            var hashBuffers = await Task.WhenAll(digests);
            var leafHashes = new byte[actualLeaves][];
            for (int li = 0; li < actualLeaves; li++)
            {
                using (hashBuffers[li])
                using (var hashUa = new Uint8Array(hashBuffers[li]))
                    leafHashes[li] = hashUa.ReadBytes();
                inputs[li].Dispose();
            }
            var root = MerkleHasher.ComputePieceRootFromLeafHashes(leafHashes, leavesPerPiece);
            return root.AsSpan().SequenceEqual(expected);
        }
        else
        {
            // v1 / Phase 1 flat: single SHA-1 (20B) or SHA-256 (32B) over the whole piece.
            string alg = expected.Length == MerkleHasher.HashSize ? "SHA-256" : "SHA-1";
            using var hashAb = await subtle.Digest(alg, pieceData);
            using var hashUa = new Uint8Array(hashAb);
            return hashUa.ReadBytes().AsSpan().SequenceEqual(expected);
        }
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

    private int _updateWiresScheduled;

    /// <summary>
    /// Trigger piece requests on all wires - DEBOUNCED. Coalesces a burst of triggers (a media-element
    /// streaming read marking many pieces Critical, plus many block arrivals) into ONE request-loop pass per
    /// tick. Mirrors JS webtorrent: critical() and block-receipt schedule a queueMicrotask _update rather than
    /// running the O(wires*selections*pieces) request loop synchronously per trigger (torrent.js:1786). Without
    /// this, a media element's streaming reads (Critical per piece) hammered the picker and starved the bulk
    /// download - the live-Sintel moov downloaded far too slowly, so the element gave up before it arrived.
    /// </summary>
    private void UpdateWires()
    {
        if (System.Threading.Interlocked.Exchange(ref _updateWiresScheduled, 1) == 1) return;
        _ = Task.Run(() =>
        {
            System.Threading.Interlocked.Exchange(ref _updateWiresScheduled, 0);
            try { UpdateWiresNow(); } catch { }
        });
    }

    /// <summary>The actual request-loop pass (formerly UpdateWires). Always reached via the debounced
    /// <see cref="UpdateWires"/> so bursts coalesce into one pass.</summary>
    private void UpdateWiresNow()
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
        // Zero-copy web-seed concurrency is tracked in _zeroCopyInFlight, NOT wire.Requests (which stays
        // PINNED AT 0 on the zero-copy path — RequestBlock returns before wire.Request). The picker's normal
        // brakes are all keyed on wire.Requests.Count, so they never fire here. This MUST be checked INSIDE
        // the walk loops too (the breaks below), or the rarest walk re-scans EVERY piece after each
        // completion — O(pieces^2), GetRarestPiece is an O(pieces) scan — which made SD-Turbo model loads
        // crawl at ~1.5 pieces/s (~750ms/piece in interpreted WASM). Regression introduced in 4da1613.
        bool ZeroCopyFull() => wire.Type == "webSeed" && _zeroCopyInFlight.Count >= MaxWebConns
            && _store is Storage.AsyncFSChunkStore { SupportsUint8Array: true };
        if (wire.Requests.Count >= maxOutstanding || ZeroCopyFull()) return true;

        // CRITICAL-FIRST pass: read-awaited pieces (ReadFileAsync / streaming) must be
        // fetched ahead of the normal rarest/sequential walk. Without this, a piece marked
        // Critical() that sits late in the walk order waits for the walk to reach it
        // (observed: browser first-read 21.5s vs desktop 669ms over a web seed). Critical
        // pieces always allow hotswap so they can steal block reservations from
        // lower-priority in-flight pieces. RequestBlock already skips have/out-of-range.
        //
        // Order critical pieces by SELECTION PRIORITY (high first). A media player streaming a
        // non-faststart MP4 reads the front (low-priority selection) AND range-requests the tail moov
        // (high-priority selection, priority ∝ inverse range size — see RespondWithStream). Both are
        // critical, but this pass returns as soon as the wire fills (ZeroCopyFull), so arbitrary
        // _critical iteration let the continuously-advancing front read consume every freed web-seed slot
        // and starve the tail — the moov request then blocks forever and the video never demuxes. Sorting
        // by selection priority makes the small tail win the slot. _critical holds only read-awaited pieces
        // (a few), so this sort is cheap.
        if (!_critical.IsEmpty)
        {
            var criticalPieces = new List<int>(_critical.Keys);
            if (criticalPieces.Count > 1)
                criticalPieces.Sort((a, b) => PiecePriority(b).CompareTo(PiecePriority(a)));
            foreach (var piece in criticalPieces)
            {
                if (!wire.PeerHasPiece(piece)) continue;

                while (RequestBlock(wire, piece, true) &&
                       wire.Requests.Count < maxOutstanding) { }

                if (wire.Requests.Count >= maxOutstanding || ZeroCopyFull()) return true;
            }
        }

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

                    if (wire.Requests.Count >= maxOutstanding || ZeroCopyFull()) return true;
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

                    if (wire.Requests.Count >= maxOutstanding || ZeroCopyFull()) return true;
                }
            }
        }

        return false;
    }

    // Effective download priority of a piece = the priority of the highest-priority selection covering it.
    // _selections is kept priority-ordered (descending), so the first covering selection is the highest.
    // Used to order the critical-first pass so a high-priority streaming range (tail moov) outranks a
    // low-priority one (front) when both are read-awaited. Returns int.MinValue if no selection covers it.
    private int PiecePriority(int piece)
    {
        for (int i = 0; i < _selections.Length; i++)
        {
            var s = _selections.Get(i);
            if (s != null && piece >= s.From && piece <= s.To) return s.Priority;
        }
        return int.MinValue;
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
        // Null filter defends against a concurrent wire Destroy between the
        // ToArray snapshot and the OrderBy key-selector evaluation. The outer
        // DisposeAsync timer-drain handles the primary shutdown race.
        var wireStack = Wires.ToArray()
            .Where(wire => wire != null)
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
        _rechokeTimer = new Timer(_ =>
        {
            // Belt-and-suspenders: a callback that races with disposal should
            // never escape to the thread-pool unhandled-exception handler.
            try { Rechoke(); }
            catch { /* Shutdown race — callback will not fire again after DisposeAsync. */ }
        }, null, RechokeInterval, RechokeInterval);
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
        // Try the sync piece-layer path first (zero I/O; fast-path for the common case).
        var syncPayload = TryBuildV2HashesPayload(req);
        if (syncPayload != null)
        {
            SendHashesReply(wire, req, syncPayload.Value);
            return;
        }

        // Sync returned null. Either the request is definitely refusable (wrong base_layer,
        // unknown root, malformed params, bitfield shows we don't have the pieces needed)
        // or it's a leaf-level request we might be able to serve by re-hashing from the
        // store. Precheck SYNC conditions that would guarantee the leaf-level async path
        // also fails - reject immediately rather than race-pending a fire-and-forget reject.
        if (!CanPossiblyServeLeafLevel(req))
        {
            _ = wire.SendHashReject(new Bep52WireMessages.HashReject(
                req.PiecesRoot, req.BaseLayer, req.Index, req.Length, req.ProofLayers));
            return;
        }

        // Leaf-level path: need store I/O. Fire-and-forget so we don't block the wire
        // event thread.
        _ = Task.Run(async () =>
        {
            try
            {
                var asyncPayload = await TryBuildV2HashesPayloadAsync(req);
                if (asyncPayload != null)
                {
                    SendHashesReply(wire, req, asyncPayload.Value);
                }
                else
                {
                    _ = wire.SendHashReject(new Bep52WireMessages.HashReject(
                        req.PiecesRoot, req.BaseLayer, req.Index, req.Length, req.ProofLayers));
                }
            }
            catch
            {
                _ = wire.SendHashReject(new Bep52WireMessages.HashReject(
                    req.PiecesRoot, req.BaseLayer, req.Index, req.Length, req.ProofLayers));
            }
        });
    }

    /// <summary>
    /// Sync gate for whether the async leaf-level path might succeed. Returns false when
    /// any precondition visible in sync state is already unmet - so the wire thread sends
    /// hash_reject immediately instead of race-pending it behind a fire-and-forget Task.
    /// </summary>
    private bool CanPossiblyServeLeafLevel(Bep52WireMessages.HashRequest req)
    {
        if (req.BaseLayer != 0) return false;
        if (_store == null) return false;
        if (PieceLength < MerkleHasher.LeafSize || PieceLength % MerkleHasher.LeafSize != 0) return false;
        if (Files == null || FileRoots == null || FileRoots.Length == 0) return false;

        int fileIndex = -1;
        for (int i = 0; i < FileRoots.Length; i++)
        {
            if (FileRoots[i].AsSpan().SequenceEqual(req.PiecesRoot))
            {
                fileIndex = i;
                break;
            }
        }
        if (fileIndex < 0 || fileIndex >= Files.Length) return false;

        var file = Files[fileIndex];
        if (file.Length == 0) return false;

        // We must have every piece of this file (can't re-hash what we don't have).
        int fileStartGlobalPiece = (int)(file.Offset / PieceLength);
        int filePieceCount = (int)((file.Length + PieceLength - 1) / PieceLength);
        for (int pi = 0; pi < filePieceCount; pi++)
        {
            int gp = fileStartGlobalPiece + pi;
            if (gp < 0 || gp >= Bitfield.Length || !Bitfield[gp]) return false;
        }
        return true;
    }

    private static void SendHashesReply(Wire wire, Bep52WireMessages.HashRequest req, (byte[][] baseLayer, byte[][] proof) payload)
    {
        var (baseLayer, proof) = payload;
        var hashList = new byte[baseLayer.Length + proof.Length][];
        Array.Copy(baseLayer, 0, hashList, 0, baseLayer.Length);
        Array.Copy(proof, 0, hashList, baseLayer.Length, proof.Length);
        _ = wire.SendHashes(new Bep52WireMessages.Hashes(
            req.PiecesRoot, req.BaseLayer, req.Index, req.Length, req.ProofLayers, hashList));
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
    /// Async companion to <see cref="TryBuildV2HashesPayload"/>. Handles base_layer == 0
    /// (leaf-level) requests by re-hashing 16 KiB leaves from the chunk store, building
    /// the file's full leaf layer, and delegating to <see cref="MerkleProofBuilder"/>.
    /// Also handles the piece-layer case (so callers can use this single async method
    /// uniformly if they don't need the sync fast-path).
    ///
    /// Returns <c>null</c> when the request is malformed, the file root is unknown, the
    /// level is neither 0 nor the piece-layer level, or any of the pieces needed for the
    /// requested leaf range haven't been downloaded yet (can't re-hash what we don't have).
    /// </summary>
    internal async Task<(byte[][] baseLayer, byte[][] proof)?> TryBuildV2HashesPayloadAsync(
        Bep52WireMessages.HashRequest req)
    {
        if (PieceLength < MerkleHasher.LeafSize || PieceLength % MerkleHasher.LeafSize != 0) return null;
        int leavesPerPiece = PieceLength / MerkleHasher.LeafSize;
        if (leavesPerPiece < 1 || (leavesPerPiece & (leavesPerPiece - 1)) != 0) return null;
        int pieceLayerLevel = IntLog2(leavesPerPiece);

        if ((int)req.BaseLayer == pieceLayerLevel)
        {
            // Piece-layer path: zero I/O, delegate straight through.
            return TryBuildV2HashesPayload(req);
        }
        if ((int)req.BaseLayer != 0)
        {
            // Levels between 0 and pieceLayerLevel require re-combining leaves internally;
            // valid in principle but not requested in practice. Refuse politely.
            return null;
        }

        // Leaf-level: find the file whose root matches the request, read every piece we
        // need, hash each 16 KiB leaf, and build the full leaf layer.
        if (_store == null) return null;
        if (Files == null || FileRoots == null || FileRoots.Length == 0) return null;

        int fileIndex = -1;
        for (int i = 0; i < FileRoots.Length; i++)
        {
            if (FileRoots[i].AsSpan().SequenceEqual(req.PiecesRoot))
            {
                fileIndex = i;
                break;
            }
        }
        if (fileIndex < 0 || fileIndex >= Files.Length) return null;

        var file = Files[fileIndex];
        if (file.Length == 0) return null; // empty file has no leaves

        int fileLeafCount = (int)((file.Length + MerkleHasher.LeafSize - 1) / MerkleHasher.LeafSize);
        if (fileLeafCount == 0) return null;

        // File's first piece in the (possibly padded) global piece stream.
        int fileStartGlobalPiece = (int)(file.Offset / PieceLength);
        int filePieceCount = (int)((file.Length + PieceLength - 1) / PieceLength);

        // Must have every piece of this file to serve leaf hashes - can't re-hash what
        // we don't have. Shortcut: verify up-front before doing any I/O.
        for (int pi = 0; pi < filePieceCount; pi++)
        {
            int gp = fileStartGlobalPiece + pi;
            if (gp < 0 || gp >= Bitfield.Length || !Bitfield[gp]) return null;
        }

        // Read each piece and hash its 16 KiB leaves. Leaves 0..fileLeafCount-1 are REAL;
        // MerkleProofBuilder handles padding to next-pow-2 at level 0 with PadHashAtLevel(0).
        var leafLayer = new byte[fileLeafCount][];
        for (int pi = 0; pi < filePieceCount; pi++)
        {
            int gp = fileStartGlobalPiece + pi;
            var pieceData = await _store.GetAsync(gp);
            if (pieceData == null) return null;

            int pieceStartLeaf = pi * leavesPerPiece;
            int pieceEndLeaf = Math.Min(pieceStartLeaf + leavesPerPiece, fileLeafCount);
            for (int lj = pieceStartLeaf; lj < pieceEndLeaf; lj++)
            {
                int offsetInPiece = (lj - pieceStartLeaf) * MerkleHasher.LeafSize;
                int leafBytes = Math.Min(MerkleHasher.LeafSize, pieceData.Length - offsetInPiece);
                if (leafBytes <= 0) break;
                leafLayer[lj] = MerkleHasher.HashLeaf(pieceData.AsSpan(offsetInPiece, leafBytes));
            }
        }

        return MerkleProofBuilder.Build(
            leafLayer,
            baseLayerLevel: 0,
            index: req.Index,
            length: (int)req.Length,
            proofLayers: (int)req.ProofLayers,
            expectedRoot: req.PiecesRoot);
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
        // Routed through WebTorrentClient.PieceHashEngine so a GPU-backed
        // engine can intercept the hot path (default = SystemCryptoPieceHashEngine
        // which calls System.Security.Cryptography directly - byte-identical to
        // the legacy code).
        var engine = _client?.PieceHashEngine ?? Torrent._defaultEngine;
        var actual = expected.Length == 32 ? engine.Sha256(buf) : engine.Sha1(buf);
        return actual.SequenceEqual(expected);
    }
}
