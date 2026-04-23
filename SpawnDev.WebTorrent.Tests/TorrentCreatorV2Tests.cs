using System.Security.Cryptography;
using System.Text;
using NUnit.Framework;
using SpawnDev.WebTorrent;

namespace SpawnDev.WebTorrent.Tests;

/// <summary>
/// BEP 52 v2 single-file torrent creation + parsing round-trip tests.
///
/// Covers:
/// - Single-piece file (pieces root == file root, no piece layers entry).
/// - Multi-piece file (piece layers dict populated, PieceHashes decoded from it).
/// - v2 info hash formula (SHA-256 of info dict bytes).
/// - Round-trip: Create -> Parse -> assert all v2 metadata fields match.
/// - Unsupported entry points (stream, multi-file) throw with clear messages.
/// - Invalid piece sizes rejected.
/// </summary>
[TestFixture]
public class TorrentCreatorV2Tests
{
    [Test]
    public void CreateFromBytes_V2_SinglePieceFile_PopulatesV2Fields()
    {
        var data = new byte[8000]; // less than one 16 KiB piece
        Random.Shared.NextBytes(data);
        var opts = new TorrentCreatorOptions
        {
            MetaVersion = 2,
            PieceLength = MerkleHasher.LeafSize,
        };

        var (bytes, meta) = TorrentCreator.CreateFromBytes("tiny.bin", data, opts);

        Assert.That(meta.MetaVersion, Is.EqualTo(2));
        Assert.That(meta.InfoHash, Is.EqualTo(""), "v2-only torrent must not carry a v1 info hash");
        Assert.That(meta.V2InfoHash, Is.Not.Null.And.Length.EqualTo(64), "v2 info hash is 64 hex chars (SHA-256)");

        Assert.That(meta.FileRoots.Length, Is.EqualTo(1));
        var expectedRoot = MerkleHasher.ComputeFileRoot(data, MerkleHasher.LeafSize);
        Assert.That(meta.FileRoots[0], Is.EqualTo(expectedRoot));

        Assert.That(meta.PieceLayers.Count, Is.EqualTo(0),
            "Single-piece file does not carry a piece layers entry (pieces root IS the single piece hash)");

        Assert.That(meta.PieceHashes.Length, Is.EqualTo(1));
        Assert.That(meta.PieceHashes[0].Length, Is.EqualTo(32));
        Assert.That(meta.PieceCount, Is.EqualTo(1));
        Assert.That(meta.PieceHashAlgorithm, Is.EqualTo("SHA-256"));
        Assert.That(meta.TotalLength, Is.EqualTo(data.Length));
        Assert.That(meta.PieceLength, Is.EqualTo(MerkleHasher.LeafSize));
    }

    [Test]
    public void CreateFromBytes_V2_MultiPieceFile_PopulatesPieceLayers()
    {
        int pieceLen = MerkleHasher.LeafSize; // 16 KiB
        var data = new byte[pieceLen * 3 + 5000]; // 3 full pieces + partial 4th
        Random.Shared.NextBytes(data);
        var opts = new TorrentCreatorOptions { MetaVersion = 2, PieceLength = pieceLen };

        var (_, meta) = TorrentCreator.CreateFromBytes("multi.bin", data, opts);

        var expectedRoot = MerkleHasher.ComputeFileRoot(data, pieceLen);
        Assert.That(meta.FileRoots[0], Is.EqualTo(expectedRoot));

        Assert.That(meta.PieceLayers.Count, Is.EqualTo(1), "Multi-piece file has one piece-layer entry");
        Assert.That(meta.PieceLayers.ContainsKey(expectedRoot), Is.True,
            "Piece layers key is the file root");

        var concat = meta.PieceLayers[expectedRoot];
        Assert.That(concat.Length, Is.EqualTo(4 * 32), "4 pieces × 32-byte hashes");
        Assert.That(meta.PieceHashes.Length, Is.EqualTo(4));
        Assert.That(meta.PieceCount, Is.EqualTo(4));

        var expectedPieceLayer = MerkleHasher.ComputePieceLayer(data, pieceLen);
        for (int i = 0; i < 4; i++)
        {
            Assert.That(meta.PieceHashes[i], Is.EqualTo(expectedPieceLayer[i]),
                $"Piece hash [{i}] must match MerkleHasher.ComputePieceLayer output");
        }
    }

    [Test]
    public void CreateFromBytes_V2_InfoHash_IsSha256OfInfoDict()
    {
        var data = new byte[32768];
        Random.Shared.NextBytes(data);
        var opts = new TorrentCreatorOptions { MetaVersion = 2, PieceLength = 16384 };

        var (_, meta) = TorrentCreator.CreateFromBytes("test.bin", data, opts);

        Assert.That(meta.InfoDictBytes, Is.Not.Null);
        var expectedHash = Convert.ToHexString(SHA256.HashData(meta.InfoDictBytes!)).ToLowerInvariant();
        Assert.That(meta.V2InfoHash, Is.EqualTo(expectedHash));
    }

    [Test]
    public void CreateFromBytes_V2_TorrentBytesContainExpectedKeys()
    {
        var data = new byte[40000]; // > 16 KiB, multi-piece
        Random.Shared.NextBytes(data);
        var opts = new TorrentCreatorOptions { MetaVersion = 2, PieceLength = 16384 };

        var (bytes, _) = TorrentCreator.CreateFromBytes("keys.bin", data, opts);
        var asText = Encoding.ASCII.GetString(bytes);

        Assert.That(asText, Does.Contain("9:file tree"), "Info dict must contain file tree key");
        Assert.That(asText, Does.Contain("12:meta version"), "Info dict must carry meta version");
        Assert.That(asText, Does.Contain("11:pieces root"), "File tree leaf uses pieces root key");
        Assert.That(asText, Does.Contain("12:piece layers"), "Top-level must contain piece layers key for multi-piece file");
    }

    [Test]
    public void CreateFromBytes_V2_SingleLeafFile_NoPieceLayersInTorrent()
    {
        var data = new byte[500];
        Random.Shared.NextBytes(data);
        var opts = new TorrentCreatorOptions { MetaVersion = 2, PieceLength = 16384 };

        var (bytes, _) = TorrentCreator.CreateFromBytes("small.bin", data, opts);
        var asText = Encoding.ASCII.GetString(bytes);
        Assert.That(asText, Does.Not.Contain("12:piece layers"),
            "Small single-piece file must NOT emit a piece layers dict");
    }

    [Test]
    public void RoundTrip_V2_SinglePiece_ParsesBackIdentically()
    {
        var data = new byte[10000];
        Random.Shared.NextBytes(data);
        var opts = new TorrentCreatorOptions { MetaVersion = 2, PieceLength = 16384 };

        var (bytes, created) = TorrentCreator.CreateFromBytes("roundtrip-small.bin", data, opts);
        var parsed = TorrentParser.Parse(bytes);

        Assert.That(parsed.MetaVersion, Is.EqualTo(2));
        Assert.That(parsed.V2InfoHash, Is.EqualTo(created.V2InfoHash));
        Assert.That(parsed.Name, Is.EqualTo("roundtrip-small.bin"));
        Assert.That(parsed.PieceLength, Is.EqualTo(16384));
        Assert.That(parsed.TotalLength, Is.EqualTo(data.Length));

        Assert.That(parsed.Files.Length, Is.EqualTo(1));
        Assert.That(parsed.Files[0].Path, Is.EqualTo("roundtrip-small.bin"));
        Assert.That(parsed.Files[0].Length, Is.EqualTo(data.Length));

        Assert.That(parsed.FileRoots.Length, Is.EqualTo(1));
        Assert.That(parsed.FileRoots[0], Is.EqualTo(created.FileRoots[0]));
        Assert.That(parsed.PieceLayers.Count, Is.EqualTo(0));
        Assert.That(parsed.PieceHashes[0], Is.EqualTo(created.FileRoots[0]));
    }

    [Test]
    public void RoundTrip_V2_MultiPiece_PreservesPieceLayers()
    {
        int pieceLen = 32768; // 2 leaves per piece
        var data = new byte[pieceLen * 5 + 777]; // ~5.02 pieces, awkward boundary
        Random.Shared.NextBytes(data);
        var opts = new TorrentCreatorOptions { MetaVersion = 2, PieceLength = pieceLen };

        var (bytes, created) = TorrentCreator.CreateFromBytes("roundtrip-big.bin", data, opts);
        var parsed = TorrentParser.Parse(bytes);

        Assert.That(parsed.MetaVersion, Is.EqualTo(2));
        Assert.That(parsed.V2InfoHash, Is.EqualTo(created.V2InfoHash));
        Assert.That(parsed.FileRoots[0], Is.EqualTo(created.FileRoots[0]));
        Assert.That(parsed.PieceLayers.Count, Is.EqualTo(1));

        var createdConcat = created.PieceLayers[created.FileRoots[0]];
        var parsedConcat = parsed.PieceLayers[parsed.FileRoots[0]];
        Assert.That(parsedConcat, Is.EqualTo(createdConcat));

        Assert.That(parsed.PieceCount, Is.EqualTo(created.PieceCount));
        Assert.That(parsed.PieceHashes.Length, Is.EqualTo(created.PieceHashes.Length));
        for (int i = 0; i < parsed.PieceHashes.Length; i++)
        {
            Assert.That(parsed.PieceHashes[i], Is.EqualTo(created.PieceHashes[i]));
        }
    }

    [Test]
    public void CreateFromBytes_V2_InvalidPieceSize_Throws()
    {
        var data = new byte[1000];
        var bad = new TorrentCreatorOptions { MetaVersion = 2, PieceLength = 1000 }; // not multiple of 16 KiB

        Assert.Throws<ArgumentException>(() => TorrentCreator.CreateFromBytes("bad.bin", data, bad));
    }

    [Test]
    public void CreateFromBytes_V2_NonPowerOfTwoPieceSize_Throws()
    {
        var data = new byte[200000];
        // 48 KiB = 3 leaves per piece (multiple of leaf size but not power of two)
        var bad = new TorrentCreatorOptions { MetaVersion = 2, PieceLength = 48 * 1024 };

        Assert.Throws<ArgumentException>(() => TorrentCreator.CreateFromBytes("bad.bin", data, bad));
    }

    [Test]
    public async Task CreateFromStream_V2_SinglePieceFile_MatchesCreateFromBytes()
    {
        var data = new byte[10000]; // less than one piece
        Random.Shared.NextBytes(data);
        var opts = new TorrentCreatorOptions { MetaVersion = 2, PieceLength = 16384 };

        var (_, fromBytes) = TorrentCreator.CreateFromBytes("same.bin", data, opts);
        using var ms = new MemoryStream(data);
        var (_, fromStream) = await TorrentCreator.CreateFromStreamAsync("same.bin", ms, data.Length, opts);

        // The v2 Merkle-derived fields must match exactly. CreationDate/torrentBytes differ
        // because of the embedded timestamp, so we compare the content-deterministic fields.
        Assert.That(fromStream.V2InfoHash, Is.EqualTo(fromBytes.V2InfoHash));
        Assert.That(fromStream.FileRoots[0], Is.EqualTo(fromBytes.FileRoots[0]));
        Assert.That(fromStream.PieceLayers.Count, Is.EqualTo(fromBytes.PieceLayers.Count));
        Assert.That(fromStream.PieceHashes[0], Is.EqualTo(fromBytes.PieceHashes[0]));
        Assert.That(fromStream.TotalLength, Is.EqualTo(data.Length));
    }

    [Test]
    public async Task CreateFromStream_V2_MultiPieceFile_MatchesCreateFromBytes()
    {
        // Awkward multi-piece size: 3.5 pieces' worth of data.
        int pieceLen = 32768;
        var data = new byte[pieceLen * 3 + pieceLen / 2];
        Random.Shared.NextBytes(data);
        var opts = new TorrentCreatorOptions { MetaVersion = 2, PieceLength = pieceLen };

        var (_, fromBytes) = TorrentCreator.CreateFromBytes("multi.bin", data, opts);
        using var ms = new MemoryStream(data);
        var (_, fromStream) = await TorrentCreator.CreateFromStreamAsync("multi.bin", ms, data.Length, opts);

        Assert.That(fromStream.V2InfoHash, Is.EqualTo(fromBytes.V2InfoHash));
        Assert.That(fromStream.FileRoots[0], Is.EqualTo(fromBytes.FileRoots[0]));
        Assert.That(fromStream.PieceCount, Is.EqualTo(fromBytes.PieceCount));
        Assert.That(fromStream.PieceLayers.Count, Is.EqualTo(1));
        Assert.That(fromStream.PieceLayers[fromStream.FileRoots[0]],
            Is.EqualTo(fromBytes.PieceLayers[fromBytes.FileRoots[0]]));
    }

    [Test]
    public async Task CreateFromStream_V2_RoundTripThroughParser()
    {
        // End-to-end: streaming create -> parse the bytes back -> all v2 fields survive.
        int pieceLen = 65536;
        var data = new byte[pieceLen * 2 + 1234];
        Random.Shared.NextBytes(data);
        var opts = new TorrentCreatorOptions { MetaVersion = 2, PieceLength = pieceLen };

        using var ms = new MemoryStream(data);
        var (bytes, created) = await TorrentCreator.CreateFromStreamAsync("rt.bin", ms, data.Length, opts);
        var parsed = TorrentParser.Parse(bytes);

        Assert.That(parsed.MetaVersion, Is.EqualTo(2));
        Assert.That(parsed.V2InfoHash, Is.EqualTo(created.V2InfoHash));
        Assert.That(parsed.FileRoots[0], Is.EqualTo(created.FileRoots[0]));
        Assert.That(parsed.PieceLayers[parsed.FileRoots[0]], Is.EqualTo(created.PieceLayers[created.FileRoots[0]]));
        Assert.That(parsed.TotalLength, Is.EqualTo(data.Length));
    }

    [Test]
    public async Task CreateFromStream_V2_SlowStream_StillWorks()
    {
        // A stream that hands back small chunks exercises the IncrementalMerkleHasher's
        // partial-leaf accumulation paths through the TorrentCreator loop.
        int pieceLen = 32768;
        var data = new byte[100000];
        Random.Shared.NextBytes(data);
        var opts = new TorrentCreatorOptions { MetaVersion = 2, PieceLength = pieceLen };

        var (_, fromBytes) = TorrentCreator.CreateFromBytes("slow.bin", data, opts);
        using var slow = new SmallChunkStream(data, chunkSize: 777);
        var (_, fromStream) = await TorrentCreator.CreateFromStreamAsync("slow.bin", slow, data.Length, opts);

        Assert.That(fromStream.V2InfoHash, Is.EqualTo(fromBytes.V2InfoHash));
        Assert.That(fromStream.FileRoots[0], Is.EqualTo(fromBytes.FileRoots[0]));
    }

    /// <summary>
    /// Stream that returns at most <c>chunkSize</c> bytes per read, regardless of how much
    /// the caller asked for. Simulates network / slow-reader stream behavior. Purely a test
    /// helper; real stream classes rarely do this but framework contracts permit it.
    /// </summary>
    private sealed class SmallChunkStream : Stream
    {
        private readonly byte[] _data;
        private readonly int _chunkSize;
        private int _pos;
        public SmallChunkStream(byte[] data, int chunkSize) { _data = data; _chunkSize = chunkSize; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _data.Length;
        public override long Position { get => _pos; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count)
        {
            int take = Math.Min(Math.Min(_chunkSize, count), _data.Length - _pos);
            if (take <= 0) return 0;
            Array.Copy(_data, _pos, buffer, offset, take);
            _pos += take;
            return take;
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    [Test]
    public void CreateFromMultipleFiles_V2_Throws()
    {
        var files = new[]
        {
            ("a.bin", new byte[100]),
            ("b.bin", new byte[200]),
        };
        var opts = new TorrentCreatorOptions { MetaVersion = 2 };

        Assert.Throws<NotSupportedException>(() =>
            TorrentCreator.CreateFromMultipleFiles("multi", files, opts));
    }

    [Test]
    public void V1Path_UnchangedByV2Support()
    {
        // Default options (MetaVersion=1) must still produce a v1 torrent with a SHA-1 info
        // hash and flat piece hashes. This guards against accidental regressions in the v1
        // code path from the v2 additions.
        var data = new byte[32768];
        Random.Shared.NextBytes(data);

        var (_, meta) = TorrentCreator.CreateFromBytes("legacy.bin", data,
            new TorrentCreatorOptions { HashAlgorithm = "SHA-1", PieceLength = 16384 });

        Assert.That(meta.MetaVersion, Is.EqualTo(0), "v1 torrent should report MetaVersion=0 (absent)");
        Assert.That(meta.InfoHash.Length, Is.EqualTo(40), "SHA-1 info hash in hex is 40 chars");
        Assert.That(meta.V2InfoHash, Is.EqualTo(""));
        Assert.That(meta.FileRoots.Length, Is.EqualTo(0));
        Assert.That(meta.PieceLayers.Count, Is.EqualTo(0));
        Assert.That(meta.PieceHashes[0].Length, Is.EqualTo(20));
    }
}
