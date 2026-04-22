using SpawnDev.UnitTesting;
using SpawnDev.WebTorrent;

namespace SpawnDev.WebTorrent.Demo.Shared;

public abstract partial class WebTorrentTestBase
{
    [TestMethod]
    public async Task Creator_FromBytes_SHA1_RoundTrip()
    {
        var data = MakeDeterministicData(65536, seed: 100);
        var (torrentBytes, meta) = TorrentCreator.CreateFromBytes("test.bin", data,
            new TorrentCreatorOptions { HashAlgorithm = "SHA-1" });
        var parsed = TorrentParser.Parse(torrentBytes);
        if (parsed.InfoHash != meta.InfoHash) throw new Exception($"InfoHash mismatch: {parsed.InfoHash} vs {meta.InfoHash}");
        if (parsed.PieceHashes == null || parsed.PieceHashes.Length == 0) throw new Exception("No piece hashes");
        if (parsed.PieceHashes[0].Length != 20) throw new Exception($"SHA-1 hash should be 20 bytes, got {parsed.PieceHashes[0].Length}");
    }

    [TestMethod]
    public async Task Creator_FromBytes_SHA256_HashSize()
    {
        var data = MakeDeterministicData(32768, seed: 200);
        var (torrentBytes, meta) = TorrentCreator.CreateFromBytes("sha256.bin", data);
        var parsed = TorrentParser.Parse(torrentBytes);
        if (parsed.PieceHashes == null || parsed.PieceHashes.Length == 0) throw new Exception("No piece hashes");
        if (parsed.PieceHashes[0].Length != 32) throw new Exception($"SHA-256 hash should be 32 bytes, got {parsed.PieceHashes[0].Length}");
    }

    [TestMethod]
    public async Task Creator_FromBytes_SHA256_RoundTrip()
    {
        // Exactly one SHA-256 piece: parse it back and verify the stored hash matches
        // a manually computed SHA-256 over the same data. Covers create + parse + BEP 52
        // Phase 1 piece-hash-algorithm detection end-to-end.
        var data = MakeDeterministicData(16384, seed: 301);
        var (torrentBytes, meta) = TorrentCreator.CreateFromBytes("sha256-round.bin", data,
            new TorrentCreatorOptions { HashAlgorithm = "SHA-256", PieceLength = 16384 });
        var parsed = TorrentParser.Parse(torrentBytes);
        if (parsed.PieceHashes == null || parsed.PieceHashes.Length != 1)
            throw new Exception($"Expected 1 piece hash, got {parsed.PieceHashes?.Length}");
        if (parsed.PieceHashAlgorithm != "SHA-256")
            throw new Exception($"Expected PieceHashAlgorithm=SHA-256, got {parsed.PieceHashAlgorithm}");
        var expected = System.Security.Cryptography.SHA256.HashData(data);
        if (!parsed.PieceHashes[0].SequenceEqual(expected))
            throw new Exception("Piece hash doesn't match manual SHA-256");
    }

    [TestMethod]
    public async Task Metadata_PieceHashAlgorithm_DetectsSha1()
    {
        var data = MakeDeterministicData(16384, seed: 302);
        var (_, meta) = TorrentCreator.CreateFromBytes("sha1.bin", data,
            new TorrentCreatorOptions { HashAlgorithm = "SHA-1" });
        if (meta.PieceHashAlgorithm != "SHA-1")
            throw new Exception($"Expected PieceHashAlgorithm=SHA-1 for 20-byte hashes, got {meta.PieceHashAlgorithm}");
    }

    [TestMethod]
    public async Task Creator_MultiFile_FileLayout()
    {
        var file1 = MakeDeterministicData(16384, seed: 1);
        var file2 = MakeDeterministicData(8192, seed: 2);
        var files = new[] { ("dir/file1.bin", file1), ("dir/file2.txt", file2) };
        var (torrentBytes, meta) = TorrentCreator.CreateFromMultipleFiles("testdir", files);
        var parsed = TorrentParser.Parse(torrentBytes);
        if (parsed.Files == null || parsed.Files.Length != 2) throw new Exception($"Expected 2 files, got {parsed.Files?.Length}");
        if (parsed.Files[0].Length != 16384) throw new Exception($"File 0 wrong length: {parsed.Files[0].Length}");
        if (parsed.Files[1].Length != 8192) throw new Exception($"File 1 wrong length: {parsed.Files[1].Length}");
    }

    [TestMethod]
    public async Task Creator_PieceHashes_MatchManualHash()
    {
        var data = MakeDeterministicData(16384, seed: 300); // exactly 1 piece
        var (torrentBytes, meta) = TorrentCreator.CreateFromBytes("hash-test.bin", data,
            new TorrentCreatorOptions { HashAlgorithm = "SHA-1" });
        var parsed = TorrentParser.Parse(torrentBytes);
        var expected = System.Security.Cryptography.SHA1.HashData(data);
        if (!parsed.PieceHashes![0].SequenceEqual(expected))
            throw new Exception("Piece hash doesn't match manual SHA1");
    }

    [TestMethod]
    public async Task Creator_Trackers_IncludedInTorrent()
    {
        var data = MakeDeterministicData(16384, seed: 400);
        var trackers = new[] { "wss://tracker.example.com/announce", "http://tracker2.example.com/announce" };
        var (torrentBytes, meta) = TorrentCreator.CreateFromBytes("tracker-test.bin", data,
            new TorrentCreatorOptions { Trackers = trackers });
        var parsed = TorrentParser.Parse(torrentBytes);
        if (parsed.AnnounceUrls == null || parsed.AnnounceUrls.Length < 2)
            throw new Exception($"Expected 2+ trackers, got {parsed.AnnounceUrls?.Length}");
    }

    [TestMethod]
    public async Task Creator_WebSeeds_IncludedInTorrent()
    {
        var data = MakeDeterministicData(16384, seed: 500);
        var seeds = new[] { "https://cdn.example.com/file.bin" };
        var (torrentBytes, meta) = TorrentCreator.CreateFromBytes("webseed-test.bin", data,
            new TorrentCreatorOptions { WebSeeds = seeds });
        var parsed = TorrentParser.Parse(torrentBytes);
        if (parsed.UrlList == null || parsed.UrlList.Length == 0)
            throw new Exception("Web seeds not in parsed torrent");
        if (!parsed.UrlList.Contains("https://cdn.example.com/file.bin"))
            throw new Exception("Expected web seed URL not found");
    }

    [TestMethod]
    public async Task Creator_FromStream_MatchesFromBytes()
    {
        var data = MakeDeterministicData(32768, seed: 600);
        var (bytesResult, bytesMeta) = TorrentCreator.CreateFromBytes("stream-test.bin", data);
        using var stream = new MemoryStream(data);
        var (streamResult, streamMeta) = await TorrentCreator.CreateFromStreamAsync("stream-test.bin", stream, data.Length);
        if (bytesMeta.InfoHash != streamMeta.InfoHash)
            throw new Exception($"InfoHash mismatch: bytes={bytesMeta.InfoHash} stream={streamMeta.InfoHash}");
    }

    [TestMethod]
    public async Task Creator_MagnetUri_IncludesHash()
    {
        var client = CreateIsolatedClient();
        var data = MakeDeterministicData(16384, seed: 700);
        var torrent = await client.SeedAsync("magnet-test.bin", data);
        var magnet = torrent.ComputedMagnetUri;
        if (string.IsNullOrEmpty(magnet)) throw new Exception("MagnetUri is null/empty");
        if (!magnet.Contains("xt=urn:btih:")) throw new Exception($"Magnet missing xt: {magnet}");
        if (!magnet.Contains("dn=")) throw new Exception($"Magnet missing dn: {magnet}");
        await client.DisposeAsync();
    }
}
