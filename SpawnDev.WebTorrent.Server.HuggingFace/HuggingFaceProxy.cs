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

    public HuggingFaceProxy(HuggingFaceProxyOptions? options = null)
    {
        _options = options ?? new HuggingFaceProxyOptions();
        _httpClient = new HttpClient();
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("SpawnDev.WebTorrent.Server/1.0");
        Directory.CreateDirectory(_options.CacheDirectory);
    }

    /// <summary>Number of cached model files.</summary>
    public int CachedFileCount => Directory.Exists(_options.CacheDirectory)
        ? Directory.GetFiles(_options.CacheDirectory, "*", SearchOption.AllDirectories).Length : 0;

    /// <summary>Number of cached .torrent files.</summary>
    public int CachedTorrentCount => _torrentCache.Count;

    /// <summary>Total cache size in bytes.</summary>
    public long CacheSizeBytes => Directory.Exists(_options.CacheDirectory)
        ? new DirectoryInfo(_options.CacheDirectory).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length) : 0;

    /// <summary>
    /// Get or fetch a model file. Returns the local file path.
    /// If not cached, downloads from HuggingFace and caches.
    /// </summary>
    public async Task<string?> GetOrFetchAsync(string repoId, string filePath, CancellationToken ct = default)
    {
        var localPath = GetCachePath(repoId, filePath);
        if (File.Exists(localPath)) return localPath;

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

            Console.WriteLine($"[HF Proxy] Cached: {localPath} ({new FileInfo(localPath).Length:N0} bytes)");
            return localPath;
        }
        finally
        {
            _downloadLock.Release();
        }
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
                WebSeeds = new[]
                {
                    $"{serverBaseUrl}/hf/{repoId}/{filePath}",
                    $"https://huggingface.co/{repoId}/resolve/main/{filePath}",
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
        var webSeeds = $"&ws={Uri.EscapeDataString($"{serverBaseUrl}/hf/{repoId}/{filePath}")}";

        return $"magnet:?xt=urn:btih:{metadata.InfoHashHex}&dn={Uri.EscapeDataString(metadata.Name)}{trackers}{webSeeds}";
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

    private string GetCachePath(string repoId, string filePath)
        => Path.Combine(_options.CacheDirectory, repoId.Replace('/', '_'), filePath.Replace('/', Path.DirectorySeparatorChar));
}

/// <summary>HuggingFace proxy configuration.</summary>
public class HuggingFaceProxyOptions
{
    /// <summary>Local directory for cached model files.</summary>
    public string CacheDirectory { get; set; } = "hf-cache";

    /// <summary>Tracker URLs to include in generated .torrent files.</summary>
    public string[] TrackerUrls { get; set; } = new[]
    {
        "wss://tracker.webtorrent.dev",
    };
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
                webSeed = $"{serverUrl}/hf/{repoId}/{filePath}",
            });
        });

        // HuggingFace proxy stats
        app.MapGet("/hf-stats", () => new
        {
            cachedFiles = proxy.CachedFileCount,
            cachedTorrents = proxy.CachedTorrentCount,
            cacheSizeMB = proxy.CacheSizeBytes / (1024.0 * 1024.0),
        });
    }
}
