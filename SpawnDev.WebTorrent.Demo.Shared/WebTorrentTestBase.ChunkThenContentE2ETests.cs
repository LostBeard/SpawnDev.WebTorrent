using SpawnDev.UnitTesting;
using SpawnDev.WebTorrent.Storage;

namespace SpawnDev.WebTorrent.Demo.Shared;

/// <summary>
/// The ContentFiles layout END TO END, through a real <see cref="WebTorrentClient"/> rather than a store
/// built by hand.
/// </summary>
/// <remarks>
/// <para>
/// 🔴 WHAT THIS COVERS THAT THE STORE TESTS DO NOT: the WIRING. The store tests construct a
/// <see cref="ChunkThenContentStore"/> directly, so they prove nothing about whether a Torrent actually
/// builds one, whether completion actually starts an unpack, or whether the unpack actually finishes.
/// Every one of those is a separate place the feature can be silently absent.
/// </para>
/// <para>
/// ⚠️ EVERY TORRENT HERE GETS A UNIQUE NAME, hence a unique infohash, hence its own store roots. The
/// unpack RECLAIMS <c>webtorrent/{key}</c>, which is the same directory the default piece-file layout
/// uses - so a test sharing a torrent with another test would delete that test's cache. A unique
/// infohash is what makes this safe to run beside everything else.
/// </para>
/// </remarks>
public abstract partial class WebTorrentTestBase
{
    /// <summary>
    /// A client on the ContentFiles layout, backed by the shared OPFS filesystem.
    /// </summary>
    private WebTorrentClient MakeContentFilesClient() => new()
    {
        AsyncFileSystem = Client.AsyncFileSystem
            ?? throw new UnsupportedTestException("no AsyncFileSystem on the shared client"),
        StorageLayout = TorrentStorageLayout.ContentFiles,
    };

    /// <summary>
    /// Seeding bytes into a ContentFiles client must leave the torrent's REAL FILES on disk, with the
    /// piece files reclaimed - and the pieces must still read back correctly afterwards.
    /// </summary>
    [TestMethod(Timeout = 120000)]
    public async Task ContentFiles_SeedThenUnpack_LeavesRealFilesOnDisk()
    {
        if (!OperatingSystem.IsBrowser())
            throw new UnsupportedTestException("ContentFiles is an OPFS layout (browser only)");

        var fs = Client.AsyncFileSystem!;
        var client = MakeContentFilesClient();
        Torrent? torrent = null;
        string? pieceBase = null, contentBase = null;
        try
        {
            // Unique name -> unique infohash -> store roots no other test shares.
            var name = $"ctc-e2e-{Guid.NewGuid():N}.bin";
            var data = MakeDeterministicData(100_000, seed: 4242);
            torrent = await client.SeedAsync(name, data);

            pieceBase = $"webtorrent/{torrent.PersistKey}";
            contentBase = $"webtorrent-files/{torrent.PersistKey}";

            // The wiring assertion: a ContentFiles torrent must be on the two-phase store at all.
            if (torrent.PieceCount <= 1)
                throw new Exception($"fixture is too small to be meaningful: {torrent.PieceCount} piece(s)");

            // Seeding completes without CheckDone, so the unpack is started from the seed path. Awaiting
            // the tracked task is what makes this deterministic rather than a sleep-and-hope.
            await torrent.UnpackTask;

            // If the unpack did not run at all, say so precisely rather than failing on a missing file.
            if (torrent.UnpackTask.IsCompleted && !await fs.DirectoryExists(contentBase))
                throw new Exception(
                    $"no content-file root at {contentBase} after seeding + unpack - either the torrent is "
                    + "not using ChunkThenContentStore, or the unpack never started");

            var written = await fs.ReadBytes($"{contentBase}/files/{name}");
            if (written == null)
                throw new Exception($"the torrent's real file is not on disk at {contentBase}/files/{name}");
            if (written.Length != data.Length || !written.SequenceEqual(data))
                throw new Exception(
                    $"the unpacked file is {written.Length} bytes and does not match the {data.Length} "
                    + "bytes seeded - the unpack reassembled the pieces wrongly");

            // Piece files reclaimed, so the cache is not holding two copies.
            if (await fs.FileExists($"{pieceBase}/piece_0"))
                throw new Exception("piece_0 survived the unpack - the cache now holds the torrent twice");

            // ...and the torrent still serves its content through the normal read path, now backed by the
            // content files rather than the piece files it downloaded into.
            var readBack = await torrent.ReadFileAsync(0);
            if (readBack == null || !readBack.SequenceEqual(data))
                throw new Exception(
                    $"ReadFileAsync returned {readBack?.Length.ToString() ?? "null"} bytes that do not match "
                    + $"the {data.Length} seeded - the torrent cannot serve its own data after unpacking");
        }
        finally
        {
            if (torrent != null) { try { await client.RemoveAsync(torrent); } catch { } }
            await client.DisposeAsync();
            if (pieceBase != null) await DropStoreDir(pieceBase);
            if (contentBase != null) await DropStoreDir(contentBase);
        }
    }
}
