using System.Security.Cryptography;
using NUnit.Framework;
using SpawnDev.WebTorrent;

namespace SpawnDev.WebTorrent.Tests;

/// <summary>
/// Tests for the piece-verification branch matrix in <see cref="Torrent.VerifyPieceHash"/>.
///
/// Critical correctness: a v2 torrent's stored "piece hash" is the Merkle root over the
/// piece's 16 KiB leaves, NOT a flat SHA-256 of the piece bytes. When PieceLength &gt;
/// LeafSize these two values differ - a flat SHA-256 comparison would silently fail every
/// piece of a large-piece-size v2 torrent. These tests prove the branching picks the right
/// algorithm per MetaVersion.
/// </summary>
[TestFixture]
public class VerifyPieceHashTests
{
    [Test]
    public void V1_FlatSha1_VerifiesPiece()
    {
        // Classic v1: MetaVersion = 0, stored hash is 20-byte SHA-1 of piece content.
        var pieceData = MakeData(65536, seed: 1);
        var expectedSha1 = SHA1.HashData(pieceData);

        var torrent = MakeTorrent(metaVersion: 0, pieceLength: 65536, hashes: new[] { expectedSha1 });
        Assert.That(torrent.VerifyPieceHash(0, pieceData), Is.True);
    }

    [Test]
    public void V1_Phase1Sha256_VerifiesPiece()
    {
        // Phase 1 "BEP 52 Phase 1" torrents: MetaVersion = 0 but stored hash is 32-byte
        // flat SHA-256 of the piece content (not Merkle). Verifier picks SHA-256 from the
        // stored hash length.
        var pieceData = MakeData(65536, seed: 2);
        var expectedSha256 = SHA256.HashData(pieceData);

        var torrent = MakeTorrent(metaVersion: 0, pieceLength: 65536, hashes: new[] { expectedSha256 });
        Assert.That(torrent.VerifyPieceHash(0, pieceData), Is.True);
    }

    [Test]
    public void V2_LargePiece_UsesMerkleNotFlatSha256()
    {
        // THE KEY CORRECTNESS CASE. A 64 KiB piece is 4 leaves of 16 KiB each. The v2 piece
        // layer hash is SHA-256(SHA-256(leaf0||leaf1) || SHA-256(leaf2||leaf3)), NOT
        // SHA-256(64 KiB piece content). Without the MetaVersion branch the verifier would
        // compute flat SHA-256 and mismatch.
        int pieceLen = 65536; // 4 leaves
        var pieceData = MakeData(pieceLen, seed: 3);
        var flatSha256 = SHA256.HashData(pieceData);
        var merkleRoot = MerkleHasher.ComputePieceLayer(pieceData, pieceLen)[0];

        Assert.That(merkleRoot, Is.Not.EqualTo(flatSha256),
            "Sanity: Merkle root and flat SHA-256 must differ for a >1-leaf piece.");

        // v2: stored hash is Merkle root. Should verify.
        var v2Torrent = MakeTorrent(metaVersion: 2, pieceLength: pieceLen, hashes: new[] { merkleRoot });
        Assert.That(v2Torrent.VerifyPieceHash(0, pieceData), Is.True,
            "v2 verifier must accept the Merkle root of the piece.");

        // v2 with a flat-SHA-256 hash in _hashes (simulating a mistakenly-generated torrent)
        // must be rejected - not silently accepted.
        var wrongTorrent = MakeTorrent(metaVersion: 2, pieceLength: pieceLen, hashes: new[] { flatSha256 });
        Assert.That(wrongTorrent.VerifyPieceHash(0, pieceData), Is.False,
            "v2 verifier must reject a flat-SHA-256 hash in _hashes as a mismatch.");
    }

    [Test]
    public void V2_SinglePieceFile_FlatSha256EqualsMerkle()
    {
        // Boundary case: when pieceLength == leafSize (16 KiB), there's exactly one leaf
        // per piece and Merkle root == SHA-256(padded leaf). Both MetaVersion paths happen
        // to agree for this shape, so v1 Phase 1 code remains compatible here.
        int pieceLen = MerkleHasher.LeafSize;
        var pieceData = MakeData(pieceLen, seed: 4);
        var merkleRoot = MerkleHasher.ComputePieceLayer(pieceData, pieceLen)[0];
        var flatSha256 = SHA256.HashData(pieceData);
        Assert.That(merkleRoot, Is.EqualTo(flatSha256),
            "Sanity: 1-leaf piece Merkle root equals flat SHA-256 of its content.");

        var torrent = MakeTorrent(metaVersion: 2, pieceLength: pieceLen, hashes: new[] { merkleRoot });
        Assert.That(torrent.VerifyPieceHash(0, pieceData), Is.True);
    }

    [Test]
    public void V2_PartialLastPiece_ZeroPadMerkleVerifies()
    {
        // BEP 52 pads the last partial piece's leaves with zero-pad to leavesPerPiece. The
        // creator does it, and VerifyPieceHash must do the same via ComputePieceLayer.
        int pieceLen = 65536;
        var partialData = MakeData(20_000, seed: 5); // just under 2 leaves
        var piecePath = MerkleHasher.ComputePieceLayer(partialData, pieceLen)[0];

        var torrent = MakeTorrent(metaVersion: 2, pieceLength: pieceLen, hashes: new[] { piecePath });
        Assert.That(torrent.VerifyPieceHash(0, partialData), Is.True);
    }

    [Test]
    public void V2_TamperedPiece_Rejected()
    {
        int pieceLen = 65536;
        var pieceData = MakeData(pieceLen, seed: 6);
        var merkleRoot = MerkleHasher.ComputePieceLayer(pieceData, pieceLen)[0];
        var torrent = MakeTorrent(metaVersion: 2, pieceLength: pieceLen, hashes: new[] { merkleRoot });

        var tampered = (byte[])pieceData.Clone();
        tampered[0] ^= 0x01;
        Assert.That(torrent.VerifyPieceHash(0, tampered), Is.False);
    }

    [Test]
    public void IndexOutOfRange_Rejected()
    {
        var torrent = MakeTorrent(metaVersion: 0, pieceLength: 16384, hashes: new[] { new byte[20] });
        Assert.That(torrent.VerifyPieceHash(5, MakeData(16384, 7)), Is.False);
        Assert.That(torrent.VerifyPieceHash(-1, MakeData(16384, 7)), Is.False);
    }

    [Test]
    public void V2_InvalidPieceLength_Rejected()
    {
        // v2 requires pieceLength to be a multiple of 16 KiB. A misconfigured torrent with
        // pieceLength = 12 KiB should not crash - just fail verification.
        var pieceData = MakeData(12288, seed: 8);
        var torrent = MakeTorrent(metaVersion: 2, pieceLength: 12288, hashes: new[] { new byte[32] });
        Assert.That(torrent.VerifyPieceHash(0, pieceData), Is.False);
    }

    /// <summary>
    /// Pure-v2 multi-file download path - previously broken past file 0 because the parser
    /// only populated PieceHashes from FileRoots[0]'s piece layer. After the refactor the
    /// parser flattens all files' piece layers in file-tree walk order and uses padded
    /// virtual-stream offsets, so every global piece index -> PieceHashes[globalIdx] is
    /// correct. Exercise it: create a 3-file pure-v2 torrent, parse round-trip, feed each
    /// file's bytes through VerifyPieceHash at the globally-indexed pieces and assert all
    /// pass. If any file beyond index 0 fails, the parser/creator refactor regressed.
    /// </summary>
    [Test]
    public void V2_PureMultiFile_AllPiecesVerify_PastFile0()
    {
        int pieceLen = 65536;
        // Three multi-piece files with distinct content + an intentionally partial last piece
        // on file 1 (5000 bytes past a piece boundary) to exercise short-piece verification.
        var fileA = MakeData(pieceLen * 2, seed: 101);
        var fileB = MakeData(pieceLen * 3 + 5000, seed: 102);
        var fileC = MakeData(pieceLen * 2 + 100, seed: 103);
        var inputs = new[]
        {
            ("a.bin", fileA),
            ("b.bin", fileB),
            ("c.bin", fileC),
        };
        var opts = new TorrentCreatorOptions { MetaVersion = 2, PieceLength = pieceLen };

        var (bytes, _) = TorrentCreator.CreateFromMultipleFiles("pure-v2-multi", inputs, opts);
        var parsed = TorrentParser.Parse(bytes);

        if (parsed.MetaVersion != 2) Assert.Fail($"Expected MetaVersion=2, got {parsed.MetaVersion}");
        if (parsed.Files.Length != 3) Assert.Fail($"Expected 3 files, got {parsed.Files.Length}");

        // Walk files in the SAME order the parser emitted (file-tree alphabetical walk) and
        // verify each file's pieces against the flattened PieceHashes array.
        var t = MakeTorrent(parsed.MetaVersion, parsed.PieceLength, parsed.PieceHashes);
        int globalIdx = 0;
        foreach (var file in parsed.Files)
        {
            byte[] source = file.Path == "a.bin" ? fileA
                : file.Path == "b.bin" ? fileB
                : file.Path == "c.bin" ? fileC
                : throw new Exception($"Unexpected file '{file.Path}'");

            int filePieceCount = (int)((file.Length + pieceLen - 1) / pieceLen);
            for (int pi = 0; pi < filePieceCount; pi++)
            {
                int offset = pi * pieceLen;
                int len = Math.Min(pieceLen, (int)(file.Length - offset));
                var piece = new byte[len];
                Array.Copy(source, offset, piece, 0, len);

                Assert.That(t.VerifyPieceHash(globalIdx, piece), Is.True,
                    $"File '{file.Path}' piece {pi} (global={globalIdx}, len={len}) failed verification. " +
                    $"Parser+creator broke for pure-v2-multi-file past file 0.");
                globalIdx++;
            }
        }

        // Sanity: consumed every piece in the flat array.
        Assert.That(globalIdx, Is.EqualTo(parsed.PieceHashes.Length),
            $"Walked {globalIdx} pieces across files but PieceHashes.Length = {parsed.PieceHashes.Length}. " +
            $"Creator + parser must agree on total piece count.");
    }

    /// <summary>
    /// End-to-end: create a v2 torrent via TorrentCreator with a large (64 KiB) piece size,
    /// load its metadata into a Torrent, then feed each piece's bytes to VerifyPieceHash
    /// and confirm they all pass. This is the real-world path - TorrentCreator output going
    /// through TorrentParser into a live Torrent - and it would fail with the old flat-
    /// SHA-256 verifier for any piece that spans more than one leaf.
    /// </summary>
    [Test]
    public void V2_Creator_To_Parser_To_Torrent_AllPiecesVerify()
    {
        int pieceLen = 65536; // 4 leaves per piece
        var fileSize = pieceLen * 3 + 500;
        var data = MakeData(fileSize, seed: 42);
        var opts = new TorrentCreatorOptions { MetaVersion = 2, PieceLength = pieceLen };

        var (bytes, _) = TorrentCreator.CreateFromBytes("e2e.bin", data, opts);
        var parsed = TorrentParser.Parse(bytes);

        var torrent = MakeTorrent(
            metaVersion: parsed.MetaVersion,
            pieceLength: parsed.PieceLength,
            hashes: parsed.PieceHashes);

        // Feed each piece's bytes to VerifyPieceHash.
        for (int i = 0; i < parsed.PieceCount; i++)
        {
            int offset = i * pieceLen;
            int len = Math.Min(pieceLen, data.Length - offset);
            var pieceBytes = new byte[len];
            Array.Copy(data, offset, pieceBytes, 0, len);
            Assert.That(torrent.VerifyPieceHash(i, pieceBytes), Is.True,
                $"Piece {i} (len {len}) must verify against its stored v2 Merkle piece-layer hash.");
        }
    }

    // ── Helpers ──

    /// <summary>
    /// Builds a Torrent with the minimum state needed for VerifyPieceHash. Uses the
    /// internal field setters directly - piece-verify only touches MetaVersion,
    /// PieceLength, and the private _hashes array (settable via reflection or an
    /// internal test accessor).
    /// </summary>
    private static Torrent MakeTorrent(int metaVersion, int pieceLength, byte[][] hashes)
    {
        var t = new Torrent
        {
            MetaVersion = metaVersion,
            PieceLength = pieceLength,
        };
        // _hashes is private - set via reflection for tests. Production code goes through
        // SetMetadata which populates it from TorrentMetadata.PieceHashes.
        var field = typeof(Torrent).GetField("_hashes",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        if (field == null) throw new InvalidOperationException("_hashes field not found on Torrent");
        field.SetValue(t, hashes);
        return t;
    }

    private static byte[] MakeData(int size, int seed)
    {
        var data = new byte[size];
        new Random(seed).NextBytes(data);
        return data;
    }
}
