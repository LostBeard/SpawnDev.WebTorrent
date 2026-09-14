namespace SpawnDev.WebTorrent;

/// <summary>
/// Abstraction over the hash primitives used during piece verification.
/// The default implementation (<see cref="SystemCryptoPieceHashEngine"/>) uses
/// <c>System.Security.Cryptography</c> on every call - fast on desktop (SHA-NI),
/// and zero dependencies.
///
/// ⚠️ It does NOT route to SubtleCrypto in the browser, and an earlier version of
/// this comment said it did. MEASURED 2026-09-14 under PMT (published build),
/// 4 MiB: <b>68 MB/s</b> in .NET against <b>1629 MB/s</b> for SubtleCrypto over
/// the same bytes in the same run - <b>24x</b>. See
/// <c>PieceHashEngine_BrowserThroughput_StaysAboveItsRegressionFloor</c>.
///
/// Why pluggable:
/// <list type="bullet">
/// <item><description><b>Recheck workloads.</b> Verifying every piece of a 100 GB torrent is
/// 25,000+ independent SHA-256 calls. Batching them through ILGPU on a desktop
/// GPU (CUDA / OpenCL) can be ~10-30× faster than sequential CPU. The browser path
/// (WebGPU SHA-256 kernel) wins similarly on M-series and discrete GPUs.</description></item>
/// <item><description><b>Future Merkle batching.</b> BEP 52 v2 piece-layer computation issues
/// one SHA-256 per 16 KiB leaf plus one per tree level. All leaf hashes are
/// independent - a single GPU dispatch hashes them all in parallel.</description></item>
/// <item><description><b>Testability.</b> A custom engine can inject deterministic
/// failures, count invocations, or simulate slow hardware.</description></item>
/// </list>
///
/// SpawnDev.WebTorrent intentionally does NOT take a dependency on SpawnDev.ILGPU.
/// The GPU engine will ship as a separate package
/// (<c>SpawnDev.WebTorrent.GpuHash</c>) so consumers who don't need it stay
/// dependency-light.
/// </summary>
public interface IPieceHashEngine
{
    /// <summary>SHA-1 of <paramref name="input"/>. Returns 20 bytes. Used for
    /// v1 (BEP 3) piece verification when the stored hash is 20 bytes.</summary>
    byte[] Sha1(ReadOnlySpan<byte> input);

    /// <summary>SHA-256 of <paramref name="input"/>. Returns 32 bytes. Used for
    /// v1-with-SHA-256 (Phase 1) piece verification, and for individual leaf
    /// hashes inside the Merkle tree.</summary>
    byte[] Sha256(ReadOnlySpan<byte> input);

    /// <summary>
    /// Bulk SHA-256 of N independent inputs. Returns an array of N hashes
    /// (each 32 bytes), order-preserved. Default CPU implementation falls
    /// back to a loop of <see cref="Sha256"/>; GPU implementations should
    /// dispatch all inputs as one kernel batch for the per-call kernel-launch
    /// amortization.
    /// </summary>
    byte[][] BatchSha256(IReadOnlyList<ReadOnlyMemory<byte>> inputs);
}

/// <summary>
/// Default hash engine - uses <see cref="System.Security.Cryptography.SHA1"/>
/// and <see cref="System.Security.Cryptography.SHA256"/> directly. Fast on
/// desktop (hardware SHA-NI on x86 / ARMv8 cryptography extensions).
///
/// ⚠️ ON BROWSER IT IS 68 MB/s, not the "200-400 MB/s" this comment used to
/// claim - MEASURED 2026-09-14, published build, 4 MiB piece. SubtleCrypto does
/// the identical work at 1629 MB/s, so a browser piece verify through this engine
/// costs 59 ms where SubtleCrypto costs 2.5 ms. The zero-copy download path
/// already uses SubtleCrypto; this engine is what the wire-assembled and rescan
/// paths use. Zero non-BCL dependencies, which is why it is still the default.
///
/// ⚠️ Any browser hash rate measured under `dotnet run` is meaningless - that is a
/// BUILD, with no relink or wasm-opt, and it reported ~4 MB/s here (wrong by 17x)
/// while SubtleCrypto, being native browser code, measured the same either way.
/// </summary>
public sealed class SystemCryptoPieceHashEngine : IPieceHashEngine
{
    public byte[] Sha1(ReadOnlySpan<byte> input)
        => System.Security.Cryptography.SHA1.HashData(input);

    public byte[] Sha256(ReadOnlySpan<byte> input)
        => System.Security.Cryptography.SHA256.HashData(input);

    public byte[][] BatchSha256(IReadOnlyList<ReadOnlyMemory<byte>> inputs)
    {
        var result = new byte[inputs.Count][];
        for (int i = 0; i < inputs.Count; i++)
            result[i] = System.Security.Cryptography.SHA256.HashData(inputs[i].Span);
        return result;
    }
}
