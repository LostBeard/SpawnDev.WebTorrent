# SpawnDev.WebTorrent

[![NuGet](https://img.shields.io/nuget/v/SpawnDev.WebTorrent.svg)](https://www.nuget.org/packages/SpawnDev.WebTorrent)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](https://opensource.org/licenses/MIT)

Pure C# BitTorrent/WebTorrent client and server. No JavaScript dependencies. Runs on desktop (.NET) and browser (Blazor WASM).

## Features

- **Pure C#** — No JavaScript interop, no Node.js, no npm. 100% .NET.
- **Desktop + Browser** — Same library runs on .NET console/desktop apps and Blazor WebAssembly.
- **Random-Access Streaming** — Read any byte range from a torrent file as it downloads. Perfect for ML model weight streaming.
- **WebSocket Tracker** — Full tracker implementation with WebRTC signaling relay for browser peers.
- **Web Seed Support** — HTTP range request fallback (BEP 17/19) when peers aren't available.
- **HuggingFace Integration** — Optional server extension that proxies HuggingFace model CDN with local caching and automatic torrent generation.
- **.torrent Creation** — Create and parse .torrent files. Complete Bencode encoder/decoder.

## Packages

| Package | Description |
|---------|-------------|
| [SpawnDev.WebTorrent](https://www.nuget.org/packages/SpawnDev.WebTorrent) | Client library — torrents, peers, streaming |
| [SpawnDev.WebTorrent.Server](https://www.nuget.org/packages/SpawnDev.WebTorrent.Server) | Server library — tracker, web seed |

## Quick Start — Client

```csharp
using SpawnDev.WebTorrent;

var client = new WebTorrentClient();
var torrent = await client.AddAsync("magnet:?xt=urn:btih:...");

// Stream a file as it downloads
var file = torrent.Files[0];
var chunk = await file.ReadAsync(offset: 0, length: 65536);
```

## Quick Start — Server

```csharp
using SpawnDev.WebTorrent.Server;

var tracker = new TorrentTracker();
var webSeed = new WebSeedServer("seed-data");

var app = WebApplication.CreateBuilder(args).Build();
app.UseWebSockets();
app.MapWebTorrentServer(tracker, webSeed);
app.Run();
```

## Quick Start — HuggingFace Proxy

```csharp
using SpawnDev.WebTorrent.Server.HuggingFace;

var proxy = new HuggingFaceProxy(new HuggingFaceProxyOptions
{
    CacheDirectory = "hf-cache",
});
app.MapHuggingFaceProxy(proxy);

// Clients access: https://your-server/hf/{repoId}/{filePath}
// Auto-caches from HuggingFace CDN on first request
// Generates .torrent files for P2P distribution
```

## Why This Exists

AI models are big. CDNs can't scale when every user downloads the same 2GB model. SpawnDev.WebTorrent turns every browser into a peer — the more users, the faster delivery. Built for [SpawnDev.ILGPU.ML](https://github.com/LostBeard/SpawnDev.ILGPU.ML), the GPU-accelerated ML library for Blazor WebAssembly.

## Architecture

```
Browser Client                    Server (spawndev.com)
┌─────────────┐                  ┌──────────────────────┐
│ WebTorrent   │◄──WebSocket───►│ TorrentTracker       │
│ Client       │  (signaling)   │ (peer discovery)     │
│              │                │                      │
│ WebRTC ◄────►│ WebRTC         │ WebSeedServer        │
│ (P2P data)   │                │ (HTTP range fallback)│
│              │                │                      │
│ OPFS Cache   │                │ HuggingFaceProxy     │
│ (persistent) │                │ (fetch + cache + seed)│
└─────────────┘                  └──────────────────────┘
         ▲                                ▲
         │ P2P piece exchange             │ First fetch
         ▼                                ▼
┌─────────────┐                  ┌──────────────────────┐
│ Other        │                 │ HuggingFace CDN      │
│ Browser Peers│                 │ (origin source)      │
└─────────────┘                  └──────────────────────┘
```

## Credits

Built by Todd Tanner ([@LostBeard](https://github.com/LostBeard)) and the SpawnDev team.

## License

MIT
