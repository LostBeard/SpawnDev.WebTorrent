using System.Collections.Concurrent;
using Microsoft.AspNetCore.Http;

namespace SpawnDev.WebTorrent.Server.HuggingFace;

/// <summary>
/// Options for <see cref="ModelsRedirect"/>.
/// </summary>
public sealed class ModelsRedirectOptions
{
    /// <summary>False turns the redirect off; the deprecated endpoints are still counted and logged.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Where mirrored models live (a static store with free egress). No trailing slash needed.</summary>
    public string BaseUrl { get; set; } = "https://models.spawndev.com";

    /// <summary>How long "this file exists on the models host" is remembered.</summary>
    public TimeSpan ExistsCacheTtl { get; set; } = TimeSpan.FromHours(1);

    /// <summary>How long "not there yet" is remembered (short: a file can be mirrored at any moment).</summary>
    public TimeSpan MissingCacheTtl { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>
    /// <c>Cache-Control: max-age</c> on the 307. A model download is thousands of Range requests; letting the browser
    /// cache the redirect means only the first of them touches this hub at all.
    /// </summary>
    public int RedirectMaxAgeSeconds { get; set; } = 3600;

    /// <summary>The same client asking for the same path is logged once per this window (counters count every hit).</summary>
    public TimeSpan LogDedupeWindow { get; set; } = TimeSpan.FromMinutes(10);

    public TimeSpan ProbeTimeout { get; set; } = TimeSpan.FromSeconds(5);
}

/// <summary>
/// Maps the hub's DEPRECATED model endpoints onto <c>models.spawndev.com</c> paths. Pure functions, no I/O.
/// </summary>
/// <remarks>
/// models.spawndev.com (Cloudflare R2) keeps the Hugging Face layout, so a hub path maps by rewriting:
/// <list type="bullet">
/// <item><c>/hf/{org}/{repo}/{file}</c> to <c>{org}/{repo}/resolve/main/{file}</c></item>
/// <item><c>/ollama/{model}/{tag}/{layer}</c> to <c>ollama/{model}/{tag}/{model}-{tag}-{layer}.gguf</c> (the name the hub
/// cached the layer under)</item>
/// <item><c>/src?url=U[&amp;member=M]</c>: a huggingface.co URL maps to its repo path (model or <c>datasets/</c>); an
/// archive member maps to <c>{host}{path without the archive extension}/{M without its first folder}</c> (an archive
/// cannot be range-read, so its members were stored as plain files); any other URL maps to <c>{host}{path}</c>.</item>
/// </list>
/// </remarks>
public static class ModelsRedirectMap
{
    /// <summary>Which deprecated endpoint a request path belongs to, or null if it is not one.</summary>
    public static string? Classify(string path)
    {
        if (path.StartsWith("/hf/", StringComparison.Ordinal) || path == "/hf") return "hf";
        if (path.StartsWith("/hf-stats", StringComparison.Ordinal)) return "hf-stats";
        if (path.StartsWith("/model/", StringComparison.Ordinal)) return "model";
        if (path.StartsWith("/torrent/", StringComparison.Ordinal)) return "torrent";
        if (path.StartsWith("/magnet/", StringComparison.Ordinal)) return "magnet";
        if (path.StartsWith("/ollama/", StringComparison.Ordinal)) return "ollama";
        if (path.StartsWith("/ollama-", StringComparison.Ordinal)) return "ollama-other";
        if (path == "/src") return "src";
        if (path.StartsWith("/src/", StringComparison.Ordinal)) return "src-other";
        return null;
    }

    /// <summary>
    /// The object key under the models host for a redirectable request, or false. Only <c>hf</c>, <c>ollama</c> and
    /// <c>src</c> redirect; the torrent, magnet and stats endpoints have no static equivalent.
    /// </summary>
    public static bool TryMap(string path, string? srcUrl, string? srcMember, out string key)
    {
        key = "";
        var endpoint = Classify(path);
        string? mapped = endpoint switch
        {
            "hf" => MapHf(path),
            "ollama" => MapOllama(path),
            "src" => MapSrc(srcUrl, srcMember),
            _ => null,
        };
        if (mapped == null || !SafeKey(mapped)) return false;
        key = mapped;
        return true;
    }

    /// <summary>The public URL for an object key, each segment percent-encoded (real names hold spaces and '!').</summary>
    public static string ToUrl(string baseUrl, string key)
        => baseUrl.TrimEnd('/') + "/" + string.Join("/", key.Split('/').Select(Uri.EscapeDataString));

    private static string? MapHf(string path)
    {
        var seg = path.Split('/', StringSplitOptions.None);   // "", "hf", org, repo, file...
        if (seg.Length < 5 || seg[1] != "hf") return null;
        return $"{seg[2]}/{seg[3]}/resolve/main/{string.Join("/", seg.Skip(4))}";
    }

    private static string? MapOllama(string path)
    {
        var seg = path.Split('/', StringSplitOptions.None);   // "", "ollama", model, tag, layer
        if (seg.Length != 5 || seg[1] != "ollama") return null;
        return $"ollama/{seg[2]}/{seg[3]}/{seg[2]}-{seg[3]}-{seg[4]}.gguf";
    }

    private static readonly string[] ArchiveExtensions = { ".tar.bz2", ".tar.gz", ".tgz", ".zip" };

    private static string? MapSrc(string? url, string? member)
    {
        if (string.IsNullOrEmpty(url) || !Uri.TryCreate(url, UriKind.Absolute, out var u) || u.Scheme != Uri.UriSchemeHttps)
            return null;
        var path = Uri.UnescapeDataString(u.AbsolutePath);                 // "/org/repo/resolve/main/file"
        if (u.Host.Equals("huggingface.co", StringComparison.OrdinalIgnoreCase))
        {
            var seg = path.Split('/', StringSplitOptions.None);            // "", [datasets,] org, repo, resolve, rev, file...
            var d = seg.Length > 1 && seg[1] == "datasets" ? 1 : 0;
            if (seg.Length < 6 + d || seg[3 + d] != "resolve" || seg[4 + d] != "main") return null;   // only `main` is mirrored
            return $"{(d == 1 ? "datasets/" : "")}{seg[1 + d]}/{seg[2 + d]}/resolve/main/{string.Join("/", seg.Skip(5 + d))}";
        }
        if (!string.IsNullOrEmpty(member))
        {
            var ext = ArchiveExtensions.FirstOrDefault(e => path.EndsWith(e, StringComparison.OrdinalIgnoreCase));
            if (ext == null) return null;
            var slash = member.IndexOf('/');
            if (slash < 0 || slash == member.Length - 1) return null;       // member must sit under the archive's top folder
            return $"{u.Host}{path[..^ext.Length]}/{member[(slash + 1)..]}";
        }
        return $"{u.Host}{path}";
    }

    /// <summary>No empty, dot or dot-dot segments, no backslashes or control characters, no leading slash.</summary>
    private static bool SafeKey(string key)
    {
        if (key.Length == 0 || key[0] == '/') return false;
        foreach (var c in key) if (c == '\\' || c < ' ') return false;
        foreach (var s in key.Split('/'))
            if (s.Length == 0 || s == "." || s == "..") return false;
        return true;
    }
}

/// <summary>
/// Middleware for the hub's DEPRECATED model endpoints (<c>/hf</c>, <c>/ollama</c>, <c>/src</c>, <c>/model</c>,
/// <c>/torrent</c>, <c>/magnet</c>): every hit is counted and logged so we can see who still uses them, and a request
/// whose file really exists on the models host is answered with a 307 to it. Anything else falls through to the old
/// proxy untouched, so no existing client breaks.
/// </summary>
/// <remarks>
/// Register AFTER <c>UseCors()</c> so the 307 carries <c>Access-Control-Allow-Origin</c>; a browser rejects a
/// cross-origin redirect whose own response lacks it. The redirect target already allows <c>Range</c>.
/// </remarks>
public sealed class ModelsRedirect
{
    private readonly ModelsRedirectOptions _options;
    private readonly HttpClient _http;
    private readonly Func<DateTime> _now;
    private readonly ConcurrentDictionary<string, (bool? Exists, DateTime Expires)> _exists = new();
    private readonly ConcurrentDictionary<string, long> _counts = new();
    private readonly ConcurrentDictionary<string, long> _paths = new();
    private readonly ConcurrentDictionary<string, long> _clients = new();
    private readonly ConcurrentDictionary<string, DateTime> _logged = new();
    private readonly Action<string> _log;
    private readonly DateTime _started;
    private const int MaxTracked = 500;

    public ModelsRedirect(ModelsRedirectOptions options, HttpClient http, Action<string>? log = null, Func<DateTime>? now = null)
    {
        _options = options;
        _http = http;
        _log = log ?? Console.WriteLine;
        _now = now ?? (() => DateTime.UtcNow);
        _started = _now();
    }

    public async Task InvokeAsync(HttpContext ctx, Func<Task> next)
    {
        var req = ctx.Request;
        var path = req.Path.Value ?? "";
        var endpoint = ModelsRedirectMap.Classify(path);
        if (endpoint == null || HttpMethods.IsOptions(req.Method)) { await next(); return; }

        string outcome;
        string? target = null;
        var canRedirect = _options.Enabled && (HttpMethods.IsGet(req.Method) || HttpMethods.IsHead(req.Method));
        if (canRedirect && ModelsRedirectMap.TryMap(path, req.Query["url"], req.Query["member"], out var key))
        {
            target = ModelsRedirectMap.ToUrl(_options.BaseUrl, key);
            var exists = await ExistsAsync(target, ctx.RequestAborted);
            outcome = exists switch { true => "redirected", false => "legacy-not-mirrored", _ => "legacy-check-failed" };
        }
        else if (!_options.Enabled)
        {
            outcome = "legacy-disabled";
        }
        else
        {
            outcome = endpoint is "hf" or "ollama" or "src" ? "legacy-no-mapping" : "legacy-no-equivalent";
        }

        Record(ctx, endpoint, outcome, path, target);
        ctx.Response.Headers["Deprecation"] = "true";
        if (outcome == "redirected")
        {
            ctx.Response.Headers["Link"] = $"<{target}>; rel=\"successor-version\"";
            ctx.Response.Headers.CacheControl = $"public, max-age={_options.RedirectMaxAgeSeconds}";
            ctx.Response.Redirect(target!, permanent: false, preserveMethod: true);   // 307
            return;
        }
        await next();
    }

    /// <summary>Does the object exist on the models host? Null when it could not be determined (network error).</summary>
    private async Task<bool?> ExistsAsync(string url, CancellationToken ct)
    {
        var now = _now();
        if (_exists.TryGetValue(url, out var hit) && hit.Expires > now) return hit.Exists;
        bool? exists;
        TimeSpan ttl;
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(_options.ProbeTimeout);
            using var res = await _http.SendAsync(new HttpRequestMessage(HttpMethod.Head, url),
                HttpCompletionOption.ResponseHeadersRead, cts.Token);
            exists = res.IsSuccessStatusCode ? true : res.StatusCode == System.Net.HttpStatusCode.NotFound ? false : null;
            ttl = exists == true ? _options.ExistsCacheTtl : exists == false ? _options.MissingCacheTtl : TimeSpan.FromSeconds(20);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            if (ct.IsCancellationRequested) throw;
            exists = null;
            ttl = TimeSpan.FromSeconds(20);
        }
        if (_exists.Count > 5000) _exists.Clear();
        _exists[url] = (exists, now + ttl);
        return exists;
    }

    private void Record(HttpContext ctx, string endpoint, string outcome, string path, string? target)
    {
        var req = ctx.Request;
        var client = ClientKey(req);
        Bump(_counts, $"{endpoint} {outcome}", int.MaxValue);
        Bump(_paths, path, MaxTracked);
        Bump(_clients, ClientLabel(req), MaxTracked);

        var now = _now();
        var dedupe = $"{ClientIp(ctx)}|{path}";
        if (_logged.TryGetValue(dedupe, out var last) && now - last < _options.LogDedupeWindow) return;
        if (_logged.Count > 10000) foreach (var kv in _logged.Where(kv => now - kv.Value >= _options.LogDedupeWindow)) _logged.TryRemove(kv.Key, out _);
        _logged[dedupe] = now;
        var ua = req.Headers.UserAgent.ToString();
        _log($"[Deprecated] {outcome} {req.Method} {path}{(path == "/src" ? req.QueryString.Value : "")}"
           + (target != null ? $" -> {target}" : "")
           + $" client={client} ua={(ua.Length > 60 ? ua[..60] : ua)}");
    }

    private static void Bump(ConcurrentDictionary<string, long> d, string key, int maxKeys)
    {
        if (!d.ContainsKey(key) && d.Count >= maxKeys) key = "(other)";
        d.AddOrUpdate(key, 1, (_, v) => v + 1);
    }

    private static string ClientIp(HttpContext ctx) => ctx.Connection.RemoteIpAddress?.ToString() ?? "?";

    private static string ClientKey(HttpRequest req)
    {
        var origin = req.Headers.Origin.ToString();
        if (origin.Length == 0) origin = req.Headers.Referer.ToString();
        return origin.Length == 0 ? $"no-origin {ClientIp(req.HttpContext)}" : origin;
    }

    private static string ClientLabel(HttpRequest req)
    {
        var o = req.Headers.Origin.ToString();
        if (o.Length == 0 && Uri.TryCreate(req.Headers.Referer.ToString(), UriKind.Absolute, out var r)) o = $"{r.Scheme}://{r.Authority}";
        return o.Length == 0 ? "(no Origin/Referer: desktop client or direct)" : o;
    }

    /// <summary>What <c>GET /deprecated-stats</c> returns: who still uses the old endpoints, and how each request ended.</summary>
    public object Snapshot() => new
    {
        sinceUtc = _started,
        redirectEnabled = _options.Enabled,
        modelsBaseUrl = _options.BaseUrl,
        byEndpointAndOutcome = _counts.OrderByDescending(kv => kv.Value).ToDictionary(kv => kv.Key, kv => kv.Value),
        topPaths = _paths.OrderByDescending(kv => kv.Value).Take(25).ToDictionary(kv => kv.Key, kv => kv.Value),
        topClients = _clients.OrderByDescending(kv => kv.Value).Take(25).ToDictionary(kv => kv.Key, kv => kv.Value),
    };
}
