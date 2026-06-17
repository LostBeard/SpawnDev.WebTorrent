using System.Collections.Concurrent;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
namespace SpawnDev.WebTorrent.Server.HuggingFace;

/// <summary>
/// HuggingFace CDN proxy with local caching and torrent creation.
/// Fetches model files from HuggingFace on first request, caches locally,
/// generates .torrent files for P2P distribution, and serves as web seed.
///
/// Flow:
///   1. Client requests model file via torrent
///   2. If cached: serve from local storage (web seed)
///   3. If not cached: fetch from HuggingFace, cache, then serve
///   4. .torrent files auto-generated for each model file
///
/// Usage:
///   var proxy = new HuggingFaceProxy(options);
///   app.MapHuggingFaceProxy(proxy);
/// </summary>
public class HuggingFaceProxy
{
    private readonly HuggingFaceProxyOptions _options;
    private readonly HttpClient _httpClient;

    // Cache generated .torrent files to avoid regenerating on every request
    private readonly ConcurrentDictionary<string, byte[]> _torrentCache = new();

    // One range-cache per model file: sparse single data file + .ranges manifest. Serves cached ranges from
    // disk and fetches only the missing chunks from HF on demand (the hub is always an available model source).
    private readonly ConcurrentDictionary<string, PartialFileCache> _fileCaches = new();
    // Track in-progress background preparations to avoid duplicate work
    private readonly ConcurrentDictionary<string, Task> _preparingTasks = new();
    // Per-model stats: request count, last access, file size
    private readonly ConcurrentDictionary<string, ModelCacheStats> _modelStats = new();

    public HuggingFaceProxy(HuggingFaceProxyOptions? options = null)
    {
        _options = options ?? new HuggingFaceProxyOptions();
        _httpClient = new HttpClient();
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("SpawnDev.WebTorrent.Server/1.0");
        foreach (var dir in _options.CacheDirectories)
            Directory.CreateDirectory(dir);
    }

    /// <summary>Number of cached model files across all cache directories.</summary>
    public int CachedFileCount => _options.CacheDirectories
        .Where(Directory.Exists)
        .Sum(d => Directory.GetFiles(d, "*", SearchOption.AllDirectories).Length);

    /// <summary>Number of cached .torrent files.</summary>
    public int CachedTorrentCount => _torrentCache.Count;

    /// <summary>Total cache size in bytes across all cache directories.</summary>
    public long CacheSizeBytes => _options.CacheDirectories
        .Where(Directory.Exists)
        .Sum(d => new DirectoryInfo(d).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length));

    /// <summary>Per-model request stats. Key = "repoId/filePath".</summary>
    public IReadOnlyDictionary<string, ModelCacheStats> ModelStats => _modelStats;

    /// <summary>Record a request for stats tracking.</summary>
    private void RecordRequest(string cacheKey)
    {
        _modelStats.AddOrUpdate(cacheKey,
            _ => new ModelCacheStats { RequestCount = 1, LastRequestUtc = DateTime.UtcNow },
            (_, existing) => { existing.RequestCount++; existing.LastRequestUtc = DateTime.UtcNow; return existing; });
    }

    /// <summary>
    /// Get or fetch a model file. Returns the local file path.
    /// If not cached, downloads from HuggingFace and caches.
    /// </summary>
    public async Task<string?> GetOrFetchAsync(string repoId, string filePath, CancellationToken ct = default)
    {
        var cacheKey = $"{repoId}/{filePath}";
        RecordRequest(cacheKey);

        var localPath = GetCachePath(repoId, filePath);
        if (PartialFileCache.IsComplete(localPath))   // fully cached (manifest gone) — never a partial
        {
            _modelStats.AddOrUpdate(cacheKey,
                _ => new ModelCacheStats { FileSizeBytes = new FileInfo(localPath).Length },
                (_, s) => { s.FileSizeBytes = new FileInfo(localPath).Length; return s; });
            return localPath;
        }

        // Drive the range cache to completion — fetch every missing chunk into the single sparse data file.
        // The full-file consumers (.torrent / magnet) need the WHOLE file; the manifest gates completeness, so
        // a partial is never returned as complete (the bug the old .part+atomic-rename guarded against). The
        // cache also serves partial ranges to /hf clients concurrently, and de-dupes chunk fetches, so this is
        // safe to call from several requests at once.
        var result = await GetFileCache(repoId, filePath).EnsureCompleteAsync(ct);
        if (result != null)
        {
            long fileSize = new FileInfo(result).Length;
            Console.WriteLine($"[HF Proxy] Cached (complete): {result} ({fileSize:N0} bytes)");
            _modelStats.AddOrUpdate(cacheKey,
                _ => new ModelCacheStats { FileSizeBytes = fileSize },
                (_, s) => { s.FileSizeBytes = fileSize; return s; });
            await EvictIfNeededAsync();
        }
        return result;
    }

    /// <summary>
    /// Evict least-recently-requested cached models when:
    /// 1. Total cache exceeds MaxCacheSizeBytes (if configured), OR
    /// 2. Any cache drive has less than MinFreeDiskSpaceBytes free (default 2GB)
    /// </summary>
    private async Task EvictIfNeededAsync()
    {
        bool needsEviction = false;
        string reason = "";

        // Check explicit cache size limit
        var currentSize = CacheSizeBytes;
        if (_options.MaxCacheSizeBytes > 0 && currentSize > _options.MaxCacheSizeBytes)
        {
            needsEviction = true;
            reason = $"cache {currentSize / (1024 * 1024)}MB > limit {_options.MaxCacheSizeBytes / (1024 * 1024)}MB";
        }

        // Check drive free space on all cache drives
        if (!needsEviction)
        {
            foreach (var dir in _options.CacheDirectories)
            {
                try
                {
                    var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(dir)) ?? dir);
                    if (drive.AvailableFreeSpace < _options.MinFreeDiskSpaceBytes)
                    {
                        needsEviction = true;
                        reason = $"drive {drive.Name} has {drive.AvailableFreeSpace / (1024 * 1024)}MB free < {_options.MinFreeDiskSpaceBytes / (1024 * 1024)}MB minimum";
                        break;
                    }
                }
                catch { } // DriveInfo may fail for network paths
            }
        }

        if (!needsEviction) return;

        Console.WriteLine($"[HF Proxy] Cache eviction needed: {reason}");

        // Sort by last request time — evict least recently requested first
        var candidates = _modelStats
            .Where(kv => kv.Value.FileSizeBytes > 0)
            .OrderBy(kv => kv.Value.LastRequestUtc)
            .ToList();

        int evictedCount = 0;
        long evictedBytes = 0;

        foreach (var candidate in candidates)
        {
            // Re-check conditions after each eviction
            bool stillNeeded = false;
            if (_options.MaxCacheSizeBytes > 0 && currentSize > _options.MaxCacheSizeBytes * 0.8)
                stillNeeded = true;
            if (!stillNeeded)
            {
                foreach (var dir in _options.CacheDirectories)
                {
                    try
                    {
                        var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(dir)) ?? dir);
                        if (drive.AvailableFreeSpace < _options.MinFreeDiskSpaceBytes)
                        { stillNeeded = true; break; }
                    }
                    catch { }
                }
            }
            if (!stillNeeded) break;

            // Parse cache key back to repo/file path
            var idx = candidate.Key.IndexOf('/');
            if (idx < 0) continue;
            var secondSlash = candidate.Key.IndexOf('/', idx + 1);
            if (secondSlash < 0) continue;
            var repo = candidate.Key[..secondSlash];
            var file = candidate.Key[(secondSlash + 1)..];
            var cachedPath = GetCachePath(repo, file);

            if (File.Exists(cachedPath))
            {
                var size = new FileInfo(cachedPath).Length;
                File.Delete(cachedPath);
                try { if (File.Exists(cachedPath + PartialFileCache.ManifestSuffix)) File.Delete(cachedPath + PartialFileCache.ManifestSuffix); } catch { }
                try { if (File.Exists(cachedPath + ".torrent")) File.Delete(cachedPath + ".torrent"); } catch { }
                _fileCaches.TryRemove(candidate.Key, out _);   // drop the in-memory range-cache so it re-probes next time
                currentSize -= size;
                evictedBytes += size;
                evictedCount++;
                _torrentCache.TryRemove(candidate.Key, out _);
                candidate.Value.FileSizeBytes = 0;
                Console.WriteLine($"[HF Proxy] Evicted: {candidate.Key} ({size / (1024 * 1024)}MB, " +
                    $"requests={candidate.Value.RequestCount}, last={candidate.Value.LastRequestUtc:u})");
            }
        }

        Console.WriteLine($"[HF Proxy] Eviction complete: {evictedCount} files, {evictedBytes / (1024 * 1024)}MB freed, cache now {currentSize / (1024 * 1024)}MB");
    }

    /// <summary>
    /// Generate a .torrent file for a cached model file.
    /// Includes web seed pointing back to this server and the HuggingFace CDN.
    /// </summary>
    public async Task<byte[]?> CreateTorrentAsync(string repoId, string filePath,
        string serverBaseUrl, CancellationToken ct = default)
    {
        var cacheKey = $"{repoId}/{filePath}";

        // Return cached .torrent if available (in-memory)
        if (_torrentCache.TryGetValue(cacheKey, out var cached))
            return cached;

        // Load a previously-generated .torrent from DISK (survives a hub restart — no multi-GB re-hash). Only
        // trust it when the model file itself is fully cached (manifest convention), never for a partial.
        var dataPath = GetCachePath(repoId, filePath);
        var torrentPath = dataPath + ".torrent";
        if (File.Exists(torrentPath) && PartialFileCache.IsComplete(dataPath))
        {
            try
            {
                var fromDisk = await File.ReadAllBytesAsync(torrentPath, ct);
                if (fromDisk.Length > 0) { _torrentCache[cacheKey] = fromDisk; return fromDisk; }
            }
            catch { }
        }

        var localPath = await GetOrFetchAsync(repoId, filePath, ct);
        if (localPath == null) return null;

        Console.WriteLine($"[HF Proxy] Generating .torrent for: {cacheKey}");

        // Bias toward LARGER pieces for big model files. Models are delivered primarily over the HTTP
        // web seed, where each piece = one Range GET. The generic auto-calc picks 256KB for a 128MB-512MB
        // file, so a 330MB model = ~1300 Range GETs. At 4MB pieces that drops to ~82 — ~16x fewer requests
        // (and, with browser preflight caching now on, ~16x fewer round-trips). Small files (tokenizer/
        // config) keep the auto-calc so they stay finely pieced.
        long fileSize = new System.IO.FileInfo(localPath).Length;

        var (torrentBytes, metadata) = await TorrentCreator.CreateFromFileAsync(localPath,
            new TorrentCreatorOptions
            {
                Name = System.IO.Path.GetFileName(filePath),
                Trackers = _options.TrackerUrls,
                PieceLength = ModelPieceLength(fileSize),
                // BEP 17: web seed URL is the base directory — client appends /{torrentName}
                // e.g., base = /hf/Xenova/distilgpt2 + name = tokenizer.json → /hf/Xenova/distilgpt2/tokenizer.json
                WebSeeds = new[]
                {
                    BuildWebSeedUrl($"{serverBaseUrl}/hf/{repoId}", filePath),
                    BuildWebSeedUrl($"https://huggingface.co/{repoId}/resolve/main", filePath),
                },
                Comment = $"HuggingFace model: {repoId}/{filePath}",
                CreatedBy = "SpawnDev.WebTorrent.Server.HuggingFace",
                // BEP 52 hybrid v1+v2: model torrents carry both SHA-1 flat piece hashes
                // (for v1-only clients) and a SHA-256 Merkle tree (for v2-aware clients).
                // CreateFromFileAsync routes through the streaming hybrid path so large
                // model weights are hashed in bounded memory (~1 piece + incremental
                // Merkle state), not buffered whole.
                MetaVersion = 2,
                Hybrid = true,
            }, ct);

        _torrentCache[cacheKey] = torrentBytes;
        try { await File.WriteAllBytesAsync(torrentPath, torrentBytes, ct); } catch { } // persist so it survives a hub restart
        Console.WriteLine($"[HF Proxy] .torrent ready: {cacheKey} ({metadata.PieceHashes.Length} pieces, v1={metadata.InfoHash}, v2={metadata.V2InfoHash})");

        return torrentBytes;
    }

    /// <summary>
    /// Piece length for a hub model file, tuned for HTTP web-seed delivery (one Range GET per piece).
    /// Larger pieces for larger files = far fewer requests. All values are powers of two (BEP 52 v2
    /// requires it). Returns 0 for small files so the generic auto-calc keeps them finely pieced.
    /// </summary>
    internal static int ModelPieceLength(long fileSize)
    {
        if (fileSize >= 64L * 1024 * 1024) return 4 * 1024 * 1024; // >=64MB → 4MB
        if (fileSize >= 16L * 1024 * 1024) return 2 * 1024 * 1024; // >=16MB → 2MB
        if (fileSize >= 4L * 1024 * 1024) return 1 * 1024 * 1024;  // >=4MB  → 1MB
        return 0; // small files: let TorrentCreator auto-calc (16-256KB)
    }

    /// <summary>Get magnet URI for a HuggingFace model file. Creates .torrent if needed.</summary>
    public async Task<string?> GetMagnetUriAsync(string repoId, string filePath,
        string serverBaseUrl, CancellationToken ct = default)
    {
        var torrentBytes = await CreateTorrentAsync(repoId, filePath, serverBaseUrl, ct);
        if (torrentBytes == null) return null;

        var metadata = TorrentParser.Parse(torrentBytes);
        var trackers = string.Join("", _options.TrackerUrls.Select(t => $"&tr={Uri.EscapeDataString(t)}"));
        var webSeeds = $"&ws={Uri.EscapeDataString(BuildWebSeedUrl($"{serverBaseUrl}/hf/{repoId}", filePath))}";
        // xs= (exact source) lets the client fetch the full .torrent directly — no peers needed for metadata
        var exactSource = $"&xs={Uri.EscapeDataString($"{serverBaseUrl}/torrent/{repoId}/{filePath}")}";

        return $"magnet:?xt=urn:btih:{metadata.InfoHash}&dn={Uri.EscapeDataString(metadata.Name)}{trackers}{webSeeds}{exactSource}";
    }

    /// <summary>
    /// Non-blocking model request. Returns immediately with one of:
    /// - "ready": .torrent and magnet are available (cached)
    /// - "preparing": server is downloading/generating, client should use HuggingFace CDN directly
    /// Never blocks the client waiting for a large download.
    /// </summary>
    public ModelRequestResult RequestModel(string repoId, string filePath, string serverBaseUrl)
    {
        var cacheKey = $"{repoId}/{filePath}";
        var hfDirectUrl = $"https://huggingface.co/{repoId}/resolve/main/{filePath}";

        // Already cached — return immediately
        if (_torrentCache.TryGetValue(cacheKey, out var torrentBytes))
        {
            var metadata = TorrentParser.Parse(torrentBytes);
            var trackers = string.Join("", _options.TrackerUrls.Select(t => $"&tr={Uri.EscapeDataString(t)}"));
            var webSeeds = $"&ws={Uri.EscapeDataString(BuildWebSeedUrl($"{serverBaseUrl}/hf/{repoId}", filePath))}";
            var exactSource = $"&xs={Uri.EscapeDataString($"{serverBaseUrl}/torrent/{repoId}/{filePath}")}";
            var magnetUri = $"magnet:?xt=urn:btih:{metadata.InfoHash}&dn={Uri.EscapeDataString(metadata.Name)}{trackers}{webSeeds}{exactSource}";

            return new ModelRequestResult
            {
                Status = "ready",
                RepoId = repoId,
                FilePath = filePath,
                TorrentUrl = $"{serverBaseUrl}/torrent/{repoId}/{filePath}",
                MagnetUri = magnetUri,
                WebSeed = $"{serverBaseUrl}/hf/{repoId}/{filePath}",
                HuggingFaceUrl = hfDirectUrl,
            };
        }

        // Not cached — start background preparation if not already running
        _preparingTasks.GetOrAdd(cacheKey, _ =>
        {
            Console.WriteLine($"[HF Proxy] Background prepare started: {cacheKey}");
            return Task.Run(async () =>
            {
                try
                {
                    await CreateTorrentAsync(repoId, filePath, serverBaseUrl);
                    Console.WriteLine($"[HF Proxy] Background prepare complete: {cacheKey}");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[HF Proxy] Background prepare failed: {cacheKey} — {ex.Message}");
                }
                finally
                {
                    _preparingTasks.TryRemove(cacheKey, out Task? _);
                }
            });
        });

        // Return immediately — client should download from HuggingFace directly
        return new ModelRequestResult
        {
            Status = "preparing",
            RepoId = repoId,
            FilePath = filePath,
            HuggingFaceUrl = hfDirectUrl,
        };
    }

    /// <summary>Handle a web-seed request for a HuggingFace model file. Backed by <see cref="PartialFileCache"/>:
    /// a fully-cached file serves from disk; an uncached/partial file fetches only the MISSING covering chunks
    /// from HuggingFace, caches them at their byte offset (manifest-tracked, crash-safe), then serves — so the
    /// hub is always an available model source and a re-request for a range it already has hits no origin.</summary>
    public async Task HandleRequest(HttpContext context, string repoId, string filePath)
    {
        RecordRequest($"{repoId}/{filePath}");
        await GetFileCache(repoId, filePath).ServeRangeAsync(context);
    }

    /// <summary>One <see cref="PartialFileCache"/> per model file (created on first touch). Origin = HF's
    /// resolve URL (30x-redirects to its range-capable CDN; HttpClient follows). Completion just drops the
    /// manifest; the .torrent is built lazily on the next /torrent or /magnet request, which finds the
    /// now-complete file via <see cref="PartialFileCache.IsComplete"/>.</summary>
    private PartialFileCache GetFileCache(string repoId, string filePath)
        => _fileCaches.GetOrAdd($"{repoId}/{filePath}", _ => new PartialFileCache(
            GetCachePath(repoId, filePath), _httpClient,
            (start, end, ct) =>
            {
                var req = new HttpRequestMessage(HttpMethod.Get, $"https://huggingface.co/{repoId}/resolve/main/{filePath}");
                req.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(start, end);
                return Task.FromResult(req);
            }));

    /// <summary>
    /// Build BEP 17 web seed base URL. Client appends /{torrentName} to this.
    /// For "onnx/model.onnx" → returns "baseUrl/onnx"
    /// For "tokenizer.json" → returns "baseUrl" (no trailing slash)
    /// </summary>
    internal static string BuildWebSeedUrl(string baseUrl, string filePath)
    {
        // BEP 19 web seed for a single-file model torrent must point DIRECTLY at the file.
        // A BEP 19 client (our WebConn, JS WebTorrent) only appends a name when the URL ends
        // with '/', so emitting the parent directory (the old behavior) produced a URL the
        // client fetched verbatim — a directory path that 404'd, failing every web-seed piece
        // request. Emit the complete file URL with each path segment URL-encoded (slashes kept).
        var segments = filePath.Split('/');
        for (int i = 0; i < segments.Length; i++)
            segments[i] = System.Uri.EscapeDataString(segments[i]);
        return $"{baseUrl.TrimEnd('/')}/{string.Join("/", segments)}";
    }

    /// <summary>
    /// Get cache path for a model file. Checks all cache directories for existing files,
    /// returns path in first directory with sufficient free space for new files.
    /// </summary>
    private string GetCachePath(string repoId, string filePath)
    {
        var relativePath = Path.Combine(repoId.Replace('/', '_'), filePath.Replace('/', Path.DirectorySeparatorChar));

        // Check all directories for existing cached file
        foreach (var dir in _options.CacheDirectories)
        {
            var path = Path.Combine(dir, relativePath);
            if (File.Exists(path)) return path;
        }

        // New file — pick directory with most free space
        string bestDir = _options.CacheDirectories[0];
        long bestFree = 0;
        foreach (var dir in _options.CacheDirectories)
        {
            try
            {
                var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(dir)) ?? dir);
                if (drive.AvailableFreeSpace > bestFree)
                {
                    bestFree = drive.AvailableFreeSpace;
                    bestDir = dir;
                }
            }
            catch { } // DriveInfo may fail for network paths
        }

        return Path.Combine(bestDir, relativePath);
    }
}

/// <summary>Result from a non-blocking model request.</summary>
public class ModelRequestResult
{
    /// <summary>"ready" (torrent available) or "preparing" (use HuggingFace CDN directly).</summary>
    public string Status { get; set; } = "";
    public string RepoId { get; set; } = "";
    public string FilePath { get; set; } = "";
    /// <summary>Direct HuggingFace CDN URL — always available, use as immediate fallback.</summary>
    public string HuggingFaceUrl { get; set; } = "";
    /// <summary>URL to fetch .torrent file (only when Status == "ready").</summary>
    public string? TorrentUrl { get; set; }
    /// <summary>Magnet URI (only when Status == "ready").</summary>
    public string? MagnetUri { get; set; }
    /// <summary>Web seed URL on this server (only when Status == "ready").</summary>
    public string? WebSeed { get; set; }
}

/// <summary>Per-model cache statistics.</summary>
public class ModelCacheStats
{
    /// <summary>Total number of times this model was requested.</summary>
    public long RequestCount { get; set; }
    /// <summary>Last time this model was requested (UTC).</summary>
    public DateTime LastRequestUtc { get; set; } = DateTime.UtcNow;
    /// <summary>Cached file size in bytes (0 if evicted or not yet downloaded).</summary>
    public long FileSizeBytes { get; set; }
}

/// <summary>HuggingFace proxy configuration.</summary>
public class HuggingFaceProxyOptions
{
    /// <summary>
    /// Cache directories for model files. Models are stored in the first path with sufficient space.
    /// Multiple paths allow spreading cache across drives.
    /// Default: ["hf-cache"] (relative to working directory).
    /// </summary>
    public string[] CacheDirectories { get; set; } = new[] { "hf-cache" };

    /// <summary>Shortcut for single cache directory. Sets CacheDirectories[0].</summary>
    public string CacheDirectory
    {
        get => CacheDirectories.Length > 0 ? CacheDirectories[0] : "hf-cache";
        set => CacheDirectories = new[] { value };
    }

    /// <summary>Tracker URLs to include in generated .torrent files.</summary>
    public string[] TrackerUrls { get; set; } = new[]
    {
        "wss://hub.spawndev.com:44365/announce",
    };

    /// <summary>
    /// Maximum total cache size in bytes across all directories.
    /// 0 = no explicit limit (drive free space protection still applies).
    /// </summary>
    public long MaxCacheSizeBytes { get; set; } = 0;

    /// <summary>
    /// Minimum free disk space to maintain on each cache drive, in bytes.
    /// The server will evict cached models before the drive gets below this threshold.
    /// Default: 2GB.
    /// </summary>
    public long MinFreeDiskSpaceBytes { get; set; } = 2L * 1024 * 1024 * 1024; // 2GB
}

/// <summary>Extension methods for registering HuggingFace proxy endpoints.</summary>
public static class HuggingFaceProxyExtensions
{
    /// <summary>Add HuggingFace proxy endpoints to the application.</summary>
    public static void MapHuggingFaceProxy(this Microsoft.AspNetCore.Routing.IEndpointRouteBuilder app,
        HuggingFaceProxy proxy)
    {
        // Serve cached model files (web seed endpoint)
        // Route: /hf/{org}/{repo}/{filePath} → repoId = org/repo
        app.MapGet("/hf/{org}/{repo}/{**filePath}", async (HttpContext ctx, string org, string repo, string filePath) =>
        {
            await proxy.HandleRequest(ctx, $"{org}/{repo}", filePath);
        });

        // Get .torrent file for a HuggingFace model
        app.MapGet("/torrent/{org}/{repo}/{**filePath}", async (HttpContext ctx, string org, string repo, string filePath) =>
        {
            var repoId = $"{org}/{repo}";
            var serverUrl = $"{ctx.Request.Scheme}://{ctx.Request.Host}";
            var torrentBytes = await proxy.CreateTorrentAsync(repoId, filePath, serverUrl, ctx.RequestAborted);
            if (torrentBytes == null)
            {
                ctx.Response.StatusCode = 404;
                return;
            }
            ctx.Response.ContentType = "application/x-bittorrent";
            ctx.Response.Headers["Content-Disposition"] = $"attachment; filename=\"{Path.GetFileName(filePath)}.torrent\"";
            await ctx.Response.Body.WriteAsync(torrentBytes);
        });

        // Get magnet URI for a HuggingFace model (returns JSON with magnetUri + info)
        app.MapGet("/magnet/{org}/{repo}/{**filePath}", async (HttpContext ctx, string org, string repo, string filePath) =>
        {
            var repoId = $"{org}/{repo}";
            var serverUrl = $"{ctx.Request.Scheme}://{ctx.Request.Host}";
            var magnetUri = await proxy.GetMagnetUriAsync(repoId, filePath, serverUrl, ctx.RequestAborted);
            if (magnetUri == null)
            {
                ctx.Response.StatusCode = 404;
                return;
            }
            ctx.Response.ContentType = "application/json";
            await ctx.Response.WriteAsJsonAsync(new
            {
                magnetUri,
                repoId,
                filePath,
                webSeed = HuggingFaceProxy.BuildWebSeedUrl($"{serverUrl}/hf/{repoId}", filePath),
            });
        });

        // Non-blocking model request — returns immediately with torrent or HuggingFace fallback
        // Client flow: call /model/ → if "ready", use torrent/magnet → if "preparing", use huggingFaceUrl directly
        app.MapGet("/model/{org}/{repo}/{**filePath}", (HttpContext ctx, string org, string repo, string filePath) =>
        {
            var repoId = $"{org}/{repo}";
            var serverUrl = $"{ctx.Request.Scheme}://{ctx.Request.Host}";
            var result = proxy.RequestModel(repoId, filePath, serverUrl);
            return Results.Json(result);
        });

        // HuggingFace proxy stats — per-model request counts, cache usage, eviction info
        app.MapGet("/hf-stats", () => new
        {
            cachedFiles = proxy.CachedFileCount,
            cachedTorrents = proxy.CachedTorrentCount,
            cacheSizeMB = Math.Round(proxy.CacheSizeBytes / (1024.0 * 1024.0), 1),
            maxCacheSizeMB = proxy.ModelStats.Count > 0 ? Math.Round(proxy.CacheSizeBytes / (1024.0 * 1024.0), 1) : 0,
            models = proxy.ModelStats
                .OrderByDescending(kv => kv.Value.RequestCount)
                .Select(kv => new
                {
                    model = kv.Key,
                    requests = kv.Value.RequestCount,
                    lastRequest = kv.Value.LastRequestUtc.ToString("u"),
                    sizeMB = Math.Round(kv.Value.FileSizeBytes / (1024.0 * 1024.0), 1),
                    cached = kv.Value.FileSizeBytes > 0,
                }),
        });
    }
}
