using SpawnDev.SpawnJS;
using SpawnDev.SpawnJS.JSObjects;

namespace SpawnDev.WebTorrent;

/// <summary>
/// Web seed connection — converts BitTorrent block requests into HTTP range requests.
/// Direct 1:1 port of webtorrent/lib/webconn.js.
/// Acts as a fake Wire that pretends to have all pieces and fulfills requests via HTTP.
/// </summary>
public class WebConn : IAsyncDisposable
{
    // Constants matching JS exactly
    public const int SocketTimeout = 60_000;   // 60s HTTP timeout
    public const int RetryDelay = 10_000;      // 10s before re-announcing piece after HTTP failure

    // ========================
    // STATE
    // ========================

    public string Url { get; }
    public string ConnId { get; }  // unique ID (same as URL, for dedup)
    public Wire WireInstance { get; }
    public bool Destroyed { get; private set; }

    private readonly Torrent _torrent;
    private readonly HttpClient _http;

    // ========================
    // CONSTRUCTOR
    // ========================

    public WebConn(string url, Torrent torrent, HttpClient http)
    {
        Url = url;
        ConnId = url;
        _torrent = torrent;
        _http = http;

        // Create a Wire that this web seed will drive
        WireInstance = new Wire("webSeed");
        WireInstance.SetKeepAlive(true);

        // When the torrent sends us a handshake, respond with ours
        WireInstance.OnHandshake += (infoHash, peerId, exts) =>
        {
            // Use URL hash as our fake peer ID (matches JS: hash(url, 'hex'))
            var fakeId = Convert.ToHexString(
                System.Security.Cryptography.SHA1.HashData(
                    System.Text.Encoding.UTF8.GetBytes(url)
                )
            ).ToLowerInvariant();
            var infoHashBytes = Convert.FromHexString(infoHash);
            var fakeIdBytes = Convert.FromHexString(fakeId.PadRight(40, '0')[..40]);
            _ = WireInstance.Handshake(infoHashBytes, fakeIdBytes);

            // Send bitfield with all pieces set (we "have" everything)
            if (torrent.Pieces.Length > 0)
            {
                var bitfieldBytes = new byte[(int)Math.Ceiling(torrent.Pieces.Length / 8.0)];
                for (int i = 0; i < torrent.Pieces.Length; i++)
                    bitfieldBytes[i / 8] |= (byte)(1 << (7 - (i % 8)));
                _ = WireInstance.Bitfield(bitfieldBytes);
            }
        };

        // When the torrent says it's interested, unchoke it
        WireInstance.OnInterested += () => _ = WireInstance.Unchoke();

        // When the torrent requests a block, serve it via HTTP range request
        WireInstance.OnRequest += (pieceIndex, offset, length, respond) =>
        {
            _ = HandleRequestAsync(pieceIndex, offset, length, respond);
        };
    }

    // ========================
    // HTTP RANGE REQUEST (matches JS httpRequest exactly)
    // ========================

    internal async Task HandleRequestAsync(int pieceIndex, int offset, int length, Action<Exception?, byte[]?> respond)
    {
        try
        {
            long pieceOffset = (long)pieceIndex * _torrent.PieceLength;
            long rangeStart = pieceOffset + offset;
            long rangeEnd = rangeStart + length - 1;

            byte[] data;

            if (_torrent.Files == null || _torrent.Files.Length <= 1)
            {
                // Single-file torrent: if URL ends with /, append the file name (BEP 19 directory style)
                var fileUrl = Url;
                if (fileUrl.EndsWith('/'))
                {
                    var name = _torrent.Name ?? _torrent.Files?[0]?.Name ?? _torrent.Files?[0]?.Path ?? "";
                    fileUrl = fileUrl + Uri.EscapeDataString(name);
                }
                data = await FetchSingleFileCoalescedAsync(fileUrl, rangeStart, length);
            }
            else
            {
                // Multi-file torrent: split range across files. Copy each file-overlap chunk straight into ONE
                // pre-sized buffer (no MemoryStream + .ToArray() second copy — that held ~3x the piece on the
                // .NET heap at once for every file-boundary piece of a multi-file model torrent).
                data = new byte[length];
                int pos = 0;
                foreach (var file in _torrent.Files)
                {
                    long fileStart = file.Offset;
                    long fileEnd = fileStart + file.Length - 1;

                    if (fileStart > rangeEnd || fileEnd < rangeStart) continue;

                    // Build BEP 19 URL: baseUrl/torrentName/filePath
                    var filePath = (file.Path ?? file.Name ?? "").Replace('\\', '/');
                    // Ensure path includes torrent name prefix (parsed torrents have it, created may not)
                    if (_torrent.Name != null && !filePath.StartsWith(_torrent.Name + "/", StringComparison.OrdinalIgnoreCase))
                        filePath = _torrent.Name + "/" + filePath;
                    var pathSegments = filePath.Split('/');
                    var encodedPath = string.Join("/", pathSegments.Select(Uri.EscapeDataString));
                    var fileUrl = Url.TrimEnd('/') + "/" + encodedPath;
                    long start = Math.Max(rangeStart - fileStart, 0);
                    long end = Math.Min(fileEnd - fileStart, rangeEnd - fileStart);

                    var chunk = await FetchRangeAsync(fileUrl, start, end);
                    System.Array.Copy(chunk, 0, data, pos, chunk.Length);
                    pos += chunk.Length;
                }
                if (pos != data.Length) System.Array.Resize(ref data, pos);   // safety: trim if file coverage was partial
            }

            respond(null, data);
        }
        catch (Exception ex)
        {
            if (WebTorrentClient.VerboseLogging)
                Console.WriteLine($"[WebConn] HandleRequest piece={pieceIndex} offset={offset} len={length} FAILED: {ex.GetType().Name}: {ex.Message}");

            // On HTTP failure, wait RetryDelay then re-announce the piece
            _ = Task.Run(async () =>
            {
                await Task.Delay(RetryDelay);
                if (!Destroyed)
                    await WireInstance.Have(pieceIndex);
            });

            respond(ex, null);
        }
    }

    // Read-ahead coalescing (single-file web seeds): collapse the per-piece Range GET storm into a few large span
    // GETs. The fetch chunk (~1 MiB) is DECOUPLED from the piece size, so pieces stay fine-grained for hashing
    // while requests stay few. A request that falls OUTSIDE the current buffer simply moves the window there with a
    // fresh span GET — so piece-priority changes are honoured at the span boundary with NO in-flight cancellation
    // (the ~1 MiB span bounds the worst-case wait). See Plans/lazy-hash-torrents.md.
    internal static int ReadAheadSpan = 1 * 1024 * 1024;
    private readonly SemaphoreSlim _readAheadLock = new(1, 1);
    private long _bufStart = -1;
    private byte[]? _buf;

    private async Task<byte[]> FetchSingleFileCoalescedAsync(string fileUrl, long start, int length)
    {
        // Fast path: fully inside the current buffer (lock-free snapshot of the buffer references).
        var buf = _buf; var bs = _bufStart;
        if (buf != null && start >= bs && start + length <= bs + buf.Length)
            return buf.AsSpan((int)(start - bs), length).ToArray();

        await _readAheadLock.WaitAsync();
        try
        {
            // Re-check: a concurrent request may have just filled a covering span.
            buf = _buf; bs = _bufStart;
            if (buf != null && start >= bs && start + length <= bs + buf.Length)
                return buf.AsSpan((int)(start - bs), length).ToArray();

            // Miss: fetch a fresh ~1 MiB span starting AT this request (so the requested bytes sit at span[0..])
            // and the following contiguous pieces are served from the buffer with no further GETs. Never shrink
            // below the request, never run past EOF.
            long total = _torrent.Length;
            int spanLen = (int)Math.Min(Math.Max((long)ReadAheadSpan, length), total - start);
            var span = await FetchRangeAsync(fileUrl, start, start + spanLen - 1);
            _buf = span;
            _bufStart = start;
            int avail = Math.Min(length, span.Length);
            return span.AsSpan(0, avail).ToArray();
        }
        finally { _readAheadLock.Release(); }
    }

    /// <summary>Count of actual HTTP Range GETs issued by all web seeds — used by tests to prove read-ahead
    /// coalescing collapses the per-piece request storm. Not thread-isolated; reset + read in a scoped test.</summary>
    internal static int FetchRangeCount;

    private async Task<byte[]> FetchRangeAsync(string url, long start, long end)
    {
        System.Threading.Interlocked.Increment(ref FetchRangeCount);
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(start, end);
        request.Headers.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue { NoStore = true };

        using var cts = new CancellationTokenSource(SocketTimeout);
        if (WebTorrentClient.VerboseLogging)
            Console.WriteLine($"[WebConn] GET {url} Range=bytes {start}-{end}");
        using var response = await _http.SendAsync(request, cts.Token);
        if (WebTorrentClient.VerboseLogging)
            Console.WriteLine($"[WebConn] {url} -> {(int)response.StatusCode} {response.StatusCode}");
        response.EnsureSuccessStatusCode();
        var bytes = await response.Content.ReadAsByteArrayAsync(cts.Token);
        if (WebTorrentClient.VerboseLogging)
            Console.WriteLine($"[WebConn] {url} read {bytes.Length} bytes");
        return bytes;
    }

    /// <summary>
    /// ZERO-COPY browser fetch of a piece's byte range as a JS <see cref="Uint8Array"/> that NEVER enters
    /// the .NET heap. Uses the browser fetch API directly (<see cref="SpawnJSRuntime"/>), not the .NET
    /// HttpClient — which would marshal every byte into the WASM heap (the streaming/model-download
    /// bottleneck). The returned Uint8Array is hashed (SubtleCrypto) and stored to OPFS entirely JS-side.
    /// Works for single-file torrents AND for pieces INTERIOR to one file of a multi-file torrent (the caller,
    /// <c>RequestBlock</c>, sends boundary pieces down the .NET block path instead). The caller owns +
    /// disposes the returned Uint8Array. Browser-only (SpawnJSRuntime.Instance).
    /// </summary>
    internal async Task<Uint8Array> FetchPieceUint8ArrayAsync(long start, long end)
    {
        System.Threading.Interlocked.Increment(ref FetchRangeCount); // count zero-copy GETs too (total web-seed HTTP GETs)
        string fileUrl;
        long fetchStart = start, fetchEnd = end;
        var files = _torrent.Files;
        if (files != null && files.Length > 1)
        {
            // Multi-file: the caller guarantees [start,end] is interior to ONE file. Locate it, translate to a
            // file-relative range, and build its BEP 19 URL (baseUrl/torrentName/filePath) - same mapping as
            // the .NET block path in HandleRequestAsync.
            TorrentFileInfo? file = null;
            foreach (var f in files)
                if (f.Offset <= start && start <= f.Offset + f.Length - 1) { file = f; break; }
            if (file == null) throw new InvalidOperationException($"[WebConn zero-copy] no file contains offset {start}");
            fileUrl = BuildFileUrl(file);
            fetchStart = start - file.Offset;
            fetchEnd = end - file.Offset;
        }
        else
        {
            fileUrl = Url;
            if (fileUrl.EndsWith('/'))
            {
                var name = _torrent.Name ?? _torrent.Files?[0]?.Name ?? _torrent.Files?[0]?.Path ?? "";
                fileUrl = fileUrl + Uri.EscapeDataString(name);
            }
        }
        var opts = new FetchOptions { Headers = new Dictionary<string, string> { ["Range"] = $"bytes={fetchStart}-{fetchEnd}" }, Cache = "no-store" };
        if (WebTorrentClient.VerboseLogging)
            Console.WriteLine($"[WebConn zero-copy] FETCH {fileUrl} Range=bytes {fetchStart}-{fetchEnd}");
        using var response = await SpawnJSRuntime.Instance.Fetch(fileUrl, opts);
        int status = response.Status;
        if (status != 200 && status != 206)
            throw new HttpRequestException($"[WebConn zero-copy] {fileUrl} -> HTTP {status}");
        var ab = await response.ArrayBuffer();
        var ua = new Uint8Array(ab);
        ab.Dispose();
        return ua; // caller owns + disposes
    }

    /// <summary>Build the BEP 19 web-seed URL (baseUrl/torrentName/filePath) for one file of a multi-file
    /// torrent. Mirrors the inline mapping in <see cref="HandleRequestAsync"/>.</summary>
    private string BuildFileUrl(TorrentFileInfo file)
    {
        var filePath = (file.Path ?? file.Name ?? "").Replace('\\', '/');
        if (_torrent.Name != null && !filePath.StartsWith(_torrent.Name + "/", StringComparison.OrdinalIgnoreCase))
            filePath = _torrent.Name + "/" + filePath;
        var encodedPath = string.Join("/", filePath.Split('/').Select(Uri.EscapeDataString));
        return Url.TrimEnd('/') + "/" + encodedPath;
    }

    // ========================
    // DISPOSE
    // ========================

    public async ValueTask DisposeAsync()
    {
        if (Destroyed) return;
        Destroyed = true;
        WireInstance.Destroy();
    }
}
