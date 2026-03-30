using Microsoft.AspNetCore.HttpOverrides;
using SpawnDev.WebTorrent.Server;
using SpawnDev.WebTorrent.Server.HuggingFace;

var builder = WebApplication.CreateBuilder(args);

// In production (behind haproxy), ASPNETCORE_URLS env var controls binding.
// For local dev: HTTPS 5560 + HTTP 5561
if (!builder.Environment.IsProduction())
    builder.WebHost.UseUrls("https://localhost:5560", "http://localhost:5561");

// Trust forwarded headers from reverse proxy (haproxy) so ctx.Request.Scheme
// reflects the original HTTPS scheme, not the internal HTTP connection.
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    options.KnownNetworks.Clear();
    options.KnownProxies.Clear();
});

// Enable WebSockets for tracker
// Configuration from appsettings.json (falls back to defaults if not present)
var config = builder.Configuration;

var trackerOptions = new TrackerOptions
{
    AnnounceInterval = config.GetValue("Tracker:AnnounceInterval", 120),
    MaxPeersPerAnnounce = config.GetValue("Tracker:MaxPeersPerAnnounce", 50),
};
builder.Services.AddSingleton(new TorrentTracker(trackerOptions));

builder.Services.AddSingleton(new WebSeedServer(config.GetValue("WebSeed:Directory", "seed-data")!));
builder.Services.AddSingleton(new ComputeRequestBoard());

var hfOptions = new HuggingFaceProxyOptions
{
    CacheDirectories = config.GetSection("HuggingFace:CacheDirectories").Get<string[]>()
        ?? new[] { config.GetValue("HuggingFace:CacheDirectory", "hf-cache")! },
    TrackerUrls = config.GetSection("HuggingFace:TrackerUrls").Get<string[]>()
        ?? new[] { "wss://hub.spawndev.com:44365/announce" },
    MaxCacheSizeBytes = config.GetValue("HuggingFace:MaxCacheSizeBytes", 0L),
    MinFreeDiskSpaceBytes = config.GetValue("HuggingFace:MinFreeDiskSpaceBytes", 2L * 1024 * 1024 * 1024),
};
builder.Services.AddSingleton(new HuggingFaceProxy(hfOptions));

// CORS for browser clients
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader();
    });
});

var app = builder.Build();

app.UseForwardedHeaders();
app.UseWebSockets();
app.UseCors();

// Server info
app.MapGet("/", () => new
{
    name = "SpawnDev.WebTorrent.Server",
    version = "2.0.0",
    endpoints = new
    {
        tracker = "/announce (WebSocket)",
        webSeed = "/seed/{infoHash}/{filePath}",
        huggingFace = "/hf/{repoId}/{filePath}",
        torrent = "/torrent/{repoId}/{filePath}",
        stats = "/stats",
        computeRequests = "/compute/requests",
        computeStats = "/compute/stats",
        postComputeRequest = "POST /compute/request",
    }
});

// Register endpoints
var tracker = app.Services.GetRequiredService<TorrentTracker>();
var webSeed = app.Services.GetRequiredService<WebSeedServer>();
app.MapWebTorrentServer(tracker, webSeed);

var hfProxy = app.Services.GetRequiredService<HuggingFaceProxy>();
app.MapHuggingFaceProxy(hfProxy);

// Compute request board — authenticated endpoints
var computeBoard = app.Services.GetRequiredService<ComputeRequestBoard>();

// POST: signed request (requires OwnerFingerprint + PublicKey + Signature)
app.MapPost("/compute/request", (ComputeRequest request) =>
{
    var (posted, error) = computeBoard.PostSigned(request);
    if (posted != null)
        return Results.Ok(posted);
    return Results.BadRequest(new { error });
});

// GET: public — anyone can browse
app.MapGet("/compute/requests", () => computeBoard.GetActive());
app.MapGet("/compute/stats", () => computeBoard.GetStats());

// DELETE: requires fingerprint query param matching the owner
app.MapDelete("/compute/request/{id}", (string id, string? fingerprint) =>
{
    if (string.IsNullOrEmpty(fingerprint))
        return Results.BadRequest(new { error = "fingerprint query parameter required" });

    var (success, error) = computeBoard.RemoveAuthenticated(id, fingerprint);
    if (success) return Results.Ok();
    if (error == "not found") return Results.NotFound();
    return Results.Json(new { error }, statusCode: 403);
});

Console.WriteLine("SpawnDev.WebTorrent.Server starting...");
Console.WriteLine("  Tracker:      wss://localhost:5560/announce");
Console.WriteLine("  Web Seed:     https://localhost:5560/seed/{infoHash}/{filePath}");
Console.WriteLine("  HuggingFace:  https://localhost:5560/hf/{repoId}/{filePath}");
Console.WriteLine("  Torrent Gen:  https://localhost:5560/torrent/{repoId}/{filePath}");
Console.WriteLine("  Magnet URI:   https://localhost:5560/magnet/{repoId}/{filePath}");
Console.WriteLine("  Stats:        https://localhost:5560/stats");
Console.WriteLine("  HF Stats:     https://localhost:5560/hf-stats");
Console.WriteLine("  Compute Board: https://localhost:5560/compute/requests");

app.Run();
