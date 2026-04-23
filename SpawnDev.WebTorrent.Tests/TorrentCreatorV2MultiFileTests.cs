using System.Text;
using NUnit.Framework;
using SpawnDev.WebTorrent;

namespace SpawnDev.WebTorrent.Tests;

/// <summary>
/// BEP 52 v2 multi-file torrent tests. Phase 2a step 3 adds multi-file v2 output to
/// TorrentCreator.CreateFromMultipleFiles. No per-file piece alignment here (Phase 2b),
/// so the output is pure-v2 only - not safe for hybrid v1+v2 consumption.
/// </summary>
[TestFixture]
public class TorrentCreatorV2MultiFileTests
{
    [Test]
    public void MultiFile_V2_FlatFiles_ProducesFileTreeWithMultipleLeaves()
    {
        var a = RandomBytes(500);
        var b = RandomBytes(2500);
        var files = new[] { ("a.bin", a), ("b.bin", b) };
        var opts = new TorrentCreatorOptions { MetaVersion = 2, PieceLength = 16384 };

        var (_, meta) = TorrentCreator.CreateFromMultipleFiles("flat", files, opts);

        Assert.That(meta.MetaVersion, Is.EqualTo(2));
        Assert.That(meta.Files.Length, Is.EqualTo(2));
        Assert.That(meta.FileRoots.Length, Is.EqualTo(2));
        Assert.That(meta.FileRoots[0], Is.EqualTo(MerkleHasher.ComputeFileRoot(a, 16384)));
        Assert.That(meta.FileRoots[1], Is.EqualTo(MerkleHasher.ComputeFileRoot(b, 16384)));

        // Both files < pieceLength (16 KiB), so no piece layers entries.
        Assert.That(meta.PieceLayers.Count, Is.EqualTo(0));

        Assert.That(meta.TotalLength, Is.EqualTo(a.Length + b.Length));
        Assert.That(meta.Files[0].Offset, Is.EqualTo(0));
        Assert.That(meta.Files[1].Offset, Is.EqualTo(a.Length));
    }

    [Test]
    public void MultiFile_V2_NestedDirectories_SerializesCorrectTreeShape()
    {
        var files = new[]
        {
            ("dir1/sub/first.bin", RandomBytes(300)),
            ("dir1/sub/second.bin", RandomBytes(300)),
            ("dir1/loose.bin", RandomBytes(300)),
            ("root.bin", RandomBytes(300)),
        };
        var opts = new TorrentCreatorOptions { MetaVersion = 2, PieceLength = 16384 };

        var (bytes, meta) = TorrentCreator.CreateFromMultipleFiles("deep", files, opts);
        var asText = Encoding.ASCII.GetString(bytes);

        // Parent directory keys must appear in the serialized torrent bytes.
        Assert.That(asText, Does.Contain("4:dir1"));
        Assert.That(asText, Does.Contain("3:sub"));
        Assert.That(asText, Does.Contain("9:first.bin"));
        Assert.That(asText, Does.Contain("10:second.bin"));
        Assert.That(asText, Does.Contain("9:loose.bin"));
        Assert.That(asText, Does.Contain("8:root.bin"));
        Assert.That(meta.Files.Length, Is.EqualTo(4));
    }

    [Test]
    public void MultiFile_V2_MixedLargeAndSmall_OnlyLargeFilesHavePieceLayers()
    {
        int pieceLen = 16384;
        var small = RandomBytes(5000);           // < pieceLen, no piece layer entry
        var large = RandomBytes(pieceLen * 3);   // 3 pieces, gets entry
        var files = new[] { ("small.bin", small), ("large.bin", large) };
        var opts = new TorrentCreatorOptions { MetaVersion = 2, PieceLength = pieceLen };

        var (_, meta) = TorrentCreator.CreateFromMultipleFiles("mix", files, opts);

        Assert.That(meta.PieceLayers.Count, Is.EqualTo(1),
            "Only the >pieceLength file should carry a piece layers entry");
        var largeRoot = MerkleHasher.ComputeFileRoot(large, pieceLen);
        Assert.That(meta.PieceLayers.ContainsKey(largeRoot), Is.True,
            "Piece layers key must be the large file's root");
        Assert.That(meta.PieceLayers[largeRoot].Length, Is.EqualTo(3 * 32));
    }

    [Test]
    public void MultiFile_V2_PieceLayersSortedByKeyBytes()
    {
        // BEP 52 requires dict keys be sorted as raw byte strings. Generate several
        // multi-piece files whose content - and therefore root hashes - differ, then
        // extract the piece layers dict order from the emitted torrent bytes and verify
        // it's ascending by key bytes.
        int pieceLen = 16384;
        var files = new[]
        {
            ("a.bin", RandomBytes(pieceLen * 2)),
            ("b.bin", RandomBytes(pieceLen * 2)),
            ("c.bin", RandomBytes(pieceLen * 2)),
            ("d.bin", RandomBytes(pieceLen * 2)),
        };
        var opts = new TorrentCreatorOptions { MetaVersion = 2, PieceLength = pieceLen };

        var (bytes, meta) = TorrentCreator.CreateFromMultipleFiles("sort", files, opts);

        // Parse the torrent back and inspect the piece layers ordering via the raw bytes.
        var parsed = TorrentParser.Parse(bytes);
        Assert.That(parsed.PieceLayers.Count, Is.EqualTo(4));

        // Verify each file root resolves to the file's piece layer in the parsed output.
        for (int i = 0; i < 4; i++)
        {
            Assert.That(parsed.PieceLayers.ContainsKey(meta.FileRoots[i]), Is.True,
                $"Parsed torrent missing piece layers entry for file {i}");
        }

        // Locate the piece layers section in the raw bytes and verify the first key byte
        // sequence is ascending (cheap structural check for sortedness).
        var layersMarker = Encoding.ASCII.GetBytes("12:piece layersd");
        int idx = IndexOfSequence(bytes, layersMarker);
        Assert.That(idx, Is.GreaterThanOrEqualTo(0), "piece layers dict not found in torrent bytes");

        // After "12:piece layers" and 'd' opener, each entry is "32:<32 key bytes>32:<...>".
        int cursor = idx + layersMarker.Length;
        byte[]? prev = null;
        for (int i = 0; i < 4; i++)
        {
            // key length prefix is always "32:" for a SHA-256 root.
            Assert.That(Encoding.ASCII.GetString(bytes, cursor, 3), Is.EqualTo("32:"));
            cursor += 3;
            var key = new byte[32];
            Buffer.BlockCopy(bytes, cursor, key, 0, 32);
            cursor += 32;
            if (prev != null)
            {
                Assert.That(CompareBytes(prev, key), Is.LessThan(0),
                    "Piece layers keys must be strictly ascending by raw byte order");
            }
            prev = key;
            // Skip the value string: "<len>:<bytes>".
            int colon = Array.IndexOf(bytes, (byte)':', cursor);
            int valLen = int.Parse(Encoding.ASCII.GetString(bytes, cursor, colon - cursor));
            cursor = colon + 1 + valLen;
        }
    }

    [Test]
    public void MultiFile_V2_RoundTrip_FilesAndRootsPreserved()
    {
        int pieceLen = 32768;
        var files = new[]
        {
            ("models/tiny.bin", RandomBytes(1500)),
            ("models/medium.bin", RandomBytes(pieceLen * 2 + 100)),
            ("README.txt", RandomBytes(800)),
        };
        var opts = new TorrentCreatorOptions { MetaVersion = 2, PieceLength = pieceLen };

        var (bytes, created) = TorrentCreator.CreateFromMultipleFiles("proj", files, opts);
        var parsed = TorrentParser.Parse(bytes);

        Assert.That(parsed.MetaVersion, Is.EqualTo(2));
        Assert.That(parsed.V2InfoHash, Is.EqualTo(created.V2InfoHash));
        Assert.That(parsed.Files.Length, Is.EqualTo(3));
        Assert.That(parsed.TotalLength, Is.EqualTo(created.TotalLength));

        // File tree walk should order files by directory+name alphabetically. Input order was
        // models/tiny, models/medium, README.txt - so parsed order is README.txt, models/medium, models/tiny
        // (BEP 52 file tree keys are sorted bytewise; "R"=0x52 < "m"=0x6D so README comes first;
        // within models/, "medium" < "tiny").
        Assert.That(parsed.Files[0].Path, Is.EqualTo("README.txt"));
        Assert.That(parsed.Files[1].Path, Is.EqualTo("models/medium.bin"));
        Assert.That(parsed.Files[2].Path, Is.EqualTo("models/tiny.bin"));

        // All roots must round-trip. Map by filename since the order differs between input and
        // parsed output.
        foreach (var file in parsed.Files)
        {
            var createdIdx = Array.FindIndex(created.Files, f => f.Path == file.Path);
            Assert.That(createdIdx, Is.GreaterThanOrEqualTo(0), $"Created output missing file {file.Path}");
            Assert.That(file.Length, Is.EqualTo(created.Files[createdIdx].Length));
        }

        // Piece layers entry exists only for the medium (multi-piece) file.
        Assert.That(parsed.PieceLayers.Count, Is.EqualTo(1));
    }

    [Test]
    public void MultiFile_V2_EmptyFileList_Throws()
    {
        var opts = new TorrentCreatorOptions { MetaVersion = 2, PieceLength = 16384 };
        Assert.Throws<ArgumentException>(() =>
            TorrentCreator.CreateFromMultipleFiles("empty", Array.Empty<(string, byte[])>(), opts));
    }

    [Test]
    public void MultiFile_V2_DuplicatePath_Throws()
    {
        var files = new[]
        {
            ("dir/file.bin", RandomBytes(100)),
            ("dir/file.bin", RandomBytes(100)),
        };
        var opts = new TorrentCreatorOptions { MetaVersion = 2, PieceLength = 16384 };
        Assert.Throws<ArgumentException>(() =>
            TorrentCreator.CreateFromMultipleFiles("dup", files, opts));
    }

    [Test]
    public void MultiFile_V2_EmptyPathComponent_Throws()
    {
        // Leading slash => first split component is empty.
        var files = new[] { ("/dir/file.bin", RandomBytes(100)) };
        var opts = new TorrentCreatorOptions { MetaVersion = 2, PieceLength = 16384 };
        Assert.Throws<ArgumentException>(() =>
            TorrentCreator.CreateFromMultipleFiles("bad", files, opts));
    }

    [Test]
    public void MultiFile_V2_PathCollidingWithExistingFile_Throws()
    {
        // "dir" is a file. Then "dir/file" tries to use "dir" as a directory - conflict.
        var files = new[]
        {
            ("dir", RandomBytes(100)),
            ("dir/file", RandomBytes(100)),
        };
        var opts = new TorrentCreatorOptions { MetaVersion = 2, PieceLength = 16384 };
        Assert.Throws<ArgumentException>(() =>
            TorrentCreator.CreateFromMultipleFiles("conflict", files, opts));
    }

    [Test]
    public void MultiFile_V2_BackslashPathSeparator_Works()
    {
        // Users on Windows may pass backslash-separated paths. BEP 52 serializes forward-slash
        // but the input can be normalized by the creator.
        var files = new[] { ("dir\\file.bin", RandomBytes(100)) };
        var opts = new TorrentCreatorOptions { MetaVersion = 2, PieceLength = 16384 };

        var (bytes, meta) = TorrentCreator.CreateFromMultipleFiles("winpath", files, opts);
        var asText = Encoding.ASCII.GetString(bytes);
        Assert.That(asText, Does.Contain("3:dir"));
        Assert.That(asText, Does.Contain("8:file.bin"));
        Assert.That(meta.Files.Length, Is.EqualTo(1));
    }

    [Test]
    public void MultiFile_V2_V1PathUnchanged()
    {
        // Regression: default-options multi-file still produces a v1 torrent.
        var files = new[] { ("a.bin", RandomBytes(100)), ("b.bin", RandomBytes(100)) };
        var (_, meta) = TorrentCreator.CreateFromMultipleFiles("legacy", files,
            new TorrentCreatorOptions { HashAlgorithm = "SHA-1", PieceLength = 16384 });

        Assert.That(meta.MetaVersion, Is.EqualTo(0));
        Assert.That(meta.InfoHash.Length, Is.EqualTo(40));
        Assert.That(meta.V2InfoHash, Is.EqualTo(""));
        Assert.That(meta.FileRoots.Length, Is.EqualTo(0));
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

    private static int CompareBytes(byte[] a, byte[] b)
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
