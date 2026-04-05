using SpawnDev.UnitTesting;
using SpawnDev.WebTorrent;

namespace SpawnDev.WebTorrent.Demo.Shared;

/// <summary>
/// Streaming tests — verifies file.StreamURL, file.CreateReadStream(), file.ReadAsync(),
/// TorrentReadStream seeking, and on-demand piece download via random access reads.
/// These are THE core features of a WebTorrent client.
/// </summary>
public abstract partial class WebTorrentTestBase
{
    [TestMethod]
    public async Task Stream_FileHasStreamURL()
    {
        var client = CreateIsolatedClient();
        var data = MakeDeterministicData(32768, seed: 800);
        var torrent = await client.SeedAsync("stream-url.bin", data);

        if (torrent.Files == null || torrent.Files.Length == 0)
            throw new Exception("No files");

        var url = torrent.Files[0].StreamURL;
        if (string.IsNullOrEmpty(url))
            throw new Exception("StreamURL should not be null/empty");
        if (!url.Contains("/webtorrent/"))
            throw new Exception($"StreamURL should contain /webtorrent/, got: {url}");
        if (!url.Contains(torrent.InfoHashHex))
            throw new Exception($"StreamURL should contain infohash, got: {url}");

        await client.DisposeAsync();
    }

    [TestMethod]
    public async Task Stream_FileReadAsync_OnDemand()
    {
        // ReadAsync works during download — reads on demand, waits for pieces
        var client = CreateIsolatedClient();
        var data = MakeDeterministicData(65536, seed: 801);
        var torrent = await client.SeedAsync("stream-read.bin", data);
        var file = torrent.Files![0];

        // Read first 1KB
        var chunk1 = await file.ReadAsync(0, 1024);
        if (chunk1.Length != 1024) throw new Exception($"Expected 1024, got {chunk1.Length}");
        if (!chunk1.SequenceEqual(data[..1024])) throw new Exception("First chunk data mismatch");

        // Read from the middle
        var chunk2 = await file.ReadAsync(32000, 2048);
        if (chunk2.Length != 2048) throw new Exception($"Expected 2048, got {chunk2.Length}");
        if (!chunk2.SequenceEqual(data[32000..34048])) throw new Exception("Middle chunk data mismatch");

        // Read last bytes
        var chunk3 = await file.ReadAsync(65000, 536);
        if (chunk3.Length != 536) throw new Exception($"Expected 536, got {chunk3.Length}");
        if (!chunk3.SequenceEqual(data[65000..])) throw new Exception("Last chunk data mismatch");

        await client.DisposeAsync();
    }

    [TestMethod]
    public async Task Stream_FileGetArrayBuffer_FullFile()
    {
        var client = CreateIsolatedClient();
        var data = MakeDeterministicData(16384, seed: 802);
        var torrent = await client.SeedAsync("stream-full.bin", data);

        var result = await torrent.Files![0].GetArrayBufferAsync();
        if (result.Length != data.Length) throw new Exception($"Expected {data.Length}, got {result.Length}");
        if (!result.SequenceEqual(data)) throw new Exception("Full file data mismatch");

        await client.DisposeAsync();
    }

    [TestMethod]
    public async Task Stream_CreateReadStream_SequentialRead()
    {
        var client = CreateIsolatedClient();
        var data = MakeDeterministicData(32768, seed: 803);
        var torrent = await client.SeedAsync("stream-seq.bin", data);

        using var stream = torrent.Files![0].CreateReadStream();

        if (!stream.CanRead) throw new Exception("Should be readable");
        if (!stream.CanSeek) throw new Exception("Should be seekable");
        if (stream.Length != data.Length) throw new Exception($"Length: expected {data.Length}, got {stream.Length}");

        // Read 4KB at a time
        var buffer = new byte[4096];
        var allRead = new List<byte>();
        int bytesRead;
        while ((bytesRead = await stream.ReadAsync(buffer)) > 0)
        {
            allRead.AddRange(buffer[..bytesRead]);
        }

        if (allRead.Count != data.Length) throw new Exception($"Read {allRead.Count}, expected {data.Length}");
        if (!allRead.ToArray().SequenceEqual(data)) throw new Exception("Sequential read data mismatch");

        await client.DisposeAsync();
    }

    [TestMethod]
    public async Task Stream_CreateReadStream_SeekAndRead()
    {
        var client = CreateIsolatedClient();
        var data = MakeDeterministicData(65536, seed: 804);
        var torrent = await client.SeedAsync("stream-seek.bin", data);

        using var stream = torrent.Files![0].CreateReadStream();

        // Seek to offset 32000
        stream.Position = 32000;
        if (stream.Position != 32000) throw new Exception($"Position should be 32000, got {stream.Position}");

        var buffer = new byte[1024];
        var bytesRead = await stream.ReadAsync(buffer);
        if (bytesRead != 1024) throw new Exception($"Expected 1024, got {bytesRead}");
        if (!buffer.SequenceEqual(data[32000..33024])) throw new Exception("Seek read data mismatch");

        // Seek back to beginning
        stream.Seek(0, SeekOrigin.Begin);
        bytesRead = await stream.ReadAsync(buffer);
        if (bytesRead != 1024) throw new Exception($"Expected 1024 from start, got {bytesRead}");
        if (!buffer.SequenceEqual(data[..1024])) throw new Exception("Seek-to-start read data mismatch");

        // Seek from end
        stream.Seek(-512, SeekOrigin.End);
        var endBuffer = new byte[512];
        bytesRead = await stream.ReadAsync(endBuffer);
        if (bytesRead != 512) throw new Exception($"Expected 512 from end, got {bytesRead}");
        if (!endBuffer.SequenceEqual(data[^512..])) throw new Exception("Seek-from-end read data mismatch");

        await client.DisposeAsync();
    }

    [TestMethod]
    public async Task Stream_CreateReadStream_ReadBeyondEnd()
    {
        var client = CreateIsolatedClient();
        var data = MakeDeterministicData(1024, seed: 805);
        var torrent = await client.SeedAsync("stream-eof.bin", data);

        using var stream = torrent.Files![0].CreateReadStream();
        stream.Position = 1024; // at the end

        var buffer = new byte[100];
        var bytesRead = await stream.ReadAsync(buffer);
        if (bytesRead != 0) throw new Exception($"Read beyond EOF should return 0, got {bytesRead}");

        await client.DisposeAsync();
    }

    [TestMethod]
    public async Task Stream_FileIncludes_PieceRange()
    {
        var client = CreateIsolatedClient();
        var data = MakeDeterministicData(49152, seed: 806); // 3 pieces at 16KB
        var torrent = await client.SeedAsync("stream-includes.bin", data);
        var file = torrent.Files![0];

        if (!file.Includes(0)) throw new Exception("File should include piece 0");
        if (!file.Includes(1)) throw new Exception("File should include piece 1");
        if (!file.Includes(2)) throw new Exception("File should include piece 2");
        if (file.Includes(3)) throw new Exception("File should NOT include piece 3");

        await client.DisposeAsync();
    }

    [TestMethod]
    public async Task Stream_MultiFile_ReadCorrectFile()
    {
        var client = CreateIsolatedClient();
        var file1 = MakeDeterministicData(16384, seed: 807);
        var file2 = MakeDeterministicData(8192, seed: 808);
        var torrent = await client.SeedAsync("stream-multi", new[] { ("a.mp4", file1), ("b.txt", file2) });

        // Read via file API
        var r1 = await torrent.Files![0].ReadAsync(0, (int)torrent.Files[0].Length);
        var r2 = await torrent.Files![1].ReadAsync(0, (int)torrent.Files[1].Length);
        if (!r1.SequenceEqual(file1)) throw new Exception("File 0 data mismatch via ReadAsync");
        if (!r2.SequenceEqual(file2)) throw new Exception("File 1 data mismatch via ReadAsync");

        // Verify MIME types
        if (torrent.Files[0].Type != "video/mp4") throw new Exception($"Expected video/mp4, got {torrent.Files[0].Type}");
        if (torrent.Files[1].Type != "text/plain") throw new Exception($"Expected text/plain, got {torrent.Files[1].Type}");

        await client.DisposeAsync();
    }
}
