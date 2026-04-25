# SpawnDev.WebTorrent.Server.HuggingFace

[![NuGet](https://img.shields.io/nuget/v/SpawnDev.WebTorrent.Server.HuggingFace.svg)](https://www.nuget.org/packages/SpawnDev.WebTorrent.Server.HuggingFace)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](https://opensource.org/licenses/MIT)

Optional HuggingFace CDN proxy extension for [SpawnDev.WebTorrent.Server](https://github.com/LostBeard/SpawnDev.WebTorrent). Fetches model files from HuggingFace's CDN on demand, caches them locally, generates SHA-256 `.torrent` files, and serves the cached bytes as a [BEP 17/19](http://bittorrent.org/beps/bep_0019.html) web seed. Browser clients hold the .torrent in memory and stream pieces from the local cache via WebRTC peers AND the HTTP web seed in the same swarm.

`SpawnDev.WebTorrent.Server` works without this extension — `MapHuggingFaceProxy` is opt-in.

## Why this exists

AI models are big. CDNs scale poorly when every browser tab independently downloads the same 2 GB safetensors shard. SpawnDev's pattern: one cache server (this proxy) sits in front of HuggingFace, hashes everything once at SHA-256 piece level, and exposes both an HTTP range-request fallback (web seed) and a magnet URI (P2P seed). Every browser that loads the model becomes a peer for the next.

The `hub.spawndev.com` deployment runs this proxy in production for the SpawnDev model fleet.

## Install

```xml
<PackageReference Include="SpawnDev.WebTorrent.Server.HuggingFace" Version="3.2.0" />
```

Pulls in `SpawnDev.WebTorrent.Server` 3.2.0 + `SpawnDev.WebTorrent` 3.2.0 transitively.

## Quick start

```csharp
using SpawnDev.WebTorrent.Server.HuggingFace;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

var proxy = new HuggingFaceProxy(new HuggingFaceProxyOptions
{
    CacheDirectory = "hf-cache",                  // single dir (or use CacheDirectories for multi)
    TrackerUrls = new[] { "wss://your-tracker.example/announce" },
    MinFreeDiskSpaceBytes = 2L * 1024 * 1024 * 1024, // keep 2 GB headroom (default)
});

app.MapHuggingFaceProxy(proxy);
app.Run();
```

## Endpoints

The extension wires five `MapGet` routes under your ASP.NET Core app:

| Route | Purpose |
|-------|---------|
| `GET /model/{org}/{repo}/{filePath}` | Non-blocking: returns the .torrent if the file is already cached, else falls through to the HuggingFace CDN. Use as the primary URL clients hit — caching happens in the background. |
| `GET /torrent/{org}/{repo}/{filePath}` | Returns a fully-formed `.torrent` (SHA-256 piece hashes, web seed pre-populated). 404 if not yet cached. |
| `GET /magnet/{org}/{repo}/{filePath}` | Returns a magnet URI with `xt=urn:btih:` (and `xt=urn:btmh:` for v2 / hybrid torrents) plus `xs=` (exact source) and `ws=` (web seed) parameters. |
| `GET /hf/{org}/{repo}/{filePath}` | The web seed endpoint itself. Serves cached bytes with HTTP range request support; this is what mainline / desktop clients dial when they're using BEP 17/19. |
| `GET /hf-stats` | JSON with per-model request counts and cache usage. |

Path style mirrors HuggingFace's own URL shape: `org/repo/path/to/file.safetensors`.

## Options (`HuggingFaceProxyOptions`)

| Option | Type | Default | Description |
|--------|------|---------|-------------|
| `CacheDirectories` | `string[]` | `["hf-cache"]` | Cache dirs to spread storage across drives. The proxy picks the first dir with sufficient free space. |
| `CacheDirectory` | `string` | `"hf-cache"` | Convenience setter for the single-directory case (sets `CacheDirectories[0]`). |
| `TrackerUrls` | `string[]` | `["wss://hub.spawndev.com:44365/announce"]` | Trackers baked into every generated `.torrent`. Replace with your own deployment. |
| `MaxCacheSizeBytes` | `long` | `0` (no limit) | Hard ceiling across all cache directories. Combine with `MinFreeDiskSpaceBytes` for layered eviction policy. |
| `MinFreeDiskSpaceBytes` | `long` | `2 GB` | The proxy evicts (LRU) cached models before any cache drive gets below this threshold. Protects shared boxes from filling up the system drive. |

## Cache lifecycle

1. Client hits `/model/{org}/{repo}/{file}` (or `/torrent/...` / `/magnet/...`).
2. Proxy checks the cache. **Cache hit:** returns the .torrent / magnet immediately.
3. **Cache miss:** proxy starts an async download from HuggingFace's CDN. The first request falls through to the CDN URL (so the client doesn't block); subsequent requests for the same file see the cached bytes.
4. As bytes land on disk, the proxy chunks them into BEP 52 v2 pieces (16 KiB Merkle leaves, SHA-256 piece hashes) and writes a `.torrent` next to the payload.
5. LRU eviction kicks in when `MinFreeDiskSpaceBytes` is at risk OR `MaxCacheSizeBytes` is exceeded.

The HuggingFace hostname (`huggingface.co`) is a hardcoded upstream — the proxy only fetches from that origin.

## Use case: ML model delivery from Blazor

```csharp
// Server side (this package)
app.MapHuggingFaceProxy(new HuggingFaceProxy(opts));

// Client side (Blazor WASM, using SpawnDev.WebTorrent)
var magnet = await Http.GetStringAsync(
    $"https://your-server/magnet/Xenova/whisper-tiny/onnx/encoder_model.onnx");
var torrent = await Client.AddAsync(magnet);
await foreach (var piece in torrent.Files[0].StreamAsync())
{
    // Pieces arrive from peers AND from the HTTP web seed (your server) at once.
}
```

The first browser to load a model fills the cache; every subsequent browser participates in the swarm.

## Production notes

- **Disk space.** A single browser session loading a 2 GB safetensors shard fills 2 GB of cache. Provision `CacheDirectories` accordingly; the LRU eviction is the safety net, not the plan.
- **CDN bandwidth.** HuggingFace serves the first download; every subsequent download comes from your cache + peers in the swarm. Watching `/hf-stats` shows the win — request count grows but byte count from the upstream stays flat.
- **Trackers.** Override `TrackerUrls` to point at your own SpawnDev.RTC-based tracker (deploy via [SpawnDev.RTC.ServerApp](https://github.com/LostBeard/SpawnDev.RTC)). Public trackers like `wss://tracker.openwebtorrent.com` work too but are best-effort.

## Dependencies

- `SpawnDev.WebTorrent.Server` 3.2.0 (web seed server primitives)
- `SpawnDev.WebTorrent` 3.2.0 (`.torrent` creation, SHA-256 piece hashing, BEP 52 v2)
- ASP.NET Core 10 (`Microsoft.AspNetCore.App` framework reference)

## License

MIT — see [LICENSE.txt](https://github.com/LostBeard/SpawnDev.WebTorrent/blob/master/SpawnDev.WebTorrent/LICENSE.txt) in the parent repository.
