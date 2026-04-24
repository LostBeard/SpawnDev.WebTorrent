using SpawnDev.UnitTesting;
using SpawnDev.WebTorrent;

namespace SpawnDev.WebTorrent.Demo.Shared;

/// <summary>
/// Streaming v2 multi-file torrent creator tests. Per-file IncrementalMerkleHasher so
/// large multi-file torrents (HuggingFace model shards) can be hashed without buffering
/// the whole content in RAM. Verified by comparing the streaming output to the in-memory
/// output for the same content — they must produce byte-identical info dicts and piece
/// hashes because both paths use the same Merkle primitives underneath.
/// </summary>
public abstract partial class WebTorrentTestBase
{
    [TestMethod]
    public async Task CreatorStream_MultiFile_MatchesInMemory_TwoFiles()
    {
        var file1 = MakeDeterministicData(16384 * 3, seed: 6001);  // 3-piece file at 16 KiB
        var file2 = MakeDeterministicData(16384 * 5 + 777, seed: 6002);
        var opts = new TorrentCreatorOptions { MetaVersion = 2, PieceLength = 16384 };

        var (_, inMem) = TorrentCreator.CreateFromMultipleFiles(
            "sharded",
            new[] { ("shards/a.bin", file1), ("shards/b.bin", file2) },
            opts);

        using var s1 = new MemoryStream(file1);
        using var s2 = new MemoryStream(file2);
        var (_, streamed) = await TorrentCreator.CreateFromMultipleStreamsAsync(
            "sharded",
            new[]
            {
                ("shards/a.bin", (Stream)s1, (long)file1.Length),
                ("shards/b.bin", (Stream)s2, (long)file2.Length),
            },
            opts);

        // The V2 info hash is the ultimate interop anchor — if it matches, every
        // downstream derivation (piece layers, info dict bytes, file tree) matches too.
        if (streamed.V2InfoHash != inMem.V2InfoHash)
            throw new Exception($"V2InfoHash mismatch: streamed={streamed.V2InfoHash}, inMem={inMem.V2InfoHash}");
        if (streamed.PieceCount != inMem.PieceCount)
            throw new Exception($"PieceCount mismatch: streamed={streamed.PieceCount}, inMem={inMem.PieceCount}");
        if (streamed.FileRoots.Length != inMem.FileRoots.Length)
            throw new Exception($"FileRoots length mismatch");
        for (int i = 0; i < streamed.FileRoots.Length; i++)
        {
            if (!streamed.FileRoots[i].SequenceEqual(inMem.FileRoots[i]))
                throw new Exception($"FileRoots[{i}] mismatch");
        }
        if (streamed.PieceLayers.Count != inMem.PieceLayers.Count)
            throw new Exception($"PieceLayers dict count mismatch");
        for (int i = 0; i < streamed.PieceHashes.Length; i++)
        {
            if (!streamed.PieceHashes[i].SequenceEqual(inMem.PieceHashes[i]))
                throw new Exception($"PieceHashes[{i}] mismatch at piece {i}");
        }
    }

    [TestMethod]
    public async Task CreatorStream_MultiFile_RoundTripsThroughParser()
    {
        // End-to-end: streaming create -> parse the bytes back -> all v2 fields survive.
        var file1 = MakeDeterministicData(65536 + 1234, seed: 6011);
        var file2 = MakeDeterministicData(20000, seed: 6012);
        var file3 = MakeDeterministicData(65536 * 4 + 500, seed: 6013);
        var opts = new TorrentCreatorOptions { MetaVersion = 2, PieceLength = 65536 };

        using var s1 = new MemoryStream(file1);
        using var s2 = new MemoryStream(file2);
        using var s3 = new MemoryStream(file3);
        var (bytes, created) = await TorrentCreator.CreateFromMultipleStreamsAsync(
            "model",
            new[]
            {
                ("model/config.json", (Stream)s2, (long)file2.Length),
                ("model/shard-00001.safetensors", (Stream)s1, (long)file1.Length),
                ("model/shard-00002.safetensors", (Stream)s3, (long)file3.Length),
            },
            opts);

        var parsed = TorrentParser.Parse(bytes);

        if (parsed.MetaVersion != 2) throw new Exception($"MetaVersion={parsed.MetaVersion}");
        if (parsed.V2InfoHash != created.V2InfoHash) throw new Exception("V2InfoHash round-trip mismatch");
        if (parsed.Files.Length != 3) throw new Exception($"Files.Length={parsed.Files.Length}, expected 3");
        if (parsed.TotalLength != file1.Length + file2.Length + file3.Length)
            throw new Exception($"TotalLength={parsed.TotalLength}");

        // File tree walk order is bytewise — "config.json" sorts before "shard-" because
        // '.' (0x2E) < 'c' (0x63) wait actually c<s so shard... hmm these are full path
        // prefixes. "model/config.json" vs "model/shard-..." — 'c' < 's', so config comes
        // first. Assert that (both sides should see the same file order).
        if (parsed.Files[0].Path != "model/config.json")
            throw new Exception($"Files[0].Path={parsed.Files[0].Path}, expected model/config.json");
    }

    [TestMethod]
    public async Task CreatorStream_MultiFile_SlowStream_SameResult()
    {
        // Feeds bytes in small chunks to exercise the IncrementalMerkleHasher's
        // partial-leaf accumulation. Result must still match the in-memory variant.
        var file1 = MakeDeterministicData(32768 + 500, seed: 6021);
        var file2 = MakeDeterministicData(32768 * 2, seed: 6022);
        var opts = new TorrentCreatorOptions { MetaVersion = 2, PieceLength = 32768 };

        var (_, inMem) = TorrentCreator.CreateFromMultipleFiles(
            "slow",
            new[] { ("a.bin", file1), ("b.bin", file2) },
            opts);

        using var slow1 = new TorrentCreatorStreamingMultiFileTests_SmallChunkStream(file1, 333);
        using var slow2 = new TorrentCreatorStreamingMultiFileTests_SmallChunkStream(file2, 97);
        var (_, streamed) = await TorrentCreator.CreateFromMultipleStreamsAsync(
            "slow",
            new[]
            {
                ("a.bin", (Stream)slow1, (long)file1.Length),
                ("b.bin", (Stream)slow2, (long)file2.Length),
            },
            opts);

        if (streamed.V2InfoHash != inMem.V2InfoHash)
            throw new Exception($"slow-stream V2InfoHash diverged from in-memory: streamed={streamed.V2InfoHash}, inMem={inMem.V2InfoHash}");
    }

    [TestMethod]
    public async Task CreatorStream_MultiFile_DeclaredLengthMismatch_Throws()
    {
        // If a consumer lies about the stream length, the creator should refuse rather
        // than produce a torrent with a file-tree entry that disagrees with the actual
        // hashed bytes (which would make every downstream parse/verify step inconsistent).
        var file1 = MakeDeterministicData(16384, seed: 6031);
        using var s1 = new MemoryStream(file1);
        var opts = new TorrentCreatorOptions { MetaVersion = 2, PieceLength = 16384 };

        try
        {
            await TorrentCreator.CreateFromMultipleStreamsAsync(
                "bad-length",
                new[] { ("only.bin", (Stream)s1, /* declared */ (long)(file1.Length + 100)) },
                opts);
        }
        catch (InvalidOperationException) { return; }
        throw new Exception("expected InvalidOperationException when declared length disagrees with stream content");
    }

    [TestMethod]
    public async Task CreatorStream_MultiFile_HybridOption_Throws()
    {
        var file1 = MakeDeterministicData(16384, seed: 6041);
        using var s1 = new MemoryStream(file1);
        var opts = new TorrentCreatorOptions { MetaVersion = 2, Hybrid = true, PieceLength = 16384 };

        try
        {
            await TorrentCreator.CreateFromMultipleStreamsAsync(
                "hybrid",
                new[] { ("only.bin", (Stream)s1, (long)file1.Length) },
                opts);
        }
        catch (ArgumentException) { return; }
        throw new Exception("expected ArgumentException when Hybrid=true is requested (streaming hybrid multi-file not supported)");
    }

    [TestMethod]
    public async Task CreatorStream_MultiFile_V1Option_Throws()
    {
        // Streaming multi-file is v2-only. V1 should route through CreateFromMultipleFiles.
        var file1 = MakeDeterministicData(16384, seed: 6051);
        using var s1 = new MemoryStream(file1);
        var opts = new TorrentCreatorOptions { MetaVersion = 0 };

        try
        {
            await TorrentCreator.CreateFromMultipleStreamsAsync(
                "v1",
                new[] { ("only.bin", (Stream)s1, (long)file1.Length) },
                opts);
        }
        catch (ArgumentException) { return; }
        throw new Exception("expected ArgumentException when MetaVersion != 2 requested");
    }

    /// <summary>
    /// Stream that hands out at most <c>chunkSize</c> bytes per read regardless of ask,
    /// to exercise the IncrementalMerkleHasher's partial-leaf accumulation paths.
    /// </summary>
    private sealed class TorrentCreatorStreamingMultiFileTests_SmallChunkStream : Stream
    {
        private readonly byte[] _data;
        private readonly int _chunkSize;
        private int _pos;
        public TorrentCreatorStreamingMultiFileTests_SmallChunkStream(byte[] data, int chunkSize) { _data = data; _chunkSize = chunkSize; }
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
