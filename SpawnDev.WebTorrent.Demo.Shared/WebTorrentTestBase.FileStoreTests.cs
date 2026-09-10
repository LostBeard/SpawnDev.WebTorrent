using SpawnDev.UnitTesting;
using SpawnDev.WebTorrent;
using SpawnDev.WebTorrent.Storage;

namespace SpawnDev.WebTorrent.Demo.Shared;

/// <summary>
/// <see cref="AsyncFSFileStore"/> - the layout that writes a torrent's files as actual files.
/// </summary>
/// <remarks>
/// <para>
/// 🔴 THE RISK THIS COVERS IS THE MAPPING, not the I/O. Under the piece layout a piece is a whole file, so
/// a mapping bug is impossible. Here every piece is written INTO shared content files at computed offsets,
/// and a piece that straddles a file boundary is split across two of them - so an off-by-one in
/// <c>Spans</c> silently corrupts a neighbouring file's bytes rather than failing. Every test below reads
/// back and compares against the exact bytes written.
/// </para>
/// <para>
/// ⚠️ These run in the WINDOW, where <c>createSyncAccessHandle</c> does not exist (measured 2026-09-09:
/// it is dedicated-worker only). So they exercise the <c>createWritable</c> + <c>getFile</c>+<c>slice</c>
/// FALLBACK, which is the path a shared-worker or main-thread host really takes - worth covering on its
/// own. The sync-handle path is covered by the dedicated-worker probe in SpawnDev.AI's suite.
/// </para>
/// </remarks>
public abstract partial class WebTorrentTestBase
{
    private const int FsPiece = 16384;

    /// <summary>A store over a throwaway directory, plus the files it was built from.</summary>
    private (AsyncFSFileStore Store, TorrentFileInfo[] Files, string Base) MakeFileStore(
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
        var basePath = $"webtorrent-files/_test-{name}";
        return (new AsyncFSFileStore(fs, basePath, FsPiece, infos, offset), infos, basePath);
    }

    private async Task DropStoreDir(string basePath)
    {
        try
        {
            var fs = Client.AsyncFileSystem;
            if (fs != null && await fs.DirectoryExists(basePath)) await fs.Remove(basePath, recursive: true);
        }
        catch { /* a leftover test dir is noise, not a failure */ }
    }

    /// <summary>A single-file torrent round-trips every piece byte for byte.</summary>
    [TestMethod]
    public async Task FileStore_SingleFile_RoundTrips()
    {
        if (!OperatingSystem.IsBrowser())
            throw new UnsupportedTestException("AsyncFSFileStore is an OPFS store (browser only)");

        // 3.5 pieces, so the LAST piece is short - the case a fixed piece length gets wrong.
        long total = FsPiece * 3 + 5000;
        var (store, _, basePath) = MakeFileStore("single", ("model.gguf", total));
        try
        {
            var written = new Dictionary<int, byte[]>();
            for (int p = 0; p < 4; p++)
            {
                int len = (int)Math.Min(FsPiece, total - (long)p * FsPiece);
                var data = MakeDeterministicData(len, seed: 700 + p);
                written[p] = data;
                await store.PutAsync(p, data);
            }

            for (int p = 0; p < 4; p++)
            {
                var back = await store.GetAsync(p);
                if (back == null) throw new Exception($"piece {p} read back null");
                if (!back.SequenceEqual(written[p]))
                    throw new Exception($"piece {p} differs: wrote {written[p].Length} B, read {back.Length} B");
            }

            // The last piece must be SHORT, not padded to the piece length.
            var last = await store.GetAsync(3);
            if (last!.Length != 5000) throw new Exception($"last piece is {last.Length} B, expected 5000");
        }
        finally { await store.DisposeAsync(); await DropStoreDir(basePath); }
    }

    /// <summary>
    /// A piece that straddles a file boundary lands in BOTH files, at the right offsets.
    /// </summary>
    /// <remarks>
    /// 🔴 The test that would have caught a wrong <c>Spans</c>. File A is deliberately NOT a multiple of
    /// the piece length, so piece 1 is split - 8000 bytes into the tail of A and 8384 into the head of B.
    /// Reading the FILES back (rather than the pieces) is what proves the split went where it claims: a
    /// mapping that wrote both halves into one file would still round-trip through the piece API.
    /// </remarks>
    [TestMethod]
    public async Task FileStore_PieceSpanningTwoFiles_LandsInBoth()
    {
        if (!OperatingSystem.IsBrowser())
            throw new UnsupportedTestException("AsyncFSFileStore is an OPFS store (browser only)");

        const long aLen = FsPiece + 8000;      // 24384 - piece 1 starts 8000 B before the end of A
        const long bLen = FsPiece * 2;
        var (store, files, basePath) = MakeFileStore("span", ("pack/a.bin", aLen), ("pack/b.bin", bLen));
        try
        {
            long total = aLen + bLen;
            int pieces = (int)((total + FsPiece - 1) / FsPiece);
            var written = new Dictionary<int, byte[]>();
            for (int p = 0; p < pieces; p++)
            {
                int len = (int)Math.Min(FsPiece, total - (long)p * FsPiece);
                var data = MakeDeterministicData(len, seed: 800 + p);
                written[p] = data;
                await store.PutAsync(p, data);
            }
            await store.SaveBitfieldAsync();

            // Piece 1 covers [16384, 32768): 8000 B in A (at 16384) and 8384 B in B (at 0).
            var fs = Client.AsyncFileSystem!;
            var aBytes = await fs.ReadBytes($"{basePath}/files/pack/a.bin");
            var bBytes = await fs.ReadBytes($"{basePath}/files/pack/b.bin");
            if (aBytes == null || aBytes.Length != aLen)
                throw new Exception($"a.bin is {aBytes?.Length.ToString() ?? "missing"}, expected {aLen}");
            if (bBytes == null || bBytes.Length != bLen)
                throw new Exception($"b.bin is {bBytes?.Length.ToString() ?? "missing"}, expected {bLen}");

            var p1 = written[1];
            if (!aBytes[16384..24384].SequenceEqual(p1[..8000]))
                throw new Exception("the first 8000 bytes of piece 1 did not land at offset 16384 of a.bin");
            if (!bBytes[..8384].SequenceEqual(p1[8000..]))
                throw new Exception("the remaining 8384 bytes of piece 1 did not land at offset 0 of b.bin");

            // ...and the piece still reads back whole through the store.
            var back = await store.GetAsync(1);
            if (back == null || !back.SequenceEqual(p1))
                throw new Exception("piece 1 did not read back as written across the boundary");
        }
        finally { await store.DisposeAsync(); await DropStoreDir(basePath); }
    }

    /// <summary>Writing one piece must not disturb its neighbours in the same file.</summary>
    /// <remarks>
    /// 🔴 RED-CHECKED AGAINST A REAL DEFECT. The first version of the fallback write path called
    /// <c>IAsyncFS.Write(path, bytes)</c>, which REPLACES a file - so storing piece 2 destroyed pieces 0
    /// and 1 and left a file of the wrong length. Writing out of order and re-reading everything is what
    /// exposes that; a sequential write-then-read would have passed.
    /// </remarks>
    [TestMethod]
    public async Task FileStore_OutOfOrderWrites_DoNotClobber()
    {
        if (!OperatingSystem.IsBrowser())
            throw new UnsupportedTestException("AsyncFSFileStore is an OPFS store (browser only)");

        long total = FsPiece * 4;
        var (store, _, basePath) = MakeFileStore("order", ("one.bin", total));
        try
        {
            var written = new Dictionary<int, byte[]>();
            foreach (var p in new[] { 3, 0, 2, 1 })          // deliberately not sequential
            {
                var data = MakeDeterministicData(FsPiece, seed: 900 + p);
                written[p] = data;
                await store.PutAsync(p, data);
            }

            for (int p = 0; p < 4; p++)
            {
                var back = await store.GetAsync(p);
                if (back == null) throw new Exception($"piece {p} read back null after out-of-order writes");
                if (!back.SequenceEqual(written[p]))
                    throw new Exception($"piece {p} was clobbered by a later write to the same file");
            }
        }
        finally { await store.DisposeAsync(); await DropStoreDir(basePath); }
    }

    /// <summary>The bitfield survives a new store over the same directory.</summary>
    /// <remarks>
    /// 🔴 THE PROPERTY RESTORE DEPENDS ON. Under the piece layout a restored torrent rebuilds its bitfield
    /// by testing whether each piece FILE exists; here a piece has no file of its own, so if this does not
    /// persist, a fully cached model reports zero pieces and re-downloads every time.
    /// </remarks>
    [TestMethod]
    public async Task FileStore_Bitfield_SurvivesReopen()
    {
        if (!OperatingSystem.IsBrowser())
            throw new UnsupportedTestException("AsyncFSFileStore is an OPFS store (browser only)");

        long total = FsPiece * 4;
        var (store, files, basePath) = MakeFileStore("bits", ("one.bin", total));
        byte[] piece0, piece2;
        try
        {
            piece0 = MakeDeterministicData(FsPiece, seed: 1000);
            piece2 = MakeDeterministicData(FsPiece, seed: 1001);
            await store.PutAsync(0, piece0);
            await store.PutAsync(2, piece2);
            await store.SaveBitfieldAsync();
        }
        finally { await store.DisposeAsync(); }

        var fs = Client.AsyncFileSystem!;
        var reopened = new AsyncFSFileStore(fs, basePath, FsPiece, files, total);
        try
        {
            if (!await reopened.PieceExistsAsync(0)) throw new Exception("piece 0 was not remembered");
            if (await reopened.PieceExistsAsync(1)) throw new Exception("piece 1 was never stored but is claimed");
            if (!await reopened.PieceExistsAsync(2)) throw new Exception("piece 2 was not remembered");
            if (await reopened.PieceExistsAsync(3)) throw new Exception("piece 3 was never stored but is claimed");

            // ⚠️ BOTH pieces, and piece 0 especially. A red-check with KeepExistingData disabled left this
            // test green while the other three went red, because it only asserted BITS - piece 0's bytes had
            // been zeroed by the later write to piece 2 and nothing looked at them.
            var back0 = await reopened.GetAsync(0);
            if (back0 == null || !back0.SequenceEqual(piece0))
                throw new Exception("piece 0 was remembered but its bytes did not survive the write to piece 2");
            var back = await reopened.GetAsync(2);
            if (back == null || !back.SequenceEqual(piece2))
                throw new Exception("a remembered piece did not read back from the reopened store");

            // A piece the store does not claim must read as null, not as zeros.
            if (await reopened.GetAsync(1) != null)
                throw new Exception("an unstored piece returned data instead of null - the store would "
                    + "serve zeros as if they were content");
        }
        finally { await reopened.DisposeAsync(); await DropStoreDir(basePath); }
    }

    /// <summary>A store built on a non-browser filesystem refuses, rather than truncating files.</summary>
    /// <remarks>
    /// <para>
    /// ⚠️ Guards the fix for a real defect: the generic <c>IAsyncFS.Write</c> replaces a whole file, so a
    /// non-browser filesystem would have destroyed every previously stored piece on each new write. This
    /// asserts the store refuses at CONSTRUCTION, where the damage is still zero.
    /// </para>
    /// <para>
    /// 🔴 IT SUPPLIES ITS OWN FILESYSTEM, and the first version did not. That one asked
    /// <c>Client.AsyncFileSystem</c> for a NON-browser filesystem, which meant it skipped on the browser
    /// lane (the filesystem is a browser one) and skipped on the desktop lane (no filesystem on the shared
    /// client) - reported as "Skipped" on both, i.e. a test that could never fail. A stub that is
    /// <see cref="SpawnDev.AsyncFileSystem.IAsyncFS"/> and nothing more runs the guard on every lane.
    /// </para>
    /// </remarks>
    [TestMethod]
    public Task FileStore_NonBrowserFilesystem_Refused()
    {
        var fs = new NotABrowserFs();
        var files = new[] { new TorrentFileInfo { Name = "a", Path = "a", Length = FsPiece, Offset = 0 } };
        try
        {
            _ = new AsyncFSFileStore(fs, "webtorrent-files/_test-refuse", FsPiece, files, FsPiece);
        }
        catch (NotSupportedException)
        {
            return Task.CompletedTask;
        }
        throw new Exception("AsyncFSFileStore accepted a non-browser filesystem; ranged writes are "
            + "impossible there and every write would replace the whole file");
    }

    /// <summary>
    /// An <see cref="SpawnDev.AsyncFileSystem.IAsyncFS"/> that is deliberately NOT an
    /// <see cref="SpawnDev.AsyncFileSystem.IAsyncBrowserFileSystem"/>.
    /// </summary>
    /// <remarks>
    /// Every member throws: the store under test must refuse in its CONSTRUCTOR, so reaching any of these
    /// is itself the failure this fixture is looking for.
    /// </remarks>
    private sealed class NotABrowserFs : SpawnDev.AsyncFileSystem.IAsyncFS
    {
        private static Exception Nope([System.Runtime.CompilerServices.CallerMemberName] string m = "")
            => new Exception($"NotABrowserFs.{m} was called - AsyncFSFileStore should have refused this "
                + "filesystem at construction, before touching it");

        public event EventHandler<SpawnDev.AsyncFileSystem.FileSystemChangeEventArgs> FileSystemChanged
        { add { } remove { } }

        public Task Append(string path, Stream data) => throw Nope();
        public Task Append(string path, string data) => throw Nope();
        public Task Append(string path, byte[] data) => throw Nope();
        public Task CreateDirectory(string path) => throw Nope();
        public Task<bool> DirectoryExists(string path) => throw Nope();
        public Task<bool> Exists(string path) => throw Nope();
        public Task<bool> FileExists(string path) => throw Nope();
        public Task<List<string>> GetDirectories(string path) => throw Nope();
        public Task<List<string>> GetEntries(string path) => throw Nope();
        public Task<List<string>> GetFiles(string path) => throw Nope();
        public Task<SpawnDev.AsyncFileSystem.ASyncFSEntryInfo?> GetInfo(string path) => throw Nope();
        public Task<List<SpawnDev.AsyncFileSystem.ASyncFSEntryInfo>> GetInfos(string path, bool recursive = false)
            => throw Nope();
        public IAsyncEnumerable<SpawnDev.AsyncFileSystem.ASyncFSEntryInfo> EnumerateInfos(string path, bool recursive = false)
            => throw Nope();
        public Task<byte[]> ReadBytes(string path) => throw Nope();
        public Task<T> ReadJSON<T>(string path, System.Text.Json.JsonSerializerOptions? o = null) => throw Nope();
        public Task<Stream> ReadStream(string path) => throw Nope();
        public Task<string> ReadText(string path) => throw Nope();
        public Task Remove(string path, bool recursive = false) => throw Nope();
        public Task Write(string path, Stream data) => throw Nope();
        public Task Write(string path, string data) => throw Nope();
        public Task Write(string path, byte[] data) => throw Nope();
        public Task WriteJSON(string path, object data, System.Text.Json.JsonSerializerOptions? o = null) => throw Nope();
        public Task<Stream> GetWriteStream(string path) => throw Nope();
        public Task<Stream> GetReadStream(string path) => throw Nope();
    }
}
