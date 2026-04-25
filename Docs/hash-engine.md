# Pluggable Piece-Hash Engine

`IPieceHashEngine` is the abstraction over the SHA-1 / SHA-256 primitives used during piece verification. The default `SystemCryptoPieceHashEngine` calls `System.Security.Cryptography` directly (hardware-accelerated SHA-NI on modern CPUs) and is byte-identical to SpawnDev.WebTorrent 3.1.x and earlier — no behavior change unless you opt in.

The interface lets recheck-heavy workloads route piece verification through GPU / batched implementations. Shipped in **3.2.0** (2026-04-25).

## Why pluggable

- **Recheck workloads.** Verifying every piece of a 100 GB torrent is 25,000+ independent SHA-256 calls. Batching them through ILGPU on a desktop GPU (CUDA / OpenCL) can be ~10-30× faster than sequential CPU. The browser path (WebGPU SHA-256 kernel) wins similarly on M-series and discrete GPUs.
- **Future Merkle batching.** BEP 52 v2 piece-layer computation issues one SHA-256 per 16 KiB leaf plus one per tree level. All leaf hashes are independent — a single GPU dispatch hashes them all in parallel.
- **Testability.** A custom engine can inject deterministic failures, count invocations, or simulate slow hardware.

SpawnDev.WebTorrent intentionally does **not** depend on SpawnDev.ILGPU. The GPU engine will ship as a separate package (`SpawnDev.WebTorrent.GpuHash`, planned) so consumers who don't need it stay dependency-light.

## API

```csharp
public interface IPieceHashEngine
{
    /// <summary>SHA-1 of <paramref name="input"/>. Returns 20 bytes.</summary>
    byte[] Sha1(ReadOnlySpan<byte> input);

    /// <summary>SHA-256 of <paramref name="input"/>. Returns 32 bytes.</summary>
    byte[] Sha256(ReadOnlySpan<byte> input);

    /// <summary>
    /// Bulk SHA-256 of N independent inputs. Returns N hashes, order-preserved.
    /// Default CPU implementation falls back to a loop of <see cref="Sha256"/>;
    /// GPU implementations should dispatch all inputs as one kernel batch for
    /// per-call kernel-launch amortization.
    /// </summary>
    byte[][] BatchSha256(IReadOnlyList<ReadOnlyMemory<byte>> inputs);
}
```

## Default — `SystemCryptoPieceHashEngine`

```csharp
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
```

Fast on desktop (hardware SHA-NI on x86 / ARMv8 cryptography extensions) and adequate on browser (WASM SHA-256 ≈ 200-400 MB/s). Zero non-BCL dependencies.

## Wiring

### Construct-time

```csharp
await using var client = new WebTorrentClient(new WebTorrentClientOptions
{
    PieceHashEngine = myCustomEngine,   // any IPieceHashEngine
});
```

If `PieceHashEngine` is omitted (or null), the client constructs a `SystemCryptoPieceHashEngine` automatically. Existing 3.1.x code keeps running unchanged.

### Runtime

```csharp
client.PieceHashEngine = anotherEngine;   // can be flipped at any time
```

The active engine is read on every `Torrent.VerifyPieceHash` call, so swapping it takes effect on the very next piece verification.

### Read-only inspection

```csharp
IPieceHashEngine current = client.PieceHashEngine;
```

## Where the engine is called

Today (3.2.0) the engine intercepts the **v1 / Phase-1 flat hash path** inside `Torrent.VerifyPieceHash`:

- `expected.Length == 32` → `engine.Sha256(buf)`
- `expected.Length == 20` → `engine.Sha1(buf)`

The **v2 Merkle path** (`MetaVersion == 2`) still uses `MerkleHasher` directly. Routing the per-leaf SHA-256 calls through the engine for full Merkle batching is on the roadmap for the GPU package — until then v2 piece verification stays on the CPU path even if a GPU engine is registered.

## Writing a custom engine

Two common patterns:

### Counting / observability wrapper

Useful for tests, telemetry, or debugging:

```csharp
public sealed class CountingHashEngine : IPieceHashEngine
{
    private readonly IPieceHashEngine _inner = new SystemCryptoPieceHashEngine();
    public long Sha1Calls;
    public long Sha256Calls;
    public long BatchCalls;
    public long BatchInputs;

    public byte[] Sha1(ReadOnlySpan<byte> input)
    {
        Interlocked.Increment(ref Sha1Calls);
        return _inner.Sha1(input);
    }

    public byte[] Sha256(ReadOnlySpan<byte> input)
    {
        Interlocked.Increment(ref Sha256Calls);
        return _inner.Sha256(input);
    }

    public byte[][] BatchSha256(IReadOnlyList<ReadOnlyMemory<byte>> inputs)
    {
        Interlocked.Increment(ref BatchCalls);
        Interlocked.Add(ref BatchInputs, inputs.Count);
        return _inner.BatchSha256(inputs);
    }
}
```

### GPU engine sketch (illustrative, not shipped)

```csharp
public sealed class GpuPieceHashEngine : IPieceHashEngine, IDisposable
{
    private readonly Accelerator _accelerator;
    // ... shared ILGPU SHA-256 kernel + scratch buffers ...

    public byte[] Sha1(ReadOnlySpan<byte> input)
    {
        // Single-shot SHA-1 doesn't amortize GPU dispatch; fall back to CPU.
        return System.Security.Cryptography.SHA1.HashData(input);
    }

    public byte[] Sha256(ReadOnlySpan<byte> input)
    {
        // For a single piece, CPU SHA-NI usually wins. Fall through unless
        // your kernel is truly faster on N=1.
        return System.Security.Cryptography.SHA256.HashData(input);
    }

    public byte[][] BatchSha256(IReadOnlyList<ReadOnlyMemory<byte>> inputs)
    {
        // GPU batch dispatch: copy all inputs into a contiguous device buffer,
        // launch one kernel that computes N SHA-256 outputs in parallel,
        // copy the 32*N result bytes back. Amortizes kernel launch overhead
        // when N is large (full-torrent recheck = thousands of pieces).
        return DispatchSha256Batch(inputs);
    }

    public void Dispose() { _accelerator.Dispose(); }
}
```

Register it with DI alongside `IAsyncFS` etc:

```csharp
builder.Services.AddSingleton<IPieceHashEngine>(sp =>
    new GpuPieceHashEngine(sp.GetRequiredService<Accelerator>()));
```

Then construct the client with the registered engine:

```csharp
var engine = host.Services.GetRequiredService<IPieceHashEngine>();
var client = new WebTorrentClient(new WebTorrentClientOptions { PieceHashEngine = engine });
```

## Performance notes

- **Don't replace the engine on a small torrent.** A 10 MB torrent with 16 KiB pieces is 640 hashes; CPU SHA-NI does that in ~1 ms. GPU dispatch overhead dominates. Default is fine.
- **Big wins are on full recheck.** A torrent with `Pieces.Count == 25_000` rechecking in one pass is where batched GPU dispatch matters. Single-piece arrival paths ride the CPU path even with a GPU engine wired.
- **Measure before you switch.** `CountingHashEngine` (above) makes invocation counts trivial to capture. If your batch counts are low, the GPU engine is overhead.

## Reference

- Source: [`SpawnDev.WebTorrent/IPieceHashEngine.cs`](../SpawnDev.WebTorrent/IPieceHashEngine.cs)
- Tests: [`PlaywrightMultiTest/DesktopWebRtcTest.cs`](../PlaywrightMultiTest/DesktopWebRtcTest.cs) → `Desktop_PieceHashEngine_RoutesThroughCustomEngine`, `Desktop_PieceHashEngine_DefaultsToSystemCrypto`
- Call site: `Torrent.VerifyPieceHash` in [`SpawnDev.WebTorrent/Torrent.Download.cs`](../SpawnDev.WebTorrent/Torrent.Download.cs)
