using System.Security.Cryptography;
using SpawnDev.UnitTesting;
using SpawnDev.WebTorrent;

namespace SpawnDev.WebTorrent.Demo.Shared;

/// <summary>
/// BEP 52 MerkleProofBuilder round-trip tests: proofs emitted by the builder must be
/// accepted by the verifier we already trust. The builder is used on the seed path
/// (Torrent.OnV2HashRequest); if its output doesn't round-trip, downloader-side peers
/// would silently reject our hash_reply. Migrated from NUnit MerkleProofBuilderTests.cs.
/// </summary>
public abstract partial class WebTorrentTestBase
{
    [TestMethod]
    public async Task MerkleProofBuilder_4Pieces_Length2_Index0_VerifiesRoundTrip()
    {
        var leaves = MerkleProofBuilderTests_BuildPieceLayer(4, seed: 1);
        var root = MerkleProofBuilderTests_ComputeRootAtLevel(leaves, level: 0);

        var result = MerkleProofBuilder.Build(
            leaves, baseLayerLevel: 0, index: 0, length: 2, proofLayers: 1, expectedRoot: root);

        if (result is null) throw new Exception("Build must succeed for a valid request");
        var (baseLayer, proof) = result.Value;
        if (baseLayer.Length != 2) throw new Exception($"baseLayer.Length={baseLayer.Length}, expected 2");
        if (proof.Length != 1) throw new Exception($"proof.Length={proof.Length}, expected 1");
        if (!MerkleProofVerifier.Verify(root, 0, baseLayer, proof))
            throw new Exception("emitted proof failed to verify back against root");
        await Task.CompletedTask;
    }

    [TestMethod]
    public async Task MerkleProofBuilder_4Pieces_Length2_Index2_VerifiesRoundTrip()
    {
        var leaves = MerkleProofBuilderTests_BuildPieceLayer(4, seed: 2);
        var root = MerkleProofBuilderTests_ComputeRootAtLevel(leaves, level: 0);

        var result = MerkleProofBuilder.Build(
            leaves, baseLayerLevel: 0, index: 2, length: 2, proofLayers: 1, expectedRoot: root);

        if (result is null) throw new Exception("Build must succeed for right-half request");
        var (baseLayer, proof) = result.Value;
        if (!MerkleProofVerifier.Verify(root, 2, baseLayer, proof))
            throw new Exception("right-half proof failed to verify");
        await Task.CompletedTask;
    }

    [TestMethod]
    public async Task MerkleProofBuilder_8Pieces_MiddleRange_Length2_Index4_VerifiesRoundTrip()
    {
        var leaves = MerkleProofBuilderTests_BuildPieceLayer(8, seed: 3);
        var root = MerkleProofBuilderTests_ComputeRootAtLevel(leaves, level: 0);

        var result = MerkleProofBuilder.Build(
            leaves, baseLayerLevel: 0, index: 4, length: 2, proofLayers: 2, expectedRoot: root);

        if (result is null) throw new Exception("Build must succeed for middle-range request");
        var (baseLayer, proof) = result.Value;
        if (proof.Length != 2) throw new Exception($"proof.Length={proof.Length}, expected 2");
        if (!MerkleProofVerifier.Verify(root, 4, baseLayer, proof))
            throw new Exception("middle-range proof failed to verify");
        await Task.CompletedTask;
    }

    [TestMethod]
    public async Task MerkleProofBuilder_8Pieces_FullLayer_ProofLayers0_VerifiesRoundTrip()
    {
        var leaves = MerkleProofBuilderTests_BuildPieceLayer(8, seed: 4);
        var root = MerkleProofBuilderTests_ComputeRootAtLevel(leaves, level: 0);

        var result = MerkleProofBuilder.Build(
            leaves, baseLayerLevel: 0, index: 0, length: 8, proofLayers: 0, expectedRoot: root);

        if (result is null) throw new Exception("Build must succeed for full-layer request");
        var (baseLayer, proof) = result.Value;
        if (baseLayer.Length != 8) throw new Exception($"baseLayer.Length={baseLayer.Length}, expected 8");
        if (proof.Length != 0) throw new Exception($"proof.Length={proof.Length}, expected 0");
        if (!MerkleProofVerifier.Verify(root, 0, baseLayer, proof))
            throw new Exception("full-layer proof failed to verify");
        await Task.CompletedTask;
    }

    [TestMethod]
    public async Task MerkleProofBuilder_3Pieces_PaddedToNextPow2_VerifiesRoundTrip()
    {
        var leaves = MerkleProofBuilderTests_BuildPieceLayer(3, seed: 5);
        var padded = new byte[4][];
        for (int i = 0; i < 3; i++) padded[i] = leaves[i];
        padded[3] = MerkleHasher.PadHashAtLevel(0);
        var root = MerkleProofBuilderTests_ComputeRootAtLevel(padded, level: 0);

        var result = MerkleProofBuilder.Build(
            leaves, baseLayerLevel: 0, index: 0, length: 2, proofLayers: 1, expectedRoot: root);

        if (result is null) throw new Exception("Build must handle non-power-of-2 piece counts via pad propagation");
        var (baseLayer, proof) = result.Value;
        if (!MerkleProofVerifier.Verify(root, 0, baseLayer, proof))
            throw new Exception("padded proof failed to verify");
        await Task.CompletedTask;
    }

    [TestMethod]
    public async Task MerkleProofBuilder_PieceLayer_Level2_VerifiesRoundTrip()
    {
        var pieceLayerHashes = MerkleProofBuilderTests_BuildPieceLayer(8, seed: 6);
        int level = 2;
        var root = MerkleProofBuilderTests_ComputeRootAtLevel(pieceLayerHashes, level);

        var result = MerkleProofBuilder.Build(
            pieceLayerHashes, baseLayerLevel: level, index: 0, length: 4, proofLayers: 1, expectedRoot: root);

        if (result is null) throw new Exception("Build must succeed at piece-layer level");
        var (baseLayer, proof) = result.Value;
        if (baseLayer.Length != 4) throw new Exception($"baseLayer.Length={baseLayer.Length}, expected 4");
        if (proof.Length != 1) throw new Exception($"proof.Length={proof.Length}, expected 1");
        if (!MerkleProofVerifier.Verify(root, 0, baseLayer, proof))
            throw new Exception("piece-layer proof failed to verify");
        await Task.CompletedTask;
    }

    [TestMethod]
    public async Task MerkleProofBuilder_ReturnsNull_ForLengthBelow2()
    {
        var leaves = MerkleProofBuilderTests_BuildPieceLayer(4, seed: 7);
        var root = MerkleProofBuilderTests_ComputeRootAtLevel(leaves, level: 0);
        if (MerkleProofBuilder.Build(leaves, 0, 0, 1, 2, root) is not null)
            throw new Exception("length=1 should return null (spec: length >= 2)");
        if (MerkleProofBuilder.Build(leaves, 0, 0, 0, 2, root) is not null)
            throw new Exception("length=0 should return null");
        await Task.CompletedTask;
    }

    [TestMethod]
    public async Task MerkleProofBuilder_ReturnsNull_ForNonPowerOfTwoLength()
    {
        var leaves = MerkleProofBuilderTests_BuildPieceLayer(8, seed: 8);
        var root = MerkleProofBuilderTests_ComputeRootAtLevel(leaves, level: 0);
        if (MerkleProofBuilder.Build(leaves, 0, 0, 3, 1, root) is not null)
            throw new Exception("length=3 should return null (non-power-of-two)");
        if (MerkleProofBuilder.Build(leaves, 0, 0, 6, 0, root) is not null)
            throw new Exception("length=6 should return null (non-power-of-two)");
        await Task.CompletedTask;
    }

    [TestMethod]
    public async Task MerkleProofBuilder_ReturnsNull_ForIndexNotMultipleOfLength()
    {
        var leaves = MerkleProofBuilderTests_BuildPieceLayer(4, seed: 9);
        var root = MerkleProofBuilderTests_ComputeRootAtLevel(leaves, level: 0);
        if (MerkleProofBuilder.Build(leaves, 0, 1, 2, 1, root) is not null)
            throw new Exception("index=1 length=2 should return null (index must be multiple of length)");
        await Task.CompletedTask;
    }

    [TestMethod]
    public async Task MerkleProofBuilder_ReturnsNull_ForWrongProofLayerCount()
    {
        var leaves = MerkleProofBuilderTests_BuildPieceLayer(4, seed: 10);
        var root = MerkleProofBuilderTests_ComputeRootAtLevel(leaves, level: 0);
        if (MerkleProofBuilder.Build(leaves, 0, 0, 2, 0, root) is not null)
            throw new Exception("4-leaf len=2 needs 1 proof layer, 0 should be rejected");
        if (MerkleProofBuilder.Build(leaves, 0, 0, 2, 2, root) is not null)
            throw new Exception("too-many proof layers should be rejected");
        await Task.CompletedTask;
    }

    [TestMethod]
    public async Task MerkleProofBuilder_ReturnsNull_ForMismatchedExpectedRoot()
    {
        var leaves = MerkleProofBuilderTests_BuildPieceLayer(4, seed: 11);
        var wrongRoot = new byte[32];
        if (MerkleProofBuilder.Build(leaves, 0, 0, 2, 1, wrongRoot) is not null)
            throw new Exception("builder must self-check and reject when emit can't reach the claimed root");
        await Task.CompletedTask;
    }

    [TestMethod]
    public async Task MerkleProofBuilder_ReturnsNull_ForIndexBeyondPaddedLayer()
    {
        var leaves = MerkleProofBuilderTests_BuildPieceLayer(4, seed: 12);
        var root = MerkleProofBuilderTests_ComputeRootAtLevel(leaves, level: 0);
        if (MerkleProofBuilder.Build(leaves, 0, 4, 2, 1, root) is not null)
            throw new Exception("out-of-range index should return null");
        await Task.CompletedTask;
    }

    [TestMethod]
    public async Task MerkleProofBuilder_RealFile_PieceLayerRequest_VerifiesRoundTrip()
    {
        // 512 KiB file, 64 KiB pieces => 4 leaves per piece, piece layer at level 2.
        int pieceSize = 65536;
        int fileLen = pieceSize * 8;
        var fileBytes = new byte[fileLen];
        new Random(13).NextBytes(fileBytes);

        var pieceLayer = MerkleHasher.ComputePieceLayer(fileBytes, pieceSize);
        var fileRoot = MerkleHasher.ComputeFileRoot(fileBytes, pieceSize);

        int pieceLayerLevel = 2;

        var result = MerkleProofBuilder.Build(
            pieceLayer, baseLayerLevel: pieceLayerLevel,
            index: 0, length: 4, proofLayers: 1,
            expectedRoot: fileRoot);

        if (result is null) throw new Exception("real-file piece-layer Build must succeed");
        var (baseLayer, proof) = result.Value;
        if (baseLayer.Length != 4) throw new Exception($"baseLayer.Length={baseLayer.Length}, expected 4");
        if (proof.Length != 1) throw new Exception($"proof.Length={proof.Length}, expected 1");
        if (!MerkleProofVerifier.Verify(fileRoot, 0, baseLayer, proof))
            throw new Exception("real-file emitted proof failed to verify");
        await Task.CompletedTask;
    }

    // ---- helpers ----

    private static byte[][] MerkleProofBuilderTests_BuildPieceLayer(int count, int seed)
    {
        var rng = new Random(seed);
        var result = new byte[count][];
        for (int i = 0; i < count; i++)
        {
            var h = new byte[32];
            rng.NextBytes(h);
            result[i] = h;
        }
        return result;
    }

    private static byte[] MerkleProofBuilderTests_ComputeRootAtLevel(byte[][] leaves, int level)
    {
        int n = 1;
        while (n < leaves.Length) n <<= 1;
        var current = new byte[n][];
        for (int i = 0; i < leaves.Length; i++) current[i] = leaves[i];
        for (int i = leaves.Length; i < n; i++) current[i] = MerkleHasher.PadHashAtLevel(level);

        while (current.Length > 1)
        {
            var next = new byte[current.Length / 2][];
            for (int i = 0; i < next.Length; i++)
                next[i] = MerkleProofBuilderTests_HashPair(current[2 * i], current[2 * i + 1]);
            current = next;
        }
        return current[0];
    }

    private static byte[] MerkleProofBuilderTests_HashPair(byte[] l, byte[] r)
    {
        var b = new byte[64];
        l.CopyTo(b, 0);
        r.CopyTo(b, 32);
        return SHA256.HashData(b);
    }
}
