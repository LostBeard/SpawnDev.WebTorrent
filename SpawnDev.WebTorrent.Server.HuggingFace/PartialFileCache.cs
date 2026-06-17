using Microsoft.AspNetCore.Http;
using System.Collections.Concurrent;
using SpawnDev.WebTorrent;

namespace SpawnDev.WebTorrent.Server.HuggingFace;

/// <summary>
/// A crash-safe, range-cached download backed by a SINGLE data file (at its final path) plus a sidecar
/// <c>.ranges</c> chunk-bitmap manifest. A range request fetches only the MISSING covering chunks from the
/// origin (each at most once), writes them at their real byte offset in the data file, records them in the
/// manifest, then serves the requested bytes from disk. Properties:
///  • The hub is ALWAYS an available model source — a never-seen file serves immediately (proxy the missing
///    chunks), no 404-wait for a full download.
///  • Every fetched chunk is CACHED — a re-request for a range the hub already has is served straight from
///    disk, no second origin hit (and it feeds the torrent for warm/P2P loads).
///  • A partial is NEVER mistaken for complete: the MANIFEST is the source of truth. While the manifest
///    exists the data file is partial — only the chunks in its bitmap are valid; the manifest is deleted only
///    when every chunk is present. So a crash resumes from REAL coverage and no consumer serves a truncated
///    file (the generalisation of the bug the old <c>.part</c>+atomic-rename guarded against — but with NO
///    rename, so there is no rename-while-reading race on a threaded server).
/// Completeness convention used across the proxy: <c>File.Exists(dataPath) &amp;&amp; !File.Exists(dataPath + ".ranges")</c>.
/// One instance per cached file; the owning proxy keeps a map keyed by cache key.
/// </summary>
internal sealed class PartialFileCache
{
    /// <summary>Chunk size = the model-torrent piece size, so a cached chunk maps 1:1 to a torrent piece.</summary>
    public const int ChunkSize = 4 * 1024 * 1024;

    /// <summary>Sidecar manifest suffix. Present ⇒ the data file is still partial.</summary>
    public const string ManifestSuffix = ".ranges";

    /// <summary>The completeness check the rest of the proxy must use instead of a bare File.Exists.</summary>
    public static bool IsComplete(string dataPath) => File.Exists(dataPath) && !File.Exists(dataPath + ManifestSuffix);

    private readonly string _dataPath;
    private readonly string _manifestPath;
    private readonly HttpClient _http;
    // Builds a GET to the origin for byte range [start,end] (Range header set). The origin may 30x to a CDN;
    // _http must follow redirects (the default). Async because Ollama must resolve a manifest digest first.
    private readonly Func<long, long, CancellationToken, Task<HttpRequestMessage>> _buildOriginRangeRequest;
    private readonly Func<Task>? _onComplete;

    private readonly SemaphoreSlim _metaGate = new(1, 1);                       // guards metadata init + bitmap + persist
    private readonly ConcurrentDictionary<int, SemaphoreSlim> _chunkGates = new(); // per-chunk fetch dedup

    private long _totalSize = -1;
    private bool[] _present = Array.Empty<bool>();
    private int _chunkCount;
    private volatile bool _complete;

    public PartialFileCache(string dataPath, HttpClient http,
        Func<long, long, CancellationToken, Task<HttpRequestMessage>> buildOriginRangeRequest, Func<Task>? onComplete = null)
    {
        _dataPath = dataPath;
        _manifestPath = dataPath + ManifestSuffix;
        _http = http;
        _buildOriginRangeRequest = buildOriginRangeRequest;
        _onComplete = onComplete;
    }

    /// <summary>Serve a web-seed range request: fetch any missing covering chunks from the origin, then stream
    /// the requested bytes from the local data file.</summary>
    public async Task ServeRangeAsync(HttpContext context)
    {
        var ct = context.RequestAborted;
        if (IsComplete(_dataPath)) { await ServeWholeFileRangeAsync(context, _dataPath); return; }

        if (!await EnsureMetadataAsync(ct)) { context.Response.StatusCode = 502; return; } // origin unreachable

        long start = 0, end = _totalSize - 1;
        bool isRange = false;
        if (context.Request.Headers.TryGetValue("Range", out var rangeHeader))
        {
            switch (HttpByteRange.ParseSingle(rangeHeader.ToString(), _totalSize, out start, out end))
            {
                case HttpByteRange.Result.Unsatisfiable:
                    context.Response.StatusCode = 416;
                    context.Response.Headers["Content-Range"] = $"bytes */{_totalSize}";
                    return;
                case HttpByteRange.Result.Satisfiable: isRange = true; break;
                default: start = 0; end = _totalSize - 1; break;     // None → whole file
            }
        }

        int first = (int)(start / ChunkSize), last = (int)(end / ChunkSize);
        for (int c = first; c <= last; c++) await EnsureChunkAsync(c, ct);

        long length = end - start + 1;
        context.Response.StatusCode = isRange ? 206 : 200;
        context.Response.Headers["Accept-Ranges"] = "bytes";
        context.Response.ContentType = "application/octet-stream";
        if (isRange) context.Response.Headers["Content-Range"] = $"bytes {start}-{end}/{_totalSize}";
        context.Response.ContentLength = length;

        // Read from the single data file (no rename ever happens, so the path is stable for the whole read).
        using var handle = File.OpenHandle(_dataPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var buffer = new byte[(int)Math.Min(length, 65536)];
        long pos = start, remaining = length;
        while (remaining > 0 && !ct.IsCancellationRequested)
        {
            int toRead = (int)Math.Min(remaining, buffer.Length);
            int read = await RandomAccess.ReadAsync(handle, buffer.AsMemory(0, toRead), pos, ct);
            if (read == 0) break;
            await context.Response.Body.WriteAsync(buffer.AsMemory(0, read), ct);
            pos += read; remaining -= read;
        }
    }

    /// <summary>Drive the file to completion (fetch every missing chunk), then return the data path. Used by
    /// the full-file consumers (.torrent / magnet generation, which must hash the whole file).</summary>
    public async Task<string?> EnsureCompleteAsync(CancellationToken ct)
    {
        if (IsComplete(_dataPath)) return _dataPath;
        if (!await EnsureMetadataAsync(ct)) return null;
        for (int c = 0; c < _chunkCount; c++) await EnsureChunkAsync(c, ct);
        return IsComplete(_dataPath) ? _dataPath : null;
    }

    private async Task<bool> EnsureMetadataAsync(CancellationToken ct)
    {
        if (_totalSize >= 0) return true;
        await _metaGate.WaitAsync(ct);
        try
        {
            if (_totalSize >= 0) return true;
            if (LoadManifest()) return true;                          // crash recovery: resume real coverage
            // Probe the origin total size via a 0-0 range (Content-Range: bytes 0-0/TOTAL).
            using var probeReq = await _buildOriginRangeRequest(0, 0, ct);
            using var probe = await _http.SendAsync(probeReq, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!probe.IsSuccessStatusCode) return false;
            long total = probe.Content.Headers.ContentRange?.Length ?? probe.Content.Headers.ContentLength ?? -1;
            if (total <= 0) return false;
            _totalSize = total;
            _chunkCount = (int)((total + ChunkSize - 1) / ChunkSize);
            _present = new bool[_chunkCount];
            var dir = Path.GetDirectoryName(_dataPath);
            if (dir != null) Directory.CreateDirectory(dir);
            // The data file is created (and grown via positional writes) on the first chunk fetch; a positional
            // write past EOF zero-fills the gap, and the bitmap guarantees we only ever READ chunks we wrote.
            // The manifest written here marks the file partial before any consumer can mistake it for complete.
            PersistManifest();
            return true;
        }
        catch { return false; }
        finally { _metaGate.Release(); }
    }

    private async Task EnsureChunkAsync(int chunkIndex, CancellationToken ct)
    {
        if (chunkIndex < 0 || chunkIndex >= _chunkCount || _present[chunkIndex] || _complete) return;
        var gate = _chunkGates.GetOrAdd(chunkIndex, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            if (_present[chunkIndex] || _complete) return;
            long start = (long)chunkIndex * ChunkSize;
            long end = Math.Min(start + ChunkSize, _totalSize) - 1;
            using (var req = await _buildOriginRangeRequest(start, end, ct))
            using (var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                resp.EnsureSuccessStatusCode();
                var bytes = await resp.Content.ReadAsByteArrayAsync(ct);
                long expected = end - start + 1;
                if (bytes.LongLength != expected)
                    throw new IOException($"origin returned {bytes.LongLength} bytes for chunk {chunkIndex} (expected {expected}).");
                // RandomAccess.WriteAsync is safe for concurrent writes at distinct offsets of the same file.
                using var handle = File.OpenHandle(_dataPath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.ReadWrite);
                await RandomAccess.WriteAsync(handle, bytes, start, ct);
            }

            await _metaGate.WaitAsync(ct);
            try { _present[chunkIndex] = true; PersistManifest(); }
            finally { _metaGate.Release(); }

            await CompleteIfDoneAsync();
        }
        finally { gate.Release(); }
    }

    private async Task CompleteIfDoneAsync()
    {
        if (_complete) return;
        bool justCompleted = false;
        await _metaGate.WaitAsync();
        try
        {
            if (_complete) return;
            for (int i = 0; i < _chunkCount; i++) if (!_present[i]) return;
            // Every chunk present → the data file is complete. Drop the manifest (the only "complete" signal);
            // the data file stays exactly where it is. NO rename.
            try { if (File.Exists(_manifestPath)) File.Delete(_manifestPath); } catch { }
            _complete = true;
            justCompleted = true;
        }
        finally { _metaGate.Release(); }
        if (justCompleted && _onComplete != null) { try { await _onComplete(); } catch { } }
    }

    private static async Task ServeWholeFileRangeAsync(HttpContext context, string path)
    {
        var fi = new FileInfo(path);
        context.Response.ContentType = "application/octet-stream";
        context.Response.Headers["Accept-Ranges"] = "bytes";
        if (context.Request.Headers.TryGetValue("Range", out var rh))
        {
            switch (HttpByteRange.ParseSingle(rh.ToString(), fi.Length, out long s, out long e))
            {
                case HttpByteRange.Result.Unsatisfiable:
                    context.Response.StatusCode = 416;
                    context.Response.Headers["Content-Range"] = $"bytes */{fi.Length}";
                    return;
                case HttpByteRange.Result.Satisfiable:
                    context.Response.StatusCode = 206;
                    context.Response.Headers["Content-Range"] = $"bytes {s}-{e}/{fi.Length}";
                    context.Response.ContentLength = e - s + 1;
                    using (var fs = File.OpenRead(path))
                    {
                        fs.Seek(s, SeekOrigin.Begin);
                        var buf = new byte[(int)Math.Min(e - s + 1, 65536)];
                        long rem = e - s + 1;
                        while (rem > 0)
                        {
                            int n = await fs.ReadAsync(buf.AsMemory(0, (int)Math.Min(rem, buf.Length)));
                            if (n == 0) break;
                            await context.Response.Body.WriteAsync(buf.AsMemory(0, n));
                            rem -= n;
                        }
                    }
                    return;
            }
        }
        context.Response.ContentLength = fi.Length;
        await context.Response.SendFileAsync(path);
    }

    // Manifest (binary): ['P','F','C','1'][int64 total][int32 chunkSize][int32 chunkCount][chunkCount × byte 0/1].
    private void PersistManifest()
    {
        using var fs = new FileStream(_manifestPath, FileMode.Create, FileAccess.Write, FileShare.None);
        using var w = new BinaryWriter(fs);
        w.Write(new[] { (byte)'P', (byte)'F', (byte)'C', (byte)'1' });
        w.Write(_totalSize);
        w.Write(ChunkSize);
        w.Write(_chunkCount);
        for (int i = 0; i < _chunkCount; i++) w.Write((byte)(_present[i] ? 1 : 0));
    }

    private bool LoadManifest()
    {
        try
        {
            if (!File.Exists(_manifestPath) || !File.Exists(_dataPath)) return false;
            using var fs = new FileStream(_manifestPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var r = new BinaryReader(fs);
            var magic = r.ReadBytes(4);
            if (magic.Length != 4 || magic[0] != 'P' || magic[1] != 'F' || magic[2] != 'C' || magic[3] != '1') return false;
            long total = r.ReadInt64();
            int chunkSize = r.ReadInt32();
            int count = r.ReadInt32();
            if (chunkSize != ChunkSize || total <= 0 || count <= 0) return false;
            var present = new bool[count];
            for (int i = 0; i < count; i++) present[i] = r.ReadByte() != 0;
            _totalSize = total; _chunkCount = count; _present = present;
            return true;
        }
        catch { return false; }
    }
}
