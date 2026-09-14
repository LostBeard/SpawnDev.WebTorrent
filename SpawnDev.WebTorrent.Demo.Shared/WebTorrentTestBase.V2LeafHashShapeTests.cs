using System.Diagnostics;
using SpawnDev.SpawnJS;
using SpawnDev.SpawnJS.JSObjects;
using SpawnDev.UnitTesting;

namespace SpawnDev.WebTorrent.Demo.Shared;

/// <summary>
/// What a BEP 52 v2 piece verify costs in the browser, .NET against SubtleCrypto, on a PUBLISHED build.
/// </summary>
/// <remarks>
/// <para>
/// 🔴 THE v1 ANSWER DOES NOT TRANSFER TO v2, which is the whole reason this exists separately. A v1
/// piece is ONE hash over the whole piece, where SubtleCrypto's 1629 MB/s beats .NET's 68 MB/s by 24x.
/// A v2 piece is 256 INDEPENDENT 16 KiB leaf hashes, and at that granularity SubtleCrypto pays a
/// per-call crossing on every leaf while .NET pays none. Quoting the 24x for v2 would be quoting a
/// measurement of a different computation.
/// </para>
/// <para>
/// ⚠️ PUBLISHED ONLY. An unpublished build cripples the .NET side while leaving SubtleCrypto (native
/// browser code) untouched, so it does not scale both arms - it distorts the RATIO. That error already
/// produced a retracted "500x" claim in this repo. PMT publishes; `dotnet run` does not.
/// </para>
/// <para>
/// ⚠️ Both arms must agree on the merkle ROOT, not merely produce timings. Two hashing paths that
/// disagree are two different computations and their rates are not comparable.
/// </para>
/// <para>
/// ⭐⭐ MEASURED 2026-09-14 under PMT (published), 4 MiB piece / 256 x 16 KiB leaves, best of 3:
/// </para>
/// <code>
///   .NET leaves          60.0 ms   (67 MB/s)
///   .NET BatchSha256     60.2 ms   (identical - it is a loop today)
///   SubtleCrypto leaves  44.3 ms   (90 MB/s)
///   merkle tree           1.76 ms  (shared, 2.8%)
///   v2 TOTAL  .NET 61.8 ms  vs  SubtleCrypto 46.0 ms  =  1.34x
/// </code>
/// <para>
/// ⭐ SUBTLECRYPTO'S ADVANTAGE COLLAPSES FROM 24x ON v1 TO 1.34x ON v2. It falls from 1629 MB/s to
/// 90 MB/s purely from 16 KiB call granularity - 18x - while .NET barely moves (68 -> 67 MB/s) because
/// it pays no crossing. SubtleCrypto is cheap per byte and expensive per call; .NET is the reverse. So
/// v1 and v2 need DIFFERENT fixes, and quoting v1's 24x at a v2 path is quoting the wrong measurement.
/// </para>
/// </remarks>
public abstract partial class WebTorrentTestBase
{
    [TestMethod(Timeout = 300000)]
    public async Task PieceHashEngine_V2LeafShape_DotNetVsSubtleCrypto()
    {
        if (!OperatingSystem.IsBrowser())
            throw new UnsupportedTestException("this measures the browser/WASM hash paths");

        const int pieceBytes = 4 * 1024 * 1024;
        const int rounds = 3;
        var leafBytes = MerkleHasher.LeafSize;                 // 16 KiB, fixed by BEP 52
        var leaves = pieceBytes / leafBytes;                   // 256
        var engine = Client.PieceHashEngine;
        var data = MakeDeterministicData(pieceBytes, seed: 91);

        // (a) .NET, leaf by leaf - the shape the v2 path would use through IPieceHashEngine.
        double netLeaf = double.MaxValue;
        byte[][]? netHashes = null;
        for (var r = 0; r < rounds; r++)
        {
            var sw = Stopwatch.StartNew();
            var hs = new byte[leaves][];
            for (var i = 0; i < leaves; i++)
                hs[i] = engine.Sha256(data.AsSpan(i * leafBytes, leafBytes));
            netLeaf = Math.Min(netLeaf, sw.Elapsed.TotalMilliseconds);
            netHashes = hs;
        }

        // (b) The interface's own batch seam. Identical work today (it loops), but it is the hook a GPU
        // or threaded engine would implement, so its cost is worth knowing separately.
        double netBatch = double.MaxValue;
        for (var r = 0; r < rounds; r++)
        {
            var inputs = new ReadOnlyMemory<byte>[leaves];
            for (var i = 0; i < leaves; i++) inputs[i] = new ReadOnlyMemory<byte>(data, i * leafBytes, leafBytes);
            var sw = Stopwatch.StartNew();
            _ = engine.BatchSha256(inputs);
            netBatch = Math.Min(netBatch, sw.Elapsed.TotalMilliseconds);
        }

        // (c) SubtleCrypto, leaf by leaf - SubArray + digest + collect, the production zero-copy shape.
        var JS = SpawnJSRuntime.Instance!;
        using var subtle = JS.Get<SubtleCrypto>("crypto.subtle");
        using var piece = HeapView.CreateCopy(new ReadOnlyMemory<byte>(data));
        double jsLeaf = double.MaxValue;
        byte[][]? jsHashes = null;
        for (var r = 0; r < rounds; r++)
        {
            var sw = Stopwatch.StartNew();
            var views = new Uint8Array[leaves];
            var tasks = new Task<ArrayBuffer>[leaves];
            for (var i = 0; i < leaves; i++)
            {
                views[i] = piece.SubArray(i * leafBytes, (i + 1) * leafBytes);
                tasks[i] = subtle.Digest("SHA-256", views[i]);
            }
            var bufs = await Task.WhenAll(tasks);
            var hs = new byte[leaves][];
            for (var i = 0; i < leaves; i++)
            {
                using var ua = new Uint8Array(bufs[i]);
                hs[i] = ua.ReadBytes();
                bufs[i].Dispose();
                views[i].Dispose();
            }
            jsLeaf = Math.Min(jsLeaf, sw.Elapsed.TotalMilliseconds);
            jsHashes = hs;
        }

        // (d) The tree itself, .NET either way today - shared cost, so it must not be attributed to one arm.
        double tree = double.MaxValue;
        byte[]? netRoot = null;
        for (var r = 0; r < rounds; r++)
        {
            var sw = Stopwatch.StartNew();
            netRoot = MerkleHasher.ComputePieceRootFromLeafHashes(netHashes!, leaves);
            tree = Math.Min(tree, sw.Elapsed.TotalMilliseconds);
        }
        var jsRoot = MerkleHasher.ComputePieceRootFromLeafHashes(jsHashes!, leaves);

        var mb = pieceBytes / 1048576.0;
        var netTotal = netLeaf + tree;
        var jsTotal = jsLeaf + tree;
        var report =
            $"v2 piece {mb:F0}MB / {leaves} x {leafBytes / 1024} KiB leaves, best-of-{rounds} || "
            + $".NET leaves {netLeaf:F1} ms ({mb / (netLeaf / 1000.0):F0} MB/s) | "
            + $".NET BatchSha256 {netBatch:F1} ms | "
            + $"SubtleCrypto leaves {jsLeaf:F1} ms ({mb / (jsLeaf / 1000.0):F0} MB/s) | "
            + $"merkle tree {tree:F2} ms (shared) || "
            + $"v2 TOTAL .NET {netTotal:F1} ms vs SubtleCrypto {jsTotal:F1} ms = "
            + $"{(jsTotal > 0 ? netTotal / jsTotal : 0):F2}x";

        // Same computation, or the rates describe different things.
        if (netRoot == null || jsRoot == null || !netRoot.SequenceEqual(jsRoot))
            throw new Exception($"the two paths disagree on the merkle root - rates are not comparable || {report}");

        // 🔴 THE PROPERTY WORTH PINNING is that v2 is NOT the v1 story. v1 measured 24x for SubtleCrypto;
        // v2 measured 1.34x, because SubtleCrypto collapses from 1629 MB/s to 90 MB/s at 16 KiB
        // granularity while .NET barely moves (68 -> 67 MB/s, it pays no crossing). If that ever widens
        // back out, the per-call cost has changed and every conclusion drawn from these numbers - which
        // fix belongs on which torrent version - needs revisiting.
        var ratio = jsTotal > 0 ? netTotal / jsTotal : 0;
        if (ratio > 6)
            throw new Exception(
                $"SubtleCrypto is now {ratio:F1}x better on v2 leaves (1.34x when measured). The per-call "
                + $"cost that made v1 and v2 different answers has changed || {report}");

        // The merkle tree is shared by both arms and was 2.8% of the total. It being negligible is why no
        // .NET-side tree optimisation is worth doing; if it stops being negligible, that changes.
        if (tree > netTotal * 0.25)
            throw new Exception(
                $"the merkle tree is now {tree:F1} ms of {netTotal:F1} ms (was 2.8%) - .NET-side tree work "
                + $"has become worth optimising after all || {report}");
    }
}
