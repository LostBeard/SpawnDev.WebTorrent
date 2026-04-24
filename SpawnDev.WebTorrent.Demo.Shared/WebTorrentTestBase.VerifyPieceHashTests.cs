using System.Security.Cryptography;
using SpawnDev.UnitTesting;
using SpawnDev.WebTorrent;

namespace SpawnDev.WebTorrent.Demo.Shared;

/// <summary>
/// Tests for the piece-verification branch matrix in <see cref="Torrent.VerifyPieceHash"/>.
///
/// Critical correctness: a v2 torrent's stored "piece hash" is the Merkle root over the
/// piece's 16 KiB leaves, NOT a flat SHA-256 of the piece bytes. When PieceLength &gt;
/// LeafSize these two values differ - a flat SHA-256 comparison would silently fail every
/// piece of a large-piece-size v2 torrent. These tests prove the branching picks the right
/// algorithm per MetaVersion.
/// Migrated from NUnit SpawnDev.WebTorrent.Tests/VerifyPieceHashTests.cs so these run under
/// PlaywrightMultiTest (browser + desktop) rather than desktop-only NUnit.
/// </summary>
public abstract partial class WebTorrentTestBase
{
    [TestMethod]
    public async Task VerifyPieceHash_V1_FlatSha1_VerifiesPiece()
    {
        // Classic v1: MetaVersion = 0, stored hash is 20-byte SHA-1 of piece content.
        var pieceData = VerifyPieceHashTests_MakeData(65536, seed: 4001);
        var expectedSha1 = SHA1.HashData(pieceData);

        var torrent = VerifyPieceHashTests_MakeTorrent(metaVersion: 0, pieceLength: 65536, hashes: new[] { expectedSha1 });
        if (!torrent.VerifyPieceHash(0, pieceData))
            throw new Exception("V1 flat SHA-1 piece should verify");
        await Task.CompletedTask;
    }

    [TestMethod]
    public async Task VerifyPieceHash_V1_Phase1Sha256_VerifiesPiece()
    {
        // Phase 1 "BEP 52 Phase 1" torrents: MetaVersion = 0 but stored hash is 32-byte
        // flat SHA-256 of the piece content (not Merkle). Verifier picks SHA-256 from the
        // stored hash length.
        var pieceData = VerifyPieceHashTests_MakeData(65536, seed: 4002);
        var expectedSha256 = SHA256.HashData(pieceData);

        var torrent = VerifyPieceHashTests_MakeTorrent(metaVersion: 0, pieceLength: 65536, hashes: new[] { expectedSha256 });
        if (!torrent.VerifyPieceHash(0, pieceData))
            throw new Exception("V1 Phase-1 flat SHA-256 piece should verify");
        await Task.CompletedTask;
    }

    [TestMethod]
    public async Task VerifyPieceHash_V2_LargePiece_UsesMerkleNotFlatSha256()
    {
        // THE KEY CORRECTNESS CASE. A 64 KiB piece is 4 leaves of 16 KiB each. The v2 piece
        // layer hash is SHA-256(SHA-256(leaf0||leaf1) || SHA-256(leaf2||leaf3)), NOT
        // SHA-256(64 KiB piece content). Without the MetaVersion branch the verifier would
        // compute flat SHA-256 and mismatch.
        int pieceLen = 65536; // 4 leaves
        var pieceData = VerifyPieceHashTests_MakeData(pieceLen, seed: 4003);
        var flatSha256 = SHA256.HashData(pieceData);
        var merkleRoot = MerkleHasher.ComputePieceLayer(pieceData, pieceLen)[0];

        if (merkleRoot.SequenceEqual(flatSha256))
            throw new Exception("Sanity: Merkle root and flat SHA-256 must differ for a >1-leaf piece.");

        // v2: stored hash is Merkle root. Should verify.
        var v2Torrent = VerifyPieceHashTests_MakeTorrent(metaVersion: 2, pieceLength: pieceLen, hashes: new[] { merkleRoot });
        if (!v2Torrent.VerifyPieceHash(0, pieceData))
            throw new Exception("v2 verifier must accept the Merkle root of the piece.");

        // v2 with a flat-SHA-256 hash in _hashes (simulating a mistakenly-generated torrent)
        // must be rejected - not silently accepted.
        var wrongTorrent = VerifyPieceHashTests_MakeTorrent(metaVersion: 2, pieceLength: pieceLen, hashes: new[] { flatSha256 });
        if (wrongTorrent.VerifyPieceHash(0, pieceData))
            throw new Exception("v2 verifier must reject a flat-SHA-256 hash in _hashes as a mismatch.");
        await Task.CompletedTask;
    }

    [TestMethod]
    public async Task VerifyPieceHash_V2_SinglePieceFile_FlatSha256EqualsMerkle()
    {
        // Boundary case: when pieceLength == leafSize (16 KiB), there's exactly one leaf
        // per piece and Merkle root == SHA-256(padded leaf). Both MetaVersion paths happen
        // to agree for this shape, so v1 Phase 1 code remains compatible here.
        int pieceLen = MerkleHasher.LeafSize;
        var pieceData = VerifyPieceHashTests_MakeData(pieceLen, seed: 4004);
        var merkleRoot = MerkleHasher.ComputePieceLayer(pieceData, pieceLen)[0];
        var flatSha256 = SHA256.HashData(pieceData);
        if (!merkleRoot.SequenceEqual(flatSha256))
            throw new Exception("Sanity: 1-leaf piece Merkle root equals flat SHA-256 of its content.");

        var torrent = VerifyPieceHashTests_MakeTorrent(metaVersion: 2, pieceLength: pieceLen, hashes: new[] { merkleRoot });
        if (!torrent.VerifyPieceHash(0, pieceData))
            throw new Exception("Single-leaf piece should verify via Merkle==SHA256 equivalence");
        await Task.CompletedTask;
    }

    [TestMethod]
    public async Task VerifyPieceHash_V2_PartialLastPiece_ZeroPadMerkleVerifies()
    {
        // BEP 52 pads the last partial piece's leaves with zero-pad to leavesPerPiece. The
        // creator does it, and VerifyPieceHash must do the same via ComputePieceLayer.
        int pieceLen = 65536;
        var partialData = VerifyPieceHashTests_MakeData(20_000, seed: 4005); // just under 2 leaves
        var piecePath = MerkleHasher.ComputePieceLayer(partialData, pieceLen)[0];

        var torrent = VerifyPieceHashTests_MakeTorrent(metaVersion: 2, pieceLength: pieceLen, hashes: new[] { piecePath });
        if (!torrent.VerifyPieceHash(0, partialData))
            throw new Exception("Partial last piece should verify with zero-padded Merkle");
        await Task.CompletedTask;
    }

    [TestMethod]
    public async Task VerifyPieceHash_V2_TamperedPiece_Rejected()
    {
        int pieceLen = 65536;
        var pieceData = VerifyPieceHashTests_MakeData(pieceLen, seed: 4006);
        var merkleRoot = MerkleHasher.ComputePieceLayer(pieceData, pieceLen)[0];
        var torrent = VerifyPieceHashTests_MakeTorrent(metaVersion: 2, pieceLength: pieceLen, hashes: new[] { merkleRoot });

        var tampered = (byte[])pieceData.Clone();
        tampered[0] ^= 0x01;
        if (torrent.VerifyPieceHash(0, tampered))
            throw new Exception("v2 verifier must reject a tampered piece");
        await Task.CompletedTask;
    }

    [TestMethod]
    public async Task VerifyPieceHash_IndexOutOfRange_Rejected()
    {
        var torrent = VerifyPieceHashTests_MakeTorrent(metaVersion: 0, pieceLength: 16384, hashes: new[] { new byte[20] });
        if (torrent.VerifyPieceHash(5, VerifyPieceHashTests_MakeData(16384, 4007)))
            throw new Exception("Out-of-range index (5) must be rejected");
        if (torrent.VerifyPieceHash(-1, VerifyPieceHashTests_MakeData(16384, 4008)))
            throw new Exception("Negative index (-1) must be rejected");
        await Task.CompletedTask;
    }

    [TestMethod]
    public async Task VerifyPieceHash_V2_InvalidPieceLength_Rejected()
    {
        // v2 requires pieceLength to be a multiple of 16 KiB. A misconfigured torrent with
        // pieceLength = 12 KiB should not crash - just fail verification.
        var pieceData = VerifyPieceHashTests_MakeData(12288, seed: 4009);
        var torrent = VerifyPieceHashTests_MakeTorrent(metaVersion: 2, pieceLength: 12288, hashes: new[] { new byte[32] });
        if (torrent.VerifyPieceHash(0, pieceData))
            throw new Exception("v2 verifier must reject a torrent with invalid (non-16KiB-multiple) pieceLength");
        await Task.CompletedTask;
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
    [TestMethod]
    public async Task VerifyPieceHash_V2_PureMultiFile_AllPiecesVerify_PastFile0()
    {
        int pieceLen = 65536;
        // Three multi-piece files with distinct content + an intentionally partial last piece
        // on file 1 (5000 bytes past a piece boundary) to exercise short-piece verification.
        var fileA = VerifyPieceHashTests_MakeData(pieceLen * 2, seed: 4101);
        var fileB = VerifyPieceHashTests_MakeData(pieceLen * 3 + 5000, seed: 4102);
        var fileC = VerifyPieceHashTests_MakeData(pieceLen * 2 + 100, seed: 4103);
        var inputs = new[]
        {
            ("a.bin", fileA),
            ("b.bin", fileB),
            ("c.bin", fileC),
        };
        var opts = new TorrentCreatorOptions { MetaVersion = 2, PieceLength = pieceLen };

        var (bytes, _) = TorrentCreator.CreateFromMultipleFiles("pure-v2-multi", inputs, opts);
        var parsed = TorrentParser.Parse(bytes);

        if (parsed.MetaVersion != 2) throw new Exception($"Expected MetaVersion=2, got {parsed.MetaVersion}");
        if (parsed.Files.Length != 3) throw new Exception($"Expected 3 files, got {parsed.Files.Length}");

        // Walk files in the SAME order the parser emitted (file-tree alphabetical walk) and
        // verify each file's pieces against the flattened PieceHashes array.
        var t = VerifyPieceHashTests_MakeTorrent(parsed.MetaVersion, parsed.PieceLength, parsed.PieceHashes);
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

                if (!t.VerifyPieceHash(globalIdx, piece))
                    throw new Exception(
                        $"File '{file.Path}' piece {pi} (global={globalIdx}, len={len}) failed verification. " +
                        $"Parser+creator broke for pure-v2-multi-file past file 0.");
                globalIdx++;
            }
        }

        // Sanity: consumed every piece in the flat array.
        if (globalIdx != parsed.PieceHashes.Length)
            throw new Exception(
                $"Walked {globalIdx} pieces across files but PieceHashes.Length = {parsed.PieceHashes.Length}. " +
                $"Creator + parser must agree on total piece count.");
        await Task.CompletedTask;
    }

    /// <summary>
    /// End-to-end: create a v2 torrent via TorrentCreator with a large (64 KiB) piece size,
    /// load its metadata into a Torrent, then feed each piece's bytes to VerifyPieceHash
    /// and confirm they all pass. This is the real-world path - TorrentCreator output going
    /// through TorrentParser into a live Torrent - and it would fail with the old flat-
    /// SHA-256 verifier for any piece that spans more than one leaf.
    /// </summary>
    [TestMethod]
    public async Task VerifyPieceHash_V2_Creator_To_Parser_To_Torrent_AllPiecesVerify()
    {
        int pieceLen = 65536; // 4 leaves per piece
        var fileSize = pieceLen * 3 + 500;
        var data = VerifyPieceHashTests_MakeData(fileSize, seed: 4042);
        var opts = new TorrentCreatorOptions { MetaVersion = 2, PieceLength = pieceLen };

        var (bytes, _) = TorrentCreator.CreateFromBytes("e2e.bin", data, opts);
        var parsed = TorrentParser.Parse(bytes);

        var torrent = VerifyPieceHashTests_MakeTorrent(
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
            if (!torrent.VerifyPieceHash(i, pieceBytes))
                throw new Exception(
                    $"Piece {i} (len {len}) must verify against its stored v2 Merkle piece-layer hash.");
        }
        await Task.CompletedTask;
    }

    // ---- Helpers ----

    /// <summary>
    /// Builds a Torrent with the minimum state needed for VerifyPieceHash. Uses the
    /// internal field setters directly - piece-verify only touches MetaVersion,
    /// PieceLength, and the private _hashes array (settable via reflection or an
    /// internal test accessor).
    /// </summary>
    private static Torrent VerifyPieceHashTests_MakeTorrent(int metaVersion, int pieceLength, byte[][] hashes)
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

    private static byte[] VerifyPieceHashTests_MakeData(int size, int seed)
    {
        var data = new byte[size];
        new Random(seed).NextBytes(data);
        return data;
    }
}
