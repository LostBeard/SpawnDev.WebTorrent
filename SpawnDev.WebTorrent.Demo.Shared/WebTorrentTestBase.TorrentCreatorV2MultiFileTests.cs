using System.Text;
using SpawnDev.UnitTesting;
using SpawnDev.WebTorrent;

namespace SpawnDev.WebTorrent.Demo.Shared;

/// <summary>
/// BEP 52 v2 multi-file torrent tests. Phase 2a step 3 adds multi-file v2 output to
/// TorrentCreator.CreateFromMultipleFiles. No per-file piece alignment here (Phase 2b),
/// so the output is pure-v2 only - not safe for hybrid v1+v2 consumption.
/// Migrated from NUnit SpawnDev.WebTorrent.Tests/TorrentCreatorV2MultiFileTests.cs so these
/// run under PlaywrightMultiTest (browser + desktop) rather than desktop-only NUnit.
/// </summary>
public abstract partial class WebTorrentTestBase
{
    [TestMethod]
    public async Task MultiFileV2_FlatFiles_ProducesFileTreeWithMultipleLeaves()
    {
        var a = TorrentCreatorV2MultiFileTests_RandomBytes(500, 2001);
        var b = TorrentCreatorV2MultiFileTests_RandomBytes(2500, 2002);
        var files = new[] { ("a.bin", a), ("b.bin", b) };
        var opts = new TorrentCreatorOptions { MetaVersion = 2, PieceLength = 16384 };

        var (_, meta) = TorrentCreator.CreateFromMultipleFiles("flat", files, opts);

        if (meta.MetaVersion != 2) throw new Exception($"MetaVersion should be 2, got {meta.MetaVersion}");
        if (meta.Files.Length != 2) throw new Exception($"Files.Length should be 2, got {meta.Files.Length}");
        if (meta.FileRoots.Length != 2) throw new Exception($"FileRoots.Length should be 2, got {meta.FileRoots.Length}");
        if (!meta.FileRoots[0].SequenceEqual(MerkleHasher.ComputeFileRoot(a, 16384)))
            throw new Exception("FileRoots[0] mismatch vs MerkleHasher.ComputeFileRoot(a)");
        if (!meta.FileRoots[1].SequenceEqual(MerkleHasher.ComputeFileRoot(b, 16384)))
            throw new Exception("FileRoots[1] mismatch vs MerkleHasher.ComputeFileRoot(b)");

        // Both files < pieceLength (16 KiB), so no piece layers entries.
        if (meta.PieceLayers.Count != 0)
            throw new Exception($"PieceLayers.Count should be 0, got {meta.PieceLayers.Count}");

        if (meta.TotalLength != a.Length + b.Length)
            throw new Exception($"TotalLength mismatch, got {meta.TotalLength}, expected {a.Length + b.Length}");
        // BEP 52: file offsets in v2 multi-file torrents are in the PADDED virtual stream
        // (each file starts on a piece boundary). a is 500 bytes + implicit zero-pad to
        // pieceLength (16384) so b starts at 16384, not at 500. This is the global-piece-
        // index addressing spec requires for wire-level piece messages.
        if (meta.Files[0].Offset != 0)
            throw new Exception($"Files[0].Offset should be 0, got {meta.Files[0].Offset}");
        if (meta.Files[1].Offset != 16384)
            throw new Exception($"Files[1].Offset should be 16384, got {meta.Files[1].Offset}");
        await Task.CompletedTask;
    }

    [TestMethod]
    public async Task MultiFileV2_NestedDirectories_SerializesCorrectTreeShape()
    {
        var files = new[]
        {
            ("dir1/sub/first.bin", TorrentCreatorV2MultiFileTests_RandomBytes(300, 2003)),
            ("dir1/sub/second.bin", TorrentCreatorV2MultiFileTests_RandomBytes(300, 2004)),
            ("dir1/loose.bin", TorrentCreatorV2MultiFileTests_RandomBytes(300, 2005)),
            ("root.bin", TorrentCreatorV2MultiFileTests_RandomBytes(300, 2006)),
        };
        var opts = new TorrentCreatorOptions { MetaVersion = 2, PieceLength = 16384 };

        var (bytes, meta) = TorrentCreator.CreateFromMultipleFiles("deep", files, opts);
        var asText = Encoding.ASCII.GetString(bytes);

        // Parent directory keys must appear in the serialized torrent bytes.
        if (!asText.Contains("4:dir1")) throw new Exception("missing key '4:dir1'");
        if (!asText.Contains("3:sub")) throw new Exception("missing key '3:sub'");
        if (!asText.Contains("9:first.bin")) throw new Exception("missing key '9:first.bin'");
        if (!asText.Contains("10:second.bin")) throw new Exception("missing key '10:second.bin'");
        if (!asText.Contains("9:loose.bin")) throw new Exception("missing key '9:loose.bin'");
        if (!asText.Contains("8:root.bin")) throw new Exception("missing key '8:root.bin'");
        if (meta.Files.Length != 4) throw new Exception($"Files.Length should be 4, got {meta.Files.Length}");
        await Task.CompletedTask;
    }

    [TestMethod]
    public async Task MultiFileV2_MixedLargeAndSmall_OnlyLargeFilesHavePieceLayers()
    {
        int pieceLen = 16384;
        var small = TorrentCreatorV2MultiFileTests_RandomBytes(5000, 2007);           // < pieceLen, no piece layer entry
        var large = TorrentCreatorV2MultiFileTests_RandomBytes(pieceLen * 3, 2008);   // 3 pieces, gets entry
        var files = new[] { ("small.bin", small), ("large.bin", large) };
        var opts = new TorrentCreatorOptions { MetaVersion = 2, PieceLength = pieceLen };

        var (_, meta) = TorrentCreator.CreateFromMultipleFiles("mix", files, opts);

        if (meta.PieceLayers.Count != 1)
            throw new Exception($"Only the >pieceLength file should carry a piece layers entry, got count {meta.PieceLayers.Count}");
        var largeRoot = MerkleHasher.ComputeFileRoot(large, pieceLen);
        if (!meta.PieceLayers.ContainsKey(largeRoot))
            throw new Exception("Piece layers key must be the large file's root");
        if (meta.PieceLayers[largeRoot].Length != 3 * 32)
            throw new Exception($"Piece layers length should be {3 * 32}, got {meta.PieceLayers[largeRoot].Length}");
        await Task.CompletedTask;
    }

    [TestMethod]
    public async Task MultiFileV2_PieceLayersSortedByKeyBytes()
    {
        // BEP 52 requires dict keys be sorted as raw byte strings. Generate several
        // multi-piece files whose content - and therefore root hashes - differ, then
        // extract the piece layers dict order from the emitted torrent bytes and verify
        // it's ascending by key bytes.
        int pieceLen = 16384;
        var files = new[]
        {
            ("a.bin", TorrentCreatorV2MultiFileTests_RandomBytes(pieceLen * 2, 2009)),
            ("b.bin", TorrentCreatorV2MultiFileTests_RandomBytes(pieceLen * 2, 2010)),
            ("c.bin", TorrentCreatorV2MultiFileTests_RandomBytes(pieceLen * 2, 2011)),
            ("d.bin", TorrentCreatorV2MultiFileTests_RandomBytes(pieceLen * 2, 2012)),
        };
        var opts = new TorrentCreatorOptions { MetaVersion = 2, PieceLength = pieceLen };

        var (bytes, meta) = TorrentCreator.CreateFromMultipleFiles("sort", files, opts);

        // Parse the torrent back and inspect the piece layers ordering via the raw bytes.
        var parsed = TorrentParser.Parse(bytes);
        if (parsed.PieceLayers.Count != 4)
            throw new Exception($"PieceLayers.Count should be 4, got {parsed.PieceLayers.Count}");

        // Verify each file root resolves to the file's piece layer in the parsed output.
        for (int i = 0; i < 4; i++)
        {
            if (!parsed.PieceLayers.ContainsKey(meta.FileRoots[i]))
                throw new Exception($"Parsed torrent missing piece layers entry for file {i}");
        }

        // Locate the piece layers section in the raw bytes and verify the first key byte
        // sequence is ascending (cheap structural check for sortedness).
        var layersMarker = Encoding.ASCII.GetBytes("12:piece layersd");
        int idx = TorrentCreatorV2MultiFileTests_IndexOfSequence(bytes, layersMarker);
        if (idx < 0) throw new Exception("piece layers dict not found in torrent bytes");

        // After "12:piece layers" and 'd' opener, each entry is "32:<32 key bytes>32:<...>".
        int cursor = idx + layersMarker.Length;
        byte[]? prev = null;
        for (int i = 0; i < 4; i++)
        {
            // key length prefix is always "32:" for a SHA-256 root.
            var prefix = Encoding.ASCII.GetString(bytes, cursor, 3);
            if (prefix != "32:")
                throw new Exception($"expected '32:' prefix, got '{prefix}'");
            cursor += 3;
            var key = new byte[32];
            Buffer.BlockCopy(bytes, cursor, key, 0, 32);
            cursor += 32;
            if (prev != null)
            {
                if (TorrentCreatorV2MultiFileTests_CompareBytes(prev, key) >= 0)
                    throw new Exception("Piece layers keys must be strictly ascending by raw byte order");
            }
            prev = key;
            // Skip the value string: "<len>:<bytes>".
            int colon = Array.IndexOf(bytes, (byte)':', cursor);
            int valLen = int.Parse(Encoding.ASCII.GetString(bytes, cursor, colon - cursor));
            cursor = colon + 1 + valLen;
        }
        await Task.CompletedTask;
    }

    [TestMethod]
    public async Task MultiFileV2_RoundTrip_FilesAndRootsPreserved()
    {
        int pieceLen = 32768;
        var files = new[]
        {
            ("models/tiny.bin", TorrentCreatorV2MultiFileTests_RandomBytes(1500, 2013)),
            ("models/medium.bin", TorrentCreatorV2MultiFileTests_RandomBytes(pieceLen * 2 + 100, 2014)),
            ("README.txt", TorrentCreatorV2MultiFileTests_RandomBytes(800, 2015)),
        };
        var opts = new TorrentCreatorOptions { MetaVersion = 2, PieceLength = pieceLen };

        var (bytes, created) = TorrentCreator.CreateFromMultipleFiles("proj", files, opts);
        var parsed = TorrentParser.Parse(bytes);

        if (parsed.MetaVersion != 2) throw new Exception($"MetaVersion should be 2, got {parsed.MetaVersion}");
        if (parsed.V2InfoHash != created.V2InfoHash)
            throw new Exception($"V2InfoHash mismatch, got {parsed.V2InfoHash}");
        if (parsed.Files.Length != 3) throw new Exception($"Files.Length should be 3, got {parsed.Files.Length}");
        if (parsed.TotalLength != created.TotalLength)
            throw new Exception($"TotalLength mismatch, got {parsed.TotalLength}");

        // File tree walk should order files by directory+name alphabetically. Input order was
        // models/tiny, models/medium, README.txt - so parsed order is README.txt, models/medium, models/tiny
        // (BEP 52 file tree keys are sorted bytewise; "R"=0x52 < "m"=0x6D so README comes first;
        // within models/, "medium" < "tiny").
        if (parsed.Files[0].Path != "README.txt")
            throw new Exception($"Files[0].Path should be 'README.txt', got '{parsed.Files[0].Path}'");
        if (parsed.Files[1].Path != "models/medium.bin")
            throw new Exception($"Files[1].Path should be 'models/medium.bin', got '{parsed.Files[1].Path}'");
        if (parsed.Files[2].Path != "models/tiny.bin")
            throw new Exception($"Files[2].Path should be 'models/tiny.bin', got '{parsed.Files[2].Path}'");

        // All roots must round-trip. Map by filename since the order differs between input and
        // parsed output.
        foreach (var file in parsed.Files)
        {
            var createdIdx = Array.FindIndex(created.Files, f => f.Path == file.Path);
            if (createdIdx < 0) throw new Exception($"Created output missing file {file.Path}");
            if (file.Length != created.Files[createdIdx].Length)
                throw new Exception($"Length mismatch for '{file.Path}', parsed={file.Length} vs created={created.Files[createdIdx].Length}");
        }

        // Piece layers entry exists only for the medium (multi-piece) file.
        if (parsed.PieceLayers.Count != 1)
            throw new Exception($"PieceLayers.Count should be 1, got {parsed.PieceLayers.Count}");
        await Task.CompletedTask;
    }

    [TestMethod]
    public async Task MultiFileV2_EmptyFileList_Throws()
    {
        var opts = new TorrentCreatorOptions { MetaVersion = 2, PieceLength = 16384 };
        try { TorrentCreator.CreateFromMultipleFiles("empty", Array.Empty<(string, byte[])>(), opts); }
        catch (ArgumentException) { await Task.CompletedTask; return; }
        throw new Exception("expected ArgumentException for empty file list");
    }

    [TestMethod]
    public async Task MultiFileV2_DuplicatePath_Throws()
    {
        var files = new[]
        {
            ("dir/file.bin", TorrentCreatorV2MultiFileTests_RandomBytes(100, 2016)),
            ("dir/file.bin", TorrentCreatorV2MultiFileTests_RandomBytes(100, 2017)),
        };
        var opts = new TorrentCreatorOptions { MetaVersion = 2, PieceLength = 16384 };
        try { TorrentCreator.CreateFromMultipleFiles("dup", files, opts); }
        catch (ArgumentException) { await Task.CompletedTask; return; }
        throw new Exception("expected ArgumentException for duplicate path");
    }

    [TestMethod]
    public async Task MultiFileV2_EmptyPathComponent_Throws()
    {
        // Leading slash => first split component is empty.
        var files = new[] { ("/dir/file.bin", TorrentCreatorV2MultiFileTests_RandomBytes(100, 2018)) };
        var opts = new TorrentCreatorOptions { MetaVersion = 2, PieceLength = 16384 };
        try { TorrentCreator.CreateFromMultipleFiles("bad", files, opts); }
        catch (ArgumentException) { await Task.CompletedTask; return; }
        throw new Exception("expected ArgumentException for empty path component");
    }

    [TestMethod]
    public async Task MultiFileV2_PathCollidingWithExistingFile_Throws()
    {
        // "dir" is a file. Then "dir/file" tries to use "dir" as a directory - conflict.
        var files = new[]
        {
            ("dir", TorrentCreatorV2MultiFileTests_RandomBytes(100, 2019)),
            ("dir/file", TorrentCreatorV2MultiFileTests_RandomBytes(100, 2020)),
        };
        var opts = new TorrentCreatorOptions { MetaVersion = 2, PieceLength = 16384 };
        try { TorrentCreator.CreateFromMultipleFiles("conflict", files, opts); }
        catch (ArgumentException) { await Task.CompletedTask; return; }
        throw new Exception("expected ArgumentException for path colliding with existing file");
    }

    [TestMethod]
    public async Task MultiFileV2_BackslashPathSeparator_Works()
    {
        // Users on Windows may pass backslash-separated paths. BEP 52 serializes forward-slash
        // but the input can be normalized by the creator.
        var files = new[] { ("dir\\file.bin", TorrentCreatorV2MultiFileTests_RandomBytes(100, 2021)) };
        var opts = new TorrentCreatorOptions { MetaVersion = 2, PieceLength = 16384 };

        var (bytes, meta) = TorrentCreator.CreateFromMultipleFiles("winpath", files, opts);
        var asText = Encoding.ASCII.GetString(bytes);
        if (!asText.Contains("3:dir")) throw new Exception("missing key '3:dir' in serialized torrent");
        if (!asText.Contains("8:file.bin")) throw new Exception("missing key '8:file.bin' in serialized torrent");
        if (meta.Files.Length != 1) throw new Exception($"Files.Length should be 1, got {meta.Files.Length}");
        await Task.CompletedTask;
    }

    [TestMethod]
    public async Task MultiFileV2_V1PathUnchanged()
    {
        // Regression: default-options multi-file still produces a v1 torrent.
        var files = new[]
        {
            ("a.bin", TorrentCreatorV2MultiFileTests_RandomBytes(100, 2022)),
            ("b.bin", TorrentCreatorV2MultiFileTests_RandomBytes(100, 2023)),
        };
        var (_, meta) = TorrentCreator.CreateFromMultipleFiles("legacy", files,
            new TorrentCreatorOptions { HashAlgorithm = "SHA-1", PieceLength = 16384 });

        if (meta.MetaVersion != 0) throw new Exception($"MetaVersion should be 0 for v1, got {meta.MetaVersion}");
        if (meta.InfoHash.Length != 40) throw new Exception($"InfoHash.Length should be 40, got {meta.InfoHash.Length}");
        if (meta.V2InfoHash != "") throw new Exception($"V2InfoHash should be empty, got '{meta.V2InfoHash}'");
        if (meta.FileRoots.Length != 0) throw new Exception($"FileRoots.Length should be 0, got {meta.FileRoots.Length}");
        await Task.CompletedTask;
    }

    // ---- helpers ----

    private static byte[] TorrentCreatorV2MultiFileTests_RandomBytes(int n, int seed)
    {
        var b = new byte[n];
        new Random(seed).NextBytes(b);
        return b;
    }

    private static int TorrentCreatorV2MultiFileTests_IndexOfSequence(byte[] haystack, byte[] needle)
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

    private static int TorrentCreatorV2MultiFileTests_CompareBytes(byte[] a, byte[] b)
    {
        int len = Math.Min(a.Length, b.Length);
        for (int i = 0; i < len; i++)
        {
            int d = a[i] - b[i];
            if (d != 0) return d;
        }
        return a.Length - b.Length;
    }
}
