namespace SpawnDev.WebTorrent;

/// <summary>
/// Abstraction over the hash primitives used during piece verification.
/// The default implementation (<see cref="SystemCryptoPieceHashEngine"/>) uses
/// <c>System.Security.Cryptography</c> on every call - fast on desktop (SHA-NI)
/// and browser (SubtleCrypto via WASM SHA fallback), and zero dependencies.
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
/// desktop (hardware SHA-NI on x86 / ARMv8 cryptography extensions) and
/// adequate on browser (WASM SHA-256 ≈ 200-400 MB/s). Zero non-BCL
/// dependencies.
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
