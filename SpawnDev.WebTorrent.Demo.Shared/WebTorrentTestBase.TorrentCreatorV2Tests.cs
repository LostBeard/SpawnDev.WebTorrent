using System.Security.Cryptography;
using System.Text;
using SpawnDev.UnitTesting;
using SpawnDev.WebTorrent;

namespace SpawnDev.WebTorrent.Demo.Shared;

/// <summary>
/// BEP 52 v2 single-file torrent creation + parsing round-trip tests.
/// Migrated from NUnit TorrentCreatorV2Tests.cs so they run under PlaywrightMultiTest.
/// </summary>
public abstract partial class WebTorrentTestBase
{
    [TestMethod]
    public async Task CreatorV2_FromBytes_SinglePieceFile_PopulatesV2Fields()
    {
        var data = new byte[8000];
        new Random(1001).NextBytes(data);
        var opts = new TorrentCreatorOptions { MetaVersion = 2, PieceLength = MerkleHasher.LeafSize };

        var (bytes, meta) = TorrentCreator.CreateFromBytes("tiny.bin", data, opts);

        if (meta.MetaVersion != 2) throw new Exception($"MetaVersion={meta.MetaVersion}, expected 2");
        if (meta.InfoHash != "") throw new Exception($"v2-only must have empty InfoHash, got {meta.InfoHash}");
        if (meta.V2InfoHash == null || meta.V2InfoHash.Length != 64)
            throw new Exception($"V2InfoHash must be 64 hex chars, got length {meta.V2InfoHash?.Length}");

        if (meta.FileRoots.Length != 1) throw new Exception($"FileRoots.Length={meta.FileRoots.Length}, expected 1");
        var expectedRoot = MerkleHasher.ComputeFileRoot(data, MerkleHasher.LeafSize);
        if (!meta.FileRoots[0].SequenceEqual(expectedRoot)) throw new Exception("FileRoots[0] mismatch");

        if (meta.PieceLayers.Count != 0)
            throw new Exception($"single-piece file should not carry piece layers dict, got {meta.PieceLayers.Count}");

        if (meta.PieceHashes.Length != 1) throw new Exception($"PieceHashes.Length={meta.PieceHashes.Length}, expected 1");
        if (meta.PieceHashes[0].Length != 32) throw new Exception($"PieceHashes[0].Length={meta.PieceHashes[0].Length}, expected 32");
        if (meta.PieceCount != 1) throw new Exception($"PieceCount={meta.PieceCount}, expected 1");
        if (meta.PieceHashAlgorithm != "SHA-256") throw new Exception($"PieceHashAlgorithm={meta.PieceHashAlgorithm}");
        if (meta.TotalLength != data.Length) throw new Exception($"TotalLength={meta.TotalLength}, expected {data.Length}");
        if (meta.PieceLength != MerkleHasher.LeafSize) throw new Exception($"PieceLength={meta.PieceLength}, expected {MerkleHasher.LeafSize}");
        await Task.CompletedTask;
    }

    [TestMethod]
    public async Task CreatorV2_FromBytes_MultiPieceFile_PopulatesPieceLayers()
    {
        int pieceLen = MerkleHasher.LeafSize;
        var data = new byte[pieceLen * 3 + 5000];
        new Random(1002).NextBytes(data);
        var opts = new TorrentCreatorOptions { MetaVersion = 2, PieceLength = pieceLen };

        var (_, meta) = TorrentCreator.CreateFromBytes("multi.bin", data, opts);

        var expectedRoot = MerkleHasher.ComputeFileRoot(data, pieceLen);
        if (!meta.FileRoots[0].SequenceEqual(expectedRoot)) throw new Exception("FileRoots[0] mismatch");
        if (meta.PieceLayers.Count != 1) throw new Exception($"PieceLayers.Count={meta.PieceLayers.Count}, expected 1");
        if (!meta.PieceLayers.ContainsKey(expectedRoot)) throw new Exception("piece layers key must be the file root");

        var concat = meta.PieceLayers[expectedRoot];
        if (concat.Length != 4 * 32) throw new Exception($"piece layer concat length={concat.Length}, expected 128");
        if (meta.PieceHashes.Length != 4) throw new Exception($"PieceHashes.Length={meta.PieceHashes.Length}, expected 4");
        if (meta.PieceCount != 4) throw new Exception($"PieceCount={meta.PieceCount}, expected 4");

        var expectedPieceLayer = MerkleHasher.ComputePieceLayer(data, pieceLen);
        for (int i = 0; i < 4; i++)
        {
            if (!meta.PieceHashes[i].SequenceEqual(expectedPieceLayer[i]))
                throw new Exception($"PieceHashes[{i}] mismatch");
        }
        await Task.CompletedTask;
    }

    [TestMethod]
    public async Task CreatorV2_FromBytes_InfoHash_IsSha256OfInfoDict()
    {
        var data = new byte[32768];
        new Random(1003).NextBytes(data);
        var opts = new TorrentCreatorOptions { MetaVersion = 2, PieceLength = 16384 };

        var (_, meta) = TorrentCreator.CreateFromBytes("test.bin", data, opts);

        if (meta.InfoDictBytes == null) throw new Exception("InfoDictBytes must be populated");
        var expectedHash = Convert.ToHexString(SHA256.HashData(meta.InfoDictBytes)).ToLowerInvariant();
        if (meta.V2InfoHash != expectedHash)
            throw new Exception($"V2InfoHash={meta.V2InfoHash} must equal SHA256(InfoDictBytes)={expectedHash}");
        await Task.CompletedTask;
    }

    [TestMethod]
    public async Task CreatorV2_FromBytes_TorrentBytesContainExpectedKeys()
    {
        var data = new byte[40000];
        new Random(1004).NextBytes(data);
        var opts = new TorrentCreatorOptions { MetaVersion = 2, PieceLength = 16384 };

        var (bytes, _) = TorrentCreator.CreateFromBytes("keys.bin", data, opts);
        var asText = Encoding.ASCII.GetString(bytes);

        if (!asText.Contains("9:file tree")) throw new Exception("info dict must contain file tree key");
        if (!asText.Contains("12:meta version")) throw new Exception("info dict must carry meta version");
        if (!asText.Contains("11:pieces root")) throw new Exception("file tree leaf uses pieces root key");
        if (!asText.Contains("12:piece layers"))
            throw new Exception("top-level must contain piece layers key for multi-piece file");
        await Task.CompletedTask;
    }

    [TestMethod]
    public async Task CreatorV2_FromBytes_SingleLeafFile_NoPieceLayersInTorrent()
    {
        var data = new byte[500];
        new Random(1005).NextBytes(data);
        var opts = new TorrentCreatorOptions { MetaVersion = 2, PieceLength = 16384 };

        var (bytes, _) = TorrentCreator.CreateFromBytes("small.bin", data, opts);
        var asText = Encoding.ASCII.GetString(bytes);
        if (asText.Contains("12:piece layers"))
            throw new Exception("small single-piece file must NOT emit a piece layers dict");
        await Task.CompletedTask;
    }

    [TestMethod]
    public async Task CreatorV2_RoundTrip_SinglePiece_ParsesBackIdentically()
    {
        var data = new byte[10000];
        new Random(1006).NextBytes(data);
        var opts = new TorrentCreatorOptions { MetaVersion = 2, PieceLength = 16384 };

        var (bytes, created) = TorrentCreator.CreateFromBytes("roundtrip-small.bin", data, opts);
        var parsed = TorrentParser.Parse(bytes);

        if (parsed.MetaVersion != 2) throw new Exception($"MetaVersion={parsed.MetaVersion}");
        if (parsed.V2InfoHash != created.V2InfoHash) throw new Exception($"V2InfoHash mismatch");
        if (parsed.Name != "roundtrip-small.bin") throw new Exception($"Name={parsed.Name}");
        if (parsed.PieceLength != 16384) throw new Exception($"PieceLength={parsed.PieceLength}");
        if (parsed.TotalLength != data.Length) throw new Exception($"TotalLength={parsed.TotalLength}");

        if (parsed.Files.Length != 1) throw new Exception($"Files.Length={parsed.Files.Length}");
        if (parsed.Files[0].Path != "roundtrip-small.bin") throw new Exception($"Files[0].Path={parsed.Files[0].Path}");
        if (parsed.Files[0].Length != data.Length) throw new Exception($"Files[0].Length={parsed.Files[0].Length}");

        if (parsed.FileRoots.Length != 1) throw new Exception($"FileRoots.Length={parsed.FileRoots.Length}");
        if (!parsed.FileRoots[0].SequenceEqual(created.FileRoots[0])) throw new Exception("FileRoots[0] mismatch");
        if (parsed.PieceLayers.Count != 0) throw new Exception($"PieceLayers.Count={parsed.PieceLayers.Count}");
        if (!parsed.PieceHashes[0].SequenceEqual(created.FileRoots[0]))
            throw new Exception("for single-piece file, PieceHashes[0] should equal FileRoots[0]");
        await Task.CompletedTask;
    }

    [TestMethod]
    public async Task CreatorV2_RoundTrip_MultiPiece_PreservesPieceLayers()
    {
        int pieceLen = 32768;
        var data = new byte[pieceLen * 5 + 777];
        new Random(1007).NextBytes(data);
        var opts = new TorrentCreatorOptions { MetaVersion = 2, PieceLength = pieceLen };

        var (bytes, created) = TorrentCreator.CreateFromBytes("roundtrip-big.bin", data, opts);
        var parsed = TorrentParser.Parse(bytes);

        if (parsed.MetaVersion != 2) throw new Exception($"MetaVersion={parsed.MetaVersion}");
        if (parsed.V2InfoHash != created.V2InfoHash) throw new Exception("V2InfoHash mismatch");
        if (!parsed.FileRoots[0].SequenceEqual(created.FileRoots[0])) throw new Exception("FileRoots[0] mismatch");
        if (parsed.PieceLayers.Count != 1) throw new Exception($"PieceLayers.Count={parsed.PieceLayers.Count}");

        var createdConcat = created.PieceLayers[created.FileRoots[0]];
        var parsedConcat = parsed.PieceLayers[parsed.FileRoots[0]];
        if (!parsedConcat.SequenceEqual(createdConcat)) throw new Exception("piece layers concat mismatch");

        if (parsed.PieceCount != created.PieceCount) throw new Exception("PieceCount mismatch");
        if (parsed.PieceHashes.Length != created.PieceHashes.Length) throw new Exception("PieceHashes.Length mismatch");
        for (int i = 0; i < parsed.PieceHashes.Length; i++)
        {
            if (!parsed.PieceHashes[i].SequenceEqual(created.PieceHashes[i]))
                throw new Exception($"PieceHashes[{i}] mismatch");
        }
        await Task.CompletedTask;
    }

    [TestMethod]
    public async Task CreatorV2_FromBytes_InvalidPieceSize_Throws()
    {
        var data = new byte[1000];
        var bad = new TorrentCreatorOptions { MetaVersion = 2, PieceLength = 1000 };
        try { TorrentCreator.CreateFromBytes("bad.bin", data, bad); }
        catch (ArgumentException) { await Task.CompletedTask; return; }
        throw new Exception("expected ArgumentException for non-multiple-of-16-KiB piece length");
    }

    [TestMethod]
    public async Task CreatorV2_FromBytes_NonPowerOfTwoPieceSize_Throws()
    {
        var data = new byte[200000];
        var bad = new TorrentCreatorOptions { MetaVersion = 2, PieceLength = 48 * 1024 };
        try { TorrentCreator.CreateFromBytes("bad.bin", data, bad); }
        catch (ArgumentException) { await Task.CompletedTask; return; }
        throw new Exception("expected ArgumentException for non-power-of-two piece length");
    }

    [TestMethod]
    public async Task CreatorV2_FromStream_SinglePieceFile_MatchesCreateFromBytes()
    {
        var data = new byte[10000];
        new Random(1010).NextBytes(data);
        var opts = new TorrentCreatorOptions { MetaVersion = 2, PieceLength = 16384 };

        var (_, fromBytes) = TorrentCreator.CreateFromBytes("same.bin", data, opts);
        using var ms = new MemoryStream(data);
        var (_, fromStream) = await TorrentCreator.CreateFromStreamAsync("same.bin", ms, data.Length, opts);

        if (fromStream.V2InfoHash != fromBytes.V2InfoHash) throw new Exception("V2InfoHash mismatch");
        if (!fromStream.FileRoots[0].SequenceEqual(fromBytes.FileRoots[0])) throw new Exception("FileRoots[0] mismatch");
        if (fromStream.PieceLayers.Count != fromBytes.PieceLayers.Count) throw new Exception("PieceLayers.Count mismatch");
        if (!fromStream.PieceHashes[0].SequenceEqual(fromBytes.PieceHashes[0])) throw new Exception("PieceHashes[0] mismatch");
        if (fromStream.TotalLength != data.Length) throw new Exception($"TotalLength={fromStream.TotalLength}");
    }

    [TestMethod]
    public async Task CreatorV2_FromStream_MultiPieceFile_MatchesCreateFromBytes()
    {
        int pieceLen = 32768;
        var data = new byte[pieceLen * 3 + pieceLen / 2];
        new Random(1011).NextBytes(data);
        var opts = new TorrentCreatorOptions { MetaVersion = 2, PieceLength = pieceLen };

        var (_, fromBytes) = TorrentCreator.CreateFromBytes("multi.bin", data, opts);
        using var ms = new MemoryStream(data);
        var (_, fromStream) = await TorrentCreator.CreateFromStreamAsync("multi.bin", ms, data.Length, opts);

        if (fromStream.V2InfoHash != fromBytes.V2InfoHash) throw new Exception("V2InfoHash mismatch");
        if (!fromStream.FileRoots[0].SequenceEqual(fromBytes.FileRoots[0])) throw new Exception("FileRoots[0] mismatch");
        if (fromStream.PieceCount != fromBytes.PieceCount) throw new Exception("PieceCount mismatch");
        if (fromStream.PieceLayers.Count != 1) throw new Exception("PieceLayers.Count must be 1");
        if (!fromStream.PieceLayers[fromStream.FileRoots[0]].SequenceEqual(fromBytes.PieceLayers[fromBytes.FileRoots[0]]))
            throw new Exception("piece layers mismatch");
    }

    [TestMethod]
    public async Task CreatorV2_FromStream_RoundTripThroughParser()
    {
        int pieceLen = 65536;
        var data = new byte[pieceLen * 2 + 1234];
        new Random(1012).NextBytes(data);
        var opts = new TorrentCreatorOptions { MetaVersion = 2, PieceLength = pieceLen };

        using var ms = new MemoryStream(data);
        var (bytes, created) = await TorrentCreator.CreateFromStreamAsync("rt.bin", ms, data.Length, opts);
        var parsed = TorrentParser.Parse(bytes);

        if (parsed.MetaVersion != 2) throw new Exception($"MetaVersion={parsed.MetaVersion}");
        if (parsed.V2InfoHash != created.V2InfoHash) throw new Exception("V2InfoHash mismatch");
        if (!parsed.FileRoots[0].SequenceEqual(created.FileRoots[0])) throw new Exception("FileRoots[0] mismatch");
        if (!parsed.PieceLayers[parsed.FileRoots[0]].SequenceEqual(created.PieceLayers[created.FileRoots[0]]))
            throw new Exception("piece layers mismatch");
        if (parsed.TotalLength != data.Length) throw new Exception($"TotalLength={parsed.TotalLength}");
    }

    [TestMethod]
    public async Task CreatorV2_FromStream_SlowStream_StillWorks()
    {
        int pieceLen = 32768;
        var data = new byte[100000];
        new Random(1013).NextBytes(data);
        var opts = new TorrentCreatorOptions { MetaVersion = 2, PieceLength = pieceLen };

        var (_, fromBytes) = TorrentCreator.CreateFromBytes("slow.bin", data, opts);
        using var slow = new TorrentCreatorV2Tests_SmallChunkStream(data, chunkSize: 777);
        var (_, fromStream) = await TorrentCreator.CreateFromStreamAsync("slow.bin", slow, data.Length, opts);

        if (fromStream.V2InfoHash != fromBytes.V2InfoHash) throw new Exception("V2InfoHash mismatch");
        if (!fromStream.FileRoots[0].SequenceEqual(fromBytes.FileRoots[0])) throw new Exception("FileRoots[0] mismatch");
    }

    [TestMethod]
    public async Task CreatorV2_V1Path_UnchangedByV2Support()
    {
        var data = new byte[32768];
        new Random(1014).NextBytes(data);

        var (_, meta) = TorrentCreator.CreateFromBytes("legacy.bin", data,
            new TorrentCreatorOptions { HashAlgorithm = "SHA-1", PieceLength = 16384 });

        if (meta.MetaVersion != 0) throw new Exception($"v1 torrent should report MetaVersion=0, got {meta.MetaVersion}");
        if (meta.InfoHash.Length != 40) throw new Exception($"SHA-1 info hash should be 40 hex chars, got length {meta.InfoHash.Length}");
        if (meta.V2InfoHash != "") throw new Exception($"v1 torrent should not have V2InfoHash, got {meta.V2InfoHash}");
        if (meta.FileRoots.Length != 0) throw new Exception($"v1 torrent should not have FileRoots, got count {meta.FileRoots.Length}");
        if (meta.PieceLayers.Count != 0) throw new Exception($"v1 torrent should not have PieceLayers, got count {meta.PieceLayers.Count}");
        if (meta.PieceHashes[0].Length != 20) throw new Exception($"SHA-1 piece hash should be 20 bytes, got {meta.PieceHashes[0].Length}");
        await Task.CompletedTask;
    }

    /// <summary>
    /// Stream that hands out at most <c>chunkSize</c> bytes per read regardless of ask,
    /// to exercise the IncrementalMerkleHasher's partial-leaf accumulation paths.
    /// </summary>
    private sealed class TorrentCreatorV2Tests_SmallChunkStream : Stream
    {
        private readonly byte[] _data;
        private readonly int _chunkSize;
        private int _pos;
        public TorrentCreatorV2Tests_SmallChunkStream(byte[] data, int chunkSize) { _data = data; _chunkSize = chunkSize; }
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
}
