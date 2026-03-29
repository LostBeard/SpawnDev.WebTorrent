# SpawnDev.WebTorrent

[![NuGet](https://img.shields.io/nuget/v/SpawnDev.WebTorrent.svg)](https://www.nuget.org/packages/SpawnDev.WebTorrent)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](https://opensource.org/licenses/MIT)

Pure C# BitTorrent/WebTorrent client and server. No JavaScript dependencies. Runs on desktop (.NET) and browser (Blazor WASM). 444 tests. 15 BEPs implemented.

## Features

- **Pure C#** — No JavaScript interop, no Node.js, no npm. 100% .NET.
- **Desktop + Browser** — Same library, same API. WPF, console, Blazor WebAssembly.
- **Real WebRTC P2P** — Browser (SpawnDev.BlazorJS) and desktop (SIPSorcery) peers interop seamlessly via the same tracker.
- **15 BEPs** — Full wire protocol, DHT, Fast Extension, ut_metadata, ut_pex, private torrents, magnet file selection, and more.
- **3 Tracker Types** — WebSocket (browser+desktop), HTTP/HTTPS, UDP (desktop).
- **Web Seed Download** — HTTP range requests with multi-file piece assembly (BEP 17/19).
- **Persistent Storage** — OPFS in browser (survives page reloads), filesystem on desktop.
- **Media Streaming** — Service worker intercepts video/audio range requests and serves pieces on demand from the torrent client. True seeking support — no loading entire files into memory. HTTP server for desktop media players.
- **Service Worker** — `webtorrent-sw.js` ships with the library. Handles Cross-Origin-Isolation headers, Blazor loading, and torrent streaming via MessageChannel. One `<script>` tag and your app streams.
- **Random-Access Streaming** — Read any byte range from a torrent file as it downloads. Perfect for ML model weight streaming.
- **Seeding** — Upload pieces to requesting peers with configurable rate limiting.
- **Speed Tracking** — Real-time download/upload bytes/sec per torrent.
- **HuggingFace Integration** — Optional server extension that proxies HuggingFace model CDN with local caching and automatic torrent generation.
- **.torrent Creation** — Create and parse .torrent files. Complete Bencode encoder/decoder.
- **444 Unit Tests** — Every BEP tested, ECDSA crypto verified in browser, security tests for signature verification, protocol compliance verified via PlaywrightMultiTest.

## Packages

| Package | Description |
|---------|-------------|
| [SpawnDev.WebTorrent](https://www.nuget.org/packages/SpawnDev.WebTorrent) | Client library — torrents, peers, streaming |
| [SpawnDev.WebTorrent.Server](https://www.nuget.org/packages/SpawnDev.WebTorrent.Server) | Server library — tracker, web seed |

## Quick Start — Client

```csharp
using SpawnDev.WebTorrent;

var client = new WebTorrentClient();

// Download a torrent
var torrent = await client.AddAsync("magnet:?xt=urn:btih:...");

// Stream a file as it downloads
var file = torrent.Files[0];
var chunk = await file.ReadAsync(offset: 0, length: 65536);

// Seed data
var seeded = await client.SeedAsync(myBytes, "model.onnx");
Console.WriteLine(seeded.MagnetURI);

// Create HTTP server for media streaming (desktop)
var server = client.CreateServer(8080);
// Now play at: http://localhost:8080/{infoHash}/movie.mp4
```

## Service Worker — Media Streaming with Seeking

SpawnDev.WebTorrent includes a service worker (`webtorrent-sw.js`) that enables media streaming with full seeking support. The service worker intercepts HTTP range requests from `<video>` and `<audio>` elements and serves piece data directly from the torrent client.

### Setup

In your `index.html`, replace the static Blazor script tag with the WebTorrent loader:

```html
<!-- This registers the SW, waits for COI headers, then loads Blazor -->
<script src="webtorrent-sw.js"></script>
```

The file deploys automatically to your app root when you reference the SpawnDev.WebTorrent NuGet package. No manual copying needed.

### How It Works

1. `webtorrent-sw.js` registers itself as a service worker and reloads the page to apply Cross-Origin-Isolation headers
2. Once the SW is active, it loads `_framework/blazor.webassembly.js` to start the Blazor app
3. When a `<video src="/webtorrent/{infoHash}/{fileIndex}">` element makes range requests, the SW intercepts them
4. The SW forwards the request to the main window via `MessageChannel`
5. Your Blazor code reads the requested byte range from the torrent's chunk store and responds
6. The SW wraps the data in a proper HTTP `206 Partial Content` response with `Content-Range` headers

### Streaming a Video

```csharp
// Point a video element at the service worker URL
var videoUrl = $"/webtorrent/{infoHashHex}/{fileIndex}";
// The browser handles seeking automatically via range requests
```

### What the Service Worker Provides

- **Cross-Origin-Isolation** — COOP/COEP headers for SharedArrayBuffer support
- **Torrent Streaming** — Intercepts `/webtorrent/` requests, serves pieces via MessageChannel
- **Range Request Support** — Proper `206 Partial Content` responses for seeking
- **Blazor Loading** — Waits for SW ready, then dynamically loads Blazor WebAssembly

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

## Demo Apps

| App | Platform | Features |
|-----|----------|----------|
| Blazor WASM Demo | Browser | Full torrent client UI, media streaming with seeking (via SW), WebRTC P2P, OPFS storage, seeding, .torrent creation |
| WPF Desktop Demo | Windows | Full torrent client UI, media player with seeking, drag-drop .torrent files, SIPSorcery WebRTC |

Both demos connect to the same trackers and can P2P with each other.

## BEP Support

15 BitTorrent Enhancement Proposals implemented:

| BEP | Title | Desktop | Browser |
|-----|-------|---------|---------|
| 3 | BitTorrent Protocol | Yes | Yes |
| 5 | DHT (Kademlia) | Yes | Via relay |
| 44 | DHT Storage | Yes | Via relay |
| 46 | Mutable Items (AI Agents) | Yes | Yes |
| 6 | Fast Extension | Yes | Yes |
| 9 | Magnet Links / ut_metadata | Yes | Yes |
| 10 | Extension Protocol | Yes | Yes |
| 11 | Peer Exchange (ut_pex) | Yes | Yes |
| 15 | UDP Tracker | Yes | N/A |
| 17/19 | Web Seeds | Yes | Yes |
| 20 | Peer ID Conventions | Yes | Yes |
| 23 | Compact Peer Lists | Yes | Yes |
| 27 | Private Torrents | Yes | Yes |
| 53 | Magnet File Selection | Yes | Yes |

See [Docs/bep-support.md](Docs/bep-support.md) for full details.

## Why This Exists

AI models are big. CDNs can't scale when every user downloads the same 2GB model. SpawnDev.WebTorrent turns every browser into a peer — the more users, the faster delivery. Built for [SpawnDev.ILGPU.ML](https://github.com/LostBeard/SpawnDev.ILGPU.ML), the GPU-accelerated ML library for Blazor WebAssembly.

## Architecture

```
Browser Client                    Desktop Client
+---------------+                 +------------------+
| WebTorrent    |                 | WebTorrent       |
| Client        |                 | Client           |
|               |                 |                  |
| WebRTC (P2P)<-+------+-------->+ SIPSorcery (P2P) |
| BlazorJS      |      |         | RTCPeerConnection|
|               |      |         |                  |
| OPFS Storage  |      |         | FileChunkStore   |
+---------------+      |         +------------------+
        |               |                |
        v               v                v
+-------------------------------------------+
| hub.spawndev.com                          |
| TorrentTracker (WebSocket signaling)      |
| WebSeedServer (HTTP range fallback)       |
| HuggingFaceProxy (model CDN cache)        |
+-------------------------------------------+
```

## Documentation

| Doc | Description |
|-----|-------------|
| [Quick Start](Docs/quickstart.md) | Full API examples: download, seed, stream, events, config |
| [Architecture](Docs/architecture.md) | Project structure, data flow, transport layer, wire protocol |
| [Peer ID](Docs/peer-id.md) | Azureus-style peer ID convention (`-SD0110-`) and version history |
| [Trackers](Docs/trackers.md) | Default tracker list, running your own tracker, protocol details |
| [BEP Support](Docs/bep-support.md) | BitTorrent Enhancement Proposal implementation status |
| [AI Agents](Docs/ai-agents.md) | DHT pub/sub for AI agent communication, shared compute, BEP 46 |
| [Service Worker](Docs/service-worker.md) | Media streaming, COI headers, Blazor loading, range requests |
| [Deployment](Docs/deployment.md) | Production server setup, GitHub Pages demo, local development |

## Credits

Built by Todd Tanner ([@LostBeard](https://github.com/LostBeard)) and the SpawnDev team.

## License

MIT
