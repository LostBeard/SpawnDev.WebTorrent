using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
namespace SpawnDev.WebTorrent.Server.HuggingFace;

/// <summary>
/// Ollama model-registry proxy with local caching and torrent creation — the ollama twin of
/// <see cref="HuggingFaceProxy"/>. Resolves an ollama model reference (<c>model:tag</c> + a layer kind) to a
/// content-addressed blob in the ollama OCI registry, fetches it on first request, caches it locally, and
/// serves it as a BitTorrent web seed with an auto-generated .torrent — so the SAME WebTorrent client flow
/// that delivers HuggingFace ONNX/GGUF files also delivers ollama GGUF + mmproj weights.
///
/// Ollama registry protocol (verified 2026-06-15 against registry.ollama.ai):
///   • Manifest: GET https://registry.ollama.ai/v2/library/{model}/manifests/{tag}  (OCI/Docker v2 JSON).
///   • Layers carry mediaTypes; the two we serve:
///       - <c>application/vnd.ollama.image.model</c>     → the GGUF weights   (layer kind "model")
///       - <c>application/vnd.ollama.image.projector</c> → the mmproj/CLIP    (layer kind "projector")
///   • Blob: GET https://registry.ollama.ai/v2/library/{model}/blobs/{digest}  (307 → CDN, range-capable).
///
/// Flow mirrors the HF proxy: client asks for a magnet → if cached serve the torrent, else background-prepare
/// (resolve manifest → download blob → cache → generate .torrent). The blob is content-addressed by its
/// sha256 digest, so the registry blob URL doubles as a second web seed.
/// </summary>
public class OllamaProxy
{
    private readonly OllamaProxyOptions _options;
    private readonly HttpClient _httpClient;
    private readonly ConcurrentDictionary<string, byte[]> _torrentCache = new();
    // One range-cache per layer blob: sparse single data file + .ranges manifest (on-demand chunk fetch + cache).
    private readonly ConcurrentDictionary<string, PartialFileCache> _fileCaches = new();
    private readonly ConcurrentDictionary<string, Task> _preparingTasks = new();
    private readonly ConcurrentDictionary<string, ModelCacheStats> _modelStats = new();
    // Resolved manifests: "{model}:{tag}" → (layerKind → (digest, size)). Avoids re-fetching the manifest.
    private readonly ConcurrentDictionary<string, IReadOnlyDictionary<string, (string Digest, long Size)>> _manifestCache = new();

    /// <summary>Friendly layer kind → ollama mediaType.</summary>
    private static readonly Dictionary<string, string> LayerMediaTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["model"] = "application/vnd.ollama.image.model",
        ["projector"] = "application/vnd.ollama.image.projector",
        ["params"] = "application/vnd.ollama.image.params",
        ["template"] = "application/vnd.ollama.image.template",
        ["license"] = "application/vnd.ollama.image.license",
    };

    public OllamaProxy(OllamaProxyOptions? options = null)
    {
        _options = options ?? new OllamaProxyOptions();
        _httpClient = new HttpClient();
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("SpawnDev.WebTorrent.Server/1.0");
        // The registry serves the manifest as docker/oci v2 JSON; advertise both so we always get JSON.
        _httpClient.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.docker.distribution.manifest.v2+json");
        _httpClient.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.oci.image.manifest.v1+json");
        _httpClient.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        foreach (var dir in _options.CacheDirectories)
            Directory.CreateDirectory(dir);
    }

    public int CachedFileCount => _options.CacheDirectories.Where(Directory.Exists)
        .Sum(d => Directory.GetFiles(d, "*", SearchOption.AllDirectories).Length);
    public int CachedTorrentCount => _torrentCache.Count;
    public long CacheSizeBytes => _options.CacheDirectories.Where(Directory.Exists)
        .Sum(d => new DirectoryInfo(d).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length));
    public IReadOnlyDictionary<string, ModelCacheStats> ModelStats => _modelStats;

    private void RecordRequest(string cacheKey) => _modelStats.AddOrUpdate(cacheKey,
        _ => new ModelCacheStats { RequestCount = 1, LastRequestUtc = DateTime.UtcNow },
        (_, e) => { e.RequestCount++; e.LastRequestUtc = DateTime.UtcNow; return e; });

    private string RegistryBase(string model) =>
        $"{_options.RegistryBaseUrl.TrimEnd('/')}/v2/{Namespaced(model)}";

    // Bare names are in the "library" namespace; "user/model" names are used verbatim (e.g. "hf.co/...").
    private static string Namespaced(string model) => model.Contains('/') ? model : $"library/{model}";

    private static string CacheKey(string model, string tag, string layer) => $"{model}/{tag}/{layer}";

    /// <summary>Filename used as the torrent name (and what a consumer sees). e.g. "gemma4-12b-model.gguf".</summary>
    private static string LayerFileName(string model, string tag, string layer)
    {
        var safe = $"{model}-{tag}-{layer}".Replace('/', '_').Replace(':', '_');
        return safe + ".gguf";
    }

    /// <summary>Resolve a model:tag manifest to its layer digests (cached). Throws if the registry 404s.</summary>
    private async Task<IReadOnlyDictionary<string, (string Digest, long Size)>> ResolveManifestAsync(
        string model, string tag, CancellationToken ct)
    {
        var key = $"{model}:{tag}";
        if (_manifestCache.TryGetValue(key, out var cached)) return cached;

        var url = $"{RegistryBase(model)}/manifests/{tag}";
        using var resp = await _httpClient.GetAsync(url, ct);
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"Ollama manifest {model}:{tag} → HTTP {(int)resp.StatusCode} ({url}).");
        var json = await resp.Content.ReadAsStringAsync(ct);

        var map = new Dictionary<string, (string, long)>(StringComparer.OrdinalIgnoreCase);
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.TryGetProperty("layers", out var layers) && layers.ValueKind == JsonValueKind.Array)
        {
            foreach (var layer in layers.EnumerateArray())
            {
                var mt = layer.GetProperty("mediaType").GetString() ?? "";
                var kind = LayerMediaTypes.FirstOrDefault(kv => kv.Value == mt).Key;
                if (kind == null) continue;
                var digest = layer.GetProperty("digest").GetString() ?? "";
                long size = layer.TryGetProperty("size", out var sz) ? sz.GetInt64() : 0;
                map[kind] = (digest, size);
            }
        }
        if (map.Count == 0)
            throw new InvalidOperationException($"Ollama manifest {model}:{tag} exposed no recognized layers ({url}).");

        _manifestCache[key] = map;
        return map;
    }

    /// <summary>Get or fetch a single layer blob. Returns the local file path (or null if the layer is absent).</summary>
    public async Task<string?> GetOrFetchAsync(string model, string tag, string layer, CancellationToken ct = default)
    {
        if (!LayerMediaTypes.ContainsKey(layer))
            throw new ArgumentException($"Unknown ollama layer kind '{layer}'. Known: {string.Join(", ", LayerMediaTypes.Keys)}.");

        var cacheKey = CacheKey(model, tag, layer);
        RecordRequest(cacheKey);

        var localPath = GetCachePath(model, tag, layer);
        if (PartialFileCache.IsComplete(localPath))   // fully cached (manifest gone) — never a partial
        {
            _modelStats.AddOrUpdate(cacheKey, _ => new ModelCacheStats { FileSizeBytes = new FileInfo(localPath).Length },
                (_, s) => { s.FileSizeBytes = new FileInfo(localPath).Length; return s; });
            return localPath;
        }

        // A layer the model doesn't expose (e.g. a text-only model asked for "projector") has no blob to cache.
        var manifest = await ResolveManifestAsync(model, tag, ct);
        if (!manifest.TryGetValue(layer, out _))
        {
            Console.WriteLine($"[Ollama Proxy] {model}:{tag} has no '{layer}' layer.");
            return null;
        }

        // Drive the range cache to completion (fetch every missing chunk into the single sparse data file). The
        // manifest gates completeness, so a partial is never returned as complete (the old .part discipline,
        // generalised to random access with no rename).
        var result = await GetFileCache(model, tag, layer).EnsureCompleteAsync(ct);
        if (result != null)
        {
            long fileSize = new FileInfo(result).Length;
            Console.WriteLine($"[Ollama Proxy] Cached (complete): {result} ({fileSize:N0} bytes)");
            _modelStats.AddOrUpdate(cacheKey, _ => new ModelCacheStats { FileSizeBytes = fileSize },
                (_, s) => { s.FileSizeBytes = fileSize; return s; });
        }
        return result;
    }

    /// <summary>Generate a .torrent for a layer blob (web seeds: this server + the registry blob CDN).</summary>
    public async Task<byte[]?> CreateTorrentAsync(string model, string tag, string layer,
        string serverBaseUrl, CancellationToken ct = default)
    {
        var cacheKey = CacheKey(model, tag, layer);
        if (_torrentCache.TryGetValue(cacheKey, out var cached)) return cached;

        // Load a previously-generated .torrent from DISK (survives a hub restart — no multi-GB re-hash). Only
        // trust it when the layer blob itself is fully cached (manifest convention), never for a partial.
        var dataPath = GetCachePath(model, tag, layer);
        var torrentPath = dataPath + ".torrent";
        if (File.Exists(torrentPath) && PartialFileCache.IsComplete(dataPath))
        {
            try { var fromDisk = await File.ReadAllBytesAsync(torrentPath, ct); if (fromDisk.Length > 0) { _torrentCache[cacheKey] = fromDisk; return fromDisk; } } catch { }
        }

        var localPath = await GetOrFetchAsync(model, tag, layer, ct);
        if (localPath == null) return null;

        // The registry blob URL is content-addressed (sha256 digest) → a stable, range-capable second web seed.
        var manifest = await ResolveManifestAsync(model, tag, ct);
        var registryBlobUrl = manifest.TryGetValue(layer, out var e) ? $"{RegistryBase(model)}/blobs/{e.Digest}" : null;

        long fileSize = new FileInfo(localPath).Length;
        var name = LayerFileName(model, tag, layer);
        var webSeeds = new List<string> { $"{serverBaseUrl.TrimEnd('/')}/ollama/{model}/{tag}/{layer}" };
        if (registryBlobUrl != null) webSeeds.Add(registryBlobUrl);

        var (torrentBytes, metadata) = await TorrentCreator.CreateFromFileAsync(localPath,
            new TorrentCreatorOptions
            {
                Name = name,
                Trackers = _options.TrackerUrls,
                PieceLength = HuggingFaceProxy.ModelPieceLength(fileSize),
                WebSeeds = webSeeds.ToArray(),
                Comment = $"Ollama model: {model}:{tag}/{layer}",
                CreatedBy = "SpawnDev.WebTorrent.Server.Ollama",
                MetaVersion = 2,
                Hybrid = true,
            }, ct);

        _torrentCache[cacheKey] = torrentBytes;
        try { await File.WriteAllBytesAsync(torrentPath, torrentBytes, ct); } catch { } // persist so it survives a hub restart
        Console.WriteLine($"[Ollama Proxy] .torrent ready: {cacheKey} ({metadata.PieceHashes.Length} pieces, v1={metadata.InfoHash})");
        return torrentBytes;
    }

    /// <summary>Magnet URI for an ollama layer (xs= exact source + ws= web seed), creating the torrent if needed.</summary>
    public async Task<string?> GetMagnetUriAsync(string model, string tag, string layer,
        string serverBaseUrl, CancellationToken ct = default)
    {
        var torrentBytes = await CreateTorrentAsync(model, tag, layer, serverBaseUrl, ct);
        if (torrentBytes == null) return null;

        var metadata = TorrentParser.Parse(torrentBytes);
        var trackers = string.Join("", _options.TrackerUrls.Select(t => $"&tr={Uri.EscapeDataString(t)}"));
        var ws = $"&ws={Uri.EscapeDataString($"{serverBaseUrl.TrimEnd('/')}/ollama/{model}/{tag}/{layer}")}";
        var xs = $"&xs={Uri.EscapeDataString($"{serverBaseUrl.TrimEnd('/')}/ollama-torrent/{model}/{tag}/{layer}")}";
        return $"magnet:?xt=urn:btih:{metadata.InfoHash}&dn={Uri.EscapeDataString(metadata.Name)}{trackers}{ws}{xs}";
    }

    /// <summary>Non-blocking request: "ready" with the magnet if cached, else "preparing" (background prepare started).</summary>
    public OllamaRequestResult RequestModel(string model, string tag, string layer, string serverBaseUrl)
    {
        var cacheKey = CacheKey(model, tag, layer);
        if (_torrentCache.TryGetValue(cacheKey, out var torrentBytes))
        {
            var metadata = TorrentParser.Parse(torrentBytes);
            var trackers = string.Join("", _options.TrackerUrls.Select(t => $"&tr={Uri.EscapeDataString(t)}"));
            var ws = $"&ws={Uri.EscapeDataString($"{serverBaseUrl.TrimEnd('/')}/ollama/{model}/{tag}/{layer}")}";
            var xs = $"&xs={Uri.EscapeDataString($"{serverBaseUrl.TrimEnd('/')}/ollama-torrent/{model}/{tag}/{layer}")}";
            var magnet = $"magnet:?xt=urn:btih:{metadata.InfoHash}&dn={Uri.EscapeDataString(metadata.Name)}{trackers}{ws}{xs}";
            return new OllamaRequestResult { Status = "ready", Model = model, Tag = tag, Layer = layer, MagnetUri = magnet,
                WebSeed = $"{serverBaseUrl.TrimEnd('/')}/ollama/{model}/{tag}/{layer}" };
        }

        _preparingTasks.GetOrAdd(cacheKey, _ => Task.Run(async () =>
        {
            try { await CreateTorrentAsync(model, tag, layer, serverBaseUrl); Console.WriteLine($"[Ollama Proxy] Background prepare complete: {cacheKey}"); }
            catch (Exception ex) { Console.WriteLine($"[Ollama Proxy] Background prepare failed: {cacheKey} — {ex.Message}"); }
            finally { _preparingTasks.TryRemove(cacheKey, out Task? _); }
        }));
        return new OllamaRequestResult { Status = "preparing", Model = model, Tag = tag, Layer = layer };
    }

    /// <summary>Web-seed serve a layer blob. Backed by <see cref="PartialFileCache"/>: a fully-cached blob
    /// serves from disk; an uncached/partial blob fetches only the MISSING covering chunks from the ollama
    /// registry CDN, caches them at their byte offset (manifest-tracked, crash-safe), then serves — so the hub
    /// is always an available model source and a re-request for a range it already has hits no origin.</summary>
    public async Task HandleRequest(HttpContext context, string model, string tag, string layer)
    {
        RecordRequest(CacheKey(model, tag, layer));
        await GetFileCache(model, tag, layer).ServeRangeAsync(context);
    }

    /// <summary>One <see cref="PartialFileCache"/> per layer blob (created on first touch). Origin = the
    /// content-addressed registry blob (resolve the manifest digest → {registry}/blobs/{digest}; 307-redirects
    /// to the range-capable CDN, HttpClient follows). Server-side fetch, so no registry CORS is needed — the
    /// browser only ever talks to the hub's /ollama route. Completion drops the manifest; the .torrent is built
    /// lazily on the next /ollama-torrent / /ollama-magnet request via <see cref="PartialFileCache.IsComplete"/>.</summary>
    private PartialFileCache GetFileCache(string model, string tag, string layer)
        => _fileCaches.GetOrAdd(CacheKey(model, tag, layer), _ => new PartialFileCache(
            GetCachePath(model, tag, layer), _httpClient,
            async (start, end, ct) =>
            {
                var manifest = await ResolveManifestAsync(model, tag, ct);
                if (!manifest.TryGetValue(layer, out var entry))
                    throw new InvalidOperationException($"Ollama {model}:{tag} has no '{layer}' layer.");
                var req = new HttpRequestMessage(HttpMethod.Get, $"{RegistryBase(model)}/blobs/{entry.Digest}");
                req.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(start, end);
                return req;
            }));

    private string GetCachePath(string model, string tag, string layer)
    {
        var relativePath = Path.Combine("ollama", model.Replace('/', '_').Replace(':', '_'),
            tag.Replace('/', '_'), LayerFileName(model, tag, layer));
        foreach (var dir in _options.CacheDirectories)
        {
            var path = Path.Combine(dir, relativePath);
            if (File.Exists(path)) return path;
        }
        string bestDir = _options.CacheDirectories[0];
        long bestFree = 0;
        foreach (var dir in _options.CacheDirectories)
        {
            try
            {
                var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(dir)) ?? dir);
                if (drive.AvailableFreeSpace > bestFree) { bestFree = drive.AvailableFreeSpace; bestDir = dir; }
            }
            catch { }
        }
        return Path.Combine(bestDir, relativePath);
    }
}

/// <summary>Result from a non-blocking ollama model request.</summary>
public class OllamaRequestResult
{
    public string Status { get; set; } = "";
    public string Model { get; set; } = "";
    public string Tag { get; set; } = "";
    public string Layer { get; set; } = "";
    public string? MagnetUri { get; set; }
    public string? WebSeed { get; set; }
}

/// <summary>Ollama proxy configuration (parallel to <see cref="HuggingFaceProxyOptions"/>).</summary>
public class OllamaProxyOptions
{
    /// <summary>Ollama OCI registry base. Default: the public registry.</summary>
    public string RegistryBaseUrl { get; set; } = "https://registry.ollama.ai";
    public string[] CacheDirectories { get; set; } = new[] { "ollama-cache" };
    public string CacheDirectory { get => CacheDirectories.Length > 0 ? CacheDirectories[0] : "ollama-cache"; set => CacheDirectories = new[] { value }; }
    public string[] TrackerUrls { get; set; } = new[] { "wss://hub.spawndev.com:44365/announce" };
}

/// <summary>Endpoint registration for the ollama proxy (parallel to <see cref="HuggingFaceProxyExtensions"/>).</summary>
public static class OllamaProxyExtensions
{
    /// <summary>Map ollama proxy endpoints. Routes use {model}/{tag}/{layer}; layer ∈ model|projector|params|template|license.</summary>
    public static void MapOllamaProxy(this IEndpointRouteBuilder app, OllamaProxy proxy)
    {
        // Web seed: serve a cached layer blob.
        app.MapGet("/ollama/{model}/{tag}/{layer}", async (HttpContext ctx, string model, string tag, string layer) =>
            await proxy.HandleRequest(ctx, model, tag, layer));

        // .torrent for an ollama layer.
        app.MapGet("/ollama-torrent/{model}/{tag}/{layer}", async (HttpContext ctx, string model, string tag, string layer) =>
        {
            var serverUrl = $"{ctx.Request.Scheme}://{ctx.Request.Host}";
            var torrentBytes = await proxy.CreateTorrentAsync(model, tag, layer, serverUrl, ctx.RequestAborted);
            if (torrentBytes == null) { ctx.Response.StatusCode = 404; return; }
            ctx.Response.ContentType = "application/x-bittorrent";
            ctx.Response.Headers["Content-Disposition"] = $"attachment; filename=\"{model}-{tag}-{layer}.torrent\"";
            await ctx.Response.Body.WriteAsync(torrentBytes);
        });

        // Magnet URI (JSON) for an ollama layer.
        app.MapGet("/ollama-magnet/{model}/{tag}/{layer}", async (HttpContext ctx, string model, string tag, string layer) =>
        {
            var serverUrl = $"{ctx.Request.Scheme}://{ctx.Request.Host}";
            var magnetUri = await proxy.GetMagnetUriAsync(model, tag, layer, serverUrl, ctx.RequestAborted);
            if (magnetUri == null) { ctx.Response.StatusCode = 404; return; }
            ctx.Response.ContentType = "application/json";
            await ctx.Response.WriteAsJsonAsync(new { magnetUri, model, tag, layer,
                webSeed = $"{serverUrl}/ollama/{model}/{tag}/{layer}" });
        });

        // Non-blocking request.
        app.MapGet("/ollama-model/{model}/{tag}/{layer}", (HttpContext ctx, string model, string tag, string layer) =>
        {
            var serverUrl = $"{ctx.Request.Scheme}://{ctx.Request.Host}";
            return Results.Json(proxy.RequestModel(model, tag, layer, serverUrl));
        });

        // Stats.
        app.MapGet("/ollama-stats", () => new
        {
            cachedFiles = proxy.CachedFileCount,
            cachedTorrents = proxy.CachedTorrentCount,
            cacheSizeMB = Math.Round(proxy.CacheSizeBytes / (1024.0 * 1024.0), 1),
            models = proxy.ModelStats.OrderByDescending(kv => kv.Value.RequestCount).Select(kv => new
            {
                model = kv.Key, requests = kv.Value.RequestCount,
                lastRequest = kv.Value.LastRequestUtc.ToString("u"),
                sizeMB = Math.Round(kv.Value.FileSizeBytes / (1024.0 * 1024.0), 1),
                cached = kv.Value.FileSizeBytes > 0,
            }),
        });
    }
}
