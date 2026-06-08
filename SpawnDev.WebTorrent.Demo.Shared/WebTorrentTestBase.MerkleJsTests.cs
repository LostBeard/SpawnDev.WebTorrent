using SpawnDev.BlazorJS;
using SpawnDev.BlazorJS.JSObjects;
using SpawnDev.UnitTesting;
using SpawnDev.WebTorrent;

namespace SpawnDev.WebTorrent.Demo.Shared;

/// <summary>
/// Equivalence guard for the JS BEP-52 Merkle piece-root module (<c>/webtorrent-merkle.js</c>), which the
/// zero-copy browser web-seed download path uses (Torrent.Download.cs -> VerifyPieceZeroCopyAsync) to compute
/// a piece's Merkle root entirely in JS - per-leaf SubtleCrypto hashing + tree build - returning only the
/// 32-byte root, instead of marshaling every leaf digest back into .NET per piece.
///
/// These assert the JS <c>computePieceRoot</c> output is BYTE-IDENTICAL to the .NET reference
/// (<see cref="MerkleHasher.ComputePieceLayer"/> / <see cref="MerkleHasher.ComputePieceRootFromLeafHashes"/>)
/// across the cases that actually occur in a real download: single-leaf pieces, multi-leaf full pieces, a
/// partial final leaf, and a partial final piece with empty (zero-pad) leaf slots.
///
/// Browser-only: the module needs <c>crypto.subtle</c> + dynamic <c>import()</c>. Desktop skips (its download
/// path keeps the .NET Merkle, which the MerkleHasher tests already cover).
/// </summary>
public abstract partial class WebTorrentTestBase
{
    private const int Leaf = MerkleHasher.LeafSize; // 16 KiB

    [TestMethod]
    public async Task MerkleJs_ComputePieceRoot_MatchesDotNet_AllShapes()
    {
        if (!OperatingSystem.IsBrowser())
            throw new UnsupportedTestException("JS Merkle module needs crypto.subtle + dynamic import - browser only");

        using var module = await BlazorJSRuntime.JS.Import("/webtorrent-merkle.js");
        using var computePieceRoot = module.GetExportClass("computePieceRoot");

        // (pieceSize, totalContentLen) covering every shape the download path hits.
        var cases = new (int pieceSize, int contentLen)[]
        {
            (Leaf,       Leaf),            // 1) single full leaf, leavesPerPiece == 1
            (Leaf,       5000),            // 2) single PARTIAL leaf (zero-padded to 16 KiB)
            (Leaf * 4,   Leaf * 4),        // 3) 4-leaf full piece
            (Leaf * 4,   Leaf * 2 + 7000), // 4) partial piece: 3 actual leaves (last partial) + 1 zero-pad slot
            (Leaf * 8,   Leaf * 8 - 1),    // 5) 8-leaf piece, final leaf one byte short
            (Leaf * 2,   Leaf * 2 * 5 + 9000), // 6) multi-piece file: each piece verified independently
        };

        foreach (var (pieceSize, contentLen) in cases)
        {
            var content = DeterministicBytes(contentLen, seed: pieceSize ^ contentLen);
            int leavesPerPiece = pieceSize / Leaf;

            // .NET reference: per-piece roots for the whole file.
            var dotNetRoots = MerkleHasher.ComputePieceLayer(content, pieceSize);

            int totalPieces = (contentLen + pieceSize - 1) / pieceSize;
            for (int p = 0; p < totalPieces; p++)
            {
                int pieceOffset = p * pieceSize;
                int pieceLen = Math.Min(pieceSize, contentLen - pieceOffset);
                var pieceBytes = content.AsSpan(pieceOffset, pieceLen).ToArray();

                using var ua = new Uint8Array(pieceBytes);
                using var rootUa = await computePieceRoot.CallAsync<Uint8Array>(null, ua, pieceLen, leavesPerPiece);
                var jsRoot = rootUa.ReadBytes();

                if (jsRoot.Length != MerkleHasher.HashSize)
                    throw new Exception($"case(piece={pieceSize},len={contentLen}) piece {p}: JS root len {jsRoot.Length} != 32");
                if (!jsRoot.AsSpan().SequenceEqual(dotNetRoots[p]))
                    throw new Exception(
                        $"case(piece={pieceSize},len={contentLen}) piece {p}: JS root != .NET root\n" +
                        $"  js  = {Convert.ToHexString(jsRoot)}\n" +
                        $"  net = {Convert.ToHexString(dotNetRoots[p])}");
            }
        }
    }

    private static byte[] DeterministicBytes(int len, int seed)
    {
        var b = new byte[len];
        // Simple LCG so content is non-trivial (not all-zero / not a constant) and reproducible.
        uint s = unchecked((uint)seed * 2654435761u + 1u);
        for (int i = 0; i < len; i++)
        {
            s = unchecked(s * 1664525u + 1013904223u);
            b[i] = (byte)(s >> 24);
        }
        return b;
    }
}
