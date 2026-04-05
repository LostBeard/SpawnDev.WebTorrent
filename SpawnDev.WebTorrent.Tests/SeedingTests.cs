using NUnit.Framework;
using SpawnDev.WebTorrent;
using SpawnDev.WebTorrent.Storage;

namespace SpawnDev.WebTorrent.Tests;

/// <summary>
/// Tests for seeding — create torrents, seed them, verify all pieces are stored
/// and can be served back correctly. Uses real data, real hashing, real stores.
/// </summary>
[TestFixture]
public class SeedingTests
{
    [Test]
    public async Task SeedAsync_SingleFile_AllPiecesInStore()
    {
        var client = new WebTorrentClient();
        var data = new byte[65536];
        Random.Shared.NextBytes(data);

        var torrent = await client.SeedAsync("seed-test.bin", data, new TorrentCreatorOptions
        {
            HashAlgorithm = "SHA-1",
            PieceLength = 16384,
        });

        // Verify torrent state
        Assert.That(torrent.Done, Is.True, "Torrent should be marked as done after seeding");
        Assert.That(torrent.HasMetadata, Is.True, "Torrent should have metadata");
        Assert.That(torrent.Name, Is.EqualTo("seed-test.bin"));
        Assert.That(torrent.Length, Is.EqualTo(65536));
        Assert.That(torrent.PieceCount, Is.EqualTo(4));
        Assert.That(torrent.Progress, Is.EqualTo(1.0).Within(0.001));
        Assert.That(torrent.Downloaded, Is.EqualTo(65536));
        Assert.That(torrent.InfoHash, Is.Not.Null.And.Length.EqualTo(40));

        // Verify all pieces are in the store
        Assert.That(torrent._store, Is.Not.Null, "Store should be initialized");
        for (int i = 0; i < 4; i++)
        {
            var piece = await torrent._store!.GetAsync(i);
            Assert.That(piece, Is.Not.Null, $"Piece {i} should be in store");
            Assert.That(piece!.Length, Is.EqualTo(16384), $"Piece {i} should be 16384 bytes");

            // Verify piece data matches original
            var expected = new byte[16384];
            Array.Copy(data, i * 16384, expected, 0, 16384);
            Assert.That(piece, Is.EqualTo(expected), $"Piece {i} data should match original");
        }

        // Verify bitfield
        Assert.That(torrent.Bitfield.All(b => b), Is.True, "All bitfield entries should be true");

        await client.DisposeAsync();
    }

    [Test]
    public async Task SeedAsync_MultiFile_AllPiecesInStore()
    {
        var client = new WebTorrentClient();
        var file1 = new byte[32768];
        var file2 = new byte[16384];
        Random.Shared.NextBytes(file1);
        Random.Shared.NextBytes(file2);

        var torrent = await client.SeedAsync("multi-test",
            new[] { ("readme.txt", file1), ("model.bin", file2) },
            new TorrentCreatorOptions { HashAlgorithm = "SHA-1", PieceLength = 16384 });

        Assert.That(torrent.Done, Is.True);
        Assert.That(torrent.Length, Is.EqualTo(49152));
        Assert.That(torrent.PieceCount, Is.EqualTo(3));
        Assert.That(torrent.Files!.Length, Is.EqualTo(2));

        // Verify concatenated data is stored correctly
        var allData = new byte[49152];
        Array.Copy(file1, 0, allData, 0, 32768);
        Array.Copy(file2, 0, allData, 32768, 16384);

        for (int i = 0; i < 3; i++)
        {
            var piece = await torrent._store!.GetAsync(i);
            Assert.That(piece, Is.Not.Null);
            var expected = new byte[16384];
            Array.Copy(allData, i * 16384, expected, 0, 16384);
            Assert.That(piece, Is.EqualTo(expected), $"Piece {i} should match concatenated file data");
        }

        await client.DisposeAsync();
    }

    [Test]
    public async Task SeedAsync_OnRequestHandler_ServesCorrectData()
    {
        var client = new WebTorrentClient();
        var data = new byte[32768];
        Random.Shared.NextBytes(data);

        var torrent = await client.SeedAsync("serve-test.bin", data, new TorrentCreatorOptions
        {
            HashAlgorithm = "SHA-1",
            PieceLength = 16384,
        });

        // Verify the store can serve partial reads (what OnRequest does)
        var partial = await torrent._store!.GetAsync(0, 0, 1024);
        Assert.That(partial, Is.Not.Null);
        Assert.That(partial!.Length, Is.EqualTo(1024));
        Assert.That(partial, Is.EqualTo(data[..1024]));

        // Read from second piece with offset
        var partial2 = await torrent._store!.GetAsync(1, 4096, 2048);
        Assert.That(partial2, Is.Not.Null);
        Assert.That(partial2!.Length, Is.EqualTo(2048));
        var expected = new byte[2048];
        Array.Copy(data, 16384 + 4096, expected, 0, 2048);
        Assert.That(partial2, Is.EqualTo(expected));

        await client.DisposeAsync();
    }

    [Test]
    public async Task SeedAsync_TorrentFileBytes_CanBeExportedAndReparsed()
    {
        var client = new WebTorrentClient();
        var data = new byte[16384];
        Random.Shared.NextBytes(data);

        var torrent = await client.SeedAsync("export-test.bin", data, new TorrentCreatorOptions
        {
            HashAlgorithm = "SHA-1",
            Trackers = new[] { "wss://example.com/announce" },
        });

        // Verify .torrent bytes can be exported
        Assert.That(torrent.TorrentFileBytes, Is.Not.Null, "TorrentFileBytes should be set");

        // Reparse and verify it matches
        var parsed = TorrentParser.Parse(torrent.TorrentFileBytes!);
        Assert.That(parsed.InfoHash, Is.EqualTo(torrent.InfoHash));
        Assert.That(parsed.Name, Is.EqualTo("export-test.bin"));
        Assert.That(parsed.TotalLength, Is.EqualTo(16384));

        await client.DisposeAsync();
    }

    [Test]
    public async Task SeedAsync_ComputedMagnetUri_IsValid()
    {
        var client = new WebTorrentClient();
        var data = new byte[1024];

        var torrent = await client.SeedAsync("magnet-test.bin", data, new TorrentCreatorOptions
        {
            HashAlgorithm = "SHA-1",
            Trackers = new[] { "wss://tracker.example.com/announce" },
        });

        var magnet = torrent.ComputedMagnetUri;
        Assert.That(magnet, Does.StartWith("magnet:?xt=urn:btih:"));
        Assert.That(magnet, Does.Contain(torrent.InfoHash!));
        Assert.That(magnet, Does.Contain("dn=magnet-test.bin"));
        Assert.That(magnet, Does.Contain("tracker.example.com"));

        await client.DisposeAsync();
    }

    [Test]
    public async Task SeedAsync_ClientGet_FindsByHash()
    {
        var client = new WebTorrentClient();
        var data = new byte[1024];

        var torrent = await client.SeedAsync("get-test.bin", data);

        var found = client.Get(torrent.InfoHash!);
        Assert.That(found, Is.Not.Null);
        Assert.That(found, Is.SameAs(torrent));

        var notFound = client.Get("0000000000000000000000000000000000000000");
        Assert.That(notFound, Is.Null);

        await client.DisposeAsync();
    }

    [Test]
    public async Task SeedAsync_FileInfo_HasCorrectProperties()
    {
        var client = new WebTorrentClient();
        var data = new byte[32768];
        Random.Shared.NextBytes(data);

        var torrent = await client.SeedAsync("fileinfo-test.mp4", data, new TorrentCreatorOptions
        {
            HashAlgorithm = "SHA-1",
            PieceLength = 16384,
        });

        Assert.That(torrent.Files, Is.Not.Null);
        Assert.That(torrent.Files!.Length, Is.EqualTo(1));

        var file = torrent.Files[0];
        Assert.That(file.Name, Is.EqualTo("fileinfo-test.mp4"));
        Assert.That(file.Length, Is.EqualTo(32768));
        Assert.That(file.Type, Is.EqualTo("video/mp4"));
        Assert.That(file.Progress, Is.EqualTo(1.0).Within(0.001));
        Assert.That(file.Downloaded, Is.EqualTo(32768));
        Assert.That(file.Done, Is.True);

        await client.DisposeAsync();
    }

    [Test]
    public async Task SeedAsync_SpeedTracking_InitializesCorrectly()
    {
        var client = new WebTorrentClient();
        var data = new byte[1024];

        var torrent = await client.SeedAsync("speed-test.bin", data);

        // Speed should be 0 initially (just created, no transfers)
        Assert.That(torrent.DownloadSpeed, Is.EqualTo(0));
        Assert.That(torrent.UploadSpeed, Is.EqualTo(0));
        Assert.That(torrent.Ratio, Is.EqualTo(0)); // 0 uploaded, but also 0 downloaded from peers

        await client.DisposeAsync();
    }
}
