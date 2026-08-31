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

    public SourceProxy(SourceProxyOptions options, HttpClient http)
    {
        _options = options;
        _http = http;
        Directory.CreateDirectory(_options.CacheDirectory);
    }

    /// <summary>Hosts this proxy will fetch from. Empty means the proxy refuses everything.</summary>
    public IReadOnlyList<string> AllowedHosts => _options.AllowedHosts;

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
            await GetFileCache(uri!).ServeRangeAsync(ctx);
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

        var memberPath = Path.Combine(_options.CacheDirectory,
            ArchiveMemberExtractor.MemberCacheName(uri, member));
        if (File.Exists(memberPath)) return memberPath;

        // One extraction at a time per member: a second concurrent request would otherwise decompress the
        // same archive again and race on the same output file.
        var gate = _memberGates.GetOrAdd(memberPath, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ctx.RequestAborted);
        try
        {
            if (File.Exists(memberPath)) return memberPath;

            var archivePath = await GetFileCache(uri).EnsureCompleteAsync(ctx.RequestAborted);
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
    private PartialFileCache GetFileCache(Uri uri)
        => _fileCaches.GetOrAdd(uri.AbsoluteUri, key => new PartialFileCache(
            Path.Combine(_options.CacheDirectory, CacheFileName(uri)),
            _http,
            (start, end, ct) =>
            {
                var req = new HttpRequestMessage(HttpMethod.Get, uri);
                req.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(start, end);
                return Task.FromResult(req);
            }));

    /// <summary>Enumerate an archive's members, fetching it once if needed.</summary>
    public async Task<List<ArchiveEntryInfo>?> ListArchiveAsync(Uri uri, ArchiveKind kind, CancellationToken ct)
    {
        var archivePath = await GetFileCache(uri).EnsureCompleteAsync(ct);
        if (archivePath == null) return null;
        return await ArchiveMemberExtractor.ListAsync(archivePath, kind, ct);
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
            var entries = await proxy.ListArchiveAsync(uri!, kind, ctx.RequestAborted);
            if (entries == null)
            {
                ctx.Response.StatusCode = (int)HttpStatusCode.BadGateway;
                await ctx.Response.WriteAsync("could not fetch the archive from its origin");
                return;
            }
            await ctx.Response.WriteAsJsonAsync(new { archive = uri!.AbsoluteUri, kind = kind.ToString(), entries });
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
}
