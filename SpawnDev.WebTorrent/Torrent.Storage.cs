namespace SpawnDev.WebTorrent;

public partial class Torrent
{
    /// <summary>
    /// Copy this torrent's cached pieces out of the old piece-per-file layout and into its
    /// content-file store, locally, instead of re-downloading them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// 🔴 WHY THIS IS NEEDED AT ALL. <see cref="TorrentStorageLayout.PieceFiles"/> and
    /// <see cref="TorrentStorageLayout.ContentFiles"/> use separate store roots on purpose - a
    /// <c>piece_0</c> read as content bytes is silent corruption - so turning the new layout on otherwise
    /// orphans every cached torrent. For a 7.1 GB model that is a 7.1 GB re-download in exchange for a
    /// faster load, which is the worst possible trade for whoever most wants the faster load. This is an
    /// OPFS-to-OPFS copy instead, and every piece moves as a JS buffer with no managed copy.
    /// </para>
    /// <para>
    /// ⚠️ Safe to call on anything: a torrent already on the piece layout, one with nothing cached, or one
    /// already migrated all return 0 without touching disk. Interrupting it leaves the store
    /// under-reporting (those pieces re-download) rather than claiming bytes it does not hold.
    /// </para>
    /// </remarks>
    /// <param name="progress">Called with (copied, total).</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>Pieces copied.</returns>
    public async Task<int> MigrateStorageLayoutAsync(Action<int, int>? progress = null,
        CancellationToken ct = default)
    {
        if (_store is not Storage.AsyncFSFileStore fileStore) return 0;
        var fs = _client?.AsyncFileSystem;
        if (fs == null || string.IsNullOrEmpty(PersistKey)) return 0;

        var oldBase = $"webtorrent/{PersistKey}";
        if (!await fs.DirectoryExists(oldBase).ConfigureAwait(false)) return 0;

        var old = new Storage.AsyncFSChunkStore(fs, oldBase, PieceLength);
        try
        {
            if (!old.SupportsUint8Array) return 0;
            var (copied, covered) = await fileStore.MigrateFromAsync(old, PieceCount, progress, ct)
                .ConfigureAwait(false);
            if (copied == 0) return 0;

            // The in-memory bitfield is what the download scheduler reads, so it has to learn what just
            // arrived - otherwise every migrated piece is immediately re-requested from the network.
            for (int i = 0; i < PieceCount; i++)
            {
                if (Bitfield[i]) continue;
                if (!await fileStore.PieceExistsAsync(i, ct).ConfigureAwait(false)) continue;
                Bitfield[i] = true;
                Pieces[i] = new Piece(0);
            }
            if (PieceCount > 0 && Bitfield.All(b => b)) Done = true;
            Console.WriteLine($"[Torrent] '{Name}': migrated {copied} piece(s) from {oldBase} into the "
                + "content-file layout - no re-download needed.");

            // 🔴 RECLAIM THE OLD COPY, or the cache DOUBLES. Migration copies rather than moves - it has to,
            // because a partial copy must be able to fall back - so leaving the piece files behind means a
            // 7 GB model occupies 14 GB of a quota it was already close to. Only after every piece the
            // source claimed is verified readable from the NEW store: an unverified delete would turn a
            // half-finished migration into permanent data loss, and re-downloading beats that.
            if (covered)
            {
                try
                {
                    await fs.Remove(oldBase, recursive: true).ConfigureAwait(false);
                    Console.WriteLine($"[Torrent] '{Name}': removed {oldBase} - every piece it held is now "
                        + "verified present in the content-file layout.");
                }
                catch (Exception ex)
                {
                    // Not fatal: the data is safely in the new layout either way, it just costs quota.
                    Console.WriteLine($"[Torrent] '{Name}': migrated, but could not remove {oldBase} "
                        + $"({ex.Message}) - it is now a redundant second copy using storage quota.");
                }
            }
            else
            {
                Console.WriteLine($"[Torrent] '{Name}': KEEPING {oldBase} - the migration did not carry "
                    + "every piece across, so the old copy is still the complete one.");
            }
            return copied;
        }
        finally { await old.DisposeAsync().ConfigureAwait(false); }
    }
}
