using SpawnDev.UnitTesting;
using SpawnDev.WebTorrent;

namespace SpawnDev.WebTorrent.Demo.Shared;

public abstract partial class WebTorrentTestBase
{
    [TestMethod]
    public async Task File_ReadAsync_EntireFile()
    {
        var client = CreateIsolatedClient();
        var data = MakeDeterministicData(32768, seed: 30);
        var torrent = await client.SeedAsync("read-full.bin", data);
        var result = await torrent.ReadFileAsync(0);
        if (result.Length != data.Length) throw new Exception($"Expected {data.Length} bytes, got {result.Length}");
        if (!result.SequenceEqual(data)) throw new Exception("ReadFileAsync data mismatch");
        await client.DisposeAsync();
    }

    [TestMethod]
    public async Task File_ReadAsync_CrossPieceBoundary()
    {
        var client = CreateIsolatedClient();
        var data = MakeDeterministicData(49152, seed: 31); // 3 pieces at 16KB
        var torrent = await client.SeedAsync("cross-piece.bin", data);
        // Read across piece boundary (piece 0 ends at 16384)
        var result = await torrent.ReadFileAsync(0, 16000, 1000);
        if (result.Length != 1000) throw new Exception($"Expected 1000 bytes, got {result.Length}");
        var expected = data[16000..17000];
        if (!result.SequenceEqual(expected)) throw new Exception("Cross-piece boundary read mismatch");
        await client.DisposeAsync();
    }

    [TestMethod]
    public async Task File_ReadAsync_MultipleRandomReads()
    {
        var client = CreateIsolatedClient();
        var data = MakeDeterministicData(65536, seed: 32);
        var torrent = await client.SeedAsync("random-read.bin", data);
        var rng = new Random(999);
        for (int i = 0; i < 6; i++)
        {
            int offset = rng.Next(0, data.Length - 256);
            var result = await torrent.ReadFileAsync(0, offset, 256);
            var expected = data[offset..(offset + 256)];
            if (!result.SequenceEqual(expected))
                throw new Exception($"Random read {i} at offset {offset} mismatch");
        }
        await client.DisposeAsync();
    }

    [TestMethod]
    public async Task File_ReadAsync_MultiFile_CorrectFile()
    {
        var client = CreateIsolatedClient();
        var file1 = MakeDeterministicData(16384, seed: 33);
        var file2 = MakeDeterministicData(8192, seed: 34);
        var torrent = await client.SeedAsync("multiread", new[] { ("f1.bin", file1), ("f2.bin", file2) });
        var r1 = await torrent.ReadFileAsync(0);
        var r2 = await torrent.ReadFileAsync(1);
        if (!r1.SequenceEqual(file1)) throw new Exception("File 0 data mismatch");
        if (!r2.SequenceEqual(file2)) throw new Exception("File 1 data mismatch");
        await client.DisposeAsync();
    }

    [TestMethod]
    public async Task File_ReadAsync_WaitsForPiece()
    {
        // Seed, then read — since torrent is already Done, the wait-for-piece path
        // should return immediately. This tests the code path exists without hanging.
        var client = CreateIsolatedClient();
        var data = MakeDeterministicData(32768, seed: 35);
        var torrent = await client.SeedAsync("wait-piece.bin", data);
        using var cts = new CancellationTokenSource(5000);
        var result = await torrent.ReadFileAsync(0, 0, 100, cts.Token);
        if (result.Length != 100) throw new Exception($"Expected 100 bytes, got {result.Length}");
        await client.DisposeAsync();
    }

    [TestMethod]
    public async Task File_Select_Deselect()
    {
        var client = CreateIsolatedClient();
        var file1 = MakeDeterministicData(32768, seed: 36);
        var file2 = MakeDeterministicData(32768, seed: 37);
        var torrent = await client.SeedAsync("sel-desel", new[] { ("a.bin", file1), ("b.bin", file2) });
        if (torrent.Files == null || torrent.Files.Length < 2)
            throw new Exception("Not enough files");
        // Deselect file 1 — should not throw
        torrent.Files[1].Deselect();
        // Re-select it
        torrent.Files[1].Select();
        await client.DisposeAsync();
    }

    [TestMethod]
    public async Task File_CriticalPieces_Prioritized()
    {
        var client = CreateIsolatedClient();
        var data = MakeDeterministicData(49152, seed: 38); // 3 pieces
        var torrent = await client.SeedAsync("critical.bin", data);
        // Mark piece 1 as critical — should not throw
        torrent.Critical(1, 1);
        await client.DisposeAsync();
    }

    [TestMethod]
    public async Task File_SeekPattern_SimulatesVideo()
    {
        // Simulate video seeking: read small chunks at various offsets
        var client = CreateIsolatedClient();
        var data = MakeDeterministicData(65536, seed: 39); // 4 pieces
        var torrent = await client.SeedAsync("seek.bin", data);
        // Seek to 5 positions: start, 25%, 50%, 75%, near end
        int[] positions = { 0, 16384, 32768, 49152, 65000 };
        foreach (var pos in positions)
        {
            int len = Math.Min(256, data.Length - pos);
            var chunk = await torrent.ReadFileAsync(0, pos, len);
            var expected = data[pos..(pos + len)];
            if (!chunk.SequenceEqual(expected))
                throw new Exception($"Seek at {pos} returned wrong data");
        }
        await client.DisposeAsync();
    }
}
