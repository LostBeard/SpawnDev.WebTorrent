using NUnit.Framework;
using SpawnDev.WebTorrent;

namespace SpawnDev.WebTorrent.Tests;

/// <summary>
/// Tests for TorrentCreator — create .torrent files, verify round-trip parsing,
/// multi-file support, and piece hash correctness.
/// All tests use real data and real hashing. No mocks.
/// </summary>
[TestFixture]
public class TorrentCreatorTests
{
    [Test]
    public void CreateFromBytes_SingleFile_ProducesValidTorrent()
    {
        var data = new byte[65536];
        Random.Shared.NextBytes(data);

        var (torrentBytes, metadata) = TorrentCreator.CreateFromBytes("test.bin", data, new TorrentCreatorOptions
        {
            HashAlgorithm = "SHA-1",
            PieceLength = 16384,
        });

        Assert.That(torrentBytes, Is.Not.Null);
        Assert.That(torrentBytes.Length, Is.GreaterThan(0));
        Assert.That(metadata.Name, Is.EqualTo("test.bin"));
        Assert.That(metadata.TotalLength, Is.EqualTo(65536));
        Assert.That(metadata.PieceLength, Is.EqualTo(16384));
        Assert.That(metadata.PieceCount, Is.EqualTo(4)); // 65536 / 16384
        Assert.That(metadata.PieceHashes.Length, Is.EqualTo(4));
        Assert.That(metadata.PieceHashes[0].Length, Is.EqualTo(20)); // SHA-1
        Assert.That(metadata.InfoHash, Is.Not.Null.And.Length.EqualTo(40)); // hex string
        Assert.That(metadata.Files.Length, Is.EqualTo(1));
        Assert.That(metadata.Files[0].Name, Is.EqualTo("test.bin"));
        Assert.That(metadata.Files[0].Length, Is.EqualTo(65536));
        Assert.That(metadata.OriginalTorrentBytes, Is.EqualTo(torrentBytes));
    }

    [Test]
    public void CreateFromBytes_SHA256_ProducesCorrectHashSize()
    {
        var data = new byte[32768];
        Random.Shared.NextBytes(data);

        var (_, metadata) = TorrentCreator.CreateFromBytes("test.bin", data, new TorrentCreatorOptions
        {
            HashAlgorithm = "SHA-256",
            PieceLength = 16384,
        });

        Assert.That(metadata.PieceHashes[0].Length, Is.EqualTo(32)); // SHA-256
        Assert.That(metadata.PieceCount, Is.EqualTo(2));
    }

    [Test]
    public void CreateFromBytes_RoundTrip_ParseBackCorrectly()
    {
        var data = new byte[49152]; // 3 pieces of 16384
        Random.Shared.NextBytes(data);

        var (torrentBytes, original) = TorrentCreator.CreateFromBytes("roundtrip.bin", data, new TorrentCreatorOptions
        {
            HashAlgorithm = "SHA-1",
            PieceLength = 16384,
            Trackers = new[] { "wss://tracker.example.com/announce" },
            WebSeeds = new[] { "https://example.com/files/" },
            Comment = "Test torrent",
        });

        // Parse it back
        var parsed = TorrentParser.Parse(torrentBytes);

        Assert.That(parsed.InfoHash, Is.EqualTo(original.InfoHash));
        Assert.That(parsed.Name, Is.EqualTo("roundtrip.bin"));
        Assert.That(parsed.TotalLength, Is.EqualTo(49152));
        Assert.That(parsed.PieceLength, Is.EqualTo(16384));
        Assert.That(parsed.PieceCount, Is.EqualTo(3));
        Assert.That(parsed.PieceHashes.Length, Is.EqualTo(3));
        // Verify piece hashes match
        for (int i = 0; i < 3; i++)
            Assert.That(parsed.PieceHashes[i], Is.EqualTo(original.PieceHashes[i]));
    }

    [Test]
    public void CreateFromMultipleFiles_ProducesValidTorrent()
    {
        var file1 = new byte[32768];
        var file2 = new byte[16384];
        Random.Shared.NextBytes(file1);
        Random.Shared.NextBytes(file2);

        var (torrentBytes, metadata) = TorrentCreator.CreateFromMultipleFiles("test-dir",
            new[] { ("docs/readme.txt", file1), ("data/model.bin", file2) },
            new TorrentCreatorOptions { HashAlgorithm = "SHA-1", PieceLength = 16384 });

        Assert.That(metadata.Name, Is.EqualTo("test-dir"));
        Assert.That(metadata.TotalLength, Is.EqualTo(49152));
        Assert.That(metadata.Files.Length, Is.EqualTo(2));
        Assert.That(metadata.Files[0].Path, Is.EqualTo("docs/readme.txt"));
        Assert.That(metadata.Files[0].Length, Is.EqualTo(32768));
        Assert.That(metadata.Files[1].Path, Is.EqualTo("data/model.bin"));
        Assert.That(metadata.Files[1].Length, Is.EqualTo(16384));

        // Parse back and verify
        var parsed = TorrentParser.Parse(torrentBytes);
        Assert.That(parsed.InfoHash, Is.EqualTo(metadata.InfoHash));
        Assert.That(parsed.Files.Length, Is.EqualTo(2));
    }

    [Test]
    public void CreateFromBytes_WithTrackers_IncludesAnnounce()
    {
        var data = new byte[1024];

        var (torrentBytes, metadata) = TorrentCreator.CreateFromBytes("small.bin", data, new TorrentCreatorOptions
        {
            HashAlgorithm = "SHA-1",
            Trackers = new[] { "wss://tracker1.example.com/announce", "wss://tracker2.example.com/announce" },
        });

        var parsed = TorrentParser.Parse(torrentBytes);
        Assert.That(parsed.AnnounceUrls, Is.Not.Null);
        Assert.That(parsed.AnnounceUrls!.Length, Is.GreaterThanOrEqualTo(1));
    }

    [Test]
    public void CreateFromBytes_PieceHashesMatchManualHash()
    {
        var data = new byte[16384];
        Random.Shared.NextBytes(data);

        var (_, metadata) = TorrentCreator.CreateFromBytes("hash-test.bin", data, new TorrentCreatorOptions
        {
            HashAlgorithm = "SHA-1",
            PieceLength = 16384,
        });

        // Manually hash the same data
        var expected = System.Security.Cryptography.SHA1.HashData(data);
        Assert.That(metadata.PieceHashes[0], Is.EqualTo(expected));
    }

    [Test]
    public async Task CreateFromStream_MatchesCreateFromBytes()
    {
        var data = new byte[32768];
        Random.Shared.NextBytes(data);

        var opts = new TorrentCreatorOptions { HashAlgorithm = "SHA-1", PieceLength = 16384 };
        var (bytesResult, bytesMeta) = TorrentCreator.CreateFromBytes("stream-test.bin", data, opts);

        using var ms = new MemoryStream(data);
        var (streamResult, streamMeta) = await TorrentCreator.CreateFromStreamAsync("stream-test.bin", ms, data.Length, opts);

        Assert.That(streamMeta.InfoHash, Is.EqualTo(bytesMeta.InfoHash));
        Assert.That(streamMeta.PieceCount, Is.EqualTo(bytesMeta.PieceCount));
        for (int i = 0; i < bytesMeta.PieceCount; i++)
            Assert.That(streamMeta.PieceHashes[i], Is.EqualTo(bytesMeta.PieceHashes[i]));
    }
}
