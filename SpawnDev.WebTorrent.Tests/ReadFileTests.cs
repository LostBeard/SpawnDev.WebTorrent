using NUnit.Framework;
using SpawnDev.WebTorrent;
using SpawnDev.WebTorrent.Storage;

namespace SpawnDev.WebTorrent.Tests;

/// <summary>
/// Tests for ReadFileAsync — random-access file reading from torrent pieces,
/// cross-piece boundary reads, reads during download (partial availability),
/// and complete file reads. All use real data and real hashing.
/// </summary>
[TestFixture]
public class ReadFileTests
{
    [Test]
    public async Task ReadFileAsync_CompleteTorrent_ReturnsCorrectData()
    {
        var client = new WebTorrentClient();
        var data = new byte[65536];
        Random.Shared.NextBytes(data);

        var torrent = await client.SeedAsync("read-test.bin", data, new TorrentCreatorOptions
        {
            HashAlgorithm = "SHA-1",
            PieceLength = 16384,
        });

        // Read entire file
        var result = await torrent.ReadFileAsync(0);
        Assert.That(result, Is.EqualTo(data), "Full file read should match original data exactly");

        await client.DisposeAsync();
    }

    [Test]
    public async Task ReadFileAsync_RandomAccess_ReturnsCorrectSlice()
    {
        var client = new WebTorrentClient();
        var data = new byte[65536];
        Random.Shared.NextBytes(data);

        var torrent = await client.SeedAsync("random-access.bin", data, new TorrentCreatorOptions
        {
            HashAlgorithm = "SHA-1",
            PieceLength = 16384,
        });

        // Read from middle of file (crosses piece boundary: piece 1 end + piece 2 start)
        var offset = 14000L; // near end of piece 0
        var length = 5000;   // crosses into piece 1
        var result = await torrent.ReadFileAsync(0, offset, length);

        Assert.That(result.Length, Is.EqualTo(length));
        var expected = new byte[length];
        Array.Copy(data, offset, expected, 0, length);
        Assert.That(result, Is.EqualTo(expected), "Cross-piece random access read should match");

        await client.DisposeAsync();
    }

    [Test]
    public async Task ReadFileAsync_MultipleRandomReads_AllCorrect()
    {
        var client = new WebTorrentClient();
        var data = new byte[131072]; // 128KB = 8 pieces of 16384
        Random.Shared.NextBytes(data);

        var torrent = await client.SeedAsync("multi-read.bin", data, new TorrentCreatorOptions
        {
            HashAlgorithm = "SHA-1",
            PieceLength = 16384,
        });

        // Simulate random-access reading pattern (like an ML model loader)
        var reads = new (long offset, int length)[]
        {
            (0, 1024),           // Start of file
            (65536, 4096),       // Middle of file (start of piece 4)
            (130000, 1072),      // Near end of file
            (16000, 768),        // Cross piece 0→1 boundary
            (32768, 16384),      // Exactly piece 2
            (100000, 20000),     // Cross multiple pieces
        };

        foreach (var (offset, length) in reads)
        {
            var result = await torrent.ReadFileAsync(0, offset, length);
            Assert.That(result.Length, Is.EqualTo(length), $"Read at offset {offset} length {length}");
            var expected = new byte[length];
            Array.Copy(data, offset, expected, 0, length);
            Assert.That(result, Is.EqualTo(expected), $"Data at offset {offset} should match");
        }

        await client.DisposeAsync();
    }

    [Test]
    public async Task ReadFileAsync_MultiFileTorrent_ReadsCorrectFile()
    {
        var client = new WebTorrentClient();
        var file1 = new byte[32768]; // 2 pieces
        var file2 = new byte[16384]; // 1 piece
        Random.Shared.NextBytes(file1);
        Random.Shared.NextBytes(file2);

        var torrent = await client.SeedAsync("multi-file",
            new[] { ("video.mp4", file1), ("audio.mp3", file2) },
            new TorrentCreatorOptions { HashAlgorithm = "SHA-1", PieceLength = 16384 });

        // Read file 0 (video.mp4) — first 1024 bytes
        var result0 = await torrent.ReadFileAsync(0, 0, 1024);
        Assert.That(result0, Is.EqualTo(file1[..1024]));

        // Read file 1 (audio.mp3) — first 1024 bytes
        var result1 = await torrent.ReadFileAsync(1, 0, 1024);
        Assert.That(result1, Is.EqualTo(file2[..1024]));

        // Read entire file 1
        var fullFile1 = await torrent.ReadFileAsync(1);
        Assert.That(fullFile1, Is.EqualTo(file2));

        await client.DisposeAsync();
    }

    [Test]
    public async Task ReadFileAsync_DuringDownload_WaitsForPiece()
    {
        // Simulate reading during download by creating a torrent with only some pieces
        var client = new WebTorrentClient();
        var data = new byte[49152]; // 3 pieces of 16384
        Random.Shared.NextBytes(data);

        // Create the torrent metadata (but don't seed — simulate download in progress)
        var (torrentBytes, metadata) = TorrentCreator.CreateFromBytes("partial.bin", data, new TorrentCreatorOptions
        {
            HashAlgorithm = "SHA-1",
            PieceLength = 16384,
        });

        // Add from .torrent bytes (starts empty — no pieces downloaded yet)
        var torrent = client.Add(torrentBytes);

        // Manually store pieces 0 and 2 (piece 1 is "still downloading")
        await torrent._store!.PutAsync(0, data.AsMemory(0, 16384));
        torrent.Bitfield[0] = true;
        torrent.Pieces[0] = new Piece(0);

        await torrent._store!.PutAsync(2, data.AsMemory(32768, 16384));
        torrent.Bitfield[2] = true;
        torrent.Pieces[2] = new Piece(0);

        // Reading piece 0 should succeed immediately
        var piece0Data = await torrent.ReadFileAsync(0, 0, 16384);
        Assert.That(piece0Data, Is.EqualTo(data[..16384]), "Available piece should read immediately");

        // Reading piece 2 should succeed immediately
        var piece2Data = await torrent.ReadFileAsync(0, 32768, 16384);
        Assert.That(piece2Data, Is.EqualTo(data[32768..49152]), "Available piece should read immediately");

        // Start a read that needs piece 1 (will block until piece arrives)
        var readTask = torrent.ReadFileAsync(0, 16384, 16384);

        // Verify it hasn't completed yet (piece 1 not available)
        await Task.Delay(200);
        Assert.That(readTask.IsCompleted, Is.False, "Read should block waiting for piece 1");

        // Simulate piece 1 arriving (as if downloaded from a peer)
        await torrent._store!.PutAsync(1, data.AsMemory(16384, 16384));
        torrent.Bitfield[1] = true;
        torrent.Pieces[1] = new Piece(0);

        // Now the read should complete
        var piece1Data = await readTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.That(piece1Data, Is.EqualTo(data[16384..32768]), "Read should complete after piece arrives");

        await client.DisposeAsync();
    }

    [Test]
    public async Task ReadFileAsync_CrossPieceBoundary_DuringDownload()
    {
        var client = new WebTorrentClient();
        var data = new byte[49152]; // 3 pieces
        Random.Shared.NextBytes(data);

        var (torrentBytes, _) = TorrentCreator.CreateFromBytes("cross-boundary.bin", data, new TorrentCreatorOptions
        {
            HashAlgorithm = "SHA-1",
            PieceLength = 16384,
        });

        var torrent = client.Add(torrentBytes);

        // Store all pieces (simulating complete download)
        for (int i = 0; i < 3; i++)
        {
            await torrent._store!.PutAsync(i, data.AsMemory(i * 16384, 16384));
            torrent.Bitfield[i] = true;
            torrent.Pieces[i] = new Piece(0);
        }

        // Read across piece boundary (piece 0 end → piece 1 start)
        var crossRead = await torrent.ReadFileAsync(0, 16000, 1000);
        Assert.That(crossRead.Length, Is.EqualTo(1000));
        var expected = new byte[1000];
        Array.Copy(data, 16000, expected, 0, 1000);
        Assert.That(crossRead, Is.EqualTo(expected), "Cross-piece boundary read should be correct");

        // Read across all 3 pieces
        var fullRead = await torrent.ReadFileAsync(0, 10000, 30000);
        Assert.That(fullRead.Length, Is.EqualTo(30000));
        var expectedFull = new byte[30000];
        Array.Copy(data, 10000, expectedFull, 0, 30000);
        Assert.That(fullRead, Is.EqualTo(expectedFull), "Multi-piece spanning read should be correct");

        await client.DisposeAsync();
    }

    [Test]
    public async Task ReadFileAsync_SeekingPattern_SimulatesVideoSeeking()
    {
        // Simulate the pattern of a video player seeking: read header, then jump to various positions
        var client = new WebTorrentClient();
        var data = new byte[262144]; // 256KB = 16 pieces of 16384
        Random.Shared.NextBytes(data);

        var torrent = await client.SeedAsync("video-seek.mp4", data, new TorrentCreatorOptions
        {
            HashAlgorithm = "SHA-1",
            PieceLength = 16384,
        });

        // 1. Read header (first 4KB) — video player reads this first
        var header = await torrent.ReadFileAsync(0, 0, 4096);
        Assert.That(header, Is.EqualTo(data[..4096]));

        // 2. Seek to 50% — read 8KB
        var mid = await torrent.ReadFileAsync(0, 131072, 8192);
        var expectedMid = new byte[8192];
        Array.Copy(data, 131072, expectedMid, 0, 8192);
        Assert.That(mid, Is.EqualTo(expectedMid));

        // 3. Seek to 75% — read 16KB
        var threeQuarter = await torrent.ReadFileAsync(0, 196608, 16384);
        var expectedTQ = new byte[16384];
        Array.Copy(data, 196608, expectedTQ, 0, 16384);
        Assert.That(threeQuarter, Is.EqualTo(expectedTQ));

        // 4. Seek back to 25% — random access backward
        var quarter = await torrent.ReadFileAsync(0, 65536, 4096);
        Assert.That(quarter, Is.EqualTo(data[65536..69632]));

        // 5. Read last 1024 bytes (footer/index)
        var footer = await torrent.ReadFileAsync(0, 261120, 1024);
        Assert.That(footer, Is.EqualTo(data[261120..262144]));

        Console.WriteLine("[Test] All 5 seek positions read correctly — video seeking simulation passed");
        await client.DisposeAsync();
    }
}
