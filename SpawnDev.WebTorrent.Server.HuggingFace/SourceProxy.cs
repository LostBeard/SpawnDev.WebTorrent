using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace SpawnDev.WebTorrent.Server.HuggingFace;

/// <summary>
/// A CORS-enabled, range-capable, caching proxy for any ALLOWLISTED origin.
/// </summary>
/// <remarks>
/// <para>
/// The hub already had two hand-written proxies - <see cref="HuggingFaceProxy"/> and
/// <see cref="OllamaProxy"/> - and every new source meant a third. This is the generic form: a host
/// allowlist plus the same <see cref="PartialFileCache"/> core, so anything we need becomes a URL the
/// browser can read. That matters because the lazy-hash streamer only ever needed a URL; the thing it
/// could not do was reach an origin that sends no CORS headers, or one that has no per-file URL at all.
/// </para>
/// <para>
/// The immediate motivation: ZipVoice's vocoder (<c>vocos_24khz.onnx</c>, mel -&gt; magnitude/cos/sin) is
/// not on HuggingFace in that form - the one repo that looks right holds the mel EXTRACTOR, the inverse
/// direction - so it ships only inside a sherpa-onnx GitHub release. With an allowlist entry it is simply
/// a URL again.
/// </para>
/// <para>
/// ⚠️ THE ALLOWLIST IS THE SECURITY BOUNDARY, and an open proxy is not a smaller version of this - it is a
/// different thing: it would let anyone use the hub to launder requests to arbitrary hosts (including the
/// hub's own network) and to spend its bandwidth. So the default is EMPTY, an unlisted host is refused,
/// and literal IP addresses are refused outright rather than resolved and checked, because a DNS name that
/// passes the check can still resolve to a private address later.
/// </para>
/// </remarks>
public class SourceProxy
{
    private readonly SourceProxyOptions _options;
    private readonly HttpClient _http;
    private readonly ConcurrentDictionary<string, PartialFileCache> _fileCaches = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _memberGates = new();
    private readonly SourceCacheEvictor _evictor;

    /// <summary>Fetches running in the background on behalf of <c>/src/warm</c>, keyed by cache path.</summary>
    private readonly ConcurrentDictionary<string, WarmJob> _warming = new(StringComparer.OrdinalIgnoreCase);

    private sealed class WarmJob
    {
        public Task? Task;
        public long TotalBytes = -1;
        public string? Error;
        public IDisposable? Pin;
    }

    public SourceProxy(SourceProxyOptions options, HttpClient http)
    {
        _options = options;
        _http = http;
        _evictor = new SourceCacheEvictor(options);
        // Evicting the bytes without forgetting the reader leaves a reader convinced it still has a complete
        // file, which fails the NEXT request with a 500 instead of re-fetching. Found by evicting for real.
        _evictor.Evicted += path => _fileCaches.TryRemove(path, out _);
        Directory.CreateDirectory(_options.CacheDirectory);
    }

    /// <summary>Hosts this proxy will fetch from. Empty means the proxy refuses everything.</summary>
    public IReadOnlyList<string> AllowedHosts => _options.AllowedHosts;

    /// <summary>This proxy's configuration.</summary>
    public SourceProxyOptions Options => _options;

    /// <summary>Bytes currently held in the cache directory.</summary>
    public long CacheSizeBytes => _evictor.CurrentSizeBytes();

    /// <summary>Free bytes on the cache drive.</summary>
    public long CacheDriveFreeBytes => _evictor.FreeSpaceBytes();

    /// <summary>
    /// Whether <paramref name="url"/> is one this proxy may fetch.
    /// </summary>
    /// <remarks>
    /// Matching is on the HOST, case-insensitively, either exactly or as a subdomain of an allowlist entry
    /// - so "github.com" also permits "objects.githubusercontent.com" only if that host is listed too.
    /// Release downloads redirect across hosts, so list every host a source actually serves from; being
    /// explicit is the point.
    /// </remarks>
    public bool IsAllowed(string url, out Uri? parsed, out string? reason)
    {
        parsed = null;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        { reason = "not an absolute URL"; return false; }
        if (uri.Scheme != Uri.UriSchemeHttps)
        { reason = "only https is proxied"; return false; }
        // A literal IP bypasses the whole point of naming hosts, and is the classic SSRF shape.
        if (uri.HostNameType is UriHostNameType.IPv4 or UriHostNameType.IPv6)
        { reason = "literal IP addresses are not proxied"; return false; }

        foreach (var allowed in _options.AllowedHosts)
        {
            if (uri.Host.Equals(allowed, StringComparison.OrdinalIgnoreCase)
                || uri.Host.EndsWith("." + allowed, StringComparison.OrdinalIgnoreCase))
            { parsed = uri; reason = null; return true; }
        }
        reason = $"host '{uri.Host}' is not in the allowlist";
        return false;
    }

    /// <summary>Serve <paramref name="url"/> through the cache, honouring Range and adding CORS headers.</summary>
    /// <param name="ctx">The request being served.</param>
    /// <param name="url">Absolute https URL on the allowlist.</param>
    /// <param name="member">
    /// When set, serve this file from INSIDE the archive at <paramref name="url"/> instead of the archive
    /// itself - so an archived model becomes a plain URL.
    /// </param>
    public async Task HandleRequestAsync(HttpContext ctx, string url, string? member = null)
    {
        AddCorsHeaders(ctx);

        if (!IsAllowed(url, out var uri, out var reason))
        {
            ctx.Response.StatusCode = (int)HttpStatusCode.Forbidden;
            // Name the reason: a silent 403 on a proxy is indistinguishable from the origin being down,
            // and the caller cannot tell "add this host" from "the file moved".
            await ctx.Response.WriteAsync($"refused: {reason}");
            return;
        }

        if (string.IsNullOrEmpty(member))
        {
            // Pin and touch BEFORE making room, not after. This file is the one request that is definitely
            // in use, so it must be both the last candidate by recency and off the table entirely - a warm
            // hit that evicts its own cache entry and immediately re-fetches it is a cache working backwards.
            var cachePath = CachePath(uri!);
            _evictor.Touch(cachePath);
            using var pin = _evictor.Pin(cachePath);

            // Before the fetch, not after, and for the RIGHT number of bytes. Asking for room without saying
            // how much only notices a limit once it has already been broken - which is the same failure as
            // checking afterwards, wearing a guard's clothes. The number asked for is the ADDITIONAL disk a
            // fetch would consume, so a warm hit asks for nothing and can never be refused for space.
            var cache = GetFileCache(uri!);
            var incoming = await cache.ProbeAdditionalBytesAsync(ctx.RequestAborted);
            if (!await _evictor.EnsureRoomAsync(Math.Max(0, incoming)))
            {
                ctx.Response.StatusCode = (int)HttpStatusCode.InsufficientStorage;
                await ctx.Response.WriteAsync(
                    "the cache drive is out of room and nothing further can be evicted");
                DiscardUnstartedFetch(cachePath);
                return;
            }
            await cache.ServeRangeAsync(ctx);
            return;
        }

        var memberPath = await EnsureMemberAsync(ctx, uri!, member);
        if (memberPath == null) return;   // EnsureMemberAsync has written the failure
        await ServeFileRangeAsync(ctx, memberPath);
    }

    /// <summary>
    /// Ensure one member of an archive is extracted and cached; returns its path, or null having already
    /// written the error response.
    /// </summary>
    /// <remarks>
    /// The archive is fetched ONCE through the same partial cache and then decompressed once. A
    /// <c>.tar.bz2</c> cannot be seeked into, so somebody has to pay a full fetch - doing it here means one
    /// machine pays it instead of every visitor.
    /// </remarks>
    private async Task<string?> EnsureMemberAsync(HttpContext ctx, Uri uri, string member)
    {
        if (!ArchiveMemberExtractor.IsSafeMemberPath(member))
        {
            ctx.Response.StatusCode = (int)HttpStatusCode.BadRequest;
            await ctx.Response.WriteAsync("refused: member path must be relative and free of '..'");
            return null;
        }

        var kind = ArchiveMemberExtractor.DetectKind(uri);
        if (kind == ArchiveKind.None)
        {
            ctx.Response.StatusCode = (int)HttpStatusCode.BadRequest;
            await ctx.Response.WriteAsync(
                "refused: url does not look like an archive (.tar, .tar.gz/.tgz, .tar.bz2/.tbz2, .zip)");
            return null;
        }

        var memberPath = Path.GetFullPath(Path.Combine(_options.CacheDirectory,
            ArchiveMemberExtractor.MemberCacheName(uri, member)));
        if (File.Exists(memberPath)) { _evictor.Touch(memberPath); return memberPath; }

        // One extraction at a time per member: a second concurrent request would otherwise decompress the
        // same archive again and race on the same output file.
        var gate = _memberGates.GetOrAdd(memberPath, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ctx.RequestAborted);
        try
        {
            if (File.Exists(memberPath)) return memberPath;

            var archiveCachePath = CachePath(uri);
            _evictor.Touch(archiveCachePath);
            using var archivePin = _evictor.Pin(archiveCachePath);
            using var memberPin = _evictor.Pin(memberPath);

            // An archive arrives WHOLE and they are large - the fp32 ZipVoice package is 634 MB - so this
            // is the request most able to fill a drive, and the one most worth checking beforehand.
            var archiveCache = GetFileCache(uri);
            var incoming = await archiveCache.ProbeAdditionalBytesAsync(ctx.RequestAborted);
            if (!await _evictor.EnsureRoomAsync(Math.Max(0, incoming)))
            {
                ctx.Response.StatusCode = (int)HttpStatusCode.InsufficientStorage;
                await ctx.Response.WriteAsync(
                    "the cache drive is out of room and nothing further can be evicted");
                DiscardUnstartedFetch(archiveCachePath);
                return null;
            }

            var archivePath = await archiveCache.EnsureCompleteAsync(ctx.RequestAborted);
            if (archivePath == null)
            {
                ctx.Response.StatusCode = (int)HttpStatusCode.BadGateway;
                await ctx.Response.WriteAsync("could not fetch the archive from its origin");
                return null;
            }

            if (!await ArchiveMemberExtractor.ExtractAsync(archivePath, kind, member, memberPath,
                                                           ctx.RequestAborted))
            {
                ctx.Response.StatusCode = (int)HttpStatusCode.NotFound;
                // Point at the listing rather than leaving the caller to guess the exact in-archive path,
                // which is the single most likely thing to get wrong.
                await ctx.Response.WriteAsync(
                    $"no member '{member}' in that archive - GET /src/list?url=... to see the names");
                return null;
            }
            // Both the extracted member AND the archive it came from count as used: evicting the archive
            // while its members are hot would force a 634 MB re-fetch on the next member request.
            _evictor.Touch(memberPath);
            _evictor.Touch(archivePath);

            // The member's size could NOT be known before extracting it, so the pre-check budgeted only for
            // the archive. Settle up now, while both are still pinned - so the cache is back under its limit
            // before the next request arrives rather than one request later.
            await _evictor.EnsureRoomAsync();
            return memberPath;
        }
        finally { gate.Release(); }
    }

    /// <summary>Serve a local file with Range support.</summary>
    private static async Task ServeFileRangeAsync(HttpContext ctx, string path)
    {
        var info = new FileInfo(path);
        ctx.Response.Headers["Accept-Ranges"] = "bytes";
        ctx.Response.ContentType = "application/octet-stream";

        var rangeHeader = ctx.Request.Headers.Range.ToString();
        long start = 0, end = info.Length - 1;
        if (!string.IsNullOrEmpty(rangeHeader) && rangeHeader.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase))
        {
            var span = rangeHeader["bytes=".Length..].Split('-');
            if (span.Length == 2)
            {
                if (long.TryParse(span[0], out var s)) start = s;
                if (long.TryParse(span[1], out var e)) end = Math.Min(e, info.Length - 1);
            }
            if (start > end || start >= info.Length)
            {
                ctx.Response.StatusCode = (int)HttpStatusCode.RequestedRangeNotSatisfiable;
                ctx.Response.Headers["Content-Range"] = $"bytes */{info.Length}";
                return;
            }
            ctx.Response.StatusCode = (int)HttpStatusCode.PartialContent;
            ctx.Response.Headers["Content-Range"] = $"bytes {start}-{end}/{info.Length}";
        }

        ctx.Response.ContentLength = end - start + 1;
        await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        fs.Seek(start, SeekOrigin.Begin);
        await CopyRangeAsync(fs, ctx.Response.Body, end - start + 1, ctx.RequestAborted);
    }

    private static async Task CopyRangeAsync(Stream src, Stream dst, long count, CancellationToken ct)
    {
        var buffer = new byte[81920];
        while (count > 0)
        {
            int want = (int)Math.Min(buffer.Length, count);
            int got = await src.ReadAsync(buffer.AsMemory(0, want), ct);
            if (got <= 0) break;
            await dst.WriteAsync(buffer.AsMemory(0, got), ct);
            count -= got;
        }
    }

    /// <summary>
    /// One cache per URL. The on-disk name is a hash so that any URL is a safe filename, with a readable
    /// prefix kept purely so a human can tell what a cache directory holds.
    /// </summary>
    /// <summary>
    /// The memoised cache reader for a URL, keyed by its CACHE PATH.
    /// </summary>
    /// <remarks>
    /// Keyed by path rather than by URL so eviction can drop the right entry: the evictor knows only which
    /// FILE it deleted, and a reader left holding stale per-file state would go on serving from a file that
    /// no longer exists. The path is a deterministic function of the URL, so it is no weaker as a key.
    /// </remarks>
    /// <summary>
    /// Undo the state a size probe created for a fetch that was then refused.
    /// </summary>
    /// <remarks>
    /// Probing the origin writes a manifest so a resumed download knows what it already has. When the fetch
    /// is refused for space that manifest describes a file that does not exist, and manifests are not
    /// eviction candidates - so it would sit there for good. Small, but a cache that leaks an entry per
    /// refusal is a cache that leaks fastest exactly when it is already full.
    /// </remarks>
    private void DiscardUnstartedFetch(string cachePath)
    {
        try
        {
            if (File.Exists(cachePath)) return;   // a real partial download: leave it to be resumed or evicted
            var manifest = cachePath + PartialFileCache.ManifestSuffix;
            if (File.Exists(manifest)) File.Delete(manifest);
            _fileCaches.TryRemove(cachePath, out _);
        }
        catch { /* best effort - never fail a request over cleanup */ }
    }

    /// <summary>Absolute path of the cache file for a URL. Always absolute - see SourceCacheEvictor.Norm.</summary>
    private string CachePath(Uri uri) => Path.GetFullPath(Path.Combine(_options.CacheDirectory, CacheFileName(uri)));

    private PartialFileCache GetFileCache(Uri uri)
        => _fileCaches.GetOrAdd(CachePath(uri), path => new PartialFileCache(
            path,
            _http,
            (start, end, ct) =>
            {
                var req = new HttpRequestMessage(HttpMethod.Get, uri);
                req.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(start, end);
                return Task.FromResult(req);
            }));

    /// <summary>
    /// Start (or report on) a background fetch of <paramref name="uri"/> without holding the request open.
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⚠️ First contact with a large archive takes MINUTES - a <c>.tar.bz2</c> cannot be seeked into, so the
    /// 634 MB fp32 ZipVoice package is fetched whole and decompressed before a single member can be served.
    /// A request that blocks for that long does not merely feel slow, it gets killed by whatever sits in
    /// front of the server: the gateway answered 504 at 25 seconds while the hub was still working
    /// perfectly, which reads as a broken hub and is not one.
    /// </para>
    /// <para>
    /// Raising that timeout treats the symptom and fails again on a bigger archive. This is the fix that
    /// does not care how long the fetch takes: nothing is held open, the caller polls, and progress is a
    /// real number rather than a guess.
    /// </para>
    /// </remarks>
    /// <param name="member">
    /// When set, warm through to this file INSIDE the archive rather than stopping at the archive.
    /// </param>
    /// <remarks>
    /// ⚠️ Warming the archive alone is not enough, and measurement is why: with the 634 MB package already
    /// fully cached, extracting one member still took <b>29.6 seconds</b> - bzip2 has to be decompressed
    /// from the start to reach any member - which is still over the gateway's limit. Warming that stopped at
    /// the archive would have moved the 504 rather than removed it.
    /// </remarks>
    public WarmStatus Warm(Uri uri, string? member = null)
    {
        var cachePath = CachePath(uri);

        // The completion test is the MEMBER when one is named. Otherwise a caller polls until the archive
        // lands, is told to proceed, and then blocks for half a minute on the extraction it was told was done.
        string? memberPath = null;
        if (!string.IsNullOrEmpty(member))
        {
            if (!ArchiveMemberExtractor.IsSafeMemberPath(member))
                return new WarmStatus(false, 0, -1, "member path must be relative and free of '..'");
            if (ArchiveMemberExtractor.DetectKind(uri) == ArchiveKind.None)
                return new WarmStatus(false, 0, -1, "url does not look like an archive");
            memberPath = Path.GetFullPath(Path.Combine(_options.CacheDirectory,
                ArchiveMemberExtractor.MemberCacheName(uri, member)));
        }

        var doneWhen = memberPath ?? cachePath;
        bool done = memberPath != null ? File.Exists(memberPath) : PartialFileCache.IsComplete(cachePath);
        if (done)
        {
            _warming.TryRemove(doneWhen, out var finished);
            finished?.Pin?.Dispose();
            _evictor.Touch(doneWhen);
            var len = new FileInfo(doneWhen).Length;
            return new WarmStatus(true, len, len, null);
        }

        var job = _warming.GetOrAdd(doneWhen, _ => new WarmJob());
        lock (job)
        {
            if (job.Task == null)
            {
                // Pinned for the whole warm, so eviction cannot delete the very file being fetched.
                job.Pin = _evictor.Pin(cachePath);
                job.Task = Task.Run(async () =>
                {
                    try
                    {
                        var cache = GetFileCache(uri);
                        // ⚠️ CancellationToken.None on purpose: this outlives the request that started it.
                        // Tying it to RequestAborted would cancel the fetch the instant the caller polls
                        // and disconnects - which is exactly what every caller does.
                        // ADDITIONAL bytes, not total - so the reported total is what is already on disk
                        // plus what is still to come. Reporting the additional figure as the total makes a
                        // resumed fetch show more than 100% complete.
                        long onDisk = File.Exists(cachePath) ? new FileInfo(cachePath).Length : 0;
                        long additional = await cache.ProbeAdditionalBytesAsync(CancellationToken.None);
                        job.TotalBytes = additional >= 0 ? onDisk + additional : -1;
                        if (!await _evictor.EnsureRoomAsync(Math.Max(0, additional)))
                        {
                            DiscardUnstartedFetch(cachePath);
                            job.Error = "the cache drive is out of room and nothing further can be evicted";
                            return;
                        }
                        var archivePath = await cache.EnsureCompleteAsync(CancellationToken.None);
                        if (archivePath == null) { job.Error = "could not fetch from the origin"; return; }
                        _evictor.Touch(cachePath);

                        if (memberPath == null) return;

                        // Extraction is the slow half once the archive is local, so it belongs INSIDE the
                        // background job - see this method's remarks for the measurement that says so.
                        using var memberPin = _evictor.Pin(memberPath);
                        if (!await ArchiveMemberExtractor.ExtractAsync(
                                archivePath, ArchiveMemberExtractor.DetectKind(uri), member!, memberPath,
                                CancellationToken.None))
                            job.Error = $"no member '{member}' in that archive";
                        else
                        {
                            _evictor.Touch(memberPath);
                            await _evictor.EnsureRoomAsync();
                        }
                    }
                    catch (Exception ex) { job.Error = $"{ex.GetType().Name}: {ex.Message}"; }
                    finally { job.Pin?.Dispose(); job.Pin = null; }
                });
            }
        }

        // Progress is reported against the ARCHIVE even when warming to a member: it is the part with a
        // knowable size, and it is the part that takes the time.
        long have = File.Exists(cachePath) ? new FileInfo(cachePath).Length : 0;
        var error = job.Error;
        // Drop a failed job so the next call starts a fresh attempt rather than replaying the failure.
        if (error != null && _warming.TryRemove(doneWhen, out var dead)) dead.Pin?.Dispose();
        bool ready = memberPath != null ? File.Exists(memberPath) : PartialFileCache.IsComplete(cachePath);
        return new WarmStatus(ready, have, job.TotalBytes > 0 ? job.TotalBytes : -1, error);
    }

    /// <summary>Progress of a background warm.</summary>
    public readonly record struct WarmStatus(bool Cached, long HaveBytes, long TotalBytes, string? Error);

    /// <summary>
    /// Warm an archive's member LISTING - fetching the archive first if needed - without blocking.
    /// </summary>
    /// <remarks>
    /// ⚠️ Enumerating a <c>.tar.bz2</c> is not cheap even when the archive is already local: listing the
    /// warm 634 MB package measured <b>31.2 seconds</b>, because bzip2 must be decompressed from the start
    /// to walk the member headers. So a listing is computed once, in the background, and CACHED beside the
    /// archive. Recomputing it per request would leave <c>/src/list</c> permanently over the gateway's
    /// limit on exactly the archives it is most needed for.
    /// </remarks>
    public WarmStatus WarmListing(Uri uri, ArchiveKind kind)
    {
        var cachePath = CachePath(uri);
        var listingPath = cachePath + ListingSuffix;
        if (File.Exists(listingPath))
        {
            _warming.TryRemove(listingPath, out var finished);
            finished?.Pin?.Dispose();
            _evictor.Touch(listingPath);
            var len = new FileInfo(listingPath).Length;
            return new WarmStatus(true, len, len, null);
        }

        var job = _warming.GetOrAdd(listingPath, _ => new WarmJob());
        lock (job)
        {
            if (job.Task == null)
            {
                job.Pin = _evictor.Pin(cachePath);
                job.Task = Task.Run(async () =>
                {
                    try
                    {
                        var cache = GetFileCache(uri);
                        long onDisk = File.Exists(cachePath) ? new FileInfo(cachePath).Length : 0;
                        long additional = await cache.ProbeAdditionalBytesAsync(CancellationToken.None);
                        job.TotalBytes = additional >= 0 ? onDisk + additional : -1;
                        if (!await _evictor.EnsureRoomAsync(Math.Max(0, additional)))
                        {
                            DiscardUnstartedFetch(cachePath);
                            job.Error = "the cache drive is out of room and nothing further can be evicted";
                            return;
                        }
                        var archivePath = await cache.EnsureCompleteAsync(CancellationToken.None);
                        if (archivePath == null) { job.Error = "could not fetch from the origin"; return; }
                        _evictor.Touch(cachePath);

                        var entries = await ArchiveMemberExtractor.ListAsync(
                            archivePath, kind, CancellationToken.None);
                        if (entries == null) { job.Error = "could not read the archive"; return; }

                        // Written via a temp file and moved, so an interrupted write cannot leave a
                        // TRUNCATED listing that later looks like a complete cache hit.
                        var tmp = listingPath + ".partial";
                        await File.WriteAllTextAsync(tmp,
                            System.Text.Json.JsonSerializer.Serialize(entries), CancellationToken.None);
                        File.Move(tmp, listingPath, true);
                        _evictor.Touch(listingPath);
                    }
                    catch (Exception ex) { job.Error = $"{ex.GetType().Name}: {ex.Message}"; }
                    finally { job.Pin?.Dispose(); job.Pin = null; }
                });
            }
        }

        long have = File.Exists(cachePath) ? new FileInfo(cachePath).Length : 0;
        var error = job.Error;
        if (error != null && _warming.TryRemove(listingPath, out var dead)) dead.Pin?.Dispose();
        return new WarmStatus(File.Exists(listingPath), have,
                              job.TotalBytes > 0 ? job.TotalBytes : -1, error);
    }

    /// <summary>Suffix of the cached member listing that sits beside an archive.</summary>
    public const string ListingSuffix = ".listing.json";

    /// <summary>The cached member listing for an archive, or null when it has not been computed yet.</summary>
    public string? ReadCachedListing(Uri uri)
    {
        var listingPath = CachePath(uri) + ListingSuffix;
        if (!File.Exists(listingPath)) return null;
        _evictor.Touch(listingPath);
        try { return File.ReadAllText(listingPath); }
        catch { return null; }
    }

    /// <summary>Enumerate an archive's members, fetching it once if needed.</summary>
    /// <returns>
    /// The entries, or null with <c>OutOfRoom</c> saying WHY. The two failures must stay distinguishable:
    /// reporting "could not fetch from the origin" when the origin was never asked blames a healthy remote
    /// for our own disk limit, and sends whoever debugs it to the wrong machine.
    /// </returns>
    public async Task<(List<ArchiveEntryInfo>? Entries, bool OutOfRoom)> ListArchiveAsync(
        Uri uri, ArchiveKind kind, CancellationToken ct)
    {
        var cachePath = CachePath(uri);
        _evictor.Touch(cachePath);
        using var pin = _evictor.Pin(cachePath);

        var cache = GetFileCache(uri);
        if (!await _evictor.EnsureRoomAsync(Math.Max(0, await cache.ProbeAdditionalBytesAsync(ct))))
        {
            DiscardUnstartedFetch(cachePath);
            return (null, true);
        }

        var archivePath = await cache.EnsureCompleteAsync(ct);
        if (archivePath == null) return (null, false);
        _evictor.Touch(archivePath);
        return (await ArchiveMemberExtractor.ListAsync(archivePath, kind, ct), false);
    }

    internal static string CacheFileName(Uri uri)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(uri.AbsoluteUri)))[..16];
        var leaf = Path.GetFileName(uri.AbsolutePath);
        if (string.IsNullOrEmpty(leaf)) leaf = "file";
        foreach (var c in Path.GetInvalidFileNameChars()) leaf = leaf.Replace(c, '_');
        if (leaf.Length > 60) leaf = leaf[^60..];
        return $"{hash}_{leaf}";
    }

    /// <summary>
    /// CORS for a browser range reader.
    /// </summary>
    /// <remarks>
    /// ⚠️ <c>Access-Control-Expose-Headers</c> is the one that gets forgotten. Without it a cross-origin
    /// caller can receive a 206 and still not read <c>Content-Range</c> or <c>Content-Length</c>, so a
    /// seeking reader cannot work out what it got - which looks like a broken server rather than a missing
    /// header.
    /// </remarks>
    private static void AddCorsHeaders(HttpContext ctx)
    {
        var h = ctx.Response.Headers;
        h["Access-Control-Allow-Origin"] = "*";
        h["Access-Control-Allow-Methods"] = "GET, HEAD, OPTIONS";
        h["Access-Control-Allow-Headers"] = "Range, Content-Type";
        h["Access-Control-Expose-Headers"] = "Content-Range, Content-Length, Accept-Ranges, ETag";
        h["Access-Control-Max-Age"] = "86400";
    }

    /// <summary>Registers the proxy endpoints.</summary>
    public static void Map(IEndpointRouteBuilder app, SourceProxy proxy)
    {
        // The upstream URL travels as a query parameter rather than a path segment: a path-embedded URL has
        // to survive two rounds of slash/encoding rules and silently loses query strings, which release
        // and CDN links frequently carry.
        app.MapMethods("/src", new[] { "OPTIONS" }, (HttpContext ctx) =>
        {
            AddCorsHeaders(ctx);
            ctx.Response.StatusCode = (int)HttpStatusCode.NoContent;
            return Task.CompletedTask;
        });

        app.MapGet("/src", async (HttpContext ctx) =>
        {
            var url = ctx.Request.Query["url"].ToString();
            if (string.IsNullOrWhiteSpace(url))
            {
                AddCorsHeaders(ctx);
                ctx.Response.StatusCode = (int)HttpStatusCode.BadRequest;
                await ctx.Response.WriteAsync("usage: /src?url=<https url on the allowlist>");
                return;
            }
            await proxy.HandleRequestAsync(ctx, url, ctx.Request.Query["member"].ToString());
        });

        // What is INSIDE an archive? Without this a caller has to know the exact in-archive path, which is
        // the most likely thing to get wrong and the least discoverable.
        app.MapGet("/src/list", async (HttpContext ctx) =>
        {
            AddCorsHeaders(ctx);
            var url = ctx.Request.Query["url"].ToString();
            if (string.IsNullOrWhiteSpace(url))
            {
                ctx.Response.StatusCode = (int)HttpStatusCode.BadRequest;
                await ctx.Response.WriteAsync("usage: /src/list?url=<https archive url on the allowlist>");
                return;
            }
            if (!proxy.IsAllowed(url, out var uri, out var reason))
            {
                ctx.Response.StatusCode = (int)HttpStatusCode.Forbidden;
                await ctx.Response.WriteAsync($"refused: {reason}");
                return;
            }
            var kind = ArchiveMemberExtractor.DetectKind(uri!);
            if (kind == ArchiveKind.None)
            {
                ctx.Response.StatusCode = (int)HttpStatusCode.BadRequest;
                await ctx.Response.WriteAsync("refused: url does not look like an archive");
                return;
            }
            // Neither fetching NOR enumerating happens inside this request: both outlive the gateway's
            // patience on a large archive, and the second one does so even when the archive is already local.
            var warm = proxy.WarmListing(uri!, kind);
            if (warm.Error != null)
            {
                ctx.Response.StatusCode = (int)HttpStatusCode.BadGateway;
                await ctx.Response.WriteAsync(warm.Error);
                return;
            }
            if (!warm.Cached)
            {
                ctx.Response.StatusCode = (int)HttpStatusCode.Accepted;
                ctx.Response.Headers["Retry-After"] = "10";
                await ctx.Response.WriteAsJsonAsync(new
                {
                    cached = false,
                    haveBytes = warm.HaveBytes,
                    totalBytes = warm.TotalBytes,
                    message = "archive is being fetched and enumerated; poll this URL until it returns 200",
                });
                return;
            }

            var listing = proxy.ReadCachedListing(uri!);
            if (listing == null)
            {
                ctx.Response.StatusCode = (int)HttpStatusCode.BadGateway;
                await ctx.Response.WriteAsync("the cached listing disappeared; retry");
                return;
            }
            // The cached listing is already JSON, so it is spliced in rather than deserialised and
            // re-serialised just to be handed straight back out.
            ctx.Response.ContentType = "application/json";
            var head = System.Text.Json.JsonSerializer.Serialize(uri!.AbsoluteUri);
            await ctx.Response.WriteAsync(
                "{\"archive\":" + head + ",\"kind\":\"" + kind + "\",\"entries\":" + listing + "}");
        });

        // Warm an origin WITHOUT holding a connection open for it. See SourceProxy.Warm for why a blocking
        // first fetch is not merely slow but actively killed by the gateway in front of this server.
        app.MapGet("/src/warm", async (HttpContext ctx) =>
        {
            AddCorsHeaders(ctx);
            var url = ctx.Request.Query["url"].ToString();
            if (string.IsNullOrWhiteSpace(url))
            {
                ctx.Response.StatusCode = (int)HttpStatusCode.BadRequest;
                await ctx.Response.WriteAsync(
                    "usage: /src/warm?url=<https url on the allowlist>[&member=<path inside the archive>]");
                return;
            }
            if (!proxy.IsAllowed(url, out var uri, out var reason))
            {
                ctx.Response.StatusCode = (int)HttpStatusCode.Forbidden;
                await ctx.Response.WriteAsync($"refused: {reason}");
                return;
            }

            var st = proxy.Warm(uri!, ctx.Request.Query["member"].ToString());
            if (st.Error != null)
            {
                // The failed job is discarded as it is reported, so the next call RETRIES. A remembered
                // failure would turn one bad minute at the origin into a permanently unfetchable URL.
                ctx.Response.StatusCode = (int)HttpStatusCode.BadGateway;
                await ctx.Response.WriteAsJsonAsync(new { cached = false, error = st.Error });
                return;
            }
            // 202 while it works, 200 when it is ready - so a caller can poll on the STATUS CODE alone and
            // never has to parse a body to find out whether it may proceed.
            ctx.Response.StatusCode = (int)(st.Cached ? HttpStatusCode.OK : HttpStatusCode.Accepted);
            if (!st.Cached) ctx.Response.Headers["Retry-After"] = "10";
            await ctx.Response.WriteAsJsonAsync(new
            {
                cached = st.Cached,
                haveBytes = st.HaveBytes,
                totalBytes = st.TotalBytes,
                percent = st.TotalBytes > 0 ? Math.Round(100.0 * st.HaveBytes / st.TotalBytes, 1) : (double?)null,
            });
        });

        // Cache health, so a full drive is something an operator can SEE coming rather than discover as an
        // outage.
        app.MapGet("/src/stats", (HttpContext ctx) =>
        {
            AddCorsHeaders(ctx);
            return Results.Json(new
            {
                cacheBytes = proxy.CacheSizeBytes,
                freeBytes = proxy.CacheDriveFreeBytes,
                minFreeBytes = proxy.Options.MinFreeDiskSpaceBytes,
                maxCacheBytes = proxy.Options.MaxCacheSizeBytes,
            });
        });

        // What may this hub fetch? Published so a caller can find out WITHOUT trial and error, and so the
        // allowlist is inspectable rather than folklore.
        app.MapGet("/src/allowed", (HttpContext ctx) =>
        {
            AddCorsHeaders(ctx);
            return Results.Json(new { allowedHosts = proxy.AllowedHosts });
        });
    }
}

/// <summary>Configuration for <see cref="SourceProxy"/>.</summary>
public class SourceProxyOptions
{
    /// <summary>
    /// Hosts the proxy may fetch from. An entry also permits its subdomains.
    /// </summary>
    /// <remarks>
    /// ⚠️ EMPTY BY DEFAULT, deliberately. A proxy that ships open is an abuse vector the moment it is
    /// reachable, and "we will lock it down later" is how it stays open. Add hosts explicitly.
    /// Note that large downloads often REDIRECT to a different host (GitHub releases land on
    /// objects.githubusercontent.com, HuggingFace on cdn-lfs domains), so a source usually needs more than
    /// one entry - which is a feature: each is a deliberate decision.
    /// </remarks>
    public string[] AllowedHosts { get; set; } = Array.Empty<string>();

    /// <summary>Where proxied files are cached.</summary>
    public string CacheDirectory { get; set; } = "src-cache";

    /// <summary>
    /// Maximum bytes the cache may hold. 0 = no explicit limit (the free-space floor still applies).
    /// </summary>
    public long MaxCacheSizeBytes { get; set; } = 0;

    /// <summary>
    /// Free space to keep available on the cache drive. Default 10 GB.
    /// </summary>
    /// <remarks>
    /// Deliberately higher than the HuggingFace proxy's 2 GB, because the units differ: that proxy caches
    /// individual model FILES, while this one caches whole ARCHIVES that arrive in one piece - the fp32
    /// ZipVoice package alone is 634 MB, so a 2 GB floor is three fetches deep. The floor has to exceed the
    /// largest single thing that can arrive, or the guard trips only after the fetch it was meant to
    /// prevent.
    /// </remarks>
    public long MinFreeDiskSpaceBytes { get; set; } = 10L * 1024 * 1024 * 1024;
}
