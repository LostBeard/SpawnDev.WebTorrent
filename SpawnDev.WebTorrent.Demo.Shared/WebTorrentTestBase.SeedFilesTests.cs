using System.Net;
using System.Security.Cryptography;
using SpawnDev.UnitTesting;
using SpawnDev.WebTorrent;

namespace SpawnDev.WebTorrent.Demo.Shared;

/// <summary>
/// Seeding from files on disk, streaming: a world can be gigabytes, and the in-memory <c>SeedAsync(name, (path, byte[])[])</c> needs all of it in memory twice.
/// Desktop-only: browser has no disk to stream from.
/// </summary>
public abstract partial class WebTorrentTestBase
{
    private static string TempDir(string prefix)
    {
        var dir = Path.Combine(Path.GetTempPath(), prefix + "_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        return dir;
    }

    [TestMethod]
    public async Task CreateFromFilePaths_MakesExactlyTheTorrentTheInMemoryCreatorMakes()
    {
        if (OperatingSystem.IsBrowser()) throw new UnsupportedTestException("no disk in the browser");
        var dir = TempDir("seedfiles_same");
        try
        {
            // sizes chosen to straddle piece boundaries: smaller than a piece, exactly a piece, a piece plus one, several pieces, and an empty file in the middle
            var sizes = new[] { 1, 16384, 16385, 100_003, 0, 70_000 };
            var inMemory = new List<(string path, byte[] data)>();
            var onDisk = new List<(string path, string fullPath)>();
            for (int i = 0; i < sizes.Length; i++)
            {
                var data = RandomNumberGenerator.GetBytes(sizes[i]);
                var path = $"dir{i % 2}/file{i}.bin";
                var full = Path.Combine(dir, $"f{i}.bin");
                File.WriteAllBytes(full, data);
                inMemory.Add((path, data));
                onDisk.Add((path, full));
            }
            var options = new TorrentCreatorOptions { PieceLength = 16384, HashAlgorithm = "SHA-1" };
            var (memBytes, memMeta) = TorrentCreator.CreateFromMultipleFiles("world", inMemory.ToArray(), options);
            var (diskBytes, diskMeta) = await TorrentCreator.CreateFromFilePathsAsync("world", onDisk.ToArray(), options);

            if (diskMeta.InfoHash != memMeta.InfoHash) throw new Exception($"info hash differs: disk {diskMeta.InfoHash}, memory {memMeta.InfoHash}");
            if (!diskBytes.SequenceEqual(memBytes)) throw new Exception("the .torrent bytes differ");
            if (diskMeta.PieceCount != memMeta.PieceCount || diskMeta.TotalLength != memMeta.TotalLength) throw new Exception("piece count or length differ");
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [TestMethod]
    public async Task CreateFromFilePaths_DoesNotHoldTheContentInMemory_AndNamesAMissingFile()
    {
        if (OperatingSystem.IsBrowser()) throw new UnsupportedTestException("no disk in the browser");
        var dir = TempDir("seedfiles_mem");
        try
        {
            // 300 MB of zeros as a sparse file (cheap to make): the in-memory creator would need 300 MB for it, and again for the concatenation
            var big = Path.Combine(dir, "big.bin");
            using (var fs = new FileStream(big, FileMode.Create)) fs.SetLength(300L * 1024 * 1024);
            var small = Path.Combine(dir, "small.bin");
            File.WriteAllBytes(small, RandomNumberGenerator.GetBytes(5000));

            long before = GC.GetTotalAllocatedBytes(true);
            var (_, meta) = await TorrentCreator.CreateFromFilePathsAsync("big-world", new[] { ("big.bin", big), ("small.bin", small) }, new TorrentCreatorOptions { HashAlgorithm = "SHA-1" });
            long allocated = GC.GetTotalAllocatedBytes(true) - before;

            if (meta.TotalLength != 300L * 1024 * 1024 + 5000) throw new Exception($"total length {meta.TotalLength}");
            if (allocated > 60L * 1024 * 1024)
                throw new Exception($"creating a torrent of 300 MB allocated {allocated / (1024 * 1024)} MB: the content must be streamed through one piece-sized buffer, not read into memory");

            bool threw = false;
            try { await TorrentCreator.CreateFromFilePathsAsync("x", new[] { ("a.bin", Path.Combine(dir, "does-not-exist.bin")) }); }
            catch (FileNotFoundException e) { threw = e.Message.Contains("does-not-exist"); }
            if (!threw) throw new Exception("a missing file must be reported by name");
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [TestMethod(RetryCount = 1)]
    public async Task SeedFilesAsync_SeedsFromDisk_AndAPeerDownloadsEveryFileIntact()
    {
        if (OperatingSystem.IsBrowser()) throw new UnsupportedTestException("no disk in the browser");
        var dir = TempDir("seedfiles_peer");
        try
        {
            var contents = new Dictionary<string, byte[]>
            {
                ["assets/model.glb"] = RandomNumberGenerator.GetBytes(300_000),
                ["room.json"] = RandomNumberGenerator.GetBytes(1234),
                ["assets/more/texture.bin"] = RandomNumberGenerator.GetBytes(700_001),
            };
            var files = new List<(string path, string fullPath)>();
            foreach (var (path, data) in contents)
            {
                var full = Path.Combine(dir, path.Replace('/', '_'));
                File.WriteAllBytes(full, data);
                files.Add((path, full));
            }

            await using var seeder = NewTcpOnlyClient();
            await seeder.EnsureTcpListenerAsync(0, IPAddress.Loopback);
            var seeded = await seeder.SeedFilesAsync("disk-world", files.ToArray(), new TorrentCreatorOptions { HashAlgorithm = "SHA-1" });
            if (!seeded.Done) throw new Exception("a seeded torrent must be done");
            foreach (var (path, data) in contents)
            {
                var file = seeded.Files!.First(f => f.Path.Replace("\\", "/").EndsWith(path));
                if (!(await file.ReadAsync(0, (int)file.Length)).AsSpan().SequenceEqual(data)) throw new Exception($"the seeder cannot read back '{path}'");
            }

            await using var leecher = NewTcpOnlyClient();
            var torrent = leecher.Add(seeded.InfoHash!, new AddTorrentOptions { Path = dir });
            var tcp = new TcpPeer(initiator: true);
            await tcp.ConnectAsync($"127.0.0.1:{seeder.TcpListener!.LocalEndPoint.Port}");
            torrent.AddPeer(tcp);
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (!torrent.Done && DateTime.UtcNow < deadline) await Task.Delay(100);
            if (!torrent.Done) throw new Exception($"the peer did not finish: {torrent.Progress:P0}, metadata {(torrent.HasMetadata ? "yes" : "NO")}");
            foreach (var (path, data) in contents)
            {
                var file = torrent.Files!.First(f => f.Path.Replace("\\", "/").EndsWith(path));
                if (!(await file.ReadAsync(0, (int)file.Length)).AsSpan().SequenceEqual(data)) throw new Exception($"the peer got different bytes for '{path}'");
            }
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [TestMethod(RetryCount = 1)]
    public async Task StorageFolder_KeepsPiecesOnDisk_ForSeederAndDownloader_AndRemoveWithDataDeletesThem()
    {
        if (OperatingSystem.IsBrowser()) throw new UnsupportedTestException("no disk in the browser");
        var dir = TempDir("storage_folder");
        try
        {
            var data = new Dictionary<string, byte[]> { ["a/one.bin"] = RandomNumberGenerator.GetBytes(400_000), ["two.bin"] = RandomNumberGenerator.GetBytes(90_001) };
            var files = new List<(string path, string fullPath)>();
            foreach (var (path, bytes) in data) { var full = Path.Combine(dir, path.Replace('/', '_')); File.WriteAllBytes(full, bytes); files.Add((path, full)); }

            WebTorrentClient Client(string storage) => new(new WebTorrentClientOptions
            {
                StorageFolder = storage, EnableTrackers = false, EnableDht = false, EnableLsd = false, EnableUtPex = false, DefaultTrackers = Array.Empty<string>(),
            });
            var seedStore = Path.Combine(dir, "seed-store");
            var leechStore = Path.Combine(dir, "leech-store");
            await using var seeder = Client(seedStore);
            await seeder.EnsureTcpListenerAsync(0, IPAddress.Loopback);
            var seeded = await seeder.SeedFilesAsync("w", files.ToArray(), new TorrentCreatorOptions { PieceLength = 32768, HashAlgorithm = "SHA-1" });

            int SeedPieces() => Directory.GetFiles(Path.Combine(seedStore, seeded.InfoHash!), "piece_*.bin").Length;
            if (SeedPieces() != seeded.PieceCount) throw new Exception($"the seeder holds {SeedPieces()} piece files on disk, expected {seeded.PieceCount}: seeding must store pieces on disk when a StorageFolder is set");

            await using var leecher = Client(leechStore);
            var torrent = leecher.Add(seeded.InfoHash!);
            var tcp = new TcpPeer(initiator: true);
            await tcp.ConnectAsync($"127.0.0.1:{seeder.TcpListener!.LocalEndPoint.Port}");
            torrent.AddPeer(tcp);
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (!torrent.Done && DateTime.UtcNow < deadline) await Task.Delay(100);
            if (!torrent.Done) throw new Exception($"the downloader did not finish: {torrent.Progress:P0}");
            int leechPieces = Directory.GetFiles(Path.Combine(leechStore, seeded.InfoHash!), "piece_*.bin").Length;
            if (leechPieces != seeded.PieceCount) throw new Exception($"the downloader holds {leechPieces} piece files on disk, expected {seeded.PieceCount}");
            foreach (var (path, bytes) in data)
            {
                var file = torrent.Files!.First(f => f.Path.Replace("\\", "/").EndsWith(path));
                if (!(await file.ReadAsync(0, (int)file.Length)).AsSpan().SequenceEqual(bytes)) throw new Exception($"'{path}' read back wrong from the disk store");
            }
            if (Directory.GetFiles(Path.Combine(leechStore, seeded.InfoHash!), "*.tmp").Length != 0) throw new Exception("a temporary piece file was left behind");

            await leecher.RemoveWithDataAsync(torrent);
            if (Directory.Exists(Path.Combine(leechStore, seeded.InfoHash!))) throw new Exception("RemoveWithDataAsync left the torrent's pieces on disk");
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [TestMethod]
    public async Task FileChunkStore_ReadsRangesWithoutLoadingThePiece_NeverShowsATornPiece_AndAnswersNullForWhatIsNotThere()
    {
        if (OperatingSystem.IsBrowser()) throw new UnsupportedTestException("no disk in the browser");
        var dir = TempDir("chunkstore");
        try
        {
            var store = new SpawnDev.WebTorrent.Storage.FileChunkStore(dir, 1000);
            var piece = Enumerable.Range(0, 1000).Select(i => (byte)(i % 251)).ToArray();
            await store.PutAsync(3, piece);
            if (!(await store.GetAsync(3))!.SequenceEqual(piece)) throw new Exception("whole piece differs");
            if (!(await store.GetAsync(3, 100, 50))!.SequenceEqual(piece.Skip(100).Take(50))) throw new Exception("a range differs");
            if (!(await store.GetAsync(3, 0, 1000))!.SequenceEqual(piece)) throw new Exception("the full range differs");
            if (await store.GetAsync(3, 990, 20) != null) throw new Exception("a range past the end of the piece must be null, not short");
            if (await store.GetAsync(3, -1, 5) != null) throw new Exception("a negative offset must be null");
            if (await store.GetAsync(9) != null || await store.GetAsync(9, 0, 5) != null) throw new Exception("a piece that was never stored must be null");

            // ten writers of the same piece at once (the same piece arriving from several peers): the file is always one of them whole, never a mixture, and nothing is left behind
            var versions = Enumerable.Range(0, 10).Select(v => Enumerable.Repeat((byte)(v + 1), 1000).ToArray()).ToArray();
            await Task.WhenAll(versions.Select(v => store.PutAsync(5, v)));
            var final = (await store.GetAsync(5))!;
            if (!versions.Any(v => v.SequenceEqual(final))) throw new Exception("the piece is a mixture of two writers");
            if (Directory.GetFiles(dir, "*.tmp").Length != 0) throw new Exception("temporary piece files were left behind");

            // replacing a piece while someone reads it: the reader sees the old piece whole or the new piece whole, never a part of the new one
            var big = new SpawnDev.WebTorrent.Storage.FileChunkStore(Path.Combine(dir, "big"), 48 * 1024 * 1024);
            await big.PutAsync(0, new byte[1000]);
            var replacement = new byte[48 * 1024 * 1024];
            var writing = Task.Run(() => big.PutAsync(0, replacement));
            var lengths = new HashSet<int>();
            while (!writing.IsCompleted) { var seen = await big.GetAsync(0); if (seen != null) lengths.Add(seen.Length); }
            await writing;
            lengths.Add((await big.GetAsync(0))!.Length);
            if (lengths.Any(l => l != 1000 && l != replacement.Length)) throw new Exception($"a reader saw a partly written piece: lengths seen {string.Join(", ", lengths)}");
            if (!lengths.Contains(replacement.Length)) throw new Exception("the replacement never appeared");

            await store.RemoveAsync(3);
            if (await store.GetAsync(3) != null) throw new Exception("a removed piece is still served");
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}
