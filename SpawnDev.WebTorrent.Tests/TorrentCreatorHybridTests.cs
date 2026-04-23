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

    [Test]
    public void Hybrid_MultiFile_BothInfoHashesPopulated()
    {
        var files = new[]
        {
            ("a.bin", RandomBytes(500)),
            ("b.bin", RandomBytes(2500)),
            ("c.bin", RandomBytes(1500)),
        };
        var opts = new TorrentCreatorOptions { MetaVersion = 2, Hybrid = true, PieceLength = 16384 };

        var (_, meta) = TorrentCreator.CreateFromMultipleFiles("multi", files, opts);

        Assert.That(meta.MetaVersion, Is.EqualTo(2));
        Assert.That(meta.InfoHash.Length, Is.EqualTo(40), "v1 SHA-1 hex");
        Assert.That(meta.V2InfoHash.Length, Is.EqualTo(64), "v2 SHA-256 hex");
        Assert.That(meta.InfoHash, Is.Not.EqualTo(meta.V2InfoHash));
        Assert.That(meta.FileRoots.Length, Is.EqualTo(3));
        Assert.That(meta.Files.Length, Is.EqualTo(3), "Metadata Files list contains only real files (pad entries filtered out)");
    }

    [Test]
    public void Hybrid_MultiFile_EmitsPadFilesInInfoDict()
    {
        // Two files where the first does not end on a piece boundary => the first file
        // should be followed by a pad file entry with attr=p in the v1 files list.
        int pieceLen = 16384;
        var files = new[]
        {
            ("first.bin", RandomBytes(1000)),   // 1000 bytes, short of 16384
            ("second.bin", RandomBytes(500)),
        };
        var opts = new TorrentCreatorOptions { MetaVersion = 2, Hybrid = true, PieceLength = pieceLen };

        var (bytes, _) = TorrentCreator.CreateFromMultipleFiles("pad", files, opts);
        var asText = Encoding.ASCII.GetString(bytes);

        // Pad file path should be present as ".pad" / "<padLen>" where padLen = 16384 - 1000 = 15384.
        Assert.That(asText, Does.Contain("4:.pad"), "Pad file path[0] must be '.pad'");
        Assert.That(asText, Does.Contain("5:15384"), "Pad file path[1] must be padLen as decimal string");
        Assert.That(asText, Does.Contain("4:attrd") | Does.Contain("4:attr1:p"), "Pad file entry carries attr=p");
    }

    [Test]
    public void Hybrid_MultiFile_NoPadAfterLastFile()
    {
        // Last file ends mid-piece but MUST NOT get a pad file after it (v1 handles the
        // partial last piece naturally).
        int pieceLen = 16384;
        var files = new[]
        {
            ("a.bin", RandomBytes(pieceLen)),  // exactly one piece, no pad after
            ("last.bin", RandomBytes(500)),    // partial last piece, NO pad after this
        };
        var opts = new TorrentCreatorOptions { MetaVersion = 2, Hybrid = true, PieceLength = pieceLen };

        var (bytes, _) = TorrentCreator.CreateFromMultipleFiles("lastpart", files, opts);
        var asText = Encoding.ASCII.GetString(bytes);

        // There should be no ".pad" anywhere (first file is piece-aligned, second is last).
        Assert.That(asText, Does.Not.Contain("4:.pad"), "No pad file should appear when first file is piece-aligned and second is last");
    }

    [Test]
    public void Hybrid_MultiFile_V1PiecesCoverPaddedStream()
    {
        // Re-derive expected v1 piece hashes from the padded virtual stream and verify the
        // pieces concatenation in the info dict matches. This is the end-to-end correctness
        // check that v1 clients can verify pieces of the hybrid torrent.
        int pieceLen = 16384;
        var a = RandomBytes(1000);
        var b = RandomBytes(500);
        var opts = new TorrentCreatorOptions { MetaVersion = 2, Hybrid = true, PieceLength = pieceLen };

        var (_, meta) = TorrentCreator.CreateFromMultipleFiles("virt",
            new[] { ("a.bin", a), ("b.bin", b) }, opts);

        // File a: 1000 bytes padded to 16384 = one piece (1000 real bytes + 15384 zeros).
        // File b: 500 bytes, last file = partial piece (500 real bytes, no pad).
        var piece0 = new byte[pieceLen];
        a.AsSpan().CopyTo(piece0);
        var expectedA = SHA1.HashData(piece0);
        var expectedB = SHA1.HashData(b);

        var expectedConcat = new byte[40];
        Buffer.BlockCopy(expectedA, 0, expectedConcat, 0, 20);
        Buffer.BlockCopy(expectedB, 0, expectedConcat, 20, 20);

        int idx = IndexOfSequence(meta.InfoDictBytes!, expectedConcat);
        Assert.That(idx, Is.GreaterThanOrEqualTo(0), "v1 pieces concatenation must match the padded-stream SHA-1s");
    }

    [Test]
    public void Hybrid_MultiFile_RoundTripThroughParser()
    {
        int pieceLen = 32768;
        var files = new[]
        {
            ("dir/small.bin", RandomBytes(200)),
            ("dir/big.bin", RandomBytes(pieceLen * 2 + 500)),
            ("readme.txt", RandomBytes(900)),
        };
        var opts = new TorrentCreatorOptions { MetaVersion = 2, Hybrid = true, PieceLength = pieceLen };

        var (bytes, created) = TorrentCreator.CreateFromMultipleFiles("rt", files, opts);
        var parsed = TorrentParser.Parse(bytes);

        Assert.That(parsed.MetaVersion, Is.EqualTo(2));
        Assert.That(parsed.InfoHash, Is.EqualTo(created.InfoHash));
        Assert.That(parsed.V2InfoHash, Is.EqualTo(created.V2InfoHash));
        Assert.That(parsed.FileRoots.Length, Is.EqualTo(3));
        // v2 file tree walk orders files alphabetically; verify all 3 real files roundtrip.
        Assert.That(parsed.Files.Select(f => f.Path).OrderBy(p => p, StringComparer.Ordinal),
            Is.EqualTo(new[] { "dir/big.bin", "dir/small.bin", "readme.txt" }));
    }

    [Test]
    public void Hybrid_MultiFile_PieceAlignedFileNeedsNoPad()
    {
        // First file is exactly one piece - no pad before the second. Second is mid-piece but
        // is also the last file - no pad after it. Info dict should contain zero ".pad" entries.
        int pieceLen = 16384;
        var files = new[]
        {
            ("aligned.bin", RandomBytes(pieceLen)),
            ("tail.bin", RandomBytes(500)),
        };
        var opts = new TorrentCreatorOptions { MetaVersion = 2, Hybrid = true, PieceLength = pieceLen };

        var (bytes, _) = TorrentCreator.CreateFromMultipleFiles("aligned", files, opts);
        var asText = Encoding.ASCII.GetString(bytes);
        Assert.That(asText, Does.Not.Contain("4:.pad"));
    }

    [Test]
    public async Task Hybrid_Streaming_MatchesInMemory_SinglePiece()
    {
        var data = RandomBytes(8000);
        var opts = new TorrentCreatorOptions { MetaVersion = 2, Hybrid = true, PieceLength = 16384 };

        var (_, fromBytes) = TorrentCreator.CreateFromBytes("stream.bin", data, opts);
        using var ms = new MemoryStream(data);
        var (_, fromStream) = await TorrentCreator.CreateFromStreamAsync("stream.bin", ms, data.Length, opts);

        Assert.That(fromStream.InfoHash, Is.EqualTo(fromBytes.InfoHash),
            "v1 SHA-1 infohash must match between streaming and in-memory paths");
        Assert.That(fromStream.V2InfoHash, Is.EqualTo(fromBytes.V2InfoHash),
            "v2 SHA-256 infohash must match between streaming and in-memory paths");
        Assert.That(fromStream.FileRoots[0], Is.EqualTo(fromBytes.FileRoots[0]));
    }

    [Test]
    public async Task Hybrid_Streaming_MatchesInMemory_MultiPiece()
    {
        int pieceLen = 32768;
        var data = RandomBytes(pieceLen * 3 + 777); // awkward boundary
        var opts = new TorrentCreatorOptions { MetaVersion = 2, Hybrid = true, PieceLength = pieceLen };

        var (_, fromBytes) = TorrentCreator.CreateFromBytes("big.bin", data, opts);
        using var ms = new MemoryStream(data);
        var (_, fromStream) = await TorrentCreator.CreateFromStreamAsync("big.bin", ms, data.Length, opts);

        Assert.That(fromStream.InfoHash, Is.EqualTo(fromBytes.InfoHash));
        Assert.That(fromStream.V2InfoHash, Is.EqualTo(fromBytes.V2InfoHash));
        Assert.That(fromStream.FileRoots[0], Is.EqualTo(fromBytes.FileRoots[0]));
        Assert.That(fromStream.PieceLayers[fromStream.FileRoots[0]],
            Is.EqualTo(fromBytes.PieceLayers[fromBytes.FileRoots[0]]));
        Assert.That(fromStream.PieceCount, Is.EqualTo(fromBytes.PieceCount));
    }

    [Test]
    public async Task Hybrid_Streaming_SlowStream_StillMatches()
    {
        // Pathological stream that returns 321 bytes max per Read - exercises the piece
        // buffer accumulation across many partial reads. Must still produce bit-identical
        // hybrid output vs in-memory.
        int pieceLen = 16384;
        var data = RandomBytes(pieceLen * 2 + 999);
        var opts = new TorrentCreatorOptions { MetaVersion = 2, Hybrid = true, PieceLength = pieceLen };

        var (_, fromBytes) = TorrentCreator.CreateFromBytes("slow.bin", data, opts);
        using var slow = new PathologicalStream(data, readSize: 321);
        var (_, fromStream) = await TorrentCreator.CreateFromStreamAsync("slow.bin", slow, data.Length, opts);

        Assert.That(fromStream.InfoHash, Is.EqualTo(fromBytes.InfoHash));
        Assert.That(fromStream.V2InfoHash, Is.EqualTo(fromBytes.V2InfoHash));
        Assert.That(fromStream.FileRoots[0], Is.EqualTo(fromBytes.FileRoots[0]));
    }

    private sealed class PathologicalStream : Stream
    {
        private readonly byte[] _data;
        private readonly int _readSize;
        private int _pos;
        public PathologicalStream(byte[] data, int readSize) { _data = data; _readSize = readSize; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _data.Length;
        public override long Position { get => _pos; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count)
        {
            int take = Math.Min(Math.Min(_readSize, count), _data.Length - _pos);
            if (take <= 0) return 0;
            Array.Copy(_data, _pos, buffer, offset, take);
            _pos += take;
            return take;
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
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
