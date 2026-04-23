using SpawnDev.UnitTesting;
using SpawnDev.WebTorrent;
using System.Security.Cryptography;

namespace SpawnDev.WebTorrent.Demo.Shared;

/// <summary>
/// BEP 52 v2 tests running via SpawnDev.UnitTesting / PlaywrightMultiTest so the v2
/// Merkle hasher + bencode + creator + parser paths are exercised on both desktop .NET
/// and Blazor WASM. The `SpawnDev.WebTorrent.Tests` NUnit project covers the same code
/// more comprehensively on desktop - this file's job is to prove the v2 paths behave
/// identically under the browser SHA-256 / stream / encoding stack.
/// </summary>
public abstract partial class WebTorrentTestBase
{
    // ── MerkleHasher primitive ──

    [TestMethod]
    public async Task Bep52_MerkleHasher_PadHashZeroLevelMatchesSha256OfZeros()
    {
        var expected = SHA256.HashData(new byte[MerkleHasher.LeafSize]);
        var actual = MerkleHasher.PadHashAtLevel(0);
        if (!actual.AsSpan().SequenceEqual(expected))
            throw new Exception("PadHashAtLevel(0) must equal SHA-256 of 16 KiB of zero bytes.");
        await Task.CompletedTask;
    }

    [TestMethod]
    public async Task Bep52_MerkleHasher_FileRootInvariantUnderPieceSize()
    {
        // A 200-KB file's v2 root must be the same whether computed with 16 KiB or 128 KiB
        // pieces. This catches pad-hash-level mistakes that are otherwise invisible on a
        // self-round-trip.
        var data = MakeDeterministicData(200_000, seed: 17);
        var rootAt16k = MerkleHasher.ComputeFileRoot(data, 16384);
        var rootAt128k = MerkleHasher.ComputeFileRoot(data, 131072);
        if (!rootAt16k.AsSpan().SequenceEqual(rootAt128k))
            throw new Exception("ComputeFileRoot must be piece-size invariant.");
        await Task.CompletedTask;
    }

    // ── v2 torrent creation + parsing ──

    [TestMethod]
    public async Task Bep52_V2Torrent_SingleFileRoundTrip()
    {
        var data = MakeDeterministicData(10_000, seed: 42);
        var opts = new TorrentCreatorOptions { MetaVersion = 2, PieceLength = 16384 };

        var (bytes, created) = TorrentCreator.CreateFromBytes("v2.bin", data, opts);
        var parsed = TorrentParser.Parse(bytes);

        if (parsed.MetaVersion != 2)
            throw new Exception($"Parsed MetaVersion should be 2, got {parsed.MetaVersion}");
        if (parsed.V2InfoHash.Length != 64)
            throw new Exception($"V2InfoHash hex should be 64 chars, got {parsed.V2InfoHash.Length}");
        if (!parsed.FileRoots[0].AsSpan().SequenceEqual(created.FileRoots[0]))
            throw new Exception("Parsed FileRoots[0] must match created value.");
        await Task.CompletedTask;
    }

    [TestMethod]
    public async Task Bep52_V2Torrent_MultiPieceFile_HasPieceLayers()
    {
        int pieceLen = 16384;
        var data = MakeDeterministicData(pieceLen * 3 + 500, seed: 99); // 4 pieces (last partial)
        var opts = new TorrentCreatorOptions { MetaVersion = 2, PieceLength = pieceLen };

        var (_, meta) = TorrentCreator.CreateFromBytes("multi.bin", data, opts);

        if (meta.PieceLayers.Count != 1)
            throw new Exception($"Expected one piece layers entry, got {meta.PieceLayers.Count}");
        if (meta.PieceHashes.Length != 4)
            throw new Exception($"Expected 4 piece hashes, got {meta.PieceHashes.Length}");
        if (meta.PieceHashes[0].Length != 32)
            throw new Exception($"v2 piece hashes are 32 bytes (SHA-256), got {meta.PieceHashes[0].Length}");
        await Task.CompletedTask;
    }

    // ── Hybrid v1+v2 ──

    [TestMethod]
    public async Task Bep52_Hybrid_BothInfoHashesPopulated()
    {
        var data = MakeDeterministicData(20_000, seed: 7);
        var opts = new TorrentCreatorOptions { MetaVersion = 2, Hybrid = true, PieceLength = 16384 };

        var (_, meta) = TorrentCreator.CreateFromBytes("hybrid.bin", data, opts);

        if (meta.InfoHash == null || meta.InfoHash.Length != 40)
            throw new Exception($"Hybrid must emit v1 SHA-1 infohash (40 hex chars). Got length {meta.InfoHash?.Length}");
        if (meta.V2InfoHash.Length != 64)
            throw new Exception($"Hybrid must emit v2 SHA-256 infohash (64 hex chars). Got length {meta.V2InfoHash.Length}");
        if (meta.InfoHash == meta.V2InfoHash)
            throw new Exception("SHA-1 and SHA-256 of the info dict should differ.");
        await Task.CompletedTask;
    }

    [TestMethod]
    public async Task Bep52_Hybrid_InfoHashes_AreSha1AndSha256OfInfoDict()
    {
        var data = MakeDeterministicData(5_000, seed: 123);
        var opts = new TorrentCreatorOptions { MetaVersion = 2, Hybrid = true, PieceLength = 16384 };

        var (_, meta) = TorrentCreator.CreateFromBytes("match.bin", data, opts);

        if (meta.InfoDictBytes == null) throw new Exception("InfoDictBytes should not be null");
        var expectedSha1 = Convert.ToHexString(SHA1.HashData(meta.InfoDictBytes)).ToLowerInvariant();
        var expectedSha256 = Convert.ToHexString(SHA256.HashData(meta.InfoDictBytes)).ToLowerInvariant();
        if (meta.InfoHash != expectedSha1)
            throw new Exception($"InfoHash should be SHA-1(InfoDictBytes). Expected {expectedSha1}, got {meta.InfoHash}");
        if (meta.V2InfoHash != expectedSha256)
            throw new Exception($"V2InfoHash should be SHA-256(InfoDictBytes). Expected {expectedSha256}, got {meta.V2InfoHash}");
        await Task.CompletedTask;
    }

    // ── v2 magnet URI ──

    [TestMethod]
    public async Task Bep52_Magnet_V2ParsePopulatesV2InfoHash()
    {
        const string V2Hex = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
        var t = new Torrent();
        t.ParseMagnet($"magnet:?xt=urn:btmh:1220{V2Hex}");
        if (t.V2InfoHash != V2Hex)
            throw new Exception($"V2InfoHash should be {V2Hex}, got {t.V2InfoHash}");
        await Task.CompletedTask;
    }

    [TestMethod]
    public async Task Bep52_Magnet_HybridParsePopulatesBoth()
    {
        const string V1Hex = "aaaabbbbccccddddeeeeffff1111222233334444";
        const string V2Hex = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
        var t = new Torrent();
        t.ParseMagnet($"magnet:?xt=urn:btih:{V1Hex}&xt=urn:btmh:1220{V2Hex}");
        if (t.InfoHash != V1Hex) throw new Exception($"V1 hash mismatch: {t.InfoHash}");
        if (t.V2InfoHash != V2Hex) throw new Exception($"V2 hash mismatch: {t.V2InfoHash}");
        await Task.CompletedTask;
    }

    // ── Phase 2c step 2: v2 peer-wire extension (messages 21 / 22 / 23) ──

    [TestMethod]
    public async Task Bep52_Wire_HashRequestCodec_RoundTrip()
    {
        // Big-endian u32 wire format per BEP 52 §"Protocol extension". A request encoded
        // here must decode back byte-identical. Proves the browser's byte-order handling
        // (Span<byte> / BinaryPrimitives) behaves exactly as desktop.
        var root = new byte[32];
        for (int i = 0; i < 32; i++) root[i] = (byte)(i * 7);
        var original = new Bep52WireMessages.HashRequest(root, BaseLayer: 2, Index: 4, Length: 8, ProofLayers: 3);

        var encoded = Bep52WireMessages.Encode(original);
        if (encoded.Length != 48)
            throw new Exception($"hash_request payload MUST be 48 bytes per BEP 52, got {encoded.Length}");

        var decoded = Bep52WireMessages.DecodeHashRequest(encoded);
        if (!decoded.PiecesRoot.AsSpan().SequenceEqual(root)) throw new Exception("PiecesRoot mismatch");
        if (decoded.BaseLayer != 2u) throw new Exception($"BaseLayer: {decoded.BaseLayer}");
        if (decoded.Index != 4u) throw new Exception($"Index: {decoded.Index}");
        if (decoded.Length != 8u) throw new Exception($"Length: {decoded.Length}");
        if (decoded.ProofLayers != 3u) throw new Exception($"ProofLayers: {decoded.ProofLayers}");
        await Task.CompletedTask;
    }

    [TestMethod]
    public async Task Bep52_Wire_HashesCodec_RoundTrip_WithHashList()
    {
        var root = new byte[32];
        for (int i = 0; i < 32; i++) root[i] = (byte)i;
        var hashList = new byte[5][];
        for (int h = 0; h < 5; h++)
        {
            hashList[h] = new byte[32];
            for (int i = 0; i < 32; i++) hashList[h][i] = (byte)((h + 1) * 31 + i);
        }
        var original = new Bep52WireMessages.Hashes(root, BaseLayer: 0, Index: 0, Length: 4, ProofLayers: 1, HashList: hashList);

        var encoded = Bep52WireMessages.Encode(original);
        // 48 (header) + 5 * 32 (hashes) = 208
        if (encoded.Length != 48 + 5 * 32)
            throw new Exception($"hashes payload should be 48 + N*32 bytes, got {encoded.Length}");

        var decoded = Bep52WireMessages.DecodeHashes(encoded);
        if (decoded.HashList.Length != 5) throw new Exception($"HashList length: {decoded.HashList.Length}");
        for (int h = 0; h < 5; h++)
        {
            if (!decoded.HashList[h].AsSpan().SequenceEqual(hashList[h]))
                throw new Exception($"HashList[{h}] mismatch");
        }
        await Task.CompletedTask;
    }

    [TestMethod]
    public async Task Bep52_MerkleProofVerifier_RoundTripAcceptsBuilderOutput()
    {
        // MerkleProofBuilder (seed side) produces a payload that MerkleProofVerifier
        // (peer side) accepts. Covers the symmetric pair - the same code path a browser
        // peer would exercise when serving a hash_request or receiving one.
        int pieceSize = 65536;
        int fileLen = pieceSize * 8;
        var data = MakeDeterministicData(fileLen, seed: 201);
        var pieceLayer = MerkleHasher.ComputePieceLayer(data, pieceSize);
        var fileRoot = MerkleHasher.ComputeFileRoot(data, pieceSize);
        int pieceLayerLevel = 2; // log2(64 KiB / 16 KiB)

        var built = MerkleProofBuilder.Build(
            pieceLayer, pieceLayerLevel, index: 0, length: 4, proofLayers: 1, expectedRoot: fileRoot);
        if (built is null) throw new Exception("Builder returned null for a valid request");
        var (baseLayer, proof) = built.Value;
        if (baseLayer.Length != 4) throw new Exception($"baseLayer length: {baseLayer.Length}");
        if (proof.Length != 1) throw new Exception($"proof length: {proof.Length}");

        if (!MerkleProofVerifier.Verify(fileRoot, 0, baseLayer, proof))
            throw new Exception("Verifier must accept the builder's output");
        await Task.CompletedTask;
    }

    [TestMethod]
    public async Task Bep52_MerkleProofVerifier_RejectsTamperedBaseLayer()
    {
        int pieceSize = 65536;
        var data = MakeDeterministicData(pieceSize * 8, seed: 202);
        var pieceLayer = MerkleHasher.ComputePieceLayer(data, pieceSize);
        var fileRoot = MerkleHasher.ComputeFileRoot(data, pieceSize);

        var built = MerkleProofBuilder.Build(pieceLayer, 2, 0, 4, 1, fileRoot)!.Value;
        // Flip one bit in the base layer - verifier MUST detect.
        built.baseLayer[2][7] ^= 0x01;

        if (MerkleProofVerifier.Verify(fileRoot, 0, built.baseLayer, built.proof))
            throw new Exception("Verifier accepted a tampered base-layer hash - cryptographic check broken");
        await Task.CompletedTask;
    }

    [TestMethod]
    public async Task Bep52_V2HashCoord_RequestAsync_ResolvesWithVerifiedHashes()
    {
        // Core coordinator state machine. Same code path a v2-only magnet bootstrap would
        // exercise: issue hash_request, peer responds with hashes, coordinator verifies
        // and resolves. Proves async + TCS + lock patterns work identically in browser.
        int pieceSize = 65536;
        var data = MakeDeterministicData(pieceSize * 8, seed: 203);
        var pieceLayer = MerkleHasher.ComputePieceLayer(data, pieceSize);
        var fileRoot = MerkleHasher.ComputeFileRoot(data, pieceSize);
        int pieceLayerLevel = 2;

        var coord = new V2HashRequestCoordinator();
        var req = new Bep52WireMessages.HashRequest(fileRoot, (uint)pieceLayerLevel, 0, 4, 1);

        var task = coord.RequestAsync(req, send: _ => Task.CompletedTask);

        // Simulate peer response
        var built = MerkleProofBuilder.Build(pieceLayer, pieceLayerLevel, 0, 4, 1, fileRoot)!.Value;
        var hashList = new byte[built.baseLayer.Length + built.proof.Length][];
        Array.Copy(built.baseLayer, 0, hashList, 0, built.baseLayer.Length);
        Array.Copy(built.proof, 0, hashList, built.baseLayer.Length, built.proof.Length);
        coord.HandleHashes(new Bep52WireMessages.Hashes(
            fileRoot, (uint)pieceLayerLevel, 0, 4, 1, hashList));

        var result = await task.WaitAsync(TimeSpan.FromSeconds(5));
        if (result.Length != 5) throw new Exception($"Expected 5 hashes (4 base + 1 proof), got {result.Length}");
        if (coord.OutstandingCount != 0)
            throw new Exception($"OutstandingCount must drop to 0 on success, got {coord.OutstandingCount}");
    }

    [TestMethod]
    public async Task Bep52_V2HashCoord_RequestAsync_FailsOnHashReject()
    {
        var coord = new V2HashRequestCoordinator();
        var req = new Bep52WireMessages.HashRequest(new byte[32], 0, 0, 2, 1);

        var task = coord.RequestAsync(req, send: _ => Task.CompletedTask);
        coord.HandleReject(new Bep52WireMessages.HashReject(new byte[32], 0, 0, 2, 1));

        try
        {
            await task.WaitAsync(TimeSpan.FromSeconds(5));
            throw new Exception("RequestAsync should have thrown HashRejectedException");
        }
        catch (HashRejectedException) { /* expected */ }
        if (coord.OutstandingCount != 0)
            throw new Exception($"OutstandingCount must drop to 0 after reject");
    }

    [TestMethod]
    public async Task Bep52_Torrent_V2HashCoord_AllocatedForV2_NullForV1()
    {
        // v2 torrent → coordinator allocated. v1 torrent → null. Proves per-torrent
        // allocation gate in SetMetadata works in browser exactly as desktop.
        int pieceLen = 65536;
        var data = MakeDeterministicData(pieceLen * 2, seed: 204);

        var v2Bytes = TorrentCreator.CreateFromBytes("t.bin", data,
            new TorrentCreatorOptions { MetaVersion = 2, PieceLength = pieceLen }).torrentBytes;
        var v2Torrent = new Torrent();
        v2Torrent.SetMetadata(TorrentParser.Parse(v2Bytes));
        if (v2Torrent.V2HashCoord is null)
            throw new Exception("V2 torrent MUST allocate V2HashCoord");

        var v1Bytes = TorrentCreator.CreateFromBytes("t.bin", data,
            new TorrentCreatorOptions { MetaVersion = 0, PieceLength = pieceLen, HashAlgorithm = "SHA-1" }).torrentBytes;
        var v1Torrent = new Torrent();
        v1Torrent.SetMetadata(TorrentParser.Parse(v1Bytes));
        if (v1Torrent.V2HashCoord is not null)
            throw new Exception("v1 torrent MUST NOT allocate V2HashCoord - wasted alloc + wrong semantics");

        await Task.CompletedTask;
    }

    [TestMethod]
    public async Task Bep52_Torrent_V2_VerifyPieceHash_UsesMerkle_NotFlatSha256()
    {
        // Critical correctness: a 64 KiB piece in a v2 torrent has Merkle-root piece-layer
        // hash (NOT flat SHA-256 of the 64 KiB piece bytes). Flat SHA-256 would mismatch.
        // Covers the MetaVersion-aware branch in Torrent.VerifyPieceHash.
        int pieceLen = 65536;
        var data = MakeDeterministicData(pieceLen * 4 + 200, seed: 205);
        var opts = new TorrentCreatorOptions { MetaVersion = 2, PieceLength = pieceLen };
        var (bytes, _) = TorrentCreator.CreateFromBytes("verify.bin", data, opts);
        var parsed = TorrentParser.Parse(bytes);

        var t = new Torrent();
        t.SetMetadata(parsed);

        for (int i = 0; i < parsed.PieceCount; i++)
        {
            int offset = i * pieceLen;
            int len = Math.Min(pieceLen, data.Length - offset);
            var piece = new byte[len];
            Array.Copy(data, offset, piece, 0, len);
            if (!t.VerifyPieceHash(i, piece))
                throw new Exception($"v2 VerifyPieceHash MUST accept piece {i} ({len} bytes) - Merkle branch broken");
        }

        // Tamper detection
        var tampered = new byte[pieceLen];
        Array.Copy(data, 0, tampered, 0, pieceLen);
        tampered[0] ^= 0x01;
        if (t.VerifyPieceHash(0, tampered))
            throw new Exception("v2 VerifyPieceHash MUST reject a tampered piece");

        await Task.CompletedTask;
    }

    [TestMethod]
    public async Task Bep52_Torrent_SeedPath_BuildV2HashesPayload_ForKnownRoot()
    {
        // Seeding path: a peer asks us for piece-layer hashes for a file root we hold
        // (PieceLayers dict populated by SetMetadata from parsed metadata). We should
        // be able to build a hashes payload that round-trips through the verifier.
        int pieceLen = 65536;
        var data = MakeDeterministicData(pieceLen * 8, seed: 206);
        var opts = new TorrentCreatorOptions { MetaVersion = 2, PieceLength = pieceLen };
        var (bytes, _) = TorrentCreator.CreateFromBytes("seed.bin", data, opts);

        var t = new Torrent();
        t.SetMetadata(TorrentParser.Parse(bytes));

        var root = t.FileRoots[0];
        int pieceLayerLevel = 2;
        var req = new Bep52WireMessages.HashRequest(root, (uint)pieceLayerLevel, 0, 4, 1);

        var payload = t.TryBuildV2HashesPayload(req);
        if (payload is null)
            throw new Exception("TryBuildV2HashesPayload returned null for a known root - seed path broken");

        var (baseLayer, proof) = payload.Value;
        if (baseLayer.Length != 4) throw new Exception($"baseLayer length: {baseLayer.Length}");
        if (proof.Length != 1) throw new Exception($"proof length: {proof.Length}");

        if (!MerkleProofVerifier.Verify(root, 0, baseLayer, proof))
            throw new Exception("Seed-built hashes payload must verify against pieces_root");

        await Task.CompletedTask;
    }

    [TestMethod]
    public async Task Bep52_PureV2MultiFile_AllPiecesVerify_PastFile0()
    {
        // Mirrors the desktop NUnit VerifyPieceHashTests.V2_PureMultiFile_AllPiecesVerify_PastFile0
        // through SpawnDev.UnitTesting so Blazor WASM's SHA-256 / bencode / Merkle paths prove
        // pure-v2 multi-file create -> parse -> verify works past file 0 in the browser too.
        // Pre-2026-04-23 the parser only emitted PieceHashes for FileRoots[0], so any piece
        // at a global index past the first file's count failed VerifyPieceHash.
        int pieceLen = 65536;
        var fileA = MakeDeterministicData(pieceLen * 2, seed: 301);
        var fileB = MakeDeterministicData(pieceLen * 3 + 5000, seed: 302); // partial last piece
        var fileC = MakeDeterministicData(pieceLen * 2 + 100, seed: 303);  // partial last piece

        var inputs = new[]
        {
            ("a.bin", fileA),
            ("b.bin", fileB),
            ("c.bin", fileC),
        };
        var opts = new TorrentCreatorOptions { MetaVersion = 2, PieceLength = pieceLen };
        var (bytes, _) = TorrentCreator.CreateFromMultipleFiles("pure-v2-multi", inputs, opts);
        var parsed = TorrentParser.Parse(bytes);

        if (parsed.MetaVersion != 2) throw new Exception($"MetaVersion: {parsed.MetaVersion}");
        if (parsed.Files.Length != 3) throw new Exception($"Files.Length: {parsed.Files.Length}");

        // SetMetadata drives the Torrent from the parsed metadata and populates _hashes
        // for VerifyPieceHash to use.
        var t = new Torrent();
        t.SetMetadata(parsed);

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
                    throw new Exception($"File '{file.Path}' piece {pi} (global={globalIdx}, len={len}) " +
                        $"failed VerifyPieceHash. Pure-v2 multi-file parser regressed in browser.");
                globalIdx++;
            }
        }

        if (globalIdx != parsed.PieceHashes.Length)
            throw new Exception($"Walked {globalIdx} pieces but PieceHashes.Length = {parsed.PieceHashes.Length}");

        await Task.CompletedTask;
    }

    [TestMethod]
    public async Task Bep52_Torrent_SeedPath_ReturnsNull_ForUnknownRoot()
    {
        // Unknown file root → return null → caller sends hash_reject. Guards against
        // accidentally serving hashes for a root we don't own.
        int pieceLen = 65536;
        var data = MakeDeterministicData(pieceLen * 4, seed: 207);
        var opts = new TorrentCreatorOptions { MetaVersion = 2, PieceLength = pieceLen };
        var (bytes, _) = TorrentCreator.CreateFromBytes("unknown.bin", data, opts);

        var t = new Torrent();
        t.SetMetadata(TorrentParser.Parse(bytes));

        var unknownRoot = new byte[32]; // all zeros, not the real root
        var req = new Bep52WireMessages.HashRequest(unknownRoot, 2, 0, 4, 1);

        var payload = t.TryBuildV2HashesPayload(req);
        if (payload is not null)
            throw new Exception("TryBuildV2HashesPayload must return null for unknown root");

        await Task.CompletedTask;
    }
}
