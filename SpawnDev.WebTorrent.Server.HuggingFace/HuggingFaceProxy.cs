using System.Collections.Concurrent;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using SpawnDev.WebTorrent.Torrent;

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
    private readonly SemaphoreSlim _downloadLock = new(3); // max concurrent HF downloads

    // Cache generated .torrent files to avoid regenerating on every request
    private readonly ConcurrentDictionary<string, byte[]> _torrentCache = new();
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
        if (File.Exists(localPath))
        {
            // Update file size in stats
            _modelStats.AddOrUpdate(cacheKey,
                _ => new ModelCacheStats { FileSizeBytes = new FileInfo(localPath).Length },
                (_, s) => { s.FileSizeBytes = new FileInfo(localPath).Length; return s; });
            return localPath;
        }

        await _downloadLock.WaitAsync(ct);
        try
        {
            // Double-check after acquiring lock
            if (File.Exists(localPath)) return localPath;

            var url = $"https://huggingface.co/{repoId}/resolve/main/{filePath}";
            Console.WriteLine($"[HF Proxy] Downloading: {url}");

            using var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode)
            {
                Console.WriteLine($"[HF Proxy] Failed: {response.StatusCode} for {url}");
                return null;
            }

            var dir = Path.GetDirectoryName(localPath);
            if (dir != null) Directory.CreateDirectory(dir);

            using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var fileStream = File.Create(localPath);
            await stream.CopyToAsync(fileStream, ct);

            var fileSize = new FileInfo(localPath).Length;
            Console.WriteLine($"[HF Proxy] Cached: {localPath} ({fileSize:N0} bytes)");

            // Update stats with file size
            _modelStats.AddOrUpdate(cacheKey,
                _ => new ModelCacheStats { FileSizeBytes = fileSize },
                (_, s) => { s.FileSizeBytes = fileSize; return s; });

            // Evict least-recently-requested models if cache is over limit
            await EvictIfNeededAsync();

            return localPath;
        }
        finally
        {
            _downloadLock.Release();
        }
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

        // Return cached .torrent if available
        if (_torrentCache.TryGetValue(cacheKey, out var cached))
            return cached;

        var localPath = await GetOrFetchAsync(repoId, filePath, ct);
        if (localPath == null) return null;

        Console.WriteLine($"[HF Proxy] Generating .torrent for: {cacheKey}");

        var (torrentBytes, metadata) = await TorrentCreator.CreateFromFileAsync(localPath,
            new TorrentCreatorOptions
            {
                Name = System.IO.Path.GetFileName(filePath),
                Trackers = _options.TrackerUrls,
                // BEP 17: web seed URL is the base directory — client appends /{torrentName}
                // e.g., base = /hf/Xenova/distilgpt2 + name = tokenizer.json → /hf/Xenova/distilgpt2/tokenizer.json
                WebSeeds = new[]
                {
                    $"{serverBaseUrl}/hf/{repoId}/{GetDirectoryPart(filePath)}",
                    $"https://huggingface.co/{repoId}/resolve/main/{GetDirectoryPart(filePath)}",
                },
                Comment = $"HuggingFace model: {repoId}/{filePath}",
                CreatedBy = "SpawnDev.WebTorrent.Server.HuggingFace",
            }, ct);

        _torrentCache[cacheKey] = torrentBytes;
        Console.WriteLine($"[HF Proxy] .torrent ready: {cacheKey} ({metadata.PieceHashes.Length} pieces, infoHash={metadata.InfoHashHex})");

        return torrentBytes;
    }

    /// <summary>Get magnet URI for a HuggingFace model file. Creates .torrent if needed.</summary>
    public async Task<string?> GetMagnetUriAsync(string repoId, string filePath,
        string serverBaseUrl, CancellationToken ct = default)
    {
        var torrentBytes = await CreateTorrentAsync(repoId, filePath, serverBaseUrl, ct);
        if (torrentBytes == null) return null;

        var metadata = TorrentParser.Parse(torrentBytes);
        var trackers = string.Join("", _options.TrackerUrls.Select(t => $"&tr={Uri.EscapeDataString(t)}"));
        var webSeeds = $"&ws={Uri.EscapeDataString($"{serverBaseUrl}/hf/{repoId}/{GetDirectoryPart(filePath)}")}";

        return $"magnet:?xt=urn:btih:{metadata.InfoHashHex}&dn={Uri.EscapeDataString(metadata.Name)}{trackers}{webSeeds}";
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
            var webSeeds = $"&ws={Uri.EscapeDataString($"{serverBaseUrl}/hf/{repoId}/{GetDirectoryPart(filePath)}")}";
            var magnetUri = $"magnet:?xt=urn:btih:{metadata.InfoHashHex}&dn={Uri.EscapeDataString(metadata.Name)}{trackers}{webSeeds}";

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

    /// <summary>Handle a proxied request for a HuggingFace model file.</summary>
    public async Task HandleRequest(HttpContext context, string repoId, string filePath)
    {
        var localPath = await GetOrFetchAsync(repoId, filePath, context.RequestAborted);
        if (localPath == null)
        {
            context.Response.StatusCode = 404;
            return;
        }

        var fileInfo = new FileInfo(localPath);
        context.Response.ContentType = "application/octet-stream";
        context.Response.Headers["Accept-Ranges"] = "bytes";

        // Support range requests for web seed compatibility
        if (context.Request.Headers.TryGetValue("Range", out var rangeHeader))
        {
            var range = rangeHeader.ToString();
            if (range.StartsWith("bytes="))
            {
                var parts = range.Substring(6).Split('-');
                long start = long.Parse(parts[0]);
                long end = parts.Length > 1 && !string.IsNullOrEmpty(parts[1])
                    ? long.Parse(parts[1])
                    : fileInfo.Length - 1;

                int length = (int)(end - start + 1);
                context.Response.StatusCode = 206;
                context.Response.Headers["Content-Range"] = $"bytes {start}-{end}/{fileInfo.Length}";
                context.Response.ContentLength = length;

                using var fs = File.OpenRead(localPath);
                fs.Seek(start, SeekOrigin.Begin);
                var buffer = new byte[Math.Min(length, 65536)];
                int remaining = length;
                while (remaining > 0)
                {
                    int toRead = Math.Min(remaining, buffer.Length);
                    int read = await fs.ReadAsync(buffer.AsMemory(0, toRead));
                    if (read == 0) break;
                    await context.Response.Body.WriteAsync(buffer.AsMemory(0, read));
                    remaining -= read;
                }
                return;
            }
        }

        context.Response.ContentLength = fileInfo.Length;
        await context.Response.SendFileAsync(localPath);
    }

    /// <summary>Get directory portion of a file path, or empty string if no directory.</summary>
    internal static string GetDirectoryPart(string filePath)
    {
        var lastSlash = filePath.LastIndexOf('/');
        return lastSlash >= 0 ? filePath[..lastSlash] : "";
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
                webSeed = $"{serverUrl}/hf/{repoId}/{HuggingFaceProxy.GetDirectoryPart(filePath)}",
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
