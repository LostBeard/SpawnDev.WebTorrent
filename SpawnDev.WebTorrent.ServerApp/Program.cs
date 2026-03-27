using SpawnDev.WebTorrent.Server;
using SpawnDev.WebTorrent.Server.HuggingFace;

var builder = WebApplication.CreateBuilder(args);

// Configure port — 5560 to avoid conflicts with ILGPU (5550) and ML (5551)
builder.WebHost.UseUrls("https://localhost:5560", "http://localhost:5561");

// Enable WebSockets for tracker
builder.Services.AddSingleton(new TorrentTracker(new TrackerOptions
{
    AnnounceInterval = 120,
    MaxPeersPerAnnounce = 50,
}));

builder.Services.AddSingleton(new WebSeedServer("seed-data"));

builder.Services.AddSingleton(new HuggingFaceProxy(new HuggingFaceProxyOptions
{
    CacheDirectory = "hf-cache",
    TrackerUrls = new[] { "wss://localhost:5560/announce" },
}));

// CORS for browser clients
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader();
    });
});

var app = builder.Build();

app.UseWebSockets();
app.UseCors();

// Server info
app.MapGet("/", () => new
{
    name = "SpawnDev.WebTorrent.Server",
    version = "1.0.0",
    endpoints = new
    {
        tracker = "/announce (WebSocket)",
        webSeed = "/seed/{infoHash}/{filePath}",
        huggingFace = "/hf/{repoId}/{filePath}",
        torrent = "/torrent/{repoId}/{filePath}",
        stats = "/stats",
    }
});

// Register endpoints
var tracker = app.Services.GetRequiredService<TorrentTracker>();
var webSeed = app.Services.GetRequiredService<WebSeedServer>();
app.MapWebTorrentServer(tracker, webSeed);

var hfProxy = app.Services.GetRequiredService<HuggingFaceProxy>();
app.MapHuggingFaceProxy(hfProxy);

Console.WriteLine("SpawnDev.WebTorrent.Server starting...");
Console.WriteLine("  Tracker:      wss://localhost:5560/announce");
Console.WriteLine("  Web Seed:     https://localhost:5560/seed/{infoHash}/{filePath}");
Console.WriteLine("  HuggingFace:  https://localhost:5560/hf/{repoId}/{filePath}");
Console.WriteLine("  Torrent Gen:  https://localhost:5560/torrent/{repoId}/{filePath}");
Console.WriteLine("  Magnet URI:   https://localhost:5560/magnet/{repoId}/{filePath}");
Console.WriteLine("  Stats:        https://localhost:5560/stats");
Console.WriteLine("  HF Stats:     https://localhost:5560/hf-stats");

app.Run();
