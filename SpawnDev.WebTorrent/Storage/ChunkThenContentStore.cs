using SpawnDev.AsyncFileSystem;
using SpawnDev.SpawnJS.JSObjects;

namespace SpawnDev.WebTorrent.Storage;

/// <summary>
/// Downloads into piece files, then UNPACKS once into the torrent's real content files.
/// </summary>
/// <remarks>
/// <para>
/// 🔴 WHY BOTH, INSTEAD OF PICKING ONE. Writing pieces straight into shared content files needs a RANGED
/// IN-PLACE WRITE, and OPFS only offers that through <c>createSyncAccessHandle</c>, which exists solely
/// in a DedicatedWorkerGlobalScope. Everywhere else it falls back to <c>createWritable</c>, which is
/// whole-file. Piece files need no ranged write at all - every piece is its own file, written once, so
/// no two writes can ever touch the same bytes. Downloading into piece files therefore works in EVERY
/// scope with plain streams, and the awkward requirement disappears.
/// </para>
/// <para>
/// ⭐ MEASURED 2026-09-14 (OPFSStream, dedicated worker, constant 64 MiB) - this is what the trade
/// actually costs, and it is not what the original 22-33x suggested:
/// </para>
/// <code>
///   piece size | read pieces vs one file | unpack cost once | saved per later load | break-even
///       4 MiB  |         1.30x           |    ~2.9 s/GB     |     ~0.27 s/GB       | ~10 loads
///      64 KiB  |         4.26x           |   ~36.7 s/GB     |    ~23.5 s/GB        | ~1.6 loads
/// </code>
/// <para>
/// ⚠️ SO THE UNPACK IS NOT PRIMARILY A SPEED FEATURE at production piece sizes - reading piece files
/// costs only 1.30x at 4 MiB, and the unpack needs ~10 loads of the same torrent to repay itself. Its
/// real value is that it ends with the torrent's ACTUAL FILES on disk (seeding from files, the HTTP file
/// browser) while never once needing a ranged in-place write. Do not justify it with load times.
/// </para>
/// <para>
/// ⚠️ ONE BACKING IS ACTIVE AT A TIME, never a merge. Before the unpack every piece lives in the piece
/// store; after it, every piece lives in the content store. A read that had to ask both would be a read
/// that does not know where its data is.
/// </para>
/// <para>
/// ⚠️ INTERRUPTION IS SAFE BY ORDERING. <see cref="AsyncFSFileStore.MigrateFromAsync"/> COPIES, and the
/// piece files are reclaimed only after it reports every piece verified present in the content store.
/// An unpack killed halfway leaves both copies intact and simply runs again - which is why the reclaim
/// is all-or-nothing rather than per-piece.
/// </para>
/// </remarks>
public sealed class ChunkThenContentStore : IJSChunkStore
{
    private readonly AsyncFSChunkStore _pieces;
    private readonly AsyncFSFileStore _content;
    private readonly int _pieceCount;

    private bool _initialized;
    private Task? _initTask;

    /// <summary>True once every piece is in the content store and the piece files are gone.</summary>
    public bool IsUnpacked { get; private set; }

    /// <inheritdoc/>
    public int ChunkLength { get; }

    /// <summary>The store currently holding this torrent's bytes. Never both.</summary>
    private IJSChunkStore Active => IsUnpacked ? _content : _pieces;

    /// <param name="fs">Backing filesystem (OPFS in the browser).</param>
    /// <param name="pieceBase">Piece-file root, e.g. <c>webtorrent/{key}</c>.</param>
    /// <param name="contentBase">Content-file root, e.g. <c>webtorrent-files/{key}</c>.</param>
    /// <param name="chunkLength">Torrent piece length.</param>
    /// <param name="files">The torrent's files, with offsets into the concatenated stream.</param>
    /// <param name="totalLength">Sum of every file's length.</param>
    /// <param name="pieceCount">Pieces in the torrent.</param>
    public ChunkThenContentStore(IAsyncFS fs, string pieceBase, string contentBase, int chunkLength,
        TorrentFileInfo[] files, long totalLength, int pieceCount)
    {
        ChunkLength = chunkLength;
        _pieceCount = pieceCount;
        _pieces = new AsyncFSChunkStore(fs, pieceBase, chunkLength);
        _content = new AsyncFSFileStore(fs, contentBase, chunkLength, files, totalLength);
    }

    /// <summary>
    /// Decides which backing is live, ONCE, by asking the content store what it already holds.
    /// </summary>
    /// <remarks>
    /// ⚠️ A torrent that was unpacked in an earlier session must come back up reading CONTENT files, or it
    /// would look empty and re-download everything. The content bitfield is the only record of that - a
    /// piece has no file of its own in that layout, so there is nothing else to test.
    /// </remarks>
    private Task EnsureInitializedAsync()
    {
        if (_initialized) return Task.CompletedTask;
        return _initTask ??= RunAsync();

        async Task RunAsync()
        {
            var bits = await _content.LoadBitfieldAsync().ConfigureAwait(false);
            IsUnpacked = _pieceCount > 0 && bits.Length >= _pieceCount
                && !bits.Take(_pieceCount).Contains(false);
            _initialized = true;
        }
    }

    /// <summary>
    /// Copy every piece into the torrent's content files, then reclaim the piece files.
    /// </summary>
    /// <remarks>
    /// Safe and cheap to call repeatedly: already unpacked returns immediately, and a partial run resumes
    /// because <see cref="AsyncFSFileStore.MigrateFromAsync"/> skips pieces already present.
    /// <para>
    /// ⚠️ Call it AFTER the torrent reports done, not from inside the write that completes it. The unpack
    /// moves the whole torrent (~2.9 s/GB measured), and burying that in a piece write would stall the
    /// download loop and delay the completion event by seconds with nothing explaining why.
    /// </para>
    /// </remarks>
    /// <param name="progress">Called with (copied, total).</param>
    /// <param name="ct">Cancellation. An interrupted unpack leaves both copies intact.</param>
    /// <returns>Pieces copied. Zero when there was nothing to do.</returns>
    public async Task<int> UnpackAsync(Action<int, int>? progress = null, CancellationToken ct = default)
    {
        await EnsureInitializedAsync().ConfigureAwait(false);
        if (IsUnpacked) return 0;

        var (copied, covered) = await _content.MigrateFromAsync(_pieces, _pieceCount, progress, ct)
            .ConfigureAwait(false);

        // 🔴 FLIP ONLY ON `covered`. That flag means every piece the source claimed was verified readable
        // from the content store. Flipping on `copied > 0` instead would point reads at a content store
        // that is missing pieces, while the piece files that still hold them are no longer consulted.
        if (!covered) return copied;

        IsUnpacked = true;
        try
        {
            await _pieces.ClearAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Not fatal - the bytes are safely in the content store either way, this just costs quota.
            Console.WriteLine($"[ChunkThenContentStore] unpacked, but could not reclaim the piece files "
                + $"({ex.Message}) - they are now a redundant second copy using storage quota.");
        }
        return copied;
    }

    /// <inheritdoc/>
    public bool SupportsUint8Array => _content.SupportsUint8Array;

    /// <inheritdoc/>
    public async Task PutAsync(int index, ReadOnlyMemory<byte> data, CancellationToken ct = default)
    {
        await EnsureInitializedAsync().ConfigureAwait(false);
        await Active.PutAsync(index, data, ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task PutUint8ArrayAsync(int index, Uint8Array data, CancellationToken ct = default)
    {
        await EnsureInitializedAsync().ConfigureAwait(false);
        await Active.PutUint8ArrayAsync(index, data, ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<byte[]?> GetAsync(int index, CancellationToken ct = default)
    {
        await EnsureInitializedAsync().ConfigureAwait(false);
        return await Active.GetAsync(index, ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<byte[]?> GetAsync(int index, int offset, int length, CancellationToken ct = default)
    {
        await EnsureInitializedAsync().ConfigureAwait(false);
        return await Active.GetAsync(index, offset, length, ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<Uint8Array?> GetUint8ArrayAsync(int index, CancellationToken ct = default)
    {
        await EnsureInitializedAsync().ConfigureAwait(false);
        return await Active.GetUint8ArrayAsync(index, ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<Uint8Array?> GetUint8ArrayAsync(int index, int offset, int length, CancellationToken ct = default)
    {
        await EnsureInitializedAsync().ConfigureAwait(false);
        return await Active.GetUint8ArrayAsync(index, offset, length, ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<bool> PieceExistsAsync(int index, CancellationToken ct = default)
    {
        await EnsureInitializedAsync().ConfigureAwait(false);
        return await Active.PieceExistsAsync(index, ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<string> DescribePieceAsync(int index, CancellationToken ct = default)
    {
        await EnsureInitializedAsync().ConfigureAwait(false);
        var where = IsUnpacked ? "content files" : "piece files";
        return $"[{where}] " + await Active.DescribePieceAsync(index, ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task RemoveAsync(int index, CancellationToken ct = default)
    {
        await EnsureInitializedAsync().ConfigureAwait(false);
        await Active.RemoveAsync(index, ct).ConfigureAwait(false);
    }

    /// <summary>Removes BOTH backings - this torrent's data is being discarded entirely.</summary>
    /// <remarks>
    /// ⚠️ Clears both regardless of which is live. Clearing only the active one would leave the other as
    /// an orphaned copy of a torrent the caller believes they deleted.
    /// </remarks>
    public async Task ClearAsync(CancellationToken ct = default)
    {
        await _pieces.ClearAsync(ct).ConfigureAwait(false);
        await _content.ClearAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        await _pieces.DisposeAsync().ConfigureAwait(false);
        await _content.DisposeAsync().ConfigureAwait(false);
    }
}
