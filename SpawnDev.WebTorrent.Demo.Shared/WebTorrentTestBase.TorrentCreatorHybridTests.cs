using System.Security.Cryptography;
using System.Text;
using SpawnDev.UnitTesting;
using SpawnDev.WebTorrent;

namespace SpawnDev.WebTorrent.Demo.Shared;

/// <summary>
/// BEP 52 hybrid v1+v2 torrent tests. A hybrid torrent's single info dict carries both
/// the v1 flat-hash piece list AND the v2 Merkle tree, yielding two valid infohashes
/// over the same bytes (SHA-1 for v1 clients, SHA-256 for v2 clients). Phase 2b step 1
/// covers single-file hybrid (no pad files needed); multi-file hybrid comes in step 2.
/// Migrated from NUnit SpawnDev.WebTorrent.Tests/TorrentCreatorHybridTests.cs so these run
/// under PlaywrightMultiTest (browser + desktop) rather than desktop-only NUnit.
/// </summary>
public abstract partial class WebTorrentTestBase
{
    [TestMethod]
    public async Task Hybrid_SingleFile_BothInfoHashesPopulated()
    {
        var data = TorrentCreatorHybridTests_RandomBytes(40000, 3001);
        var opts = new TorrentCreatorOptions { MetaVersion = 2, Hybrid = true, PieceLength = 16384 };

        var (_, meta) = TorrentCreator.CreateFromBytes("hybrid.bin", data, opts);

        if (meta.MetaVersion != 2) throw new Exception($"MetaVersion should be 2, got {meta.MetaVersion}");
        if (meta.InfoHash is null || meta.InfoHash.Length != 40)
            throw new Exception($"v1 SHA-1 info hash hex expected 40 chars, got {meta.InfoHash?.Length ?? -1}");
        if (meta.V2InfoHash is null || meta.V2InfoHash.Length != 64)
            throw new Exception($"v2 SHA-256 info hash hex expected 64 chars, got {meta.V2InfoHash?.Length ?? -1}");
        if (meta.InfoHash == meta.V2InfoHash)
            throw new Exception("InfoHash and V2InfoHash must differ");
        if (meta.FileRoots.Length != 1) throw new Exception($"FileRoots.Length should be 1, got {meta.FileRoots.Length}");
        await Task.CompletedTask;
    }

    [TestMethod]
    public async Task Hybrid_InfoHashes_AreSha1AndSha256OfSameInfoDict()
    {
        var data = TorrentCreatorHybridTests_RandomBytes(20000, 3002);
        var opts = new TorrentCreatorOptions { MetaVersion = 2, Hybrid = true, PieceLength = 16384 };

        var (_, meta) = TorrentCreator.CreateFromBytes("match.bin", data, opts);

        if (meta.InfoDictBytes is null) throw new Exception("InfoDictBytes should not be null");
        var expectedSha1 = Convert.ToHexString(SHA1.HashData(meta.InfoDictBytes!)).ToLowerInvariant();
        var expectedSha256 = Convert.ToHexString(SHA256.HashData(meta.InfoDictBytes!)).ToLowerInvariant();
        if (meta.InfoHash != expectedSha1)
            throw new Exception($"InfoHash mismatch, expected {expectedSha1}, got {meta.InfoHash}");
        if (meta.V2InfoHash != expectedSha256)
            throw new Exception($"V2InfoHash mismatch, expected {expectedSha256}, got {meta.V2InfoHash}");
        await Task.CompletedTask;
    }

    [TestMethod]
    public async Task Hybrid_InfoDict_CarriesBothV1AndV2Keys()
    {
        var data = TorrentCreatorHybridTests_RandomBytes(40000, 3003);
        var opts = new TorrentCreatorOptions { MetaVersion = 2, Hybrid = true, PieceLength = 16384 };

        var (bytes, _) = TorrentCreator.CreateFromBytes("keys.bin", data, opts);
        var asText = Encoding.ASCII.GetString(bytes);

        // v1 keys present.
        if (!asText.Contains("6:length")) throw new Exception("v1 length key must appear");
        if (!asText.Contains("6:pieces")) throw new Exception("v1 pieces key must appear");
        // v2 keys present.
        if (!asText.Contains("9:file tree")) throw new Exception("v2 file tree key must appear");
        if (!asText.Contains("12:meta version")) throw new Exception("v2 meta version key must appear");
        if (!asText.Contains("11:pieces root")) throw new Exception("v2 pieces root key must appear");
        await Task.CompletedTask;
    }

    [TestMethod]
    public async Task Hybrid_V1Pieces_AreSha1OfPieceChunks()
    {
        // Verify the v1 pieces concatenation in the info dict matches SHA-1 over the
        // expected piece-size chunks of the input.
        int pieceLen = 16384;
        var data = TorrentCreatorHybridTests_RandomBytes(pieceLen * 3 + 500, 3004);
        var opts = new TorrentCreatorOptions { MetaVersion = 2, Hybrid = true, PieceLength = pieceLen };

        var (bytes, _) = TorrentCreator.CreateFromBytes("v1chk.bin", data, opts);
        var parsed = TorrentParser.Parse(bytes);

        // The parser's v2 post-processing overwrites PieceHashes with v2 piece layer data, so
        // we re-derive v1 pieces from the raw info dict bytes. In practice v1-only clients would
        // read the torrent from the wire using their own v1-only parsing path.
        // For this test we just assert the parser saw BOTH info hash flavors.
        if (parsed.InfoHash is null || parsed.InfoHash.Length != 40)
            throw new Exception($"InfoHash expected 40 chars, got {parsed.InfoHash?.Length ?? -1}");
        if (parsed.V2InfoHash is null || parsed.V2InfoHash.Length != 64)
            throw new Exception($"V2InfoHash expected 64 chars, got {parsed.V2InfoHash?.Length ?? -1}");
        if (parsed.MetaVersion != 2) throw new Exception($"MetaVersion should be 2, got {parsed.MetaVersion}");

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
        int idx = TorrentCreatorHybridTests_IndexOfSequence(parsed.InfoDictBytes!, expectedV1Pieces);
        if (idx < 0) throw new Exception("v1 pieces concatenation not found in info dict bytes");
        await Task.CompletedTask;
    }

    [TestMethod]
    public async Task Hybrid_RoundTrip_ViaParser_PreservesBothHashes()
    {
        int pieceLen = 32768;
        var data = TorrentCreatorHybridTests_RandomBytes(pieceLen * 2 + 777, 3005);
        var opts = new TorrentCreatorOptions { MetaVersion = 2, Hybrid = true, PieceLength = pieceLen };

        var (bytes, created) = TorrentCreator.CreateFromBytes("rt.bin", data, opts);
        var parsed = TorrentParser.Parse(bytes);

        if (parsed.MetaVersion != 2) throw new Exception($"MetaVersion should be 2, got {parsed.MetaVersion}");
        if (parsed.InfoHash != created.InfoHash) throw new Exception("InfoHash mismatch after round-trip");
        if (parsed.V2InfoHash != created.V2InfoHash) throw new Exception("V2InfoHash mismatch after round-trip");
        if (!parsed.FileRoots[0].SequenceEqual(created.FileRoots[0]))
            throw new Exception("FileRoots[0] mismatch after round-trip");
        if (!parsed.PieceLayers[parsed.FileRoots[0]].SequenceEqual(created.PieceLayers[created.FileRoots[0]]))
            throw new Exception("PieceLayers entry mismatch after round-trip");
        await Task.CompletedTask;
    }

    [TestMethod]
    public async Task Hybrid_V2Only_InfoHashEmpty()
    {
        // v2-only (Hybrid = false) produces no v1 info hash even though the underlying hasher
        // is still SHA-256. This test guards against regressing the explicit-hybrid gate.
        var data = TorrentCreatorHybridTests_RandomBytes(20000, 3006);
        var opts = new TorrentCreatorOptions { MetaVersion = 2, Hybrid = false, PieceLength = 16384 };

        var (_, meta) = TorrentCreator.CreateFromBytes("v2only.bin", data, opts);

        if (meta.InfoHash != "") throw new Exception($"v2-only torrent must not emit a v1 info hash, got '{meta.InfoHash}'");
        if (meta.V2InfoHash is null || meta.V2InfoHash.Length != 64)
            throw new Exception($"V2InfoHash expected 64 chars, got {meta.V2InfoHash?.Length ?? -1}");
        await Task.CompletedTask;
    }

    [TestMethod]
    public async Task Hybrid_V1OnlyReader_CanParseAsV1Torrent()
    {
        // Simulates a v1-only client by inspecting just the classic v1 fields from the info
        // dict bytes. This sanity-checks that the v1 view of the hybrid is structurally valid.
        var data = TorrentCreatorHybridTests_RandomBytes(40000, 3007);
        var opts = new TorrentCreatorOptions { MetaVersion = 2, Hybrid = true, PieceLength = 16384 };

        var (bytes, created) = TorrentCreator.CreateFromBytes("v1view.bin", data, opts);
        var parsed = TorrentParser.Parse(bytes);

        // The v1 infohash must be usable - i.e. be the SHA-1 of the exact info bytes, and
        // those info bytes must contain a v1-parseable length/name/piece length/pieces group.
        if (parsed.InfoHash != created.InfoHash) throw new Exception("InfoHash mismatch vs created");
        if (parsed.Name != "v1view.bin") throw new Exception($"Name should be 'v1view.bin', got '{parsed.Name}'");
        if (parsed.PieceLength != 16384) throw new Exception($"PieceLength should be 16384, got {parsed.PieceLength}");
        if (parsed.TotalLength != data.Length)
            throw new Exception($"TotalLength mismatch, got {parsed.TotalLength}, expected {data.Length}");
        // v1-only reader would see single-file form (length key present).
        var asText = Encoding.ASCII.GetString(parsed.InfoDictBytes!);
        if (!asText.Contains("6:length")) throw new Exception("info dict should contain '6:length' key");
        await Task.CompletedTask;
    }

    [TestMethod]
    public async Task Hybrid_MultiFile_BothInfoHashesPopulated()
    {
        var files = new[]
        {
            ("a.bin", TorrentCreatorHybridTests_RandomBytes(500, 3008)),
            ("b.bin", TorrentCreatorHybridTests_RandomBytes(2500, 3009)),
            ("c.bin", TorrentCreatorHybridTests_RandomBytes(1500, 3010)),
        };
        var opts = new TorrentCreatorOptions { MetaVersion = 2, Hybrid = true, PieceLength = 16384 };

        var (_, meta) = TorrentCreator.CreateFromMultipleFiles("multi", files, opts);

        if (meta.MetaVersion != 2) throw new Exception($"MetaVersion should be 2, got {meta.MetaVersion}");
        if (meta.InfoHash.Length != 40) throw new Exception($"v1 SHA-1 hex expected 40 chars, got {meta.InfoHash.Length}");
        if (meta.V2InfoHash.Length != 64) throw new Exception($"v2 SHA-256 hex expected 64 chars, got {meta.V2InfoHash.Length}");
        if (meta.InfoHash == meta.V2InfoHash) throw new Exception("InfoHash and V2InfoHash must differ");
        if (meta.FileRoots.Length != 3) throw new Exception($"FileRoots.Length should be 3, got {meta.FileRoots.Length}");
        if (meta.Files.Length != 3)
            throw new Exception($"Metadata Files list should contain only real files (pad entries filtered out); got {meta.Files.Length}");
        await Task.CompletedTask;
    }

    [TestMethod]
    public async Task Hybrid_MultiFile_EmitsPadFilesInInfoDict()
    {
        // Two files where the first does not end on a piece boundary => the first file
        // should be followed by a pad file entry with attr=p in the v1 files list.
        int pieceLen = 16384;
        var files = new[]
        {
            ("first.bin", TorrentCreatorHybridTests_RandomBytes(1000, 3011)),   // 1000 bytes, short of 16384
            ("second.bin", TorrentCreatorHybridTests_RandomBytes(500, 3012)),
        };
        var opts = new TorrentCreatorOptions { MetaVersion = 2, Hybrid = true, PieceLength = pieceLen };

        var (bytes, _) = TorrentCreator.CreateFromMultipleFiles("pad", files, opts);
        var asText = Encoding.ASCII.GetString(bytes);

        // Pad file path should be present as ".pad" / "<padLen>" where padLen = 16384 - 1000 = 15384.
        if (!asText.Contains("4:.pad")) throw new Exception("Pad file path[0] must be '.pad'");
        if (!asText.Contains("5:15384")) throw new Exception("Pad file path[1] must be padLen as decimal string");
        if (!asText.Contains("4:attrd") && !asText.Contains("4:attr1:p"))
            throw new Exception("Pad file entry must carry attr=p (expected '4:attrd' or '4:attr1:p')");
        await Task.CompletedTask;
    }

    [TestMethod]
    public async Task Hybrid_MultiFile_NoPadAfterLastFile()
    {
        // Last file ends mid-piece but MUST NOT get a pad file after it (v1 handles the
        // partial last piece naturally).
        int pieceLen = 16384;
        var files = new[]
        {
            ("a.bin", TorrentCreatorHybridTests_RandomBytes(pieceLen, 3013)),  // exactly one piece, no pad after
            ("last.bin", TorrentCreatorHybridTests_RandomBytes(500, 3014)),    // partial last piece, NO pad after this
        };
        var opts = new TorrentCreatorOptions { MetaVersion = 2, Hybrid = true, PieceLength = pieceLen };

        var (bytes, _) = TorrentCreator.CreateFromMultipleFiles("lastpart", files, opts);
        var asText = Encoding.ASCII.GetString(bytes);

        // There should be no ".pad" anywhere (first file is piece-aligned, second is last).
        if (asText.Contains("4:.pad"))
            throw new Exception("No pad file should appear when first file is piece-aligned and second is last");
        await Task.CompletedTask;
    }

    [TestMethod]
    public async Task Hybrid_MultiFile_V1PiecesCoverPaddedStream()
    {
        // Re-derive expected v1 piece hashes from the padded virtual stream and verify the
        // pieces concatenation in the info dict matches. This is the end-to-end correctness
        // check that v1 clients can verify pieces of the hybrid torrent.
        int pieceLen = 16384;
        var a = TorrentCreatorHybridTests_RandomBytes(1000, 3015);
        var b = TorrentCreatorHybridTests_RandomBytes(500, 3016);
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

        int idx = TorrentCreatorHybridTests_IndexOfSequence(meta.InfoDictBytes!, expectedConcat);
        if (idx < 0)
            throw new Exception("v1 pieces concatenation must match the padded-stream SHA-1s");
        await Task.CompletedTask;
    }

    [TestMethod]
    public async Task Hybrid_MultiFile_RoundTripThroughParser()
    {
        int pieceLen = 32768;
        var files = new[]
        {
            ("dir/small.bin", TorrentCreatorHybridTests_RandomBytes(200, 3017)),
            ("dir/big.bin", TorrentCreatorHybridTests_RandomBytes(pieceLen * 2 + 500, 3018)),
            ("readme.txt", TorrentCreatorHybridTests_RandomBytes(900, 3019)),
        };
        var opts = new TorrentCreatorOptions { MetaVersion = 2, Hybrid = true, PieceLength = pieceLen };

        var (bytes, created) = TorrentCreator.CreateFromMultipleFiles("rt", files, opts);
        var parsed = TorrentParser.Parse(bytes);

        if (parsed.MetaVersion != 2) throw new Exception($"MetaVersion should be 2, got {parsed.MetaVersion}");
        if (parsed.InfoHash != created.InfoHash) throw new Exception("InfoHash mismatch after round-trip");
        if (parsed.V2InfoHash != created.V2InfoHash) throw new Exception("V2InfoHash mismatch after round-trip");
        if (parsed.FileRoots.Length != 3) throw new Exception($"FileRoots.Length should be 3, got {parsed.FileRoots.Length}");
        // v2 file tree walk orders files alphabetically; verify all 3 real files roundtrip.
        var actualPaths = parsed.Files.Select(f => f.Path).OrderBy(p => p, StringComparer.Ordinal).ToArray();
        var expectedPaths = new[] { "dir/big.bin", "dir/small.bin", "readme.txt" };
        if (!actualPaths.SequenceEqual(expectedPaths))
            throw new Exception($"File paths mismatch. Expected [{string.Join(", ", expectedPaths)}], got [{string.Join(", ", actualPaths)}]");
        await Task.CompletedTask;
    }

    [TestMethod]
    public async Task Hybrid_MultiFile_PieceAlignedFileNeedsNoPad()
    {
        // First file is exactly one piece - no pad before the second. Second is mid-piece but
        // is also the last file - no pad after it. Info dict should contain zero ".pad" entries.
        int pieceLen = 16384;
        var files = new[]
        {
            ("aligned.bin", TorrentCreatorHybridTests_RandomBytes(pieceLen, 3020)),
            ("tail.bin", TorrentCreatorHybridTests_RandomBytes(500, 3021)),
        };
        var opts = new TorrentCreatorOptions { MetaVersion = 2, Hybrid = true, PieceLength = pieceLen };

        var (bytes, _) = TorrentCreator.CreateFromMultipleFiles("aligned", files, opts);
        var asText = Encoding.ASCII.GetString(bytes);
        if (asText.Contains("4:.pad"))
            throw new Exception("No pad file should appear when first file is piece-aligned and second is last");
        await Task.CompletedTask;
    }

    [TestMethod]
    public async Task Hybrid_Streaming_MatchesInMemory_SinglePiece()
    {
        var data = TorrentCreatorHybridTests_RandomBytes(8000, 3022);
        var opts = new TorrentCreatorOptions { MetaVersion = 2, Hybrid = true, PieceLength = 16384 };

        var (_, fromBytes) = TorrentCreator.CreateFromBytes("stream.bin", data, opts);
        using var ms = new MemoryStream(data);
        var (_, fromStream) = await TorrentCreator.CreateFromStreamAsync("stream.bin", ms, data.Length, opts);

        if (fromStream.InfoHash != fromBytes.InfoHash)
            throw new Exception("v1 SHA-1 infohash must match between streaming and in-memory paths");
        if (fromStream.V2InfoHash != fromBytes.V2InfoHash)
            throw new Exception("v2 SHA-256 infohash must match between streaming and in-memory paths");
        if (!fromStream.FileRoots[0].SequenceEqual(fromBytes.FileRoots[0]))
            throw new Exception("FileRoots[0] mismatch between streaming and in-memory paths");
    }

    [TestMethod]
    public async Task Hybrid_Streaming_MatchesInMemory_MultiPiece()
    {
        int pieceLen = 32768;
        var data = TorrentCreatorHybridTests_RandomBytes(pieceLen * 3 + 777, 3023); // awkward boundary
        var opts = new TorrentCreatorOptions { MetaVersion = 2, Hybrid = true, PieceLength = pieceLen };

        var (_, fromBytes) = TorrentCreator.CreateFromBytes("big.bin", data, opts);
        using var ms = new MemoryStream(data);
        var (_, fromStream) = await TorrentCreator.CreateFromStreamAsync("big.bin", ms, data.Length, opts);

        if (fromStream.InfoHash != fromBytes.InfoHash) throw new Exception("InfoHash mismatch vs in-memory");
        if (fromStream.V2InfoHash != fromBytes.V2InfoHash) throw new Exception("V2InfoHash mismatch vs in-memory");
        if (!fromStream.FileRoots[0].SequenceEqual(fromBytes.FileRoots[0]))
            throw new Exception("FileRoots[0] mismatch vs in-memory");
        if (!fromStream.PieceLayers[fromStream.FileRoots[0]].SequenceEqual(fromBytes.PieceLayers[fromBytes.FileRoots[0]]))
            throw new Exception("PieceLayers entry mismatch vs in-memory");
        if (fromStream.PieceCount != fromBytes.PieceCount)
            throw new Exception($"PieceCount mismatch, stream={fromStream.PieceCount}, bytes={fromBytes.PieceCount}");
    }

    [TestMethod]
    public async Task Hybrid_Streaming_SlowStream_StillMatches()
    {
        // Pathological stream that returns 321 bytes max per Read - exercises the piece
        // buffer accumulation across many partial reads. Must still produce bit-identical
        // hybrid output vs in-memory.
        int pieceLen = 16384;
        var data = TorrentCreatorHybridTests_RandomBytes(pieceLen * 2 + 999, 3024);
        var opts = new TorrentCreatorOptions { MetaVersion = 2, Hybrid = true, PieceLength = pieceLen };

        var (_, fromBytes) = TorrentCreator.CreateFromBytes("slow.bin", data, opts);
        using var slow = new TorrentCreatorHybridTests_PathologicalStream(data, readSize: 321);
        var (_, fromStream) = await TorrentCreator.CreateFromStreamAsync("slow.bin", slow, data.Length, opts);

        if (fromStream.InfoHash != fromBytes.InfoHash) throw new Exception("InfoHash mismatch vs in-memory");
        if (fromStream.V2InfoHash != fromBytes.V2InfoHash) throw new Exception("V2InfoHash mismatch vs in-memory");
        if (!fromStream.FileRoots[0].SequenceEqual(fromBytes.FileRoots[0]))
            throw new Exception("FileRoots[0] mismatch vs in-memory");
    }

    // ---- helpers ----

    private sealed class TorrentCreatorHybridTests_PathologicalStream : Stream
    {
        private readonly byte[] _data;
        private readonly int _readSize;
        private int _pos;
        public TorrentCreatorHybridTests_PathologicalStream(byte[] data, int readSize) { _data = data; _readSize = readSize; }
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

    private static byte[] TorrentCreatorHybridTests_RandomBytes(int n, int seed)
    {
        var b = new byte[n];
        new Random(seed).NextBytes(b);
        return b;
    }

    private static int TorrentCreatorHybridTests_IndexOfSequence(byte[] haystack, byte[] needle)
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
