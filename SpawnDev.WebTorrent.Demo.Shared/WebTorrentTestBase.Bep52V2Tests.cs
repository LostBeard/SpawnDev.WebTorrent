using SpawnDev.UnitTesting;
using SpawnDev.WebTorrent;
using System.Security.Cryptography;

namespace SpawnDev.WebTorrent.Demo.Shared;

/// <summary>
/// BEP 52 v2 tests running via SpawnDev.UnitTesting / PlaywrightMultiTest so the v2
/// Merkle hasher + bencode + creator + parser paths are exercised on both desktop .NET
/// and Blazor WASM. The `SpawnDev.WebTorrent.Tests` NUnit project covers the same code
/// more comprehensively on desktop - this file's job is to prove the v2 paths behave
/// identically under the browser SHA-256 / stream / encoding stack.
/// </summary>
public abstract partial class WebTorrentTestBase
{
    // ── MerkleHasher primitive ──

    [TestMethod]
    public async Task Bep52_MerkleHasher_PadHashZeroLevelMatchesSha256OfZeros()
    {
        var expected = SHA256.HashData(new byte[MerkleHasher.LeafSize]);
        var actual = MerkleHasher.PadHashAtLevel(0);
        if (!actual.AsSpan().SequenceEqual(expected))
            throw new Exception("PadHashAtLevel(0) must equal SHA-256 of 16 KiB of zero bytes.");
        await Task.CompletedTask;
    }

    [TestMethod]
    public async Task Bep52_MerkleHasher_FileRootInvariantUnderPieceSize()
    {
        // A 200-KB file's v2 root must be the same whether computed with 16 KiB or 128 KiB
        // pieces. This catches pad-hash-level mistakes that are otherwise invisible on a
        // self-round-trip.
        var data = MakeDeterministicData(200_000, seed: 17);
        var rootAt16k = MerkleHasher.ComputeFileRoot(data, 16384);
        var rootAt128k = MerkleHasher.ComputeFileRoot(data, 131072);
        if (!rootAt16k.AsSpan().SequenceEqual(rootAt128k))
            throw new Exception("ComputeFileRoot must be piece-size invariant.");
        await Task.CompletedTask;
    }

    // ── v2 torrent creation + parsing ──

    [TestMethod]
    public async Task Bep52_V2Torrent_SingleFileRoundTrip()
    {
        var data = MakeDeterministicData(10_000, seed: 42);
        var opts = new TorrentCreatorOptions { MetaVersion = 2, PieceLength = 16384 };

        var (bytes, created) = TorrentCreator.CreateFromBytes("v2.bin", data, opts);
        var parsed = TorrentParser.Parse(bytes);

        if (parsed.MetaVersion != 2)
            throw new Exception($"Parsed MetaVersion should be 2, got {parsed.MetaVersion}");
        if (parsed.V2InfoHash.Length != 64)
            throw new Exception($"V2InfoHash hex should be 64 chars, got {parsed.V2InfoHash.Length}");
        if (!parsed.FileRoots[0].AsSpan().SequenceEqual(created.FileRoots[0]))
            throw new Exception("Parsed FileRoots[0] must match created value.");
        await Task.CompletedTask;
    }

    [TestMethod]
    public async Task Bep52_V2Torrent_MultiPieceFile_HasPieceLayers()
    {
        int pieceLen = 16384;
        var data = MakeDeterministicData(pieceLen * 3 + 500, seed: 99); // 4 pieces (last partial)
        var opts = new TorrentCreatorOptions { MetaVersion = 2, PieceLength = pieceLen };

        var (_, meta) = TorrentCreator.CreateFromBytes("multi.bin", data, opts);

        if (meta.PieceLayers.Count != 1)
            throw new Exception($"Expected one piece layers entry, got {meta.PieceLayers.Count}");
        if (meta.PieceHashes.Length != 4)
            throw new Exception($"Expected 4 piece hashes, got {meta.PieceHashes.Length}");
        if (meta.PieceHashes[0].Length != 32)
            throw new Exception($"v2 piece hashes are 32 bytes (SHA-256), got {meta.PieceHashes[0].Length}");
        await Task.CompletedTask;
    }

    // ── Hybrid v1+v2 ──

    [TestMethod]
    public async Task Bep52_Hybrid_BothInfoHashesPopulated()
    {
        var data = MakeDeterministicData(20_000, seed: 7);
        var opts = new TorrentCreatorOptions { MetaVersion = 2, Hybrid = true, PieceLength = 16384 };

        var (_, meta) = TorrentCreator.CreateFromBytes("hybrid.bin", data, opts);

        if (meta.InfoHash == null || meta.InfoHash.Length != 40)
            throw new Exception($"Hybrid must emit v1 SHA-1 infohash (40 hex chars). Got length {meta.InfoHash?.Length}");
        if (meta.V2InfoHash.Length != 64)
            throw new Exception($"Hybrid must emit v2 SHA-256 infohash (64 hex chars). Got length {meta.V2InfoHash.Length}");
        if (meta.InfoHash == meta.V2InfoHash)
            throw new Exception("SHA-1 and SHA-256 of the info dict should differ.");
        await Task.CompletedTask;
    }

    [TestMethod]
    public async Task Bep52_Hybrid_InfoHashes_AreSha1AndSha256OfInfoDict()
    {
        var data = MakeDeterministicData(5_000, seed: 123);
        var opts = new TorrentCreatorOptions { MetaVersion = 2, Hybrid = true, PieceLength = 16384 };

        var (_, meta) = TorrentCreator.CreateFromBytes("match.bin", data, opts);

        if (meta.InfoDictBytes == null) throw new Exception("InfoDictBytes should not be null");
        var expectedSha1 = Convert.ToHexString(SHA1.HashData(meta.InfoDictBytes)).ToLowerInvariant();
        var expectedSha256 = Convert.ToHexString(SHA256.HashData(meta.InfoDictBytes)).ToLowerInvariant();
        if (meta.InfoHash != expectedSha1)
            throw new Exception($"InfoHash should be SHA-1(InfoDictBytes). Expected {expectedSha1}, got {meta.InfoHash}");
        if (meta.V2InfoHash != expectedSha256)
            throw new Exception($"V2InfoHash should be SHA-256(InfoDictBytes). Expected {expectedSha256}, got {meta.V2InfoHash}");
        await Task.CompletedTask;
    }

    // ── v2 magnet URI ──

    [TestMethod]
    public async Task Bep52_Magnet_V2ParsePopulatesV2InfoHash()
    {
        const string V2Hex = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
        var t = new Torrent();
        t.ParseMagnet($"magnet:?xt=urn:btmh:1220{V2Hex}");
        if (t.V2InfoHash != V2Hex)
            throw new Exception($"V2InfoHash should be {V2Hex}, got {t.V2InfoHash}");
        await Task.CompletedTask;
    }

    [TestMethod]
    public async Task Bep52_Magnet_HybridParsePopulatesBoth()
    {
        const string V1Hex = "aaaabbbbccccddddeeeeffff1111222233334444";
        const string V2Hex = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
        var t = new Torrent();
        t.ParseMagnet($"magnet:?xt=urn:btih:{V1Hex}&xt=urn:btmh:1220{V2Hex}");
        if (t.InfoHash != V1Hex) throw new Exception($"V1 hash mismatch: {t.InfoHash}");
        if (t.V2InfoHash != V2Hex) throw new Exception($"V2 hash mismatch: {t.V2InfoHash}");
        await Task.CompletedTask;
    }
}
