using System.Security.Cryptography;
using System.Text;
using NUnit.Framework;
using SpawnDev.WebTorrent;

namespace SpawnDev.WebTorrent.Tests;

/// <summary>
/// BEP 52 hybrid v1+v2 torrent tests. A hybrid torrent's single info dict carries both
/// the v1 flat-hash piece list AND the v2 Merkle tree, yielding two valid infohashes
/// over the same bytes (SHA-1 for v1 clients, SHA-256 for v2 clients). Phase 2b step 1
/// covers single-file hybrid (no pad files needed); multi-file hybrid comes in step 2.
/// </summary>
[TestFixture]
public class TorrentCreatorHybridTests
{
    [Test]
    public void Hybrid_SingleFile_BothInfoHashesPopulated()
    {
        var data = RandomBytes(40000);
        var opts = new TorrentCreatorOptions { MetaVersion = 2, Hybrid = true, PieceLength = 16384 };

        var (_, meta) = TorrentCreator.CreateFromBytes("hybrid.bin", data, opts);

        Assert.That(meta.MetaVersion, Is.EqualTo(2));
        Assert.That(meta.InfoHash, Is.Not.Null.And.Length.EqualTo(40), "v1 SHA-1 info hash, hex = 40 chars");
        Assert.That(meta.V2InfoHash, Is.Not.Null.And.Length.EqualTo(64), "v2 SHA-256 info hash, hex = 64 chars");
        Assert.That(meta.InfoHash, Is.Not.EqualTo(meta.V2InfoHash));
        Assert.That(meta.FileRoots.Length, Is.EqualTo(1));
    }

    [Test]
    public void Hybrid_InfoHashes_AreSha1AndSha256OfSameInfoDict()
    {
        var data = RandomBytes(20000);
        var opts = new TorrentCreatorOptions { MetaVersion = 2, Hybrid = true, PieceLength = 16384 };

        var (_, meta) = TorrentCreator.CreateFromBytes("match.bin", data, opts);

        Assert.That(meta.InfoDictBytes, Is.Not.Null);
        var expectedSha1 = Convert.ToHexString(SHA1.HashData(meta.InfoDictBytes!)).ToLowerInvariant();
        var expectedSha256 = Convert.ToHexString(SHA256.HashData(meta.InfoDictBytes!)).ToLowerInvariant();
        Assert.That(meta.InfoHash, Is.EqualTo(expectedSha1));
        Assert.That(meta.V2InfoHash, Is.EqualTo(expectedSha256));
    }

    [Test]
    public void Hybrid_InfoDict_CarriesBothV1AndV2Keys()
    {
        var data = RandomBytes(40000);
        var opts = new TorrentCreatorOptions { MetaVersion = 2, Hybrid = true, PieceLength = 16384 };

        var (bytes, _) = TorrentCreator.CreateFromBytes("keys.bin", data, opts);
        var asText = Encoding.ASCII.GetString(bytes);

        // v1 keys present.
        Assert.That(asText, Does.Contain("6:length"), "v1 length key must appear");
        Assert.That(asText, Does.Contain("6:pieces"), "v1 pieces key must appear");
        // v2 keys present.
        Assert.That(asText, Does.Contain("9:file tree"));
        Assert.That(asText, Does.Contain("12:meta version"));
        Assert.That(asText, Does.Contain("11:pieces root"));
    }

    [Test]
    public void Hybrid_V1Pieces_AreSha1OfPieceChunks()
    {
        // Verify the v1 pieces concatenation in the info dict matches SHA-1 over the
        // expected piece-size chunks of the input.
        int pieceLen = 16384;
        var data = RandomBytes(pieceLen * 3 + 500);
        var opts = new TorrentCreatorOptions { MetaVersion = 2, Hybrid = true, PieceLength = pieceLen };

        var (bytes, _) = TorrentCreator.CreateFromBytes("v1chk.bin", data, opts);
        var parsed = TorrentParser.Parse(bytes);

        // The parser's v2 post-processing overwrites PieceHashes with v2 piece layer data, so
        // we re-derive v1 pieces from the raw info dict bytes. In practice v1-only clients would
        // read the torrent from the wire using their own v1-only parsing path.
        // For this test we just assert the parser saw BOTH info hash flavors.
        Assert.That(parsed.InfoHash, Is.Not.Null.And.Length.EqualTo(40));
        Assert.That(parsed.V2InfoHash, Is.Not.Null.And.Length.EqualTo(64));
        Assert.That(parsed.MetaVersion, Is.EqualTo(2));

        // Also verify the raw v1 pieces in the info dict by re-parsing InfoDictBytes manually.
        // There must be ceil(dataLen/pieceLen) SHA-1 hashes concatenated.
        int expectedPieceCount = (data.Length + pieceLen - 1) / pieceLen;
        var expectedV1Pieces = new byte[expectedPieceCount * 20];
        for (int i = 0; i < expectedPieceCount; i++)
        {
            int offset = i * pieceLen;
            int len = Math.Min(pieceLen, data.Length - offset);
            var sha1 = SHA1.HashData(data.AsSpan(offset, len));
            Buffer.BlockCopy(sha1, 0, expectedV1Pieces, i * 20, 20);
        }
        // Assert InfoDictBytes contains that exact sequence.
        int idx = IndexOfSequence(parsed.InfoDictBytes!, expectedV1Pieces);
        Assert.That(idx, Is.GreaterThanOrEqualTo(0), "v1 pieces concatenation not found in info dict bytes");
    }

    [Test]
    public void Hybrid_RoundTrip_ViaParser_PreservesBothHashes()
    {
        int pieceLen = 32768;
        var data = RandomBytes(pieceLen * 2 + 777);
        var opts = new TorrentCreatorOptions { MetaVersion = 2, Hybrid = true, PieceLength = pieceLen };

        var (bytes, created) = TorrentCreator.CreateFromBytes("rt.bin", data, opts);
        var parsed = TorrentParser.Parse(bytes);

        Assert.That(parsed.MetaVersion, Is.EqualTo(2));
        Assert.That(parsed.InfoHash, Is.EqualTo(created.InfoHash));
        Assert.That(parsed.V2InfoHash, Is.EqualTo(created.V2InfoHash));
        Assert.That(parsed.FileRoots[0], Is.EqualTo(created.FileRoots[0]));
        Assert.That(parsed.PieceLayers[parsed.FileRoots[0]],
            Is.EqualTo(created.PieceLayers[created.FileRoots[0]]));
    }

    [Test]
    public void Hybrid_V2Only_InfoHashEmpty()
    {
        // v2-only (Hybrid = false) produces no v1 info hash even though the underlying hasher
        // is still SHA-256. This test guards against regressing the explicit-hybrid gate.
        var data = RandomBytes(20000);
        var opts = new TorrentCreatorOptions { MetaVersion = 2, Hybrid = false, PieceLength = 16384 };

        var (_, meta) = TorrentCreator.CreateFromBytes("v2only.bin", data, opts);

        Assert.That(meta.InfoHash, Is.EqualTo(""), "v2-only torrent must not emit a v1 info hash");
        Assert.That(meta.V2InfoHash, Is.Not.Null.And.Length.EqualTo(64));
    }

    [Test]
    public void Hybrid_V1OnlyReader_CanParseAsV1Torrent()
    {
        // Simulates a v1-only client by inspecting just the classic v1 fields from the info
        // dict bytes. This sanity-checks that the v1 view of the hybrid is structurally valid.
        var data = RandomBytes(40000);
        var opts = new TorrentCreatorOptions { MetaVersion = 2, Hybrid = true, PieceLength = 16384 };

        var (bytes, created) = TorrentCreator.CreateFromBytes("v1view.bin", data, opts);
        var parsed = TorrentParser.Parse(bytes);

        // The v1 infohash must be usable - i.e. be the SHA-1 of the exact info bytes, and
        // those info bytes must contain a v1-parseable length/name/piece length/pieces group.
        Assert.That(parsed.InfoHash, Is.EqualTo(created.InfoHash));
        Assert.That(parsed.Name, Is.EqualTo("v1view.bin"));
        Assert.That(parsed.PieceLength, Is.EqualTo(16384));
        Assert.That(parsed.TotalLength, Is.EqualTo(data.Length));
        // v1-only reader would see single-file form (length key present).
        var asText = Encoding.ASCII.GetString(parsed.InfoDictBytes!);
        Assert.That(asText, Does.Contain("6:length"));
    }

    private static byte[] RandomBytes(int n)
    {
        var b = new byte[n];
        Random.Shared.NextBytes(b);
        return b;
    }

    private static int IndexOfSequence(byte[] haystack, byte[] needle)
    {
        for (int i = 0; i <= haystack.Length - needle.Length; i++)
        {
            bool match = true;
            for (int j = 0; j < needle.Length; j++)
            {
                if (haystack[i + j] != needle[j]) { match = false; break; }
            }
            if (match) return i;
        }
        return -1;
    }
}
