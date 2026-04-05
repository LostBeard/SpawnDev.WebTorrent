using SpawnDev.UnitTesting;
using SpawnDev.WebTorrent;

namespace SpawnDev.WebTorrent.Demo.Shared;

public abstract partial class WebTorrentTestBase
{
    [TestMethod]
    public async Task Seed_SingleFile_Done()
    {
        var client = CreateIsolatedClient();
        var data = MakeDeterministicData(32768, seed: 10);
        var torrent = await client.SeedAsync("seed-done.bin", data);
        if (!torrent.Done) throw new Exception("Seeded torrent should be Done");
        if (torrent.Progress < 0.99) throw new Exception($"Progress should be ~1.0, got {torrent.Progress}");
        if (torrent.Bitfield.Any(b => !b)) throw new Exception("All bitfield entries should be true");
        await client.DisposeAsync();
    }

    [TestMethod]
    public async Task Seed_SingleFile_AllPiecesInStore()
    {
        var client = CreateIsolatedClient();
        var data = MakeDeterministicData(49152, seed: 11); // 3 pieces
        var torrent = await client.SeedAsync("pieces.bin", data);
        // Verify all pieces are in bitfield and data is readable
        for (int i = 0; i < torrent.PieceCount; i++)
        {
            if (!torrent.Bitfield[i]) throw new Exception($"Piece {i} not in bitfield");
        }
        // Verify full data round-trips through ReadFileAsync
        var readBack = await torrent.ReadFileAsync(0);
        if (!readBack.SequenceEqual(data)) throw new Exception("Data round-trip mismatch");
        await client.DisposeAsync();
    }

    [TestMethod]
    public async Task Seed_SingleFile_PartialReadCorrect()
    {
        var client = CreateIsolatedClient();
        var data = MakeDeterministicData(32768, seed: 12);
        var torrent = await client.SeedAsync("partial.bin", data);
        // Read a slice from the middle
        var slice = await torrent.ReadFileAsync(0, 100, 256);
        if (slice.Length != 256) throw new Exception($"Expected 256 bytes, got {slice.Length}");
        var expected = data[100..356];
        if (!slice.SequenceEqual(expected)) throw new Exception("Partial read data mismatch");
        await client.DisposeAsync();
    }

    [TestMethod]
    public async Task Seed_MultiFile_ConcatenatedStorage()
    {
        var client = CreateIsolatedClient();
        var file1 = MakeDeterministicData(16384, seed: 13);
        var file2 = MakeDeterministicData(8192, seed: 14);
        var torrent = await client.SeedAsync("multi", new[] { ("a.bin", file1), ("b.bin", file2) });
        if (!torrent.Done) throw new Exception("Multi-file seed should be Done");
        if (torrent.Files == null || torrent.Files.Length != 2) throw new Exception($"Expected 2 files, got {torrent.Files?.Length}");
        await client.DisposeAsync();
    }

    [TestMethod]
    public async Task Seed_ExportedTorrentBytes_Parses()
    {
        var client = CreateIsolatedClient();
        var data = MakeDeterministicData(16384, seed: 15);
        var torrent = await client.SeedAsync("export.bin", data);
        var torrentFileBytes = torrent.TorrentFileBytes;
        if (torrentFileBytes == null || torrentFileBytes.Length == 0) throw new Exception("TorrentFileBytes is null/empty");
        var parsed = TorrentParser.Parse(torrentFileBytes);
        if (parsed.InfoHash != torrent.InfoHash) throw new Exception("Exported torrent InfoHash mismatch");
        await client.DisposeAsync();
    }

    [TestMethod]
    public async Task Seed_ClientGet_FindsByHash()
    {
        var client = CreateIsolatedClient();
        var data = MakeDeterministicData(16384, seed: 16);
        var torrent = await client.SeedAsync("findme.bin", data);
        var found = client.Get(torrent.InfoHash!);
        if (found == null) throw new Exception("Client.Get returned null");
        if (found.InfoHash != torrent.InfoHash) throw new Exception("InfoHash mismatch on Get");
        await client.DisposeAsync();
    }

    [TestMethod]
    public async Task Seed_FileInfo_Properties()
    {
        var client = CreateIsolatedClient();
        var data = MakeDeterministicData(24576, seed: 17);
        var torrent = await client.SeedAsync("props.bin", data);
        if (torrent.Files == null || torrent.Files.Length == 0) throw new Exception("No files");
        var file = torrent.Files[0];
        if (file.Name != "props.bin") throw new Exception($"Wrong name: {file.Name}");
        if (file.Length != 24576) throw new Exception($"Wrong length: {file.Length}");
        if (!file.Done) throw new Exception("File should be Done");
        if (file.Progress < 0.99) throw new Exception($"File progress should be ~1.0, got {file.Progress}");
        await client.DisposeAsync();
    }

    [TestMethod]
    public async Task Seed_SpeedTracking_Initializes()
    {
        var client = CreateIsolatedClient();
        var data = MakeDeterministicData(16384, seed: 18);
        var torrent = await client.SeedAsync("speed.bin", data);
        // These should be accessible without exception
        var dl = torrent.DownloadSpeed;
        var ul = torrent.UploadSpeed;
        var ratio = torrent.Ratio;
        if (double.IsNaN(dl) || double.IsNaN(ul)) throw new Exception("Speed is NaN");
        await client.DisposeAsync();
    }
}
