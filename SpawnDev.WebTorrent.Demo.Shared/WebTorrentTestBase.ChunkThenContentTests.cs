using SpawnDev.UnitTesting;
using SpawnDev.WebTorrent.Storage;

namespace SpawnDev.WebTorrent.Demo.Shared;

/// <summary>
/// <see cref="ChunkThenContentStore"/> - download into piece files, unpack once into content files.
/// </summary>
/// <remarks>
/// <para>
/// 🔴 THE PROPERTY UNDER TEST IS WHERE THE BYTES ARE, not merely that reads work. A store that wrote
/// straight into content files the whole time would pass every round-trip assertion while defeating the
/// entire point, which is that downloading never needs a ranged in-place write. So these assert the
/// FILES ON DISK before and after the unpack, not just what the store hands back.
/// </para>
/// <para>
/// ⚠️ These run in the WINDOW, where <c>createSyncAccessHandle</c> does not exist. That is deliberate:
/// it is the scope the design exists to serve, and if piece-file downloading works here it works
/// everywhere.
/// </para>
/// </remarks>
public abstract partial class WebTorrentTestBase
{
    private const int CtcPiece = 16384;

    private (ChunkThenContentStore Store, string PieceBase, string ContentBase) MakeChunkThenContent(
        string name, params (string Path, long Length)[] files)
    {
        var fs = Client.AsyncFileSystem
            ?? throw new UnsupportedTestException("no AsyncFileSystem on the shared client");

        long offset = 0;
        var infos = new TorrentFileInfo[files.Length];
        for (int i = 0; i < files.Length; i++)
        {
            infos[i] = new TorrentFileInfo
            {
                Name = files[i].Path.Split('/')[^1],
                Path = files[i].Path,
                Length = files[i].Length,
                Offset = offset,
            };
            offset += files[i].Length;
        }
        var pieceBase = $"webtorrent/_test-ctc-{name}";
        var contentBase = $"webtorrent-files/_test-ctc-{name}";
        var pieceCount = (int)((offset + CtcPiece - 1) / CtcPiece);
        return (new ChunkThenContentStore(fs, pieceBase, contentBase, CtcPiece, infos, offset, pieceCount),
            pieceBase, contentBase);
    }

    /// <summary>Fills a store with deterministic pieces and returns what was written.</summary>
    private async Task<Dictionary<int, byte[]>> FillAsync(ChunkThenContentStore store, long total, int seed)
    {
        var written = new Dictionary<int, byte[]>();
        var pieces = (int)((total + CtcPiece - 1) / CtcPiece);
        for (int p = 0; p < pieces; p++)
        {
            int len = (int)Math.Min(CtcPiece, total - (long)p * CtcPiece);
            var data = MakeDeterministicData(len, seed: seed + p);
            written[p] = data;
            await store.PutAsync(p, data);
        }
        return written;
    }

    private async Task<bool> DirExistsAsync(string path)
    {
        var fs = Client.AsyncFileSystem!;
        try { return await fs.DirectoryExists(path); } catch { return false; }
    }

    /// <summary>
    /// Before the unpack the bytes are in PIECE files; after it they are in the torrent's real content
    /// files, the piece files are reclaimed, and every piece still reads back byte for byte.
    /// </summary>
    [TestMethod]
    public async Task ChunkThenContent_DownloadsToPieceFiles_ThenUnpacksToContentFiles()
    {
        if (!OperatingSystem.IsBrowser())
            throw new UnsupportedTestException("OPFS store (browser only)");

        long aLen = CtcPiece * 2 + 3000;          // deliberately not piece-aligned, so a piece straddles
        long bLen = CtcPiece + 1000;
        var (store, pieceBase, contentBase) = MakeChunkThenContent("unpack",
            ("pack/a.bin", aLen), ("pack/b.bin", bLen));
        var fs = Client.AsyncFileSystem!;
        try
        {
            var written = await FillAsync(store, aLen + bLen, seed: 900);

            // --- DOWNLOAD PHASE: piece files exist, content files do NOT ---
            if (store.IsUnpacked)
                throw new Exception("store reported unpacked before UnpackAsync was ever called");
            if (!await fs.FileExists($"{pieceBase}/piece_0"))
                throw new Exception(
                    "piece_0 is not on disk during download - the whole point is that a download writes "
                    + "one file per piece and never needs a ranged in-place write");
            if (await fs.FileExists($"{contentBase}/files/pack/a.bin"))
                throw new Exception(
                    "a content file exists during the download phase - bytes went straight into shared "
                    + "content files, which is exactly the ranged in-place write this design avoids");

            // --- UNPACK ---
            var copied = await store.UnpackAsync();
            if (copied != written.Count)
                throw new Exception($"unpack copied {copied} pieces, expected {written.Count}");
            if (!store.IsUnpacked) throw new Exception("unpack finished but IsUnpacked is still false");

            // --- AFTER: real files on disk, piece files reclaimed ---
            var aBytes = await fs.ReadBytes($"{contentBase}/files/pack/a.bin");
            if (aBytes == null || aBytes.Length != aLen)
                throw new Exception($"a.bin is {aBytes?.Length.ToString() ?? "missing"}, expected {aLen}");
            if (await fs.FileExists($"{pieceBase}/piece_0"))
                throw new Exception("piece_0 survived the unpack - the cache now holds two copies");

            // --- and the data is still correct, read through the store ---
            foreach (var (index, expected) in written)
            {
                var back = await store.GetAsync(index);
                if (back == null || !back.SequenceEqual(expected))
                    throw new Exception($"piece {index} did not read back correctly after the unpack");
            }
        }
        finally
        {
            await store.DisposeAsync();
            await DropStoreDir(pieceBase);
            await DropStoreDir(contentBase);
        }
    }

    /// <summary>
    /// A store opened over an ALREADY unpacked torrent comes up reading content files, and does not
    /// report its pieces missing.
    /// </summary>
    /// <remarks>
    /// 🔴 THE REGRESSION THIS EXISTS FOR is a cached torrent re-downloading itself. After an unpack a
    /// piece has no file of its own, so a store that decided "no piece files, therefore nothing cached"
    /// would discard a complete multi-GB torrent on every reload.
    /// </remarks>
    [TestMethod]
    public async Task ChunkThenContent_ReopenAfterUnpack_ReadsFromContentFiles()
    {
        if (!OperatingSystem.IsBrowser())
            throw new UnsupportedTestException("OPFS store (browser only)");

        long total = CtcPiece * 3;
        var (store, pieceBase, contentBase) = MakeChunkThenContent("reopen", ("solo.bin", total));
        Dictionary<int, byte[]> written;
        try
        {
            written = await FillAsync(store, total, seed: 950);
            await store.UnpackAsync();
        }
        finally { await store.DisposeAsync(); }

        // A SECOND store over the same roots - the reload case.
        var (reopened, _, _) = MakeChunkThenContent("reopen", ("solo.bin", total));
        try
        {
            foreach (var (index, expected) in written)
            {
                if (!await reopened.PieceExistsAsync(index))
                    throw new Exception(
                        $"piece {index} reported MISSING after reopen - a complete torrent would re-download");
                var back = await reopened.GetAsync(index);
                if (back == null || !back.SequenceEqual(expected))
                    throw new Exception($"piece {index} did not read back correctly after reopen");
            }
            if (!reopened.IsUnpacked)
                throw new Exception("reopened store did not recognise it was already unpacked");

            // Idempotent: nothing left to do, and it must not copy anything a second time.
            if (await reopened.UnpackAsync() != 0)
                throw new Exception("a second unpack copied pieces - it is not idempotent");
        }
        finally
        {
            await reopened.DisposeAsync();
            await DropStoreDir(pieceBase);
            await DropStoreDir(contentBase);
        }
    }
}
