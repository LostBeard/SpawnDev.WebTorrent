using System.Diagnostics;
using SpawnDev.SpawnJS;
using SpawnDev.SpawnJS.JSObjects;
using SpawnDev.UnitTesting;

namespace SpawnDev.WebTorrent.Demo.Shared;

/// <summary>
/// What piece verification actually costs in the browser, through the REAL
/// <see cref="IPieceHashEngine"/> the client uses.
/// </summary>
/// <remarks>
/// <para>
/// ⭐⭐ MEASURED 2026-09-14 under PMT (PUBLISHED build), 4 MiB, best of 3:
/// </para>
/// <code>
///   .NET SystemCryptoPieceHashEngine   59.2 ms     68 MB/s
///   SubtleCrypto, same bytes, same run  2.5 ms   1629 MB/s   -> 24x
/// </code>
/// <para>
/// 🔴 AND THE MEASUREMENT ONLY MEANS ANYTHING PUBLISHED. The same comparison under
/// <c>dotnet run</c> reported .NET at ~4 MB/s and a 500x ratio - wrong by 17x, because a build does not
/// relink or wasm-opt the runtime while SubtleCrypto is native browser code and does not care. Debug and
/// <c>-c Release</c> AGREED with each other, which is exactly what made the wrong number convincing.
/// Never compare a .NET rate against a browser-native one outside a published build.
/// </para>
/// <para>
/// ⚠️ <see cref="SystemCryptoPieceHashEngine"/>'s own summary still claims "adequate on browser (WASM
/// SHA-256 ~200-400 MB/s)" and says it uses "SubtleCrypto via WASM SHA fallback". Both are wrong: it is
/// 68 MB/s, 3-6x under the claim, and it never touches SubtleCrypto.
/// </para>
/// <para>
/// ⚠️ IT HAS TO RUN UNDER PMT, WHICH PUBLISHES. A <c>dotnet run -c Release</c> is a BUILD: no relink, no
/// wasm-opt, and .NET code can be several times slower than the same code published (measured 3.0x
/// elsewhere in SpawnDev). SubtleCrypto is native browser code and is unaffected by build config either
/// way, so measuring the two together in a non-published build flatters the wrong one. That is why this
/// lives here and not beside the probe that first spotted it.
/// </para>
/// <para>
/// ⚠️ The comparison is deliberately SubtleCrypto over the SAME bytes in the SAME run, not a number from
/// another session. A ratio is only meaningful when both terms were measured together.
/// </para>
/// </remarks>
public abstract partial class WebTorrentTestBase
{
    private const int HashProbeBytes = 4 * 1024 * 1024;   // a production torrent piece
    private const int HashProbeRounds = 3;                // best-of, so one hiccup cannot set the number

    [TestMethod(Timeout = 300000)]
    public async Task PieceHashEngine_BrowserThroughput_StaysAboveItsRegressionFloor()
    {
        if (!OperatingSystem.IsBrowser())
            throw new UnsupportedTestException("this measures the browser/WASM hash path");

        var engine = Client.PieceHashEngine;
        var data = MakeDeterministicData(HashProbeBytes, seed: 77);

        // The REAL engine the client verifies pieces with, on the real API.
        double net = double.MaxValue;
        byte[]? netHash = null;
        for (var r = 0; r < HashProbeRounds; r++)
        {
            var sw = Stopwatch.StartNew();
            netHash = engine.Sha256(data);
            net = Math.Min(net, sw.Elapsed.TotalMilliseconds);
        }

        // SubtleCrypto over the SAME bytes, in the SAME run - the path the zero-copy verify already uses.
        var JS = SpawnJSRuntime.Instance!;
        using var subtle = JS.Get<SubtleCrypto>("crypto.subtle");
        using var ua = HeapView.CreateCopy(new ReadOnlyMemory<byte>(data));
        double js = double.MaxValue;
        byte[]? jsHash = null;
        for (var r = 0; r < HashProbeRounds; r++)
        {
            var sw = Stopwatch.StartNew();
            using (var ab = await subtle.Digest("SHA-256", ua))
            {
                js = Math.Min(js, sw.Elapsed.TotalMilliseconds);
                using var h = new Uint8Array(ab);
                jsHash = h.ReadBytes();
            }
        }

        var mb = HashProbeBytes / 1048576.0;
        var netRate = mb / (net / 1000.0);
        var jsRate = mb / (js / 1000.0);
        var report = $"engine={engine.GetType().Name} {mb:F0}MB best-of-{HashProbeRounds} || "
                   + $".NET {net:F1} ms ({netRate:F0} MB/s) | SubtleCrypto {js:F1} ms ({jsRate:F0} MB/s) | "
                   + $"ratio {(net > 0 && js > 0 ? net / js : 0):F0}x";

        // Both must agree on the ANSWER, or one of them is not hashing what it claims to and the rates
        // describe two different computations.
        if (netHash == null || jsHash == null || !netHash.SequenceEqual(jsHash))
            throw new Exception($"the two engines disagree on the hash - the rates are not comparable || {report}");

        // A REGRESSION FLOOR, not the documented claim. Measured 68 MB/s published; 35 is half that, far
        // enough below to survive normal variance and still catch a real collapse. Asserting the
        // documented 200-400 MB/s would fail today - the doc is what is wrong, not the code - and a floor
        // set at the measured value would flake.
        if (netRate < 35)
            throw new Exception(
                $"browser piece hashing collapsed to {netRate:F1} MB/s (68 MB/s when this was written). "
                + $"A 4 MiB piece now costs {net:F0} ms and rescanning a 2.4 GB torrent ~{2400 / netRate:F0} s, "
                + $"all of it blocking WASM. SubtleCrypto did the same work at {jsRate:F0} MB/s in the same "
                + $"run. If this fires under `dotnet run` rather than PMT, suspect the build first || {report}");
    }
}
