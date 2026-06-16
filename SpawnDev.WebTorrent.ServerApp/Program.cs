using Microsoft.AspNetCore.HttpOverrides;
using SIPSorcery.Net;
using SpawnDev.RTC.Server;
using SpawnDev.RTC.Server.Extensions;
using SpawnDev.WebTorrent.Server;
using SpawnDev.WebTorrent.Server.HuggingFace;
using System.Net;

// Optional STUN/TURN + abuse-protection features (all disabled by default):
//   RTC__AllowedOrigins                         Semicolon-separated Origin allowlist on /announce
//   RTC__StunTurn__Enabled                      Run an embedded STUN/TURN server
//   RTC__StunTurn__Port                         UDP port (default 3478)
//   RTC__StunTurn__ListenAddress                IP to bind (default 0.0.0.0)
//   RTC__StunTurn__RelayAddress                 Public IP advertised in XOR-RELAYED-ADDRESS (set when NAT'd)
//   RTC__StunTurn__Realm                        TURN auth realm (default "spawndev-rtc")
//   RTC__StunTurn__Username                     Long-term credential username
//   RTC__StunTurn__Password                     Long-term credential password
//   RTC__StunTurn__EphemeralCredentialSharedSecret  HMAC secret for RFC 8489 §9.2 ephemeral creds
//   RTC__StunTurn__TrackerGated                 Only tracker-announced peers can allocate (requires shared secret)
//   RTC__StunTurn__RelayPortRangeStart          Low bound of per-allocation relay ports (inclusive, for NAT)
//   RTC__StunTurn__RelayPortRangeEnd            High bound. Constrains relay sockets to a forwardable range

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

// Tracker moved from SpawnDev.WebTorrent.Server.TorrentTracker to
// SpawnDev.RTC.Server.TrackerSignalingServer in 3.1.0 - same wire protocol,
// generic room signaling, WebTorrent-compatible. The server instance is wired
// after `app.Build()` via `app.UseRtcSignaling(...)`.
var trackerOptions = new TrackerServerOptions
{
    AnnounceIntervalSeconds = config.GetValue("Tracker:AnnounceInterval", 120),
    MaxPeersPerAnnounce = config.GetValue("Tracker:MaxPeersPerAnnounce", 50),
};

// Optional Origin allowlist for the /announce WebSocket endpoint. When unset,
// no Origin check runs (backward compatible). Accepts exact match and
// wildcard subdomain form (`https://*.example.com`). See SpawnDev.RTC.Server
// TrackerServerOptions.AllowedOrigins for full semantics.
var allowedOriginsRaw = config.GetValue<string?>("RTC:AllowedOrigins");
if (!string.IsNullOrWhiteSpace(allowedOriginsRaw))
{
    trackerOptions.AllowedOrigins = allowedOriginsRaw
        .Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .ToArray();
}

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

// Ollama model registry proxy (twin of the HF proxy): resolve {model}:{tag} layer → blob → cache → torrent.
var ollamaOptions = new OllamaProxyOptions
{
    RegistryBaseUrl = config.GetValue("Ollama:RegistryBaseUrl", "https://registry.ollama.ai")!,
    CacheDirectories = config.GetSection("Ollama:CacheDirectories").Get<string[]>()
        ?? new[] { config.GetValue("Ollama:CacheDirectory", "ollama-cache")! },
    TrackerUrls = config.GetSection("Ollama:TrackerUrls").Get<string[]>()
        ?? new[] { "wss://hub.spawndev.com:44365/announce" },
};
builder.Services.AddSingleton(new OllamaProxy(ollamaOptions));

// CORS for browser clients.
// Web-seed piece fetches carry a Range header, which makes them "non-simple" cross-origin
// requests — the browser issues a CORS preflight (OPTIONS) before EACH one. A large model is
// thousands of piece GETs, so without preflight caching the browser re-issues an OPTIONS before
// every single Range GET (observed: ~1300 preflight+GET pairs to load one 330MB model). Setting
// a long preflight Max-Age lets the browser cache the preflight result and skip the per-piece
// OPTIONS entirely — the single biggest latency win for streaming a model from the web seed.
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader()
              .SetPreflightMaxAge(TimeSpan.FromHours(24));
    });
});

var app = builder.Build();

app.UseForwardedHeaders();
app.UseWebSockets();
app.UseCors();

// Server info. Built-time version string gets bumped alongside each deploy so
// the `/` endpoint can verify "the new code is actually running" (the old
// hardcoded "2.0.0" was static across all builds and useless for that).
var serverVersion = typeof(SpawnDev.WebTorrent.WebTorrentClient).Assembly.GetName().Version?.ToString(3) ?? "unknown";
var stunTurnPort = config.GetValue("RTC:StunTurn:Port", 3478);
var stunTurnEnabledView = config.GetValue("RTC:StunTurn:Enabled", false);
var ephemeralCredsSet = !string.IsNullOrEmpty(config.GetValue<string?>("RTC:StunTurn:EphemeralCredentialSharedSecret"));
var trackerGatedView = config.GetValue("RTC:StunTurn:TrackerGated", false);

app.MapGet("/", () => new
{
    name = "SpawnDev.WebTorrent.Server",
    version = serverVersion,
    stunTurn = new
    {
        enabled = stunTurnEnabledView,
        port = stunTurnEnabledView ? (int?)stunTurnPort : null,
        authMode = stunTurnEnabledView
            ? (ephemeralCredsSet ? (trackerGatedView ? "ephemeral + tracker-gated" : "ephemeral") : "long-term")
            : null,
    },
    originAllowlistEnabled = trackerOptions.AllowedOrigins is { Count: > 0 },
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
var tracker = app.UseRtcSignaling("/announce", trackerOptions);
var webSeed = app.Services.GetRequiredService<WebSeedServer>();
app.MapWebSeedServer(webSeed);

// Optional embedded STUN/TURN server. Inline (rather than AddRtcStunTurn) so
// the tracker-gated resolver can close over the tracker instance just created.
TurnServer? turnServer = null;
var turnEnabled = config.GetValue("RTC:StunTurn:Enabled", false);
if (turnEnabled)
{
    var turnListen = config.GetValue("RTC:StunTurn:ListenAddress", "0.0.0.0");
    var turnRelay = config.GetValue<string?>("RTC:StunTurn:RelayAddress");
    var turnRealm = config.GetValue("RTC:StunTurn:Realm", "spawndev-rtc");
    var turnConfig = new TurnServerConfig
    {
        ListenAddress = IPAddress.Parse(turnListen),
        Port = config.GetValue("RTC:StunTurn:Port", 3478),
        EnableTcp = config.GetValue("RTC:StunTurn:EnableTcp", true),
        EnableUdp = config.GetValue("RTC:StunTurn:EnableUdp", true),
        RelayAddress = !string.IsNullOrWhiteSpace(turnRelay)
            ? IPAddress.Parse(turnRelay)
            : IPAddress.Parse(turnListen),
        Username = config.GetValue("RTC:StunTurn:Username", "turn-user"),
        Password = config.GetValue("RTC:StunTurn:Password", "turn-pass"),
        Realm = turnRealm,
        DefaultLifetimeSeconds = config.GetValue("RTC:StunTurn:DefaultLifetimeSeconds", 600),
        RelayPortRangeStart = config.GetValue("RTC:StunTurn:RelayPortRangeStart", 0),
        RelayPortRangeEnd = config.GetValue("RTC:StunTurn:RelayPortRangeEnd", 0),
    };

    var sharedSecret = config.GetValue<string?>("RTC:StunTurn:EphemeralCredentialSharedSecret");
    var trackerGated = config.GetValue("RTC:StunTurn:TrackerGated", false);

    if (!string.IsNullOrEmpty(sharedSecret))
    {
        turnConfig.ResolveHmacKey = trackerGated
            ? EphemeralTurnCredentials.TrackerGatedResolver(sharedSecret, turnRealm, tracker)
            : username => EphemeralTurnCredentials.ResolveLongTermKey(sharedSecret, turnRealm, username);
    }
    else if (trackerGated)
    {
        throw new InvalidOperationException(
            "RTC__StunTurn__TrackerGated=true requires RTC__StunTurn__EphemeralCredentialSharedSecret to also be set.");
    }

    turnServer = new TurnServer(turnConfig);
    turnServer.Start();
    app.Lifetime.ApplicationStopping.Register(() =>
    {
        try { turnServer.Stop(); } catch { /* best-effort */ }
        turnServer.Dispose();
    });
}

// /stats now reports from the generic signaling server.
app.MapGet("/stats", () => new
{
    rooms = tracker.Rooms.Count,
    totalPeers = tracker.TotalPeers,
    roomDetails = tracker.Rooms.Select(r => new
    {
        roomKey = r.Key,
        peers = r.Value.Peers.Count,
    }),
});

var hfProxy = app.Services.GetRequiredService<HuggingFaceProxy>();
app.MapHuggingFaceProxy(hfProxy);

var ollamaProxy = app.Services.GetRequiredService<OllamaProxy>();
app.MapOllamaProxy(ollamaProxy);

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
if (trackerOptions.AllowedOrigins is { Count: > 0 } allowList)
    Console.WriteLine($"  Origin allowlist: {string.Join(", ", allowList)}");
if (turnEnabled && turnServer != null)
{
    var authMode = !string.IsNullOrEmpty(config.GetValue<string?>("RTC:StunTurn:EphemeralCredentialSharedSecret"))
        ? (config.GetValue("RTC:StunTurn:TrackerGated", false) ? "ephemeral + tracker-gated" : "ephemeral")
        : "long-term";
    Console.WriteLine($"  STUN/TURN:    UDP :{config.GetValue("RTC:StunTurn:Port", 3478)} (auth={authMode})");
    var rangeStart = config.GetValue("RTC:StunTurn:RelayPortRangeStart", 0);
    var rangeEnd = config.GetValue("RTC:StunTurn:RelayPortRangeEnd", 0);
    if (rangeStart > 0 && rangeEnd >= rangeStart)
        Console.WriteLine($"  Relay ports:  UDP {rangeStart}-{rangeEnd} (forward this range at your NAT)");
    else
        Console.WriteLine("  Relay ports:  OS ephemeral (set RelayPortRangeStart/End when behind NAT)");
}

app.Run();
